using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using VTalk.Core;
using VTalk.Data;
using VTalk.Net;
using VTalk.Security;

namespace VTalk.Services;

/// <summary>
/// Uygulamanın merkezi: keşif, eş kayıtları, mesaj teslimi (kuyruk + onay), gelen paketlerin
/// doğrulanması, rol tabanlı duyuru yetkisi ve dizin/avatar senkronizasyonu.
/// Olaylar arka plan iş parçacıklarında tetiklenir; arayüz kendi Dispatcher'ına aktarmalıdır.
/// </summary>
public sealed class ChatService : IDisposable
{
    public const string ScopeAll = "all";
    public const string ScopeUnit = "unit";
    private const int MaxAvatarBytes = 256 * 1024;
    private const int MaxKnownPeers = 5000;
    private const int MaxInboundPerWindow = 40;
    private const int MaxNewIdentitiesPerAddress = 20;
    private static readonly TimeSpan PresenceTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan InboundWindow = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan NewIdentityWindow = TimeSpan.FromMinutes(10);

    public Identity Identity { get; }
    public Settings Settings { get; }
    public Database Db { get; }
    public DirectoryService Directory { get; }
    public PeerRegistry Registry { get; } = new();
    public FileTransferService Files { get; }
    public NetworkGuard Network { get; }

    private readonly DiscoveryService _discovery;
    private readonly PeerServer _server;
    private readonly ConnectionPool _pool;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _deliveryLocks = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastReply = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastPersist = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastDirFetch = new();
    private readonly ConcurrentDictionary<string, byte> _avatarFetching = new();
    private readonly ConcurrentDictionary<string, (DateTime window, int count)> _inboundRate = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastNudge = new();
    private readonly ConcurrentDictionary<IPAddress, (DateTime window, int count)> _newIdentities = new();
    private Timer? _presenceTimer;
    private Timer? _flushTimer;
    private int _dirFetching;
    private byte[]? _avatarBytes;
    private string? _avatarHash;
    private PresenceStatus? _autoStatus;

    public event Action<PeerSnapshot, bool>? PeerChanged;
    public event Action<StoredMessage>? MessageAdded;
    public event Action<StoredMessage>? MessageUpdated;
    public event Action<string>? NudgeReceived;
    public event Action<StoredMessage>? BulletinReceived;
    public event Action<string>? AvatarUpdated;
    public event Action? DirectoryChanged;

    public ChatService(Identity identity, Settings settings, Database db, DirectoryService directory, bool loopbackOnly = false)
    {
        Identity = identity;
        Settings = settings;
        Db = db;
        Directory = directory;
        Network = new NetworkGuard(settings.AllowPublicNetworks, settings.ExtraAddresses, loopbackOnly);
        _pool = new ConnectionPool(identity, Registry, HandlePacketAsync, Network);
        Files = new FileTransferService(this, _pool);
        _server = new PeerServer(identity, HandlePacketAsync, Files.ServeAsync, Network);
        _discovery = new DiscoveryService(identity, BuildAnnounce, ExtraTargets, Network,
            () => Registry.Online.Select(p => p.Address!).Distinct());
        _discovery.Announced += OnAnnounce;
        Directory.Changed += () =>
        {
            _discovery.Broadcast();
            DirectoryChanged?.Invoke();
        };
    }

    public PresenceStatus EffectiveStatus => _autoStatus ?? Settings.Status;
    public int TcpPort => _server.Port;
    public string? MyAvatarHash => _avatarHash;

