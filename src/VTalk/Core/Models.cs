using System.Text.Json.Serialization;

namespace VTalk.Core;

public enum PresenceStatus
{
    Online = 0,
    Busy = 1,
    Away = 2,
    Offline = 3,
}

public enum Role
{
    User = 0,
    UnitManager = 1,
    Admin = 2,
}

public enum MessageKind
{
    Text = 0,
    File = 1,
    Nudge = 2,
    Announcement = 3,
    System = 4,
}

public enum MessageDirection
{
    Incoming = 0,
    Outgoing = 1,
}

public enum MessageState
{
    Pending = 0,     // Giden: karşı taraf çevrimdışı, kuyrukta
    Delivered = 1,   // Giden: karşı taraf aldı / Gelen: alındı
    Failed = 2,
}

public enum FileState
{
    Offered = 0,
    Transferring = 1,
    Completed = 2,
    Rejected = 3,
    Failed = 4,
    Cancelled = 5,
}

/// <summary>Veritabanında şifreli JSON olarak saklanan mesaj içeriği.</summary>
public sealed class MessageData
{
    public string? Text { get; set; }
    public string? FileName { get; set; }
    public long FileSize { get; set; }
    public string? FilePath { get; set; }
    public string? FileSha256 { get; set; }
    public bool IsImage { get; set; }
    public FileState FileState { get; set; }
    public string? Scope { get; set; }
    /// <summary>Grup sohbetinde mesajı yazan kişinin kimliği (konuşma kimliği g:… iken).</summary>
    public string? SenderPeerId { get; set; }
    /// <summary>Grupta @etiketlenen üye kimlikleri; bildirim yalnızca bunlara gider.</summary>
    [JsonPropertyName("mentions")]
    public List<string>? Mentions { get; set; }
    /// <summary>Yanıtlanan mesaj (kimlik, yazanı, kısa alıntı). Alıntı gönderenin beyanıdır; yalnızca gösterilir.</summary>
    [JsonPropertyName("replyTo")] public string? ReplyToMsgId { get; set; }
    [JsonPropertyName("replySender")] public string? ReplyToSender { get; set; }
    [JsonPropertyName("replyText")] public string? ReplyToText { get; set; }
}

/// <summary>Yanıtlanan mesaja başvuru.</summary>
public sealed record ReplyRef(string MsgId, string SenderPeerId, string Text)
{
    public const int MaxQuoteLength = 140;

    /// <summary>Ağdan gelen yanıt alanlarını doğrular; geçersizse yanıt yok sayılır (mesaj yine kabul edilir).</summary>
    public static ReplyRef? TryCreate(string? msgId, string? sender, string? text)
    {
        if (!Net.Packet.IsValidId(msgId) || !VTalk.Security.OrgDirectory.IsValidPeerId(sender)) return null;
        var quote = TextLimits.Clean(text, MaxQuoteLength);
        return new ReplyRef(msgId!, sender!.ToLowerInvariant(), quote);
    }

    public void ApplyTo(MessageData data)
    {
        data.ReplyToMsgId = MsgId;
        data.ReplyToSender = SenderPeerId;
        data.ReplyToText = Text;
    }
}

public sealed class StoredMessage
{
    public long RowId { get; set; }
    public string PeerId { get; set; } = "";
    public string MsgId { get; set; } = "";
    public MessageDirection Direction { get; set; }
    public MessageKind Kind { get; set; }
    public long Timestamp { get; set; }
    public MessageState State { get; set; }
    public MessageData Data { get; set; } = new();
}

public static class RoleNames
{
    public static string ToDisplay(Role role) => role switch
    {
        Role.Admin => "Admin",
        Role.UnitManager => "Birim Yöneticisi",
        _ => "Kullanıcı",
    };
}

public static class StatusNames
{
    public static string ToDisplay(PresenceStatus s) => s switch
    {
        PresenceStatus.Online => "Çevrimiçi",
        PresenceStatus.Busy => "Meşgul",
        PresenceStatus.Away => "Dışarıda",
        _ => "Çevrimdışı",
    };
}
