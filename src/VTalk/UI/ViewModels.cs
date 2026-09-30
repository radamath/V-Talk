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

    public ContactViewModel(string peerId) => PeerId = peerId;

    public string PeerId { get; }
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
    public bool HasRoleBadge => Role != Role.User;
    public string? UnitName { get => _unitName; private set => Set(ref _unitName, value); }
    public string GroupName { get => _groupName; private set => Set(ref _groupName, value); }
    public int GroupOrder { get => _groupOrder; private set => Set(ref _groupOrder, value); }
    public bool IsVerified { get => _isVerified; private set => Set(ref _isVerified, value); }
    public ImageSource? Avatar { get => _avatar; private set => Set(ref _avatar, value); }
    public int Unread { get => _unread; set { if (Set(ref _unread, value)) Raise(nameof(HasUnread)); } }
    public bool HasUnread => Unread > 0;
    public string Machine { get => _machine; private set => Set(ref _machine, value); }
    public string User { get => _user; private set => Set(ref _user, value); }
    public string ShortId => PeerId[..8];
    public string Tooltip => $"{Name}\nKullanıcı: {User}\nBilgisayar: {Machine}\nDurum: {StatusText}\nKimlik: {ShortId}" +
                             (IsVerified ? "" : "\n⚠ Admin dizininde doğrulanmamış");

    public void Update(PeerSnapshot s, DirectoryService dir)
    {
        var member = dir.GetMember(PeerId);
        Name = member is { Name.Length: > 0 } ? member.Name : (s.Name.Length > 0 ? s.Name : ShortId);
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
        Raise(nameof(Tooltip));
    }
}

public sealed class MessageViewModel : ObservableObject
{
    private StoredMessage _model;
    private double _progress;
    private string _progressText = "";
    private ImageSource? _image;
    private bool _imageLoaded;

    public MessageViewModel(StoredMessage model, string senderName)
    {
        _model = model;
        SenderName = senderName;
    }

    public StoredMessage Model => _model;
    public long RowId => _model.RowId;
    public string MsgId => _model.MsgId;
    public bool IsOutgoing => _model.Direction == MessageDirection.Outgoing;
    public MessageKind Kind => _model.Kind;
    public string SenderName { get; set; }
    public bool ShowHeader { get; set; } = true;
    public string TimeText => DateTimeOffset.FromUnixTimeMilliseconds(_model.Timestamp).LocalDateTime.ToString("dd.MM.yyyy HH:mm");
    public string ShortTime => DateTimeOffset.FromUnixTimeMilliseconds(_model.Timestamp).LocalDateTime.ToString("HH:mm");
    public DateTime LocalTime => DateTimeOffset.FromUnixTimeMilliseconds(_model.Timestamp).LocalDateTime;
    public string Text => _model.Data.Text ?? "";

    public string DeliveryText => !IsOutgoing ? "" : _model.State switch
    {
        MessageState.Pending => "⏳ Bekliyor (kişi çevrimdışı olabilir)",
        MessageState.Failed => "⚠ İletilemedi",
        _ => "",
    };
    public bool HasDeliveryText => DeliveryText.Length > 0;

    public string BulletinTitle => _model.Data.Scope == ChatService.ScopeAll ? "📢 Genel Duyuru" : "📢 Birim Duyurusu";

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
        _model = m;
        _imageLoaded = false;
        _image = null;
        foreach (var name in new[]
                 {
                     nameof(Text), nameof(DeliveryText), nameof(HasDeliveryText), nameof(FileState), nameof(FileStateText),
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