    public void Start()
    {
        foreach (var c in Db.LoadContacts())
        {
            Registry.Upsert(new PeerSnapshot
            {
                PeerId = c.PeerId, PublicKey = c.PublicKey, Name = c.Name, User = c.User, Machine = c.Machine,
                Note = c.Note, AvatarHash = c.AvatarHash, Status = PresenceStatus.Offline,
                LastSeenUtc = DateTimeOffset.FromUnixTimeMilliseconds(c.LastSeen).UtcDateTime,
            });
        }
        ReloadAvatar();
        _server.Start(Settings.PreferredTcpPort);
        _discovery.Start();
        _presenceTimer = new Timer(_ => ExpirePeers(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        _flushTimer = new Timer(_ => FlushAllPending(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    // ------------------------------------------------------------------ Durum / profil

    public void UpdateProfile(string? displayName = null, string? note = null, PresenceStatus? status = null)
    {
        if (displayName != null) Settings.DisplayName = displayName;
        if (note != null) Settings.Note = note;
        if (status != null)
        {
            Settings.Status = status.Value;
            _autoStatus = null;
        }
        Settings.Save();
        _discovery.Broadcast();
    }

    /// <summary>Boşta kalma algılandığında otomatik "Dışarıda" durumu.</summary>
    public void SetAutoAway(bool away)
    {
        var desired = away && Settings.Status == PresenceStatus.Online ? PresenceStatus.Away : (PresenceStatus?)null;
        if (desired == _autoStatus) return;
        _autoStatus = desired;
        _discovery.Broadcast();
    }

    public void ReloadAvatar()
    {
        try
        {
            if (File.Exists(AppPaths.AvatarFile) && new FileInfo(AppPaths.AvatarFile).Length <= MaxAvatarBytes)
            {
                _avatarBytes = File.ReadAllBytes(AppPaths.AvatarFile);
                _avatarHash = Convert.ToHexString(SHA256.HashData(_avatarBytes)).ToLowerInvariant();
            }
            else
            {
                _avatarBytes = null;
                _avatarHash = null;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Avatar okunamadı: " + ex.Message);
        }
        _discovery.Broadcast();
    }

    public static string AvatarCachePath(string hash) => Path.Combine(AppPaths.AvatarCacheDir, hash + ".png");

    private AnnouncePayload BuildAnnounce() => new()
    {
        Name = Settings.DisplayName,
        User = Environment.UserName,
        Machine = Environment.MachineName,
        Status = (int)EffectiveStatus,
        Note = Settings.Note,
        Port = _server.Port,
        DirVersion = Directory.Version,
        OrgFp = Directory.OrgFingerprint,
        AvatarHash = _avatarHash,
    };

    // Tek IP'ler ve CIDR alt ağlar (VPN / farklı VLAN) tekil keşif hedeflerine genişletilir; yayın bunları geçemez.
    private IEnumerable<IPAddress> ExtraTargets() => NetworkTargets.Expand(Settings.ExtraAddresses);

    // ------------------------------------------------------------------ Keşif

    private void OnAnnounce(VerifiedAnnounce a)
    {
        var p = a.Payload;
        if (p.Kind == "bye")
        {
            var off = Registry.MarkOffline(p.Id);
            _pool.Drop(p.Id);
            if (off != null) PeerChanged?.Invoke(off, false);
            return;
        }

        var snapshot = new PeerSnapshot
        {
            PeerId = p.Id, PublicKey = a.PublicKey, Name = p.Name, User = p.User, Machine = p.Machine,
            Note = p.Note, Status = (PresenceStatus)p.Status, Address = a.Source, Port = p.Port,
            LastSeenUtc = DateTime.UtcNow, DirectoryVersion = p.DirVersion, OrgFingerprint = p.OrgFp, AvatarHash = p.AvatarHash,
        };
        if (snapshot.Status == PresenceStatus.Offline) snapshot = snapshot with { Status = PresenceStatus.Online };

        var known = Registry.Get(p.Id);
        // Kayıtlı eş sayısı için üst sınır (dizinde olmayan bilinmeyen kimlikler için yeni kayıt açılmaz).
        if (known == null && Registry.Count >= MaxKnownPeers && Directory.GetMember(p.Id) == null)
            return;

        // Çok arayüzlü / VPN ortamında aynı eş birden çok kaynak adresinden duyulabilir. Ulaşılabilirliği
        // kanıtlanmış (halen açık) bir bağlantı varken yalnızca kaynak adres değiştiyse kararlı adresi koru;
        // böylece geçerli bağlantı boşuna kapatılıp ulaşılamayan bir adrese yeniden bağlanılmaya çalışılmaz.
        if (known?.Address != null && known.Port == snapshot.Port &&
            !known.Address.Equals(snapshot.Address) && _pool.HasLiveConnection(p.Id))
        {
            snapshot = snapshot with { Address = known.Address };
        }

        var prev = Registry.Upsert(snapshot);
        bool cameOnline = prev == null || !prev.IsOnline;
        bool infoChanged = prev == null || prev.Name != snapshot.Name || prev.Note != snapshot.Note ||
                           prev.AvatarHash != snapshot.AvatarHash || prev.Machine != snapshot.Machine;
        bool presenceChanged = cameOnline || prev!.Status != snapshot.Status;

        if (prev?.Address != null && (!prev.Address.Equals(snapshot.Address) || prev.Port != snapshot.Port))
            _pool.Drop(p.Id);

        // Sıkı modda dizin dışı kimlikler kalıcı kişi listesine yazılmaz.
        if (IsPeerAllowed(p.Id) &&
            (infoChanged || cameOnline || DateTime.UtcNow - _lastPersist.GetValueOrDefault(p.Id) > TimeSpan.FromMinutes(5)))
        {
            _lastPersist[p.Id] = DateTime.UtcNow;
            PersistContact(snapshot);
        }

        if (cameOnline)
        {
            // Yeni gelen eşin bizi beklemeden görmesi için hemen tekil yanıt ver.
            if (DateTime.UtcNow - _lastReply.GetValueOrDefault(p.Id) > TimeSpan.FromSeconds(10))
            {
                _lastReply[p.Id] = DateTime.UtcNow;
                _discovery.SendTo(a.Source);
            }
            if (IsPeerAllowed(p.Id)) _ = Task.Run(() => FlushPendingAsync(p.Id));
        }

        Directory.ReportOrgFingerprintSeen(p.OrgFp);
        if (p.DirVersion > Directory.Version && (Directory.OrgFingerprint == null || p.OrgFp == Directory.OrgFingerprint))
            _ = Task.Run(() => FetchDirectoryAsync(p.Id));

        if (p.AvatarHash != null && IsVisible(p.Id) && !File.Exists(AvatarCachePath(p.AvatarHash)))
            _ = Task.Run(() => FetchAvatarAsync(p.Id, p.AvatarHash));

        if (infoChanged || presenceChanged) PeerChanged?.Invoke(snapshot, cameOnline);
    }

    private void PersistContact(PeerSnapshot s)
    {
        try
        {
            Db.UpsertContact(new ContactRecord
            {
                PeerId = s.PeerId, PublicKey = s.PublicKey, Name = s.Name, User = s.User, Machine = s.Machine,
                Note = s.Note, AvatarHash = s.AvatarHash, LastSeen = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            });
        }
        catch (Exception ex)
        {
            Log.Error("Kişi kaydedilemedi", ex);
        }
    }

    private void ExpirePeers()
    {
        foreach (var p in Registry.ExpireOlderThan(PresenceTimeout))
        {
            _pool.Drop(p.PeerId);
            PeerChanged?.Invoke(p, false);
        }
    }

    // ------------------------------------------------------------------ Gönderme

    public StoredMessage? SendText(string peerId, string text)
    {
        text = TextLimits.Clean(text, TextLimits.MessageMax, allowNewLines: true);
        if (text.Length == 0 || !IsPeerAllowed(peerId)) return null;
        return Enqueue(peerId, MessageKind.Text, new MessageData { Text = text });
    }

    public StoredMessage? SendNudge(string peerId)
    {
        var peer = Registry.Get(peerId);
        if (peer is not { IsOnline: true } || !IsPeerAllowed(peerId)) return null;
        if (DateTime.UtcNow - _lastNudge.GetValueOrDefault("out:" + peerId) < TimeSpan.FromSeconds(5)) return null;
        _lastNudge["out:" + peerId] = DateTime.UtcNow;
        return Enqueue(peerId, MessageKind.Nudge, new MessageData());
    }

    public async Task<StoredMessage?> SendFileAsync(string peerId, string path)
    {
        if (!IsPeerAllowed(peerId)) throw new InvalidOperationException("Bu kişi organizasyon dizininde olmadığı için dosya gönderilemez.");
        var info = new FileInfo(path);
        if (!info.Exists || info.Length == 0) throw new InvalidOperationException("Dosya bulunamadı veya boş.");
        if (info.Length > FileTransferService.MaxFileSize) throw new InvalidOperationException("Dosya çok büyük (en fazla 20 GB).");
        var sha = await Task.Run(() =>
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
            return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
        });
        var name = FileTransferService.SanitizeFileName(info.Name);
        return Enqueue(peerId, MessageKind.File, new MessageData
        {
            FileName = name, FileSize = info.Length, FilePath = info.FullName, FileSha256 = sha,
            IsImage = FileTransferService.IsImageName(name), FileState = FileState.Offered,
        });
    }

    public bool CanSendBulletin(string scope)
    {
        var me = Directory.GetMember(Identity.PeerId);
        if (me == null) return false;
        return scope switch
        {
            ScopeAll => me.Role == Role.Admin,
            ScopeUnit => me.Role is Role.Admin or Role.UnitManager && me.UnitId != null,
            _ => false,
        };
    }

    public int SendBulletin(string scope, string text)
    {
        if (!CanSendBulletin(scope)) throw new UnauthorizedAccessException("Bu duyuruyu gönderme yetkiniz yok.");
        text = TextLimits.Clean(text, TextLimits.MessageMax, allowNewLines: true);
        if (text.Length == 0) return 0;

        IEnumerable<string> recipients;
        if (scope == ScopeAll)
        {
            recipients = Registry.All.Select(p => p.PeerId)
                .Union(Directory.Current?.Members.Select(m => m.PeerId) ?? []);
        }
        else
        {
            var unitId = Directory.GetMember(Identity.PeerId)!.UnitId!;
            recipients = Directory.MembersOfUnit(unitId).Select(m => m.PeerId);
        }

        int count = 0;
        foreach (var peerId in recipients.Distinct().Where(id => id != Identity.PeerId && IsPeerAllowed(id)))
        {
            Enqueue(peerId, MessageKind.Announcement, new MessageData { Text = text, Scope = scope });
            count++;
        }
        return count;
    }

    private StoredMessage Enqueue(string peerId, MessageKind kind, MessageData data)
    {
        var m = new StoredMessage
        {
            PeerId = peerId, MsgId = CryptoUtil.NewId(), Direction = MessageDirection.Outgoing, Kind = kind,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), State = MessageState.Pending, Data = data,
        };
        Db.InsertMessage(m);
        MessageAdded?.Invoke(m);
        _ = Task.Run(() => FlushPendingAsync(peerId));
        return m;
    }

    private void FlushAllPending()
    {
        try
        {
            foreach (var peerId in Db.PeersWithPendingOutgoing())
            {
                if (Registry.Get(peerId) is { IsOnline: true }) _ = Task.Run(() => FlushPendingAsync(peerId));
            }
        }
        catch (Exception ex)
        {
            Log.Error("Bekleyen mesajlar taranamadı", ex);
        }
    }

    /// <summary>Kuyruktaki mesajları sırayla teslim eder; eş başına tek teslim döngüsü çalışır.</summary>
    private async Task FlushPendingAsync(string peerId)
    {
        if (Registry.Get(peerId) is not { IsOnline: true } || !IsPeerAllowed(peerId)) return;
        var gate = _deliveryLocks.GetOrAdd(peerId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            foreach (var m in Db.LoadPendingOutgoing(peerId))
            {
                if (m.Kind == MessageKind.Nudge && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - m.Timestamp > 60_000)
                {
                    m.State = MessageState.Failed;
                    Db.UpdateMessage(m);
                    MessageUpdated?.Invoke(m);
                    continue;
                }
                if (!await DeliverAsync(m)) break;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Teslim döngüsü hatası", ex);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<bool> DeliverAsync(StoredMessage m)
    {
        try
        {
            var conn = await _pool.GetAsync(m.PeerId);
            var reply = await conn.RequestAsync(ToPacket(m), RequestTimeout);
            if (reply.T == PacketTypes.Ack)
            {
                m.State = MessageState.Delivered;
                Db.UpdateMessage(m);
                MessageUpdated?.Invoke(m);
                return true;
            }
            if (reply.T == PacketTypes.Error && reply.Err == "forbidden")
            {
                m.State = MessageState.Failed;
                Db.UpdateMessage(m);
                MessageUpdated?.Invoke(m);
                return true;
            }
            return false;
        }
        catch (PeerUnavailableException)
        {
            return false;
        }
        catch (Exception ex)
        {
            Log.Warn($"Mesaj teslim edilemedi ({m.PeerId[..8]}): {ex.GetType().Name}");
            _pool.Drop(m.PeerId);
            return false;
        }
    }

    private static Packet ToPacket(StoredMessage m) => m.Kind switch
    {
        MessageKind.Text => new Packet { T = PacketTypes.Message, Id = m.MsgId, Ts = m.Timestamp, Text = m.Data.Text },
        MessageKind.Nudge => new Packet { T = PacketTypes.Nudge, Id = m.MsgId, Ts = m.Timestamp },
        MessageKind.File => new Packet
        {
            T = PacketTypes.FileOffer, Id = m.MsgId, Ts = m.Timestamp,
            Name = m.Data.FileName, Size = m.Data.FileSize, Sha = m.Data.FileSha256,
        },
        MessageKind.Announcement => new Packet
        {
            T = PacketTypes.Bulletin, Id = m.MsgId, Ts = m.Timestamp, Text = m.Data.Text, Scope = m.Data.Scope,
        },
        _ => throw new InvalidOperationException("Gönderilemeyen mesaj türü."),
    };

    /// <summary>Tek seferlik kontrol mesajı (ör. dosya iptali); teslim edilemezse sessizce atlanır.</summary>
    internal async Task SendControlAsync(string peerId, Packet packet)
    {
        try
        {
            var conn = await _pool.GetAsync(peerId);
            await conn.RequestAsync(packet, RequestTimeout);
        }
        catch
        {
            // En iyi çaba.
        }
    }

    internal void RaiseUpdated(StoredMessage m) => MessageUpdated?.Invoke(m);

    // ------------------------------------------------------------------ Alma

    private bool AllowInbound(string peerId)
    {
        var now = DateTime.UtcNow;
        if (_inboundRate.Count > 10_000)
        {
            foreach (var kv in _inboundRate.Where(kv => now - kv.Value.window > InboundWindow).ToList())
                _inboundRate.TryRemove(kv);
        }
        var e = _inboundRate.AddOrUpdate(peerId, _ => (now, 1), (_, x) =>
            now - x.window > InboundWindow ? (now, 1) : (x.window, x.count + 1));
        return e.count <= MaxInboundPerWindow;
    }

    /// <summary>Sıkı mod kapalıysa herkes, açıksa yalnızca imzalı dizindeki kimlikler iletişim kurabilir.</summary>
    public bool IsPeerAllowed(string peerId) => !Directory.MembersOnly || Directory.GetMember(peerId) != null;

    /// <summary>Kişi listede gösterilmeli mi? Sıkı modda dizin dışı kimlikleri yalnızca Admin görür (kayıt için).</summary>
    public bool IsVisible(string peerId) =>
        IsPeerAllowed(peerId) || Directory.GetMember(Identity.PeerId)?.Role == Role.Admin;

    private async Task HandlePacketAsync(PeerConnection conn, Packet p)
    {
        var peerId = conn.RemotePeerId;
        bool isRequest = Packet.IsValidId(p.Id);
        if (!AllowInbound(peerId))
        {
            if (isRequest) await conn.SendAsync(new Packet { T = PacketTypes.Error, Re = p.Id, Err = "rate" });
            return;
        }
        // Dizin dışı kimlikler de imzalı dizini alabilmeli (yeni kurulan bilgisayarın yetkilerini öğrenmesi için).
        if (p.T == PacketTypes.DirectoryGet)
        {
            var signed = Directory.CurrentSigned;
            await conn.SendAsync(signed == null
                ? new Packet { T = PacketTypes.Error, Re = p.Id, Err = "none" }
                : new Packet { T = PacketTypes.Directory, Re = p.Id, Data = JsonSerializer.Serialize(signed) });
            return;
        }
        if (!isRequest) return;
        if (!IsPeerAllowed(peerId) || !EnsureKnown(conn))
        {
            await conn.SendAsync(new Packet { T = PacketTypes.Error, Re = p.Id, Err = "forbidden" });
            return;
        }

        switch (p.T)
        {
            case PacketTypes.Message:
            case PacketTypes.Nudge:
            case PacketTypes.FileOffer:
            case PacketTypes.Bulletin:
                var result = ReceiveContent(peerId, p);
                await conn.SendAsync(result == null
                    ? new Packet { T = PacketTypes.Ack, Re = p.Id }
                    : new Packet { T = PacketTypes.Error, Re = p.Id, Err = result });
                break;

            case PacketTypes.FileCancel:
                Files.OnRemoteCancel(peerId, p.Id!);
                await conn.SendAsync(new Packet { T = PacketTypes.Ack, Re = p.Id });
                break;

            case PacketTypes.AvatarGet:
                var bytes = _avatarBytes;
                await conn.SendAsync(bytes == null || p.Sha != _avatarHash
                    ? new Packet { T = PacketTypes.Error, Re = p.Id, Err = "none" }
                    : new Packet { T = PacketTypes.Avatar, Re = p.Id, Data = Convert.ToBase64String(bytes) });
                break;

            default:
                await conn.SendAsync(new Packet { T = PacketTypes.Error, Re = p.Id, Err = "unsupported" });
                break;
        }
    }

    /// <summary>İçerik mesajını doğrular ve saklar. Hata kodu veya başarıda null döner.</summary>
    private string? ReceiveContent(string peerId, Packet p)
    {
        var m = new StoredMessage
        {
            PeerId = peerId, MsgId = p.Id!, Direction = MessageDirection.Incoming,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), State = MessageState.Delivered,
        };

        switch (p.T)
        {
            case PacketTypes.Message:
                m.Kind = MessageKind.Text;
                m.Data.Text = TextLimits.Clean(p.Text, TextLimits.MessageMax, allowNewLines: true);
                if (m.Data.Text.Length == 0) return null;
                break;

            case PacketTypes.Nudge:
                m.Kind = MessageKind.Nudge;
                break;

            case PacketTypes.FileOffer:
                if (p.Size <= 0 || p.Size > FileTransferService.MaxFileSize) return "invalid";
                if (p.Sha is not { Length: 64 } || !p.Sha.All(char.IsAsciiHexDigit)) return "invalid";
                m.Kind = MessageKind.File;
                var name = FileTransferService.SanitizeFileName(p.Name);
                m.Data = new MessageData
                {
                    FileName = name, FileSize = p.Size, FileSha256 = p.Sha.ToLowerInvariant(),
                    IsImage = FileTransferService.IsImageName(name), FileState = FileState.Offered,
                };
                break;

            case PacketTypes.Bulletin:
                if (!IsBulletinAuthorized(peerId, p.Scope))
                {
                    Log.Warn($"Yetkisiz duyuru reddedildi ({peerId[..8]}, kapsam={p.Scope}).");
                    return "forbidden";
                }
                m.Kind = MessageKind.Announcement;
                m.Data.Text = TextLimits.Clean(p.Text, TextLimits.MessageMax, allowNewLines: true);
                m.Data.Scope = p.Scope;
                if (m.Data.Text.Length == 0) return null;
                break;
        }

        if (!Db.InsertMessage(m)) return null; // Yinelenen teslim: yalnızca yeniden onayla.

        MessageAdded?.Invoke(m);
        if (m.Kind == MessageKind.Nudge)
        {
            if (DateTime.UtcNow - _lastNudge.GetValueOrDefault("in:" + peerId) > TimeSpan.FromSeconds(5))
            {
                _lastNudge["in:" + peerId] = DateTime.UtcNow;
                NudgeReceived?.Invoke(peerId);
            }
        }
        else if (m.Kind == MessageKind.Announcement)
        {
            BulletinReceived?.Invoke(m);
        }
        else if (m.Kind == MessageKind.File && m.Data.IsImage && Settings.AutoAcceptImages &&
                 m.Data.FileSize <= Settings.AutoAcceptMaxMb * 1024L * 1024L &&
                 (Directory.Current == null || Directory.GetMember(peerId) != null) &&
                 Files.TryReserveAutoAccept(peerId, m.Data.FileSize))
        {
            // Organizasyon dizini varsa yalnızca doğrulanmış kişilerden gelen resimler, kota dahilinde otomatik indirilir.
            Files.Accept(m);
        }
        return null;
    }

    /// <summary>Duyuru yetkisi, gönderenin imzalı dizindeki rolüne göre alıcı tarafında denetlenir.</summary>
    public bool IsBulletinAuthorized(string senderId, string? scope)
    {
        var sender = Directory.GetMember(senderId);
        if (sender == null) return false;
        if (scope == ScopeAll) return sender.Role == Role.Admin;
        if (scope == ScopeUnit)
        {
            var me = Directory.GetMember(Identity.PeerId);
            return sender.Role is Role.Admin or Role.UnitManager && sender.UnitId != null && me?.UnitId == sender.UnitId;
        }
        return false;
    }

    /// <summary>
    /// Keşif duyurusu görülmeden doğrudan TCP ile bağlanan kimliği kişi listesine ekler.
    /// Kişi listesinin kontrolsüz büyümemesi için toplam üst sınır ve IP başına yeni kimlik hızı uygulanır.
    /// </summary>
    private bool EnsureKnown(PeerConnection conn)
    {
        var peerId = conn.RemotePeerId;
        if (Registry.Get(peerId) != null) return true;
        bool inDirectory = Directory.GetMember(peerId) != null;
        if (!inDirectory)
        {
            if (Registry.Count >= MaxKnownPeers) return false;
            var now = DateTime.UtcNow;
            if (_newIdentities.Count > 4096) _newIdentities.Clear();
            var e = _newIdentities.AddOrUpdate(conn.RemoteAddress, _ => (now, 1), (_, x) =>
                now - x.window > NewIdentityWindow ? (now, 1) : (x.window, x.count + 1));
            if (e.count > MaxNewIdentitiesPerAddress)
            {
                Log.Warn($"Çok sayıda yeni kimlik reddedildi ({conn.RemoteAddress}).");
                return false;
            }
        }
        var s = new PeerSnapshot
        {
            PeerId = peerId, PublicKey = conn.RemotePublicKey,
            Name = "Bilinmeyen (" + peerId[..6] + ")", Status = PresenceStatus.Offline,
        };
        if (Registry.Upsert(s) == null)
        {
            PersistContact(s);
            PeerChanged?.Invoke(s, false);
        }
        return true;
    }

    // ------------------------------------------------------------------ Dizin ve avatar senkronizasyonu

    private async Task FetchDirectoryAsync(string peerId)
    {
        if (DateTime.UtcNow - _lastDirFetch.GetValueOrDefault(peerId) < TimeSpan.FromSeconds(30)) return;
        if (Interlocked.CompareExchange(ref _dirFetching, 1, 0) != 0) return;
        _lastDirFetch[peerId] = DateTime.UtcNow;
        try
        {
            var conn = await _pool.GetAsync(peerId);
            var reply = await conn.RequestAsync(new Packet { T = PacketTypes.DirectoryGet }, RequestTimeout);
            if (reply.T != PacketTypes.Directory || string.IsNullOrEmpty(reply.Data)) return;
            var signed = JsonSerializer.Deserialize<SignedDirectory>(reply.Data);
            if (signed == null) return;
            if (Directory.TryApply(signed, out var err))
                Log.Info($"Dizin güncellendi: sürüm {Directory.Version}");
            else
                Log.Warn($"Dizin reddedildi ({peerId[..8]}): {err}");
        }
        catch (Exception ex)
        {
            Log.Warn("Dizin alınamadı: " + ex.Message);
        }
        finally
        {
            Volatile.Write(ref _dirFetching, 0);
        }
    }

    private async Task FetchAvatarAsync(string peerId, string hash)
    {
        if (!_avatarFetching.TryAdd(hash, 0)) return;
        try
        {
            var conn = await _pool.GetAsync(peerId);
            var reply = await conn.RequestAsync(new Packet { T = PacketTypes.AvatarGet, Sha = hash }, RequestTimeout);
            if (reply.T != PacketTypes.Avatar || string.IsNullOrEmpty(reply.Data) || reply.Data.Length > MaxAvatarBytes * 4 / 3 + 8) return;
            var bytes = Convert.FromBase64String(reply.Data);
            if (bytes.Length > MaxAvatarBytes) return;
            if (Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() != hash) return;
            if (!ImageSafety.IsSafeImage(bytes, maxSide: 1024)) return;
            LocalProtector.WriteAtomic(AvatarCachePath(hash), bytes);
            AvatarUpdated?.Invoke(peerId);
        }
        catch (Exception ex)
        {
            Log.Warn("Avatar alınamadı: " + ex.Message);
        }
        finally
        {
            _avatarFetching.TryRemove(hash, out _);
        }
    }

    public void Dispose()
    {
        try { _discovery.SendBye(); } catch { }
        _presenceTimer?.Dispose();
        _flushTimer?.Dispose();
        _discovery.Dispose();
        _server.Dispose();
        _pool.Dispose();
        Network.Dispose();
    }
}
