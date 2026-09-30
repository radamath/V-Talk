using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Threading;
using VTalk.Core;
using VTalk.Net;
using VTalk.Security;
using VTalk.Services;

namespace VTalk.UI;

/// <summary>Servis olaylarını arayüze bağlar: kişi listesi, sohbet pencereleri, bildirimler, boşta algılama.</summary>
public sealed class AppController : IDisposable
{
    public static AppController Current { get; private set; } = null!;

    public ChatService Chat { get; }
    public SoundService Sounds { get; }
    public TrayService Tray { get; }
    public ObservableCollection<ContactViewModel> Contacts { get; } = [];

    private readonly Dictionary<string, ContactViewModel> _contacts = new();
    private readonly Dictionary<string, ChatWindow> _chatWindows = new();
    private readonly Dispatcher _ui = Application.Current.Dispatcher;
    private readonly DispatcherTimer _idleTimer;
    private readonly DateTime _startedAt = DateTime.UtcNow;
    private MainWindow? _main;
    private bool _exiting;
    private ECDsa? _adminKey;
    private DateTime _adminKeyExpiresUtc;
    private static readonly TimeSpan AdminSessionTimeout = TimeSpan.FromMinutes(10);

    public event Action? ProfileChanged;
    public event Action? DirectoryChanged;
    public event Action? NetworkChanged;

    public AppController(ChatService chat)
    {
        Current = this;
        Chat = chat;
        Sounds = new SoundService(chat.Settings);
        Tray = new TrayService();
        Tray.OpenRequested += ShowMain;
        Tray.ExitRequested += Exit;
        Tray.StatusRequested += s => SetStatus(s);

        _idleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _idleTimer.Tick += (_, _) =>
        {
            var minutes = Chat.Settings.AutoAwayMinutes;
            Chat.SetAutoAway(minutes > 0 && NativeMethods.IdleTime() > TimeSpan.FromMinutes(minutes));
            if (_adminKey != null && DateTime.UtcNow >= _adminKeyExpiresUtc) EndAdminSession();
            ProfileChanged?.Invoke();
        };
    }

    public void Start(bool startMinimized)
    {
        Chat.PeerChanged += (s, cameOnline) => _ui.BeginInvoke(() => OnPeerChanged(s, cameOnline));
        Chat.MessageAdded += m => _ui.BeginInvoke(() => OnMessageAdded(m));
        Chat.MessageUpdated += m => _ui.BeginInvoke(() => OnMessageUpdated(m));
        Chat.NudgeReceived += id => _ui.BeginInvoke(() => OnNudge(id));
        Chat.BulletinReceived += m => _ui.BeginInvoke(() => OnBulletin(m));
        Chat.AvatarUpdated += id => _ui.BeginInvoke(() => RefreshContact(id));
        Chat.DirectoryChanged += () => _ui.BeginInvoke(OnDirectoryChanged);
        Chat.Network.Changed += () => _ui.BeginInvoke(() => NetworkChanged?.Invoke());
        Chat.Files.Progress += (peer, msg, done, total) => _ui.BeginInvoke(() =>
        {
            if (_chatWindows.TryGetValue(peer, out var w)) w.UpdateProgress(msg, done, total);
        });

        Chat.Start();
        foreach (var s in Chat.Registry.All) OnPeerChanged(s, false);

        _main = new MainWindow(this);
        if (!startMinimized) _main.Show();
        _idleTimer.Start();
        UpdateTrayTooltip();

        if (Chat.Directory.IsDeployedTrustRoot == false && Chat.Directory.PinnedOrgKey == null)
            Log.Info("Henüz organizasyon dizini yok. Admin ilk girişte organizasyonu oluşturabilir.");
    }

    // ------------------------------------------------------------------ Kişiler

    public ContactViewModel? GetContact(string peerId) => _contacts.GetValueOrDefault(peerId);

