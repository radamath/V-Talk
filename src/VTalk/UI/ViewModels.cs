using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VTalk.Core;
using VTalk.Net;
using VTalk.Security;
using VTalk.Services;

namespace VTalk.UI;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public static class ImageLoader
{
    /// <summary>Güvenilmeyen resmi, izinli formatta ve boyut sınırları içindeyse küçültülmüş olarak çözer; aksi halde null döner.</summary>
    public static ImageSource? Load(string? path, int decodeWidth)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path) || !ImageSafety.IsSafeImage(path)) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.IgnoreImageCache;
            bmp.DecodePixelWidth = decodeWidth;
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }
}

public sealed class ContactViewModel : ObservableObject
{
    public const string UnverifiedGroup = "Doğrulanmamış kişiler";
    public const string NoUnitGroup = "Birimsiz";
    public const string GeneralGroup = "Genel";
    public const string GroupsSection = "Gruplar";
    public const string TemporaryGroupsSection = "Geçici sohbetler";

    private string _name = "";
    private string _note = "";
    private PresenceStatus _status = PresenceStatus.Offline;
    private Role _role;
    private string? _unitName;
    private string _groupName = GeneralGroup;
    private int _groupOrder;
    private bool _isVerified;
    private ImageSource? _avatar;
    private string? _avatarHash;
    private int _unread;
    private string _machine = "";
    private string _user = "";
    private bool _showIdentity;
    private bool _isGroupChat;

    public ContactViewModel(string peerId) => PeerId = peerId;

    public string PeerId { get; }
    public bool IsGroupChat => _isGroupChat;
    public string Name { get => _name; private set => Set(ref _name, value); }
    public string Note { get => _note; private set { if (Set(ref _note, value)) Raise(nameof(HasNote)); } }
    public bool HasNote => !string.IsNullOrWhiteSpace(Note);
    public PresenceStatus Status
    {
        get => _status;
        private set
        {
            if (!Set(ref _status, value)) return;
            Raise(nameof(IsOnline));
            Raise(nameof(StatusText));
            Raise(nameof(OnlineSort));
            Raise(nameof(Tooltip));
        }
    }
    public bool IsOnline => Status != PresenceStatus.Offline;
    public int OnlineSort => IsOnline ? 0 : 1;
    public string StatusText => StatusNames.ToDisplay(Status);
    public Role Role { get => _role; private set { if (Set(ref _role, value)) { Raise(nameof(RoleText)); Raise(nameof(HasRoleBadge)); } } }
    public string RoleText => RoleNames.ToDisplay(Role);
    public bool HasRoleBadge => !_isGroupChat && Role != Role.User;
    public string? UnitName { get => _unitName; private set => Set(ref _unitName, value); }
    public string GroupName { get => _groupName; private set => Set(ref _groupName, value); }
    public int GroupOrder { get => _groupOrder; private set => Set(ref _groupOrder, value); }
    public bool IsVerified { get => _isVerified; private set => Set(ref _isVerified, value); }
    public ImageSource? Avatar { get => _avatar; private set => Set(ref _avatar, value); }
    public int Unread { get => _unread; set { if (Set(ref _unread, value)) Raise(nameof(HasUnread)); } }
    public bool HasUnread => Unread > 0;
    public string Machine { get => _machine; private set => Set(ref _machine, value); }
    public string User { get => _user; private set => Set(ref _user, value); }
    public string ShortId => PeerId.Length >= 8 ? PeerId[..8] : PeerId;
    public string Tooltip => _isGroupChat
        ? $"{Name}\nGrup sohbeti\n{Note}"
        : $"{Name}\nKullanıcı: {User}\nBilgisayar: {Machine}\nDurum: {StatusText}" +
          (_showIdentity ? $"\nKimlik: {ShortId}" : "") +
          (IsVerified ? "" : "\n⚠ Admin dizininde doğrulanmamış");

    public void ApplyChatGroup(ChatGroup group, PresenceStatus status)
    {
        _isGroupChat = true;
        _showIdentity = false;
        Name = group.Name;
        Note = group.Temporary
            ? $"Geçici · {Math.Max(0, group.MemberIds.Count - 1)} kişi"
            : $"{group.MemberIds.Count} üye";
        Status = status;
        Machine = "";
        User = "";
        Role = Role.User;
        UnitName = null;
        IsVerified = true;
        GroupName = group.Temporary ? TemporaryGroupsSection : GroupsSection;
        GroupOrder = group.Temporary ? -2 : -1;
        Avatar = null;
        _avatarHash = null;
        Raise(nameof(HasRoleBadge));
        Raise(nameof(Tooltip));
    }

