using System.Collections.Concurrent;
using System.IO;
using VTalk.Core;
using VTalk.Net;

namespace VTalk.Services;

/// <summary>
/// "Yazıyor…" göstergesi. Yazan taraf en fazla <see cref="TypingSendInterval"/> aralıkla kısa bir paket yollar; alan taraf
/// göstergeyi birkaç saniye tutar. Yalnızca çevrimiçi kişilere gider ve kuyruğa alınmaz (kaybolması önemsizdir).
/// Eski sürümler "unsupported" döner; o kişilere oturum boyunca bir daha gönderilmez.
/// </summary>
public sealed partial class ChatService
{
    public static readonly TimeSpan TypingSendInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan TypingRequestTimeout = TimeSpan.FromSeconds(5);
    private readonly ConcurrentDictionary<string, DateTime> _lastTypingSent = new();
    private readonly ConcurrentDictionary<string, byte> _typingUnsupported = new();

    /// <summary>conversationId (kişi veya g:grup), yazan kişinin kimliği.</summary>
    public event Action<string, string>? TypingReceived;

    /// <summary>Yazma kutusunda metin değiştikçe çağrılır; kendi içinde seyreltir.</summary>
    public void NotifyTyping(string conversationId)
    {
        var now = DateTime.UtcNow;
        if (_lastTypingSent.TryGetValue(conversationId, out var last) && now - last < TypingSendInterval) return;
        _lastTypingSent[conversationId] = now;

        var groupId = ChatGroup.TryParseGroupId(conversationId);
        IEnumerable<string> targets;
        if (groupId != null)
        {
            var group = Db.GetChatGroup(groupId);
            if (group == null || !group.MemberIds.Contains(Identity.PeerId, StringComparer.OrdinalIgnoreCase)) return;
            targets = group.MemberIds.Where(id => !string.Equals(id, Identity.PeerId, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            targets = [conversationId];
        }

        foreach (var peerId in targets.Where(id => IsPeerAllowed(id) && !_typingUnsupported.ContainsKey(id) &&
                                                   Registry.Get(id) is { IsOnline: true }))
        {
            var target = peerId;
            _ = Task.Run(() => SendTypingAsync(target, groupId));
        }
    }

    private async Task SendTypingAsync(string peerId, string? groupId)
    {
        try
        {
            var conn = await _pool.GetAsync(peerId);
            var reply = await conn.RequestAsync(new Packet { T = PacketTypes.Typing, Grp = groupId }, TypingRequestTimeout);
            if (reply.Err == "unsupported") _typingUnsupported[peerId] = 0;
        }
        catch (Exception ex) when (ex is PeerUnavailableException or IOException or OperationCanceledException or TimeoutException
                                       or ObjectDisposedException or System.Net.Sockets.SocketException or ProtocolException)
        {
            // Gösterge en iyi çabadır: ulaşılamayan kişiye gitmemesi sorun değil, kuyruk ve uyarı gerekmez.
        }
    }

    private string? ReceiveTyping(string fromPeerId, Packet p)
    {
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
        if (IsVisible(fromPeerId)) TypingReceived?.Invoke(conversationId, fromPeerId);
        return null;
    }
}
