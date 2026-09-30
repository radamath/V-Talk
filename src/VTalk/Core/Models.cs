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
