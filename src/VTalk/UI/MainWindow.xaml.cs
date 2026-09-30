using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Microsoft.Win32;
using VTalk.Core;
using VTalk.Security;
using VTalk.Services;

namespace VTalk.UI;

public partial class MainWindow : Window
{
    private readonly AppController _app;
    private readonly ListCollectionView _view;
    private bool _updatingStatus;
    private bool _hintShown;

    public MainWindow(AppController app)
    {
        InitializeComponent();
        _app = app;

        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 20;
        Top = Math.Max(area.Top + 20, area.Bottom - Height - 20);

        _view = (ListCollectionView)CollectionViewSource.GetDefaultView(app.Contacts);
        _view.GroupDescriptions.Add(_grouping);
        UpdateGroupNames();
        _view.SortDescriptions.Add(new SortDescription(nameof(ContactViewModel.GroupOrder), ListSortDirection.Ascending));
        _view.SortDescriptions.Add(new SortDescription(nameof(ContactViewModel.GroupName), ListSortDirection.Ascending));
        _view.SortDescriptions.Add(new SortDescription(nameof(ContactViewModel.OnlineSort), ListSortDirection.Ascending));
        _view.SortDescriptions.Add(new SortDescription(nameof(ContactViewModel.Name), ListSortDirection.Ascending));
        _view.IsLiveSorting = true;
        _view.IsLiveGrouping = true;
        _view.IsLiveFiltering = true;
        foreach (var p in new[] { nameof(ContactViewModel.GroupOrder), nameof(ContactViewModel.GroupName), nameof(ContactViewModel.OnlineSort), nameof(ContactViewModel.Name) })
        {
            _view.LiveSortingProperties.Add(p);
            _view.LiveGroupingProperties.Add(p);
        }
        _view.LiveFilteringProperties.Add(nameof(ContactViewModel.Name));
        _view.LiveFilteringProperties.Add(nameof(ContactViewModel.IsVerified));
        ContactList.ItemsSource = _view;
        ApplyFilter();

        app.ProfileChanged += RefreshProfile;
        app.DirectoryChanged += RefreshProfile;
        app.NetworkChanged += RefreshNetworkWarning;
        RefreshProfile();
        RefreshNetworkWarning();
        UpdateCounts();
    }

    public void RefreshGrouping()
    {
        UpdateGroupNames();
        ApplyFilter();
    }

    private bool ShowPending => ShowPendingMenu.IsChecked && ShowPendingMenu.Visibility == Visibility.Visible;