    public void Update(PeerSnapshot s, DirectoryService dir, bool showIdentity)
    {
        _isGroupChat = false;
        _showIdentity = showIdentity;
        var member = dir.GetMember(PeerId);
        Name = member is { Name.Length: > 0 } ? member.Name
            : s.Name.Length > 0 ? s.Name
            : showIdentity ? ShortId
            : s.User.Length > 0 ? s.User
            : s.Machine.Length > 0 ? s.Machine
            : "Adsız kişi";
        Note = s.Note;
        Status = s.IsOnline ? s.Status : PresenceStatus.Offline;
        Machine = s.Machine;
        User = s.User;
        Role = member?.Role ?? Role.User;
        UnitName = dir.GetUnitName(member?.UnitId);
        IsVerified = member != null;

        if (dir.Current == null)
        {
            GroupName = GeneralGroup;
            GroupOrder = 0;
        }
        else if (member == null)
        {
            GroupName = UnverifiedGroup;
            GroupOrder = 2;
        }
        else
        {
            GroupName = UnitName ?? NoUnitGroup;
            GroupOrder = UnitName == null ? 1 : 0;
        }

        if (s.AvatarHash != _avatarHash || (Avatar == null && s.AvatarHash != null))
        {
            _avatarHash = s.AvatarHash;
            Avatar = s.AvatarHash == null ? null : ImageLoader.Load(ChatService.AvatarCachePath(s.AvatarHash), 96);
        }
        Raise(nameof(HasRoleBadge));
        Raise(nameof(Tooltip));
    }
}

public sealed class MessageViewModel : ObservableObject
{
    private StoredMessage _model;
    private readonly List<StoredMessage> _parts = [];
    private double _progress;
    private string _progressText = "";
    private ImageSource? _image;
    private bool _imageLoaded;

    public MessageViewModel(StoredMessage model, string senderName)
    {
        _model = model;
        _parts.Add(model);
        SenderName = senderName;
    }

    public StoredMessage Model => _model;
    public long RowId => _parts[0].RowId;
    public long MaxRowId => _parts.Max(p => p.RowId);
    public string MsgId => _model.MsgId;
    public bool IsOutgoing => _model.Direction == MessageDirection.Outgoing;
    public MessageKind Kind => _parts[0].Kind;
    public string SenderName { get; set; }
    public bool ShowHeader { get; set; } = true;
    public string TimeText => RemoteTimestamp.ToLocalTime(_parts[0].Timestamp).ToString("dd.MM.yyyy HH:mm");
    public string ShortTime => RemoteTimestamp.ToLocalTime(_parts[0].Timestamp).ToString("HH:mm");
    /// <summary>Küme birleştirmede son parçanın zamanı (sonraki mesajla komşuluk).</summary>
    public DateTime LocalTime => RemoteTimestamp.ToLocalTime(_parts[^1].Timestamp);
    /// <summary>Ardışık Enter gönderimleri tek balonda; seçim için \n ile birleşik.</summary>
    public string Text => string.Join("\n", _parts.Select(p => p.Data.Text ?? ""));

    public bool ContainsRow(long rowId) => _parts.Any(p => p.RowId == rowId);

    public void MergeAppend(StoredMessage m)
    {
        _parts.Add(m);
        _model = m;
        Raise(nameof(Text));
        Raise(nameof(DeliveryText));
        Raise(nameof(HasDeliveryText));
        Raise(nameof(LocalTime));
    }

    public void MergePrepend(StoredMessage m)
    {
        _parts.Insert(0, m);
        Raise(nameof(Text));
        Raise(nameof(TimeText));
        Raise(nameof(ShortTime));
        Raise(nameof(RowId));
        Raise(nameof(DeliveryText));
        Raise(nameof(HasDeliveryText));
    }

    public string DeliveryText
    {
        get
        {
            if (!IsOutgoing) return "";
            if (_parts.Any(p => p.State == MessageState.Failed)) return "⚠ İletilemedi";
            if (_parts.Any(p => p.State == MessageState.Pending)) return "⏳ Bekliyor (kişi çevrimdışı olabilir)";
            return "";
        }
    }
    public bool HasDeliveryText => DeliveryText.Length > 0;

    // ---- Yanıt alıntısı (balonun ilk mesajı; yanıt içeren mesaj başka balonla birleşmez) ----
    public string ReplySenderName { get; set; } = "";
    public bool HasReply => !string.IsNullOrEmpty(_parts[0].Data.ReplyToMsgId);
    public string? ReplyToMsgId => _parts[0].Data.ReplyToMsgId;
    public string ReplyQuote => _parts[0].Data.ReplyToText ?? "";
    public bool ContainsMsgId(string msgId) => _parts.Any(p => p.MsgId == msgId);

