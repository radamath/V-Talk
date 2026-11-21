using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using VTalk.Core;
using VTalk.Data;
using VTalk.Net;
using VTalk.Security;

namespace VTalk.Services;

/// <summary>Çok kişili sohbet odaları: oluşturma, davet, üyelik denetimi, grup mesajları ve eşitleme.</summary>
public sealed partial class ChatService
{
    public IReadOnlyList<ChatGroup> ListChatGroups() => Db.ListChatGroups();

    public ChatGroup? GetChatGroup(string groupId) => Db.GetChatGroup(groupId);

    /// <summary>Yeni sohbet grubu oluşturur, üyelere davet gönderir.</summary>
    public ChatGroup? CreateChatGroup(string name, IEnumerable<string> memberPeerIds, bool temporary = false)
    {
        name = TextLimits.Clean(name, TextLimits.UnitNameMax);
        if (name.Length == 0) return null;
        if (Db.ListChatGroups().Count >= ChatGroup.MaxGroups) return null;

        var members = memberPeerIds
            .Where(id => id != Identity.PeerId && IsPeerAllowed(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(ChatGroup.MaxMembers - 1)
            .ToList();
        if (members.Count == 0) return null;
        members.Insert(0, Identity.PeerId);

        var group = new ChatGroup
        {
            Id = CryptoUtil.NewId(),
            Name = name,
            CreatedBy = Identity.PeerId,
            CreatedUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            MemberIds = members,
            Temporary = temporary,
        };
        if (temporary) group.Name = AutoGroupName(members);
        Db.UpsertChatGroup(group);
        GroupsChanged?.Invoke();
        BroadcastGroupInvite(group, notifyPeers: members.Where(id => id != Identity.PeerId));
        // Davet paketi kaçsa bile ilk mesaj üyelikleri oluşturup pencereleri açtırır.
        SendGroupText(group.Id, temporary ? "Geçici sohbet başladı." : $"\"{group.Name}\" grubu oluşturuldu.");
        return group;
    }

    public void LeaveChatGroup(string groupId)
    {
        Db.DeleteChatGroup(groupId);
        GroupsChanged?.Invoke();
    }

    /// <summary>Mevcut gruba üye ekler; yeni üyelere davet, mevcut üyelere güncel liste gider.</summary>
    public ChatGroup? AddMembersToChatGroup(string groupId, IEnumerable<string> newPeerIds)
    {
        var group = Db.GetChatGroup(groupId);
        if (group == null || !group.MemberIds.Contains(Identity.PeerId, StringComparer.OrdinalIgnoreCase)) return null;

        var added = newPeerIds
            .Where(id => id != Identity.PeerId && IsPeerAllowed(id))
            .Where(id => !group.MemberIds.Contains(id, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(0, ChatGroup.MaxMembers - group.MemberIds.Count))
            .ToList();
        if (added.Count == 0) return group;

        group.MemberIds.AddRange(added);
        if (group.Temporary) group.Name = AutoGroupName(group.MemberIds);
        Db.UpsertChatGroup(group);
        GroupsChanged?.Invoke();
        // Yeni üyelere tam liste; mevcut üyelere de güncel liste (davet sonrası mesajlar herkese gider).
        BroadcastGroupInvite(group, notifyPeers: group.MemberIds.Where(id => id != Identity.PeerId));
        var joined = string.Join(", ", added.Select(PeerDisplayName));
        SendGroupText(group.Id, $"{joined} sohbete dahil edildi.");
        return group;
    }

    /// <summary>Üyeyi gruptan çıkarır. Geçici sohbette tek kişi kalırsa oda dağılır; reopenPeerId 1:1 için.</summary>
    public (ChatGroup? Group, string? ReopenPeerId) RemoveMemberFromChatGroup(string groupId, string memberPeerId)
    {
        var group = Db.GetChatGroup(groupId);
        if (group == null || !group.MemberIds.Contains(Identity.PeerId, StringComparer.OrdinalIgnoreCase))
            return (null, null);
        if (string.Equals(memberPeerId, Identity.PeerId, StringComparison.OrdinalIgnoreCase))
            return (group, null);
        // Diğer üyeler yalnızca kuranın çıkarmasını kabul eder; yerel kayıt onlarla ayrışmasın.
        if (!string.Equals(group.CreatedBy, Identity.PeerId, StringComparison.OrdinalIgnoreCase))
            return (group, null);

        var before = group.MemberIds.Count;
        group.MemberIds = group.MemberIds
            .Where(id => !string.Equals(id, memberPeerId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (group.MemberIds.Count == before) return (group, null);

        // Çıkarılan kişiye boş liste.
        BroadcastGroupInvite(new ChatGroup
        {
            Id = group.Id,
            Name = group.Name,
            CreatedBy = group.CreatedBy,
            CreatedUtc = group.CreatedUtc,
            MemberIds = [],
            Temporary = group.Temporary,
        }, notifyPeers: [memberPeerId]);

        var others = group.MemberIds.Where(id => id != Identity.PeerId).ToList();
        if (group.Temporary && others.Count <= 1)
        {
            BroadcastGroupInvite(new ChatGroup
            {
                Id = group.Id,
                Name = group.Name,
                CreatedBy = group.CreatedBy,
                CreatedUtc = group.CreatedUtc,
                MemberIds = [],
                Temporary = true,
            }, notifyPeers: others);
            Db.DeleteChatGroup(group.Id);
            GroupsChanged?.Invoke();
            return (null, others.FirstOrDefault());
        }

        if (group.Temporary) group.Name = AutoGroupName(group.MemberIds);
        Db.UpsertChatGroup(group);
        GroupsChanged?.Invoke();
        BroadcastGroupInvite(group, notifyPeers: group.MemberIds.Where(id => id != Identity.PeerId));
        return (group, null);
    }

    private string AutoGroupName(IEnumerable<string> memberIds)
    {
        var names = memberIds
            .Where(id => id != Identity.PeerId)
            .Select(PeerDisplayName)
            .Where(n => n.Length > 0)
            .Take(4);
        var joined = string.Join(", ", names);
        return TextLimits.Clean(joined.Length > 0 ? joined : "Geçici sohbet", TextLimits.UnitNameMax);
    }

    private string PeerDisplayName(string peerId)
    {
        var member = Directory.GetMember(peerId);
        if (member is { Name.Length: > 0 }) return member.Name;
        var snap = Registry.Get(peerId);
        if (snap is { Name.Length: > 0 }) return snap.Name;
        if (snap is { User.Length: > 0 }) return snap.User;
        return peerId.Length >= 8 ? peerId[..8] : peerId;
    }

    private static readonly JsonSerializerOptions GroupInviteJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private string SerializeGroupPayload(ChatGroup group, IReadOnlyList<string>? mentions = null) =>
        JsonSerializer.Serialize(new GroupInvitePayload
        {
            Id = group.Id,
            Name = group.Name,
            CreatedBy = group.CreatedBy,
            Members = group.MemberIds,
            Temporary = group.Temporary,
            Mentions = mentions is { Count: > 0 } ? mentions.ToList() : null,
        }, GroupInviteJson);

    /// <summary>
    /// Giden TCP yoksa (NAT/FW) kısa süre gelen bağlantıyı bekler — 1:1 DeliverAsync ile aynı dayanıklılık.
    /// </summary>
    private async Task<PeerConnection> GetSendConnectionAsync(string peerId)
    {
        try
        {
            return await _pool.GetAsync(peerId);
        }
        catch (PeerUnavailableException)
        {
            for (var i = 0; i < 8 && !_pool.HasLiveConnection(peerId); i++)
                await Task.Delay(500);
            return await _pool.GetAsync(peerId);
        }
    }

    private void BroadcastGroupInvite(ChatGroup group, IEnumerable<string> notifyPeers)
    {
        // Çevrimdışı üyeye davet, geri geldiğinde grup eşitlemesiyle gider.
        foreach (var peerId in notifyPeers.Where(id => IsPeerAllowed(id) && IsOnlineNow(id)))
        {
            var target = peerId;
            var snapshot = group;
            // Hata nedeni SendGroupInviteAsync içinde günlüğe yazılır.
            _ = Task.Run(() => SendGroupInviteAsync(snapshot, target));
        }
    }

    private async Task<GroupSendResult> SendGroupInviteAsync(ChatGroup group, string peerId)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (attempt > 0) await Task.Delay(500 * attempt);
                var conn = await GetSendConnectionAsync(peerId);
                var reply = await conn.RequestAsync(new Packet
                {
                    T = PacketTypes.GroupInvite,
                    Name = group.Name,
                    Data = SerializeGroupPayload(group),
                }, RequestTimeout);
                if (reply.T == PacketTypes.Ack) return GroupSendResult.Delivered;
                if (reply.Err == "unsupported")
                {
                    Log.Warn($"Grup daveti desteklenmiyor ({peerId[..Math.Min(8, peerId.Length)]}): karşı tarafın V-Talk sürümü güncel değil.");
                    return GroupSendResult.Rejected;
                }
                if (reply.Err == "forbidden")
                {
                    // Karşı taraf bu üyelik değişikliğini yetkisiz buldu; yeniden denemek sonucu değiştirmez.
                    Log.Warn($"Grup daveti reddedildi ({peerId[..Math.Min(8, peerId.Length)]}): yetkisiz üyelik değişikliği.");
                    return GroupSendResult.Rejected;
                }
                last = new IOException(reply.Err ?? reply.T);
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }
        if (last != null && !_deliveryBackoff.IsFailing(peerId))
            Log.Warn($"Grup daveti gönderilemedi ({peerId[..Math.Min(8, peerId.Length)]}): {last.Message}");
        return GroupSendResult.Unreachable;
    }

    public StoredMessage? SendGroupNudge(string groupId)
    {
        var group = Db.GetChatGroup(groupId);
        if (group == null || !group.MemberIds.Contains(Identity.PeerId, StringComparer.OrdinalIgnoreCase))
            return null;
        var key = "out:" + group.ConversationId;
        if (DateTime.UtcNow - _lastNudge.GetValueOrDefault(key) < TimeSpan.FromSeconds(5)) return null;
        _lastNudge[key] = DateTime.UtcNow;

        var conv = group.ConversationId;
        var m = new StoredMessage
        {
            PeerId = conv,
            MsgId = CryptoUtil.NewId(),
            Direction = MessageDirection.Outgoing,
            Kind = MessageKind.Nudge,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            State = MessageState.Delivered,
            Data = new MessageData { SenderPeerId = Identity.PeerId },
        };
        if (!Db.InsertMessage(m)) return null;
        MessageAdded?.Invoke(m);

        // Çevrimdışı üyeye anlık deneme yapılmaz: çevrimdışı kuyruğu, üye geri gelince eksikleri sırayla gönderir.
        foreach (var peerId in group.MemberIds.Where(id => id != Identity.PeerId && IsPeerAllowed(id) && IsOnlineNow(id)))
        {
            var target = peerId;
            var groupSnap = group;
            var msgSnap = m;
            _ = Task.Run(() => DeliverGroupNudgeAsync(groupSnap, msgSnap, target));
        }
        return m;
    }

    private Packet ToGroupNudgePacket(ChatGroup group, StoredMessage m) => new()
    {
        T = PacketTypes.Nudge,
        Id = m.MsgId,
        Ts = m.Timestamp,
        Grp = group.Id,
        Name = group.Name,
        Data = SerializeGroupPayload(group),
    };

    private async Task<bool> DeliverGroupNudgeAsync(ChatGroup group, StoredMessage m, string peerId)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (attempt > 0) await Task.Delay(500 * attempt);
                var conn = await GetSendConnectionAsync(peerId);
                var reply = await conn.RequestAsync(ToGroupNudgePacket(group, m), RequestTimeout);
                if (reply.T == PacketTypes.Ack) return true;
                if (reply.Err is "unsupported" or "forbidden" or "invalid")
                {
                    Log.Warn($"Grup titreşimi reddedildi ({peerId[..Math.Min(8, peerId.Length)]}): {reply.Err}");
                    return false;
                }
                last = new IOException(reply.Err ?? reply.T);
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }
        if (last != null && !_deliveryBackoff.IsFailing(peerId))
            Log.Warn($"Grup titreşimi iletilemedi ({peerId[..Math.Min(8, peerId.Length)]}): {last.Message}");
        return false;
    }

    public StoredMessage? SendGroupText(string groupId, string text, ReplyRef? reply = null)
    {
        text = TextLimits.Clean(text, TextLimits.MessageMax, allowNewLines: true);
        var group = Db.GetChatGroup(groupId);
        if (text.Length == 0 || group == null || !group.MemberIds.Contains(Identity.PeerId, StringComparer.OrdinalIgnoreCase))
            return null;

        var mentions = Mentions.ResolvePeerIds(text, group.MemberIds, PeerDisplayName);
        var conv = group.ConversationId;
        var m = new StoredMessage
        {
            PeerId = conv,
            MsgId = CryptoUtil.NewId(),
            Direction = MessageDirection.Outgoing,
            Kind = MessageKind.Text,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            State = MessageState.Delivered,
            Data = new MessageData
            {
                Text = text,
                SenderPeerId = Identity.PeerId,
                Mentions = mentions.Count > 0 ? mentions : null,
            },
        };
        reply?.ApplyTo(m.Data);
        if (!Db.InsertMessage(m)) return null;
        MessageAdded?.Invoke(m);

        // Çevrimdışı üyeye anlık deneme yapılmaz: çevrimdışı kuyruğu, üye geri gelince eksikleri sırayla gönderir.
        foreach (var peerId in group.MemberIds.Where(id => id != Identity.PeerId && IsPeerAllowed(id) && IsOnlineNow(id)))
        {
            var target = peerId;
            var groupSnap = group;
            var msgSnap = m;
            _ = Task.Run(() => DeliverGroupTextAsync(groupSnap, msgSnap, target));
        }
        return m;
    }

    private Packet ToGroupTextPacket(ChatGroup group, StoredMessage m) => new()
    {
        T = PacketTypes.Message,
        Id = m.MsgId,
        Ts = m.Timestamp,
        Text = m.Data.Text,
        Grp = group.Id,
        Name = group.Name,
        Data = SerializeGroupPayload(group, m.Data.Mentions),
        ReplyId = m.Data.ReplyToMsgId,
        ReplyBy = m.Data.ReplyToSender,
        ReplyText = m.Data.ReplyToText,
    };

    private async Task<GroupSendResult> DeliverGroupTextAsync(ChatGroup group, StoredMessage m, string peerId)
    {
        Exception? last = null;
        var rateLimited = false;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                // Karşı taraf hız sınırına takıldıysa ("rate") pencere dolmadan tekrar denemek yine reddedilir.
                if (attempt > 0) await Task.Delay(rateLimited ? InboundWindow + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 2000)) : TimeSpan.FromMilliseconds(500 * attempt));
                var conn = await GetSendConnectionAsync(peerId);
                var reply = await conn.RequestAsync(ToGroupTextPacket(group, m), RequestTimeout);
                if (reply.T == PacketTypes.Ack)
                {
                    RecordGroupDelivered(group, m, peerId);
                    return GroupSendResult.Delivered;
                }
                if (reply.Err is "unsupported" or "forbidden" or "invalid")
                {
                    Log.Warn($"Grup mesajı reddedildi ({peerId[..Math.Min(8, peerId.Length)]}): {reply.Err}");
                    return GroupSendResult.Rejected;
                }
                rateLimited = reply.Err == "rate";
                last = new IOException(reply.Err ?? reply.T);
            }
            catch (Exception ex)
            {
                rateLimited = false;
                last = ex;
            }
        }
        // Hız sınırı ulaşılamazlık değildir: mesaj kuyrukta kalır, eşitleme sonra aralıklı gönderir.
        if (rateLimited) return GroupSendResult.Busy;
        if (last != null && !_deliveryBackoff.IsFailing(peerId))
            Log.Warn($"Grup mesajı iletilemedi ({peerId[..Math.Min(8, peerId.Length)]}): {last.Message}");
        return GroupSendResult.Unreachable;
    }

    /// <summary>
    /// Eş yeniden erişilebilir olunca üye olduğu grupların davetini ve bizim giden grup metinlerini yeniden yollar.
    /// Karşı taraf yinelenen msg_id'yi yok sayar; ilk katılımda davet diyaloğu açılır.
    /// </summary>
    private async Task SyncGroupsToPeerAsync(string peerId)
    {
        if (!IsPeerAllowed(peerId)) return;
        var now = DateTime.UtcNow;
        if (_lastGroupSync.TryGetValue(peerId, out var last) && now - last < GroupSyncInterval)
            return;
        // Aynı eşe eşzamanlı ikinci eşitleme aynı paketleri tekrar yollamaktan başka bir şey yapmaz.
        if (!_groupSyncRunning.TryAdd(peerId, 0)) return;
        try
        {
            _lastGroupSync[peerId] = now;
            bool completed;
            try
            {
                completed = await TrySyncGroupsAsync(peerId);
            }
            catch (Exception ex)
            {
                completed = false;
                Log.Error("Grup eşitleme hatası", ex);
            }
            // Yarıda kalan eşitleme, eş yeniden ulaşılabilir olunca beklemeden tekrar denensin; zamanlayıcı ise seyreltir.
            if (!completed)
            {
                _lastGroupSync.TryRemove(new KeyValuePair<string, DateTime>(peerId, now));
                RecordDeliveryOutcome(peerId, reachable: false, "grup mesajları iletilemedi");
            }
            else
            {
                RecordDeliveryOutcome(peerId, reachable: true, null);
            }
        }
        finally
        {
            _groupSyncRunning.TryRemove(peerId, out _);
        }
    }

    /// <returns>Eşe ulaşılamadığı için yarıda kaldıysa false.</returns>
    private async Task<bool> TrySyncGroupsAsync(string peerId)
    {
        var groups = Db.ListChatGroups()
            .Where(g => g.MemberIds.Contains(peerId, StringComparer.OrdinalIgnoreCase))
            .ToList();

        foreach (var group in groups)
        {
            var invite = await SendGroupInviteAsync(group, peerId);
            if (invite == GroupSendResult.Unreachable) return false;
            // Daveti reddeden eş, aynı üye listesini taşıyan grup mesajlarını da reddeder.
            if (invite == GroupSendResult.Rejected) continue;

            // Çevrimdışı kuyruk: bu üyenin teslim almadığı mesajlar sırayla (son N değil, eksik olanların hepsi).
            foreach (var m in UndeliveredGroupMessages(group, peerId))
            {
                var result = await DeliverGroupTextAsync(group, m, peerId);
                // Hız sınırı sürüyorsa pencerenin dolmasını bekleyip aynı mesajla kaldığı yerden devam et.
                for (var wait = 0; result == GroupSendResult.Busy && wait < MaxBusyWaits; wait++)
                {
                    await Task.Delay(InboundWindow);
                    result = await DeliverGroupTextAsync(group, m, peerId);
                }
                if (result == GroupSendResult.Unreachable) return false;
                // Uzun süre meşgulse kalanlar kuyrukta durur; zamanlayıcı bir sonraki turda devam eder.
                if (result == GroupSendResult.Busy) return true;
                // Karşı tarafın hız sınırının (10 sn'de 40 istek) altında kal.
                await Task.Delay(GroupQueueSpacing);
            }
        }
        return true;
    }

    private bool IsOnlineNow(string peerId) => Registry.Get(peerId) is { IsOnline: true };

    private List<StoredMessage> UndeliveredGroupMessages(ChatGroup group, string peerId) =>
        Db.LoadUndeliveredGroupMessages(group.ConversationId, peerId,
            DateTimeOffset.UtcNow.Add(-GroupQueueMaxAge).ToUnixTimeMilliseconds(), GroupQueueMaxMessages);

    /// <summary>Zamanlayıcı: çevrimiçi görünen ve teslim bekleyen grup mesajı olan eşleri bulur.</summary>
    private bool HasUndeliveredGroupMessages(string peerId) =>
        Db.ListChatGroups()
            .Where(g => g.MemberIds.Contains(peerId, StringComparer.OrdinalIgnoreCase))
            .Any(g => Db.LoadUndeliveredGroupMessages(g.ConversationId, peerId,
                DateTimeOffset.UtcNow.Add(-GroupQueueMaxAge).ToUnixTimeMilliseconds(), 1).Count > 0);

    private enum GroupSendResult
    {
        Delivered,
        /// <summary>Karşı taraf kalıcı olarak reddetti (desteklenmiyor/yetkisiz/geçersiz); tekrar denemek fayda etmez.</summary>
        Rejected,
        /// <summary>Bağlantı veya zaman aşımı; eş yeniden erişilebilir olunca tekrar denenir.</summary>
        Unreachable,
        /// <summary>Karşı tarafın hız sınırı; mesaj kuyrukta kalır, daha sonra aralıklı gönderilir.</summary>
        Busy,
    }

    /// <summary>Karartmada özellik adları değişmesin diye sabit JSON adları (aksi halde davet/mesaj forbidden olur).</summary>
    private sealed class GroupInvitePayload
    {
        [System.Text.Json.Serialization.JsonPropertyName("id")] public string Id { get; set; } = "";
        [System.Text.Json.Serialization.JsonPropertyName("name")] public string Name { get; set; } = "";
        [System.Text.Json.Serialization.JsonPropertyName("createdBy")] public string CreatedBy { get; set; } = "";
        [System.Text.Json.Serialization.JsonPropertyName("members")] public List<string> Members { get; set; } = [];
        [System.Text.Json.Serialization.JsonPropertyName("temporary")] public bool Temporary { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("mentions")] public List<string>? Mentions { get; set; }
    }

    /// <summary>
    /// Davet veya grup mesajıyla gelen üye listesini, <see cref="ChatGroup.EvaluateUpdate"/> kurallarına göre uygular.
    /// Yetkisiz değişiklik yerel kaydı değiştirmez. Çıkarılmada grup listeden kalkar ama mesaj geçmişi silinmez.
    /// </summary>
    /// <returns>Güncelleme yetkisizse false; aksi halde (uygulandı veya yapılacak bir şey yok) true.</returns>
    private bool AcceptGroupInvite(string fromPeerId, Packet p)
    {
        GroupInvitePayload? payload = null;
        if (!string.IsNullOrEmpty(p.Data))
        {
            try { payload = JsonSerializer.Deserialize<GroupInvitePayload>(p.Data, GroupInviteJson); }
            catch (JsonException) { return true; }
        }
        if (payload == null || !ChatGroup.IsValidGroupId(payload.Id)) return true;

        var members = ChatGroup.NormalizeMembers(payload.Members);
        var existing = Db.GetChatGroup(payload.Id);
        switch (ChatGroup.EvaluateUpdate(existing, fromPeerId, Identity.PeerId, members))
        {
            case GroupUpdateDecision.Ignore:
                return true;
            case GroupUpdateDecision.Forbidden:
                Log.Warn($"Yetkisiz grup üyeliği değişikliği reddedildi ({fromPeerId[..8]}, grup {payload.Id[..8]}).");
                return false;
            case GroupUpdateDecision.RemovedMe:
                Db.DeleteChatGroup(payload.Id, keepMessages: true);
                GroupsChanged?.Invoke();
                return true;
        }

        var name = TextLimits.Clean(payload.Name.Length > 0 ? payload.Name : p.Name, TextLimits.UnitNameMax);
        if (name.Length == 0) return true;
        // Yeni davette daveti gönderen üyedir. Bilinen grupta gönderen kendini listeden çıkardıysa ayrılmıştır.
        if (existing == null && !members.Contains(fromPeerId, StringComparer.OrdinalIgnoreCase))
            members.Add(fromPeerId);
        if (members.Count < 2) return true;

        var group = new ChatGroup
        {
            Id = payload.Id,
            Name = name,
            // Kuran yalnızca ilk davette öğrenilir; sonradan değiştirilemez (çıkarma yetkisi buna bağlı).
            CreatedBy = existing?.CreatedBy ?? ResolveCreator(payload.CreatedBy, fromPeerId, members),
            CreatedUtc = existing?.CreatedUtc ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            MemberIds = members,
            Temporary = payload.Temporary || existing?.Temporary == true,
        };
        Db.UpsertChatGroup(group);
        GroupsChanged?.Invoke();
        // İlk katılımda arayüz pencere açsın / bildirsin (sonraki mesajlardaki üye güncellemesinde değil).
        if (existing == null)
            GroupInviteReceived?.Invoke(group, fromPeerId);
        return true;
    }

    /// <summary>Davetteki kuran bilgisi yalnızca geçerli ve üye listesinde ise kabul edilir; yoksa daveti gönderen.</summary>
    private static string ResolveCreator(string? claimed, string fromPeerId, IReadOnlyCollection<string> members)
    {
        var normalized = (claimed ?? "").Trim().ToLowerInvariant();
        return OrgDirectory.IsValidPeerId(normalized) && members.Contains(normalized, StringComparer.OrdinalIgnoreCase)
            ? normalized
            : fromPeerId;
    }

    private string? ReceiveGroupMessage(string fromPeerId, Packet p)
    {
        if (!ChatGroup.IsValidGroupId(p.Grp)) return "invalid";
        AcceptGroupInvite(fromPeerId, p); // üye listesini / adı güncelle
        var group = Db.GetChatGroup(p.Grp!);
        if (group == null) return "forbidden";
        if (!group.MemberIds.Contains(fromPeerId, StringComparer.OrdinalIgnoreCase) ||
            !group.MemberIds.Contains(Identity.PeerId, StringComparer.OrdinalIgnoreCase))
            return "forbidden";

        var text = TextLimits.Clean(p.Text, TextLimits.MessageMax, allowNewLines: true);
        if (text.Length == 0) return null;

        List<string>? mentions = null;
        if (!string.IsNullOrEmpty(p.Data))
        {
            try
            {
                var payload = JsonSerializer.Deserialize<GroupInvitePayload>(p.Data, GroupInviteJson);
                mentions = payload?.Mentions?
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (JsonException) { /* üyelik zaten AcceptGroupInvite'ta işlendi */ }
        }
        if (mentions is not { Count: > 0 })
            mentions = Mentions.ResolvePeerIds(text, group.MemberIds, PeerDisplayName);

        var m = new StoredMessage
        {
            PeerId = group.ConversationId,
            MsgId = p.Id!,
            Direction = MessageDirection.Incoming,
            Kind = MessageKind.Text,
            Timestamp = RemoteTimestamp.Sanitize(p.Ts),
            State = MessageState.Delivered,
            Data = new MessageData
            {
                Text = text,
                SenderPeerId = fromPeerId,
                Mentions = mentions is { Count: > 0 } ? mentions : null,
            },
        };
        ReplyRef.TryCreate(p.ReplyId, p.ReplyBy, p.ReplyText)?.ApplyTo(m.Data);
        if (!Db.InsertMessage(m)) return null;
        MessageAdded?.Invoke(m);
        return null;
    }

    private string? ReceiveGroupNudge(string fromPeerId, Packet p)
    {
        if (!ChatGroup.IsValidGroupId(p.Grp)) return "invalid";
        AcceptGroupInvite(fromPeerId, p);
        var group = Db.GetChatGroup(p.Grp!);
        if (group == null) return "forbidden";
        if (!group.MemberIds.Contains(fromPeerId, StringComparer.OrdinalIgnoreCase) ||
            !group.MemberIds.Contains(Identity.PeerId, StringComparer.OrdinalIgnoreCase))
            return "forbidden";

        var m = new StoredMessage
        {
            PeerId = group.ConversationId,
            MsgId = p.Id!,
            Direction = MessageDirection.Incoming,
            Kind = MessageKind.Nudge,
            Timestamp = RemoteTimestamp.Sanitize(p.Ts),
            State = MessageState.Delivered,
            Data = new MessageData { SenderPeerId = fromPeerId },
        };
        if (!Db.InsertMessage(m)) return null;
        MessageAdded?.Invoke(m);

        var key = "in:" + group.ConversationId;
        if (DateTime.UtcNow - _lastNudge.GetValueOrDefault(key) > TimeSpan.FromSeconds(5))
        {
            _lastNudge[key] = DateTime.UtcNow;
            NudgeReceived?.Invoke(group.ConversationId);
        }
        return null;
    }
}
