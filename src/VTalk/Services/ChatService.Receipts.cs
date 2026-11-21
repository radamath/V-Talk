using System.IO;
using System.Text.Json;
using VTalk.Core;
using VTalk.Net;

namespace VTalk.Services;

/// <summary>
/// Teslim ve okundu bilgisi. Teslim: 1:1'de karşı tarafın onayı, grupta her üyenin onayı. Okundu: alıcının o sohbetin
/// penceresi açık ve etkinken gelen mesajlar için gönderdiği "read" paketi. Eski sürümler "read" göndermez ve
/// bilinmeyen paketi "unsupported" ile yanıtlar; bu durumda yalnızca teslim bilgisi görünür.
/// </summary>
public sealed partial class ChatService
{
    private const int MaxReceiptIdsPerPacket = 100;
    private static readonly TimeSpan ReceiptSendTimeout = TimeSpan.FromSeconds(10);
    private readonly SemaphoreSlim _receiptSendGate = new(1, 1);

    /// <summary>Giden mesajın balondaki tik durumu (alıcı sayısı ve okuyan sayısıyla).</summary>
    public ReceiptStatus GetReceiptStatus(StoredMessage m)
    {
        if (m.Direction != MessageDirection.Outgoing || m.Kind is not (MessageKind.Text or MessageKind.File))
            return ReceiptStatus.None;

        if (ChatGroup.TryParseGroupId(m.PeerId) is { } groupId)
        {
            var group = Db.GetChatGroup(groupId);
            if (group == null) return ReceiptStatus.None;
            var recipients = group.MemberIds.Where(id => !string.Equals(id, Identity.PeerId, StringComparison.OrdinalIgnoreCase)).ToList();
            if (recipients.Count == 0) return ReceiptStatus.None;
            var receipts = Db.GetReceipts(m.PeerId, m.MsgId);
            int delivered = recipients.Count(r => receipts.TryGetValue(r, out var s) && s.Delivered);
            int read = recipients.Count(r => receipts.TryGetValue(r, out var s) && s.Read);
            return new ReceiptStatus(Combine(delivered, read, recipients.Count), read, recipients.Count);
        }

        var own = Db.GetReceipts(m.PeerId, m.MsgId);
        bool isRead = own.TryGetValue(m.PeerId, out var mine) && mine.Read;
        bool isDelivered = isRead || m.State == MessageState.Delivered;
        return new ReceiptStatus(isRead ? ReceiptState.Read : isDelivered ? ReceiptState.Delivered : ReceiptState.None, isRead ? 1 : 0, 1);
    }

    private static ReceiptState Combine(int delivered, int read, int total) =>
        read == total ? ReceiptState.Read : delivered == total ? ReceiptState.Delivered : ReceiptState.None;

    private void RecordGroupDelivered(ChatGroup group, StoredMessage m, string peerId)
    {
        Db.MarkReceipt(group.ConversationId, m.MsgId, peerId, read: false);
        MessageUpdated?.Invoke(m);
    }

    /// <summary>Sohbet penceresi etkinken çağrılır: henüz bildirilmemiş gelen mesajlar için gönderenlere "okundu" yollar.</summary>
    public void SendReadReceipts(string conversationId) => _ = Task.Run(() => SendReadReceiptsAsync(conversationId));

    private async Task SendReadReceiptsAsync(string conversationId)
    {
        // Pencere etkinleşmesi ve yeni mesaj aynı anda tetikleyebilir; aynı mesajlar iki kez yollanmasın.
        await _receiptSendGate.WaitAsync();
        try
        {
            var after = Db.GetReceiptsSentRowId(conversationId);
            var unread = Db.LoadIncomingAfter(conversationId, after, MaxReceiptIdsPerPacket);
            if (unread.Count == 0) return;

            var groupId = ChatGroup.TryParseGroupId(conversationId);
            var bySender = unread
                .GroupBy(m => groupId == null ? m.PeerId : m.Data.SenderPeerId ?? "")
                .Where(g => g.Key.Length > 0 && IsPeerAllowed(g.Key));
            var allSent = true;
            foreach (var sender in bySender)
            {
                if (!await TrySendReadAsync(sender.Key, groupId, sender.Select(m => m.MsgId).ToList()))
                    allSent = false;
            }
            // Ulaşılamayan gönderen olursa işaret ilerlemez: bir sonraki etkinleşmede yeniden denenir (paket tekrarlanabilir).
            if (allSent) Db.SetReceiptsSentRowId(conversationId, unread[^1].RowId);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            Log.Warn("Okundu bilgisi gönderilemedi: " + ex.Message);
        }
        finally
        {
            _receiptSendGate.Release();
        }
    }

    private async Task<bool> TrySendReadAsync(string peerId, string? groupId, List<string> msgIds)
    {
        try
        {
            var conn = await _pool.GetAsync(peerId);
            var reply = await conn.RequestAsync(new Packet
            {
                T = PacketTypes.Read,
                Grp = groupId,
                Data = JsonSerializer.Serialize(msgIds),
            }, ReceiptSendTimeout);
            // Eski sürüm "read" bilmez: tekrar denemenin anlamı yok, gönderilmiş say.
            return reply.T == PacketTypes.Ack || reply.Err is "unsupported" or "forbidden";
        }
        catch (Exception ex) when (ex is PeerUnavailableException or IOException or OperationCanceledException or TimeoutException
                                       or ObjectDisposedException or System.Net.Sockets.SocketException or ProtocolException)
        {
            return false;
        }
    }

    /// <summary>Karşı tarafın "okundu" bildirimi: yalnızca o kişiye/gruba giden kendi mesajlarımız işaretlenir.</summary>
    private string? ReceiveReadReceipt(string fromPeerId, Packet p)
    {
        List<string>? ids;
        try { ids = JsonSerializer.Deserialize<List<string>>(p.Data ?? "[]"); }
        catch (JsonException) { return "invalid"; }
        if (ids == null || ids.Count > MaxReceiptIdsPerPacket) return "invalid";

        string conversationId;
        if (string.IsNullOrEmpty(p.Grp))
        {
            conversationId = fromPeerId;
        }
        else
        {
            if (!ChatGroup.IsValidGroupId(p.Grp)) return "invalid";
            var group = Db.GetChatGroup(p.Grp);
            if (group == null || !group.MemberIds.Contains(fromPeerId, StringComparer.OrdinalIgnoreCase)) return "forbidden";
            conversationId = group.ConversationId;
        }

        foreach (var msgId in ids.Where(Packet.IsValidId).Distinct(StringComparer.Ordinal))
        {
            var m = Db.GetMessage(conversationId, msgId, MessageDirection.Outgoing);
            if (m == null) continue; // bize ait olmayan ya da silinmiş mesaj
            Db.MarkReceipt(conversationId, msgId, fromPeerId, read: true);
            MessageUpdated?.Invoke(m);
        }
        return null;
    }
}