    // ---- Teslim / okundu tiki (yalnızca giden metin) ----
    private ReceiptStatus _receipt = ReceiptStatus.None;
    public IReadOnlyList<StoredMessage> Parts => _parts;
    public bool ShowReceipt => _receipt.State != ReceiptState.None;
    public bool IsReceiptRead => _receipt.State == ReceiptState.Read;
    public string ReceiptToolTip => _receipt.State switch
    {
        ReceiptState.Read => _receipt.Recipients > 1 ? "Herkes okudu" : "Okundu",
        ReceiptState.Delivered => _receipt.Recipients > 1
            ? $"Herkese teslim edildi • Okuyan {_receipt.ReadCount}/{_receipt.Recipients}"
            : "Teslim edildi",
        _ => "",
    };

    public void SetReceipt(ReceiptStatus status)
    {
        if (status == _receipt) return;
        _receipt = status;
        Raise(nameof(ShowReceipt));
        Raise(nameof(IsReceiptRead));
        Raise(nameof(ReceiptToolTip));
    }

    public string BulletinTitle => _model.Data.Scope == ChatService.ScopeAll ? "📢 Genel Duyuru" : "📢 Birim Duyurusu";

    // Sohbet penceresinde seçilebilir metin kutularının tek satırlık içerikleri (TextBlock Run'ları seçilemez).
    public string BulletinHeaderText => $"{BulletinTitle} — {SenderName}";
    public string NudgeText => $"{SenderName} bir titreşim gönderdi. ({ShortTime})";
    public string FileInfoText => $"{FileSizeText} • {FileStateText}";

    // ---- Dosya ----
    public string FileName => _model.Data.FileName ?? "";
    public string FileSizeText => FormatSize(_model.Data.FileSize);
    public FileState FileState => _model.Data.FileState;
    public bool IsImage => _model.Data.IsImage && Kind == MessageKind.File;
    public string? FilePath => _model.Data.FilePath;
    public bool FileAvailable => FilePath != null && File.Exists(FilePath) &&
                                 (IsOutgoing || FileState == FileState.Completed);
    public bool ShowImage => IsImage && FileAvailable && Image != null;

    public ImageSource? Image
    {
        get
        {
            if (!_imageLoaded && IsImage && FileAvailable)
            {
                _imageLoaded = true;
                _image = ImageLoader.Load(FilePath, 480);
            }
            return _image;
        }
    }

    public string FileStateText => FileState switch
    {
        FileState.Offered => IsOutgoing ? "Karşı tarafın kabul etmesi bekleniyor" : "Dosya gönderildi — kabul etmek ister misiniz?",
        FileState.Transferring => "Aktarılıyor…",
        FileState.Completed => IsOutgoing ? "Gönderildi" : "Alındı",
        FileState.Rejected => IsOutgoing ? "Karşı taraf reddetti" : "Reddedildi",
        FileState.Cancelled => "İptal edildi",
        _ => "Aktarım başarısız",
    };

    public bool CanAccept => !IsOutgoing && FileState is FileState.Offered or FileState.Failed;
    public bool CanReject => !IsOutgoing && FileState == FileState.Offered;
    public bool CanCancel => FileState == FileState.Transferring || (IsOutgoing && FileState == FileState.Offered);
    public bool IsTransferring => FileState == FileState.Transferring;
    public bool CanOpen => FileAvailable;

    public double Progress { get => _progress; private set => Set(ref _progress, value); }
    public string ProgressText { get => _progressText; private set => Set(ref _progressText, value); }

    public void SetProgress(long done, long total)
    {
        Progress = total <= 0 ? 0 : done * 100.0 / total;
        ProgressText = $"{FormatSize(done)} / {FormatSize(total)}";
    }

    public void Update(StoredMessage m)
    {
        var idx = _parts.FindIndex(p => p.RowId == m.RowId);
        if (idx >= 0) _parts[idx] = m;
        else _parts.Add(m);
        _model = _parts[^1];
        _imageLoaded = false;
        _image = null;
        foreach (var name in new[]
                 {
                     nameof(Text), nameof(DeliveryText), nameof(HasDeliveryText), nameof(FileState), nameof(FileStateText),
                     nameof(FileInfoText),
                     nameof(CanAccept), nameof(CanReject), nameof(CanCancel), nameof(CanOpen), nameof(IsTransferring),
                     nameof(FileAvailable), nameof(Image), nameof(ShowImage), nameof(IsImage), nameof(FilePath),
                 })
            Raise(name);
    }

    public static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1)
        {
            v /= 1024;
            u++;
        }
        return u == 0 ? $"{bytes} B" : $"{v:0.#} {units[u]}";
    }
}