    /// <summary>
    /// Arama ve doğrulama filtresi. Dizin varken doğrulanmamış kişiler gizlenir;
    /// admin bunları Yönetim menüsünden görünür yapabilir.
    /// </summary>
    private void ApplyFilter()
    {
        var q = SearchBox.Text.Trim();
        var hidePending = _app.Chat.Directory.Current != null && !ShowPending;
        _view.Filter = q.Length == 0 && !hidePending
            ? null
            : o => o is ContactViewModel c &&
                   (!hidePending || c.IsVerified) &&
                   (q.Length == 0 ||
                    c.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase) ||
                    c.User.Contains(q, StringComparison.CurrentCultureIgnoreCase) ||
                    c.Machine.Contains(q, StringComparison.CurrentCultureIgnoreCase) ||
                    (c.UnitName?.Contains(q, StringComparison.CurrentCultureIgnoreCase) ?? false));
    }

    private readonly PropertyGroupDescription _grouping = new(nameof(ContactViewModel.GroupName));

    /// <summary>
    /// Dizindeki birimleri grup olarak önceden tanımlar; böylece boş birimler de başlık olarak görünür
    /// ve kişiler üzerine sürüklenebilir. Arama sırasında yalnızca eşleşen gruplar gösterilir.
    /// </summary>
    private void UpdateGroupNames()
    {
        _grouping.GroupNames.Clear();
        var dir = _app.Chat.Directory.Current;
        if (dir == null || SearchBox.Text.Trim().Length > 0) return;
        var myUnit = _app.Chat.Directory.GetUnitName(_app.Chat.Directory.GetMember(_app.Chat.Identity.PeerId)?.UnitId);
        foreach (var name in dir.Units.Select(u => u.Name).Distinct()
                     .OrderBy(n => n == myUnit ? 0 : 1).ThenBy(n => n, StringComparer.CurrentCultureIgnoreCase))
            _grouping.GroupNames.Add(name);
        _grouping.GroupNames.Add(ContactViewModel.NoUnitGroup);
    }

    public void UpdateCounts()
    {
        var hasDir = _app.Chat.Directory.Current != null;
        var listed = _app.Contacts.Where(c => !hasDir || c.IsVerified).ToList();
        var pending = hasDir ? _app.Contacts.Count - listed.Count : 0;
        CountText.Text = $"{listed.Count(c => c.IsOnline)} / {listed.Count} kişi çevrimiçi" +
                         (pending > 0 && ShowPendingMenu.Visibility == Visibility.Visible ? $" • {pending} doğrulama bekliyor" : "");
        VerifyAllMenu.Header = pending > 0 ? $"Bekleyenlerin tümünü doğrula ({pending})…" : "Bekleyenlerin tümünü doğrula…";
        VerifyAllMenu.IsEnabled = pending > 0;
    }

    private void RefreshProfile()
    {
        var chat = _app.Chat;
        MyName.Text = chat.Settings.DisplayName;
        var member = chat.Directory.GetMember(chat.Identity.PeerId);
        var unit = chat.Directory.GetUnitName(member?.UnitId);
        MyRole.Text = member == null
            ? (chat.Directory.Current == null ? "Organizasyon dizini henüz yok"
                : chat.Directory.MembersOnly ? "Dizinde kayıtlı değilsiniz — admin sizi ekleyene kadar mesajlaşamazsınız"
                : "Admin dizininde kayıtlı değilsiniz")
            : RoleNames.ToDisplay(member.Role) + (unit != null ? " • " + unit : "");
        if (!NoteBox.IsKeyboardFocused) NoteBox.Text = chat.Settings.Note;

        _updatingStatus = true;
        StatusCombo.SelectedIndex = (int)chat.Settings.Status;
        _updatingStatus = false;
        MyStatusDot.Fill = (System.Windows.Media.Brush)new StatusToBrushConverter().Convert(chat.EffectiveStatus, typeof(object), null!, null!);

        MyAvatar.Source = ImageLoader.Load(AppPaths.AvatarFile, 112);
        BulletinMenu.IsEnabled = chat.CanSendBulletin(ChatService.ScopeAll) || chat.CanSendBulletin(ChatService.ScopeUnit);
        ConflictBar.Visibility = chat.Directory.ConflictDetected ? Visibility.Visible : Visibility.Collapsed;
        OrgText.Text = chat.Directory.Current == null ? "" : $"Dizin v{chat.Directory.Version}" + (chat.Directory.MembersOnly ? " • sıkı mod" : "");

        var adminVis = _app.CanManageDirectory ? Visibility.Visible : Visibility.Collapsed;
        if (ShowPendingMenu.Visibility != adminVis)
        {
            ShowPendingMenu.Visibility = VerifyAllMenu.Visibility = adminVis;
            ApplyFilter();
        }
        UpdateCounts();
    }

    private void RefreshNetworkWarning()
    {
        var blocked = _app.Chat.Network.BlockedNetworkNames;
        PublicNetBar.Visibility = blocked.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        PublicNetText.Text = $"⚠ \"{string.Join("\", \"", blocked)}\" ağı Windows'ta \"Ortak (Public)\" olarak tanımlı. Güvenliğiniz için V-Talk bu ağda " +
                             "görünmez ve bağlantı kabul etmez. Bu bir şirket ağıysa Windows ağ ayarlarından \"Özel\" yapın veya Ayarlar > Ağ bölümünden izin verin.";
    }

    // ------------------------------------------------------------------ Pencere

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_app.IsExiting)
        {
            e.Cancel = true;
            Hide();
            if (!_hintShown)
            {
                _hintShown = true;
                _app.Tray.Notify("V-Talk", "V-Talk arka planda çalışmaya devam ediyor. Tamamen kapatmak için tepsi simgesinden Çıkış'ı seçin.");
            }
        }
        base.OnClosing(e);
    }

    // ------------------------------------------------------------------ Kişi listesi

    private ContactViewModel? Selected => ContactList.SelectedItem as ContactViewModel;

    private void ContactList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(ContactList, d) is ListBoxItem && Selected is { } c)
            _app.OpenChat(c.PeerId);
    }

    private void ContactList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Selected is { } c) _app.OpenChat(c.PeerId);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateGroupNames();
        ApplyFilter();
    }

    private ContactViewModel? ContextContact(object sender) =>
        (sender as FrameworkElement)?.DataContext as ContactViewModel;

    private void ContextOpen_Click(object sender, RoutedEventArgs e)
    {
        if (ContextContact(sender) is { } c) _app.OpenChat(c.PeerId);
    }

    private void ContextSendFile_Click(object sender, RoutedEventArgs e)
    {
        if (ContextContact(sender) is { } c) _app.OpenChat(c.PeerId).PickAndSendFile();
    }

    private void ContextInfo_Click(object sender, RoutedEventArgs e)
    {
        if (ContextContact(sender) is not { } c) return;
        var s = _app.Chat.Registry.Get(c.PeerId);
        var fp = s == null ? "?" : CryptoUtil.Fingerprint(s.PublicKey);
        MessageBox.Show(this,
            $"Ad: {c.Name}\nKullanıcı: {c.User}\nBilgisayar: {c.Machine}\nAdres: {s?.Address}\n" +
            $"Rol: {c.RoleText}\nBirim: {c.UnitName ?? "-"}\nDizinde doğrulanmış: {(c.IsVerified ? "Evet" : "Hayır")}\n\n" +
            $"Kimlik parmak izi:\n{fp}\n\nKimlik taklidini önlemek için bu parmak izini kişiyle başka bir kanaldan karşılaştırabilirsiniz.",
            "Kimlik bilgisi", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ContextDeleteHistory_Click(object sender, RoutedEventArgs e)
    {
        if (ContextContact(sender) is not { } c) return;
        if (MessageBox.Show(this, $"{c.Name} ile olan tüm sohbet geçmişi bu bilgisayardan kalıcı olarak silinecek. Devam edilsin mi?",
                "Geçmişi sil", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _app.Chat.Db.DeleteHistory(c.PeerId);
        foreach (var w in Application.Current.Windows.OfType<ChatWindow>().Where(w => w.PeerId == c.PeerId)) w.ReloadHistory();
    }

    // ------------------------------------------------------------------ Birim ataması (admin)

    private const string PeerDragFormat = "VTalk.PeerId";
    private Point _dragStart;
    private ContactViewModel? _dragCandidate;

    /// <summary>Kişi menüsü her açılışta yeniden oluşturulur; admin için kimlik bilgileri ve dizin seçenekleri eklenir.</summary>
    private void ContactList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        e.Handled = true;
        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) is not { DataContext: ContactViewModel c } item) return;
        item.IsSelected = true;
        var menu = new ContextMenu { DataContext = c, PlacementTarget = item };

        var dir = _app.Chat.Directory.Current;
        var admin = dir != null && _app.CanManageDirectory;
        if (admin)
        {
            var snap = _app.Chat.Registry.Get(c.PeerId);
            foreach (var line in new[]
                     {
                         $"IP: {snap?.Address?.ToString() ?? "-"}",
                         $"Bilgisayar: {(c.Machine.Length > 0 ? c.Machine : "-")}",
                         $"Kullanıcı: {(c.User.Length > 0 ? c.User : "-")}",
                         $"Parmak izi: {(snap != null ? CryptoUtil.Fingerprint(snap.PublicKey) : "-")}",
                     })
                menu.Items.Add(new MenuItem { Header = new TextBlock { Text = line }, IsEnabled = false });
            menu.Items.Add(new Separator());
        }

        AddMenuItem(menu, "Sohbet aç", ContextOpen_Click);
        AddMenuItem(menu, "Dosya gönder…", ContextSendFile_Click);
        menu.Items.Add(new Separator());
        AddMenuItem(menu, "Kimlik bilgisi", ContextInfo_Click);
        AddMenuItem(menu, "Sohbet geçmişini sil…", ContextDeleteHistory_Click);
        if (admin) AddDirectoryItems(menu, c, dir!);

        menu.IsOpen = true;
    }

    private static void AddMenuItem(ContextMenu menu, string text, RoutedEventHandler click)
    {
        var item = new MenuItem { Header = new TextBlock { Text = text } };
        item.Click += click;
        menu.Items.Add(item);
    }

    private void AddDirectoryItems(ContextMenu menu, ContactViewModel c, OrgDirectory dir)
    {
        menu.Items.Add(new Separator());
        var member = _app.Chat.Directory.GetMember(c.PeerId);
        if (member == null)
        {
            var verify = new MenuItem { Header = new TextBlock { Text = "✔ Doğrula", FontWeight = FontWeights.SemiBold } };
            verify.Click += (_, _) => _app.AssignUnit(this, c.PeerId, null, confirm: false);
            menu.Items.Add(verify);
        }

        var currentUnit = member == null ? null : _app.Chat.Directory.GetUnitName(member.UnitId) ?? "Birimsiz";
        var unitMenu = new MenuItem
        {
            Header = new TextBlock { Text = member == null ? "Doğrula ve çalıştığı birimi seç" : $"Çalıştığı birim: {currentUnit}" },
        };
        foreach (var u in dir.Units.OrderBy(u => u.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var unitId = u.Id;
            var item = new MenuItem { Header = new TextBlock { Text = u.Name }, IsChecked = member?.UnitId == unitId };
            item.Click += (_, _) => _app.AssignUnit(this, c.PeerId, unitId, confirm: false);
            unitMenu.Items.Add(item);
        }
        if (member != null)
        {
            if (dir.Units.Count > 0) unitMenu.Items.Add(new Separator());
            var none = new MenuItem { Header = new TextBlock { Text = "Birimsiz" }, IsChecked = member.UnitId == null };
            none.Click += (_, _) => _app.AssignUnit(this, c.PeerId, null, confirm: false);
            unitMenu.Items.Add(none);
        }
        if (dir.Units.Count == 0)
            unitMenu.Items.Add(new MenuItem { Header = new TextBlock { Text = "(Birim yok — Admin panelinden ekleyin)" }, IsEnabled = false });
        menu.Items.Add(unitMenu);

        var roleMenu = new MenuItem { Header = new TextBlock { Text = $"Rol: {RoleNames.ToDisplay(member?.Role ?? Role.User)}" } };
        foreach (var role in new[] { Role.User, Role.UnitManager, Role.Admin })
        {
            var r = role;
            var text = r == Role.UnitManager ? "Birim Yöneticisi (çalıştığı birimin yöneticisi)" : RoleNames.ToDisplay(r);
            var item = new MenuItem { Header = new TextBlock { Text = text }, IsChecked = member != null && member.Role == r };
            item.Click += (_, _) => _app.SetRole(this, c.PeerId, r);
            roleMenu.Items.Add(item);
        }
        menu.Items.Add(roleMenu);
    }

    private void ShowPending_Click(object sender, RoutedEventArgs e)
    {
        UpdateGroupNames();
        ApplyFilter();
        UpdateCounts();
    }

    private void VerifyAll_Click(object sender, RoutedEventArgs e)
    {
        var pending = _app.PendingContacts();
        if (pending.Count == 0) return;
        var lines = pending.Take(25).Select(c =>
            $"• {c.Name} — {_app.Chat.Registry.Get(c.PeerId)?.Address?.ToString() ?? "-"} — {c.Machine}");
        var more = pending.Count > 25 ? $"\n… ve {pending.Count - 25} kişi daha" : "";
        if (MessageBox.Show(this,
                $"Aşağıdaki {pending.Count} kişi doğrulanıp dizine \"Birimsiz\" olarak eklenecek:\n\n{string.Join("\n", lines)}{more}\n\nDevam edilsin mi?",
                "Toplu doğrulama", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _app.VerifyPeers(this, pending.Select(c => c.PeerId).ToList());
    }

    private void ContactList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(ContactList);
        _dragCandidate = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext as ContactViewModel;
    }

    private void ContactList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragCandidate is not { } c) return;
        var delta = e.GetPosition(ContactList) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragCandidate = null;
        if (!_app.CanManageDirectory) return;
        DragDrop.DoDragDrop(ContactList, new DataObject(PeerDragFormat, c.PeerId), DragDropEffects.Move);
    }

    private void ContactList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(PeerDragFormat) && TryGetDropUnit(e.OriginalSource as DependencyObject, out _)
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void ContactList_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetData(PeerDragFormat) is not string peerId || !OrgDirectory.IsValidPeerId(peerId)) return;
        if (!TryGetDropUnit(e.OriginalSource as DependencyObject, out var unitId)) return;
        Dispatcher.BeginInvoke(() => _app.AssignUnit(this, peerId, unitId, confirm: true));
    }

    /// <summary>Bırakılan yerin birimini bulur: bir kişinin üstü ya da grup başlığı. Doğrulanmamış grup hedef olamaz.</summary>
    private bool TryGetDropUnit(DependencyObject? source, out string? unitId)
    {
        unitId = null;
        if (FindAncestor<ListBoxItem>(source)?.DataContext is ContactViewModel target)
            return TryGetMemberUnit(target, out unitId);
        if (FindAncestor<GroupItem>(source)?.DataContext is not CollectionViewGroup group) return false;
        if (group.Items.OfType<ContactViewModel>().FirstOrDefault() is { } first)
            return TryGetMemberUnit(first, out unitId);

        if (_app.Chat.Directory.Current is not { } dir || group.Name is not string name) return false;
        var matches = dir.Units.Where(u => u.Name == name).ToList();
        if (matches.Count == 1)
        {
            unitId = matches[0].Id;
            return true;
        }
        return matches.Count == 0 && name == ContactViewModel.NoUnitGroup;
    }

    private bool TryGetMemberUnit(ContactViewModel c, out string? unitId)
    {
        var member = _app.Chat.Directory.GetMember(c.PeerId);
        unitId = member?.UnitId;
        return member != null;
    }

    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null && d is not T)
            d = d is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(d)
                : LogicalTreeHelper.GetParent(d);
        return d as T;
    }

    // ------------------------------------------------------------------ Profil

    private void StatusCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingStatus || StatusCombo.SelectedItem is not ComboBoxItem item) return;
        _app.SetStatus((PresenceStatus)int.Parse((string)item.Tag));
    }

    private void StatusMenu_Click(object sender, RoutedEventArgs e) =>
        _app.SetStatus((PresenceStatus)int.Parse((string)((MenuItem)sender).Tag));

    private void NoteBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitNote();
            ContactList.Focus();
        }
    }

    private void NoteBox_LostFocus(object sender, RoutedEventArgs e) => CommitNote();

    private void CommitNote()
    {
        var note = TextLimits.Clean(NoteBox.Text, TextLimits.NoteMax);
        if (note == _app.Chat.Settings.Note) return;
        _app.Chat.UpdateProfile(note: note);
    }

    private void ChangeAvatar_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Profil resmi seçin",
            Filter = "Resimler|*.png;*.jpg;*.jpeg;*.bmp;*.gif",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            AvatarHelper.SaveAvatar(dlg.FileName);
            _app.Chat.ReloadAvatar();
            RefreshProfile();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Resim kullanılamadı: " + ex.Message, "V-Talk", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenDownloads_Click(object sender, RoutedEventArgs e)
    {
        var folder = _app.Chat.Settings.DownloadFolder;
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { folder }, UseShellExecute = false });
    }

    // ------------------------------------------------------------------ Menüler

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        new SettingsWindow(_app) { Owner = this }.ShowDialog();
        RefreshProfile();
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => _app.Exit();

    private void AdminLogin_Click(object sender, RoutedEventArgs e)
    {
        var login = new AdminLoginWindow(_app) { Owner = this };
        if (login.ShowDialog() != true || login.UnlockedKey == null) return;
        new AdminPanelWindow(_app, login.UnlockedKey) { Owner = this }.Show();
    }

    private void Bulletin_Click(object sender, RoutedEventArgs e) =>
        new BulletinComposeWindow(_app) { Owner = this }.ShowDialog();

    private void About_Click(object sender, RoutedEventArgs e)
    {
        var chat = _app.Chat;
        MessageBox.Show(this,
            "V-Talk LAN Messenger 1.1\n\nSunucu gerektirmeyen, eşler arası (P2P) şifreli yerel ağ mesajlaşma uygulaması.\n\n" +
            $"Kimliğiniz: {chat.Identity.PeerId[..8]}\nParmak izi: {chat.Identity.Fingerprint}\n" +
            $"TCP port: {chat.TcpPort}  •  UDP keşif: {Net.DiscoveryService.Port}\n" +
            $"Organizasyon: {chat.Directory.OrgFingerprint ?? "henüz yok"}{(chat.Directory.IsDeployedTrustRoot ? " (BT tarafından dağıtıldı)" : "")}",
            "Hakkında", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