    /// <summary>
    /// Kişinin görünen adı. Organizasyon dizini varken dizinde olmayan kişilerin adı kendi beyanıdır;
    /// kimlik taklidine (ör. "BT Destek" adıyla parola isteme) karşı her yerde açıkça işaretlenir.
    /// </summary>
    public string DisplayName(string peerId)
    {
        if (peerId == Chat.Identity.PeerId) return Chat.Settings.DisplayName;
        var name = GetContact(peerId)?.Name ?? Chat.Registry.Get(peerId)?.Name ?? peerId[..8];
        return Chat.Directory.Current != null && Chat.Directory.GetMember(peerId) == null
            ? $"{name} (doğrulanmamış)"
            : name;
    }

    private void OnPeerChanged(PeerSnapshot s, bool cameOnline)
    {
        if (s.PeerId == Chat.Identity.PeerId) return;
        var vm = SyncContact(Chat.Registry.Get(s.PeerId) ?? s);
        if (vm == null) return;

        if (cameOnline && DateTime.UtcNow - _startedAt > TimeSpan.FromSeconds(15) && Chat.Settings.NotifyContactOnline &&
            Chat.EffectiveStatus != PresenceStatus.Busy)
        {
            Sounds.Play(SoundKind.Online);
            if (Chat.Settings.ShowNotifications)
                Tray.Notify("V-Talk", $"{DisplayName(s.PeerId)} çevrimiçi oldu.", () => OpenChat(s.PeerId));
        }
        UpdateTrayTooltip();
        _main?.UpdateCounts();
    }

    /// <summary>Kişiyi listede günceller; sıkı modda görmememiz gereken kimlikleri listeden kaldırır.</summary>
    private ContactViewModel? SyncContact(PeerSnapshot s)
    {
        if (!Chat.IsVisible(s.PeerId))
        {
            if (_contacts.Remove(s.PeerId, out var hidden)) Contacts.Remove(hidden);
            if (_chatWindows.TryGetValue(s.PeerId, out var hw)) hw.RefreshHeader();
            return null;
        }
        if (!_contacts.TryGetValue(s.PeerId, out var vm))
        {
            vm = new ContactViewModel(s.PeerId);
            _contacts[s.PeerId] = vm;
            Contacts.Add(vm);
        }
        vm.Update(s, Chat.Directory);
        if (_chatWindows.TryGetValue(s.PeerId, out var w)) w.RefreshHeader();
        return vm;
    }

    private void RefreshContact(string peerId)
    {
        if (Chat.Registry.Get(peerId) is { } s) SyncContact(s);
    }

    private void OnDirectoryChanged()
    {
        foreach (var s in Chat.Registry.All)
        {
            if (s.PeerId != Chat.Identity.PeerId) SyncContact(s);
        }
        UpdateTrayTooltip();
        _main?.UpdateCounts();
        _main?.RefreshGrouping();
        DirectoryChanged?.Invoke();
        ProfileChanged?.Invoke();
    }

    private void UpdateTrayTooltip()
    {
        var online = _contacts.Values.Count(c => c.IsOnline);
        Tray.SetTooltip($"V-Talk — {StatusNames.ToDisplay(Chat.EffectiveStatus)} — {online} kişi çevrimiçi");
    }

    // ------------------------------------------------------------------ Mesajlar

    public ChatWindow OpenChat(string peerId, bool activate = true)
    {
        if (!_chatWindows.TryGetValue(peerId, out var w))
        {
            w = new ChatWindow(this, peerId);
            w.Closed += (_, _) => _chatWindows.Remove(peerId);
            _chatWindows[peerId] = w;
            w.ShowActivated = activate;
            w.Show();
        }
        if (activate)
        {
            if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
            w.Show();
            w.Activate();
            w.FocusInput();
        }
        if (_contacts.TryGetValue(peerId, out var vm)) vm.Unread = 0;
        return w;
    }

    private void OnMessageAdded(StoredMessage m)
    {
        if (!Chat.IsVisible(m.PeerId)) return;
        if (!_contacts.ContainsKey(m.PeerId) && Chat.Registry.Get(m.PeerId) is { } snap) OnPeerChanged(snap, false);

        if (m.Direction == MessageDirection.Outgoing)
        {
            if (_chatWindows.TryGetValue(m.PeerId, out var ow)) ow.AddMessage(m);
            return;
        }
        if (m.Kind == MessageKind.Announcement) return; // OnBulletin ayrıca ele alır; pencereye aşağıda eklenir.

        var busy = Chat.EffectiveStatus == PresenceStatus.Busy;
        var window = _chatWindows.TryGetValue(m.PeerId, out var existing) ? existing : OpenChat(m.PeerId, activate: false);
        window.AddMessage(m);
        if (m.Kind == MessageKind.Nudge) return;

        if (!window.IsActive)
        {
            if (_contacts.TryGetValue(m.PeerId, out var vm)) vm.Unread++;
            NativeMethods.Flash(window);
            if (!busy)
            {
                Sounds.Play(SoundKind.Message);
                if (Chat.Settings.ShowNotifications)
                {
                    var preview = !Chat.Settings.ShowMessagePreview ? (m.Kind == MessageKind.File ? "📎 Yeni dosya" : "Yeni mesaj")
                        : m.Kind == MessageKind.File ? $"📎 Dosya: {m.Data.FileName}" : m.Data.Text ?? "";
                    var verified = Chat.Directory.Current == null || Chat.Directory.GetMember(m.PeerId) != null;
                    Tray.Notify($"{DisplayName(m.PeerId)} diyor ki:", preview, () => OpenChat(m.PeerId), warning: !verified);
                }
            }
        }
    }

    private void OnMessageUpdated(StoredMessage m)
    {
        if (_chatWindows.TryGetValue(m.PeerId, out var w)) w.UpdateMessage(m);
    }

    private void OnNudge(string peerId)
    {
        if (!Chat.IsVisible(peerId)) return;
        var w = OpenChat(peerId, activate: false);
        if (Chat.EffectiveStatus != PresenceStatus.Busy) Sounds.Play(SoundKind.Nudge);
        w.Shake();
        NativeMethods.Flash(w);
    }

    private void OnBulletin(StoredMessage m)
    {
        var sender = DisplayName(m.PeerId);
        if (_chatWindows.TryGetValue(m.PeerId, out var w)) w.AddMessage(m);
        Sounds.Play(SoundKind.Bulletin);
        var title = m.Data.Scope == ChatService.ScopeAll ? "Genel Duyuru" : "Birim Duyurusu";
        Tray.Notify($"📢 {title} — {sender}", Chat.Settings.ShowMessagePreview ? m.Data.Text ?? "" : "Yeni duyuru", () => OpenChat(m.PeerId));
        new BulletinWindow(title, sender, m.Data.Text ?? "", m.Timestamp).Show();
    }

    // ------------------------------------------------------------------ Hızlı dizin düzenleme (admin)

    /// <summary>Bu bilgisayarda, sabitlenmiş organizasyon anahtarıyla eşleşen admin anahtarı var mı.</summary>
    public bool CanManageDirectory
    {
        get
        {
            if (Chat.Directory.Current == null || Chat.Directory.PinnedOrgKey is not { } pinned) return false;
            var local = AdminKeyStore.LocalPublicKey();
            return local != null && CryptographicOperations.FixedTimeEquals(local, pinned);
        }
    }

    /// <summary>Oturum açıksa anahtarı döndürür; değilse admin parolasını sorar. İptalde null.</summary>
    public ECDsa? EnsureAdminKey(Window owner)
    {
        if (_adminKey != null && DateTime.UtcNow < _adminKeyExpiresUtc)
        {
            _adminKeyExpiresUtc = DateTime.UtcNow + AdminSessionTimeout;
            return _adminKey;
        }
        EndAdminSession();
        var login = new AdminLoginWindow(this) { Owner = owner };
        if (login.ShowDialog() != true || login.UnlockedKey == null) return null;
        _adminKey = login.UnlockedKey;
        _adminKeyExpiresUtc = DateTime.UtcNow + AdminSessionTimeout;
        return _adminKey;
    }

    public void EndAdminSession()
    {
        _adminKey?.Dispose();
        _adminKey = null;
    }

    /// <summary>Dizinde olmayan (doğrulama bekleyen) ve listede bilinen kişiler.</summary>
    public List<ContactViewModel> PendingContacts() =>
        Chat.Directory.Current == null ? [] : _contacts.Values.Where(c => !c.IsVerified).ToList();

    /// <summary>
    /// Kişiyi birime atar (unitId null ise birimsiz). Dizinde olmayan kişi Kullanıcı rolüyle dizine eklenir;
    /// confirm true ise öncesinde kimlik bilgileri gösterilip onay alınır.
    /// </summary>
    public bool AssignUnit(Window owner, string peerId, string? unitId, bool confirm)
    {
        if (!CanManageDirectory || Chat.Directory.Current is not { } cur) return false;
        if (unitId != null && cur.Units.All(u => u.Id != unitId)) return false;

        var existing = cur.Members.FirstOrDefault(m => m.PeerId == peerId);
        if (existing != null && existing.UnitId == unitId) return false;

        var snap = Chat.Registry.Get(peerId);
        var unitName = unitId == null ? "Birimsiz" : cur.Units.First(u => u.Id == unitId).Name;
        if (existing == null)
        {
            if (!OrgDirectory.IsValidPeerId(peerId)) return false;
            if (confirm)
            {
                var info = $"{snap?.Name ?? peerId[..8]} dizinde kayıtlı değil.\n" +
                           $"Bu işlem kişiyi doğrulanmış kullanıcı olarak dizine ekler ve \"{unitName}\" birimine atar.\n\n" +
                           $"Kullanıcı: {snap?.User}\nBilgisayar: {snap?.Machine}\nAdres: {snap?.Address}\n" +
                           $"Parmak izi: {(snap != null ? CryptoUtil.Fingerprint(snap.PublicKey) : "-")}\n\nDevam edilsin mi?";
                if (MessageBox.Show(owner, info, "Dizine ekle", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                    return false;
            }
        }

        return PublishMembers(owner, cur, members =>
        {
            var m = members.FirstOrDefault(x => x.PeerId == peerId);
            if (m != null) m.UnitId = unitId;
            else members.Add(NewMember(peerId, snap, unitId));
        }, $"Birim ataması: {peerId[..8]} -> {unitName}");
    }

    /// <summary>Kişinin rolünü değiştirir (dizinde yoksa bu rolle eklenir). Dizinde en az bir Admin kalmalıdır.</summary>
    public bool SetRole(Window owner, string peerId, Role role)
    {
        if (!CanManageDirectory || Chat.Directory.Current is not { } cur || !Enum.IsDefined(role)) return false;
        if (!OrgDirectory.IsValidPeerId(peerId)) return false;
        var existing = cur.Members.FirstOrDefault(m => m.PeerId == peerId);
        if (existing?.Role == role) return false;

        if (role == Role.UnitManager && existing?.UnitId == null)
        {
            MessageBox.Show(owner, "Birim yöneticisi, çalıştığı birimin yöneticisidir. Önce kişinin çalıştığı birimi seçin.",
                "V-Talk", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        if (existing?.Role == Role.Admin && cur.Members.Count(m => m.Role == Role.Admin) <= 1)
        {
            MessageBox.Show(owner, "Dizinde en az bir Admin bulunmalı.", "V-Talk", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        if (peerId == Chat.Identity.PeerId && existing?.Role == Role.Admin &&
            MessageBox.Show(owner, "Kendi Admin rolünüzü kaldırıyorsunuz. Anahtar bu bilgisayarda kaldığı sürece dizini yine yönetebilirsiniz. Devam edilsin mi?",
                "Uyarı", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return false;
        if (role == Role.Admin &&
            MessageBox.Show(owner, $"{DisplayName(peerId)} Admin yapılacak (genel duyuru ve tüm yetkiler). Devam edilsin mi?",
                "Rol değişikliği", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return false;

        var snap = Chat.Registry.Get(peerId);
        return PublishMembers(owner, cur, members =>
        {
            var m = members.FirstOrDefault(x => x.PeerId == peerId);
            if (m == null) members.Add(m = NewMember(peerId, snap, null));
            m.Role = role;
        }, $"Rol değişikliği: {peerId[..8]} -> {RoleNames.ToDisplay(role)}");
    }

    /// <summary>Verilen bekleyen kişileri tek imzayla Kullanıcı rolünde (birimsiz) dizine ekler.</summary>
    public bool VerifyPeers(Window owner, IReadOnlyCollection<string> peerIds)
    {
        if (!CanManageDirectory || Chat.Directory.Current is not { } cur) return false;
        var known = cur.Members.Select(m => m.PeerId).ToHashSet();
        var toAdd = peerIds.Where(id => OrgDirectory.IsValidPeerId(id) && known.Add(id)).ToList();
        if (toAdd.Count == 0) return false;
        if (cur.Members.Count + toAdd.Count > OrgDirectory.MaxMembers)
        {
            MessageBox.Show(owner, "Dizin üye sınırı aşılıyor.", "V-Talk", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        return PublishMembers(owner, cur,
            members => members.AddRange(toAdd.Select(id => NewMember(id, Chat.Registry.Get(id), null))),
            $"{toAdd.Count} kişi doğrulandı");
    }

    private static OrgMember NewMember(string peerId, PeerSnapshot? snap, string? unitId) => new()
    {
        PeerId = peerId,
        Name = TextLimits.Clean(snap?.Name is { Length: > 0 } n ? n : peerId[..8], TextLimits.NameMax),
        Role = Role.User,
        UnitId = unitId,
    };

    /// <summary>Mevcut dizinin kopyası üzerinde üye değişikliğini uygular, imzalar ve yayınlar.</summary>
    private bool PublishMembers(Window owner, OrgDirectory cur, Action<List<OrgMember>> change, string logText)
    {
        var key = EnsureAdminKey(owner);
        if (key == null) return false;

        var members = cur.Members.Select(m => new OrgMember { PeerId = m.PeerId, Name = m.Name, Role = m.Role, UnitId = m.UnitId }).ToList();
        change(members);
        try
        {
            Chat.Directory.SignAndApply(new OrgDirectory
            {
                Version = Chat.Directory.Version,
                Units = cur.Units.Select(u => new OrgUnit { Id = u.Id, Name = u.Name }).ToList(),
                Members = members,
                MembersOnly = cur.MembersOnly,
            }, key);
            Log.Info($"{logText} (dizin sürümü {Chat.Directory.Version})");
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, "Dizin güncellenemedi: " + ex.Message, "V-Talk", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    // ------------------------------------------------------------------ Profil

    public void SetStatus(PresenceStatus status)
    {
        Chat.UpdateProfile(status: status);
        ProfileChanged?.Invoke();
        UpdateTrayTooltip();
    }

    public void ShowMain()
    {
        _main ??= new MainWindow(this);
        _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
    }

    public bool IsExiting => _exiting;

    public void Exit()
    {
        if (_exiting) return;
        _exiting = true;
        foreach (var w in Application.Current.Windows.OfType<Window>().ToList())
        {
            try { w.Close(); } catch { }
        }
        Application.Current.Shutdown();
    }

    public void Dispose()
    {
        _idleTimer.Stop();
        EndAdminSession();
        Tray.Dispose();
    }
}
