using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using VTalk.Core;
using VTalk.Security;

namespace VTalk.UI;

public sealed class RoleChoice
{
    public Role Value { get; init; }
    public string Name { get; init; } = "";
}

public sealed class UnitRow : ObservableObject
{
    private string _name = "";
    private IReadOnlyList<MemberRow> _members = [];
    public string Id { get; init; } = "";
    public string Name { get => _name; set => Set(ref _name, value); }
    public IReadOnlyList<MemberRow> Members
    {
        get => _members;
        set { if (Set(ref _members, value)) { Raise(nameof(MemberCount)); Raise(nameof(MemberCountText)); } }
    }
    public int MemberCount => _members.Count;
    public string MemberCountText => $"{MemberCount} üye";
}

public sealed class MemberRow : ObservableObject
{
    private bool _include;
    private string _name = "";
    private Role _role;
    private string _unitKey = "";
    private string? _warning;
    public string PeerId { get; init; } = "";
    public string ShortId => PeerId[..8];
    public string User { get; init; } = "";
    public string Machine { get; init; } = "";
    public string Address { get; init; } = "";
    public string Fingerprint { get; init; } = "";
    public string StateText { get; init; } = "";
    public string? Warning { get => _warning; set { if (Set(ref _warning, value)) { Raise(nameof(HasWarning)); Raise(nameof(WarningIcon)); Raise(nameof(Tooltip)); } } }
    public bool HasWarning => _warning != null;
    public string WarningIcon => HasWarning ? "⚠" : "";
    public string Tooltip => $"Kimlik parmak izi: {Fingerprint}\nTam kimlik: {PeerId}" + (Warning != null ? "\n\n⚠ " + Warning : "");
    public bool Include { get => _include; set => Set(ref _include, value); }
    public string Name { get => _name; set => Set(ref _name, value); }
    public Role Role { get => _role; set { if (Set(ref _role, value)) Raise(nameof(RoleBadge)); } }
    public string RoleBadge => Role switch { Role.UnitManager => "★ Yönetici", Role.Admin => "Admin", _ => "" };
    /// <summary>Boş dize = birim yok.</summary>
    public string UnitKey { get => _unitKey; set => Set(ref _unitKey, value ?? ""); }
}

public partial class AdminPanelWindow : Window
{
    private static readonly TimeSpan SessionTimeout = TimeSpan.FromMinutes(15);

    private readonly AppController _app;
    private readonly ECDsa _orgKey;
    private readonly ObservableCollection<MemberRow> _members = [];
    private readonly ObservableCollection<UnitRow> _units = [];
    private readonly ObservableCollection<UnitRow> _unitChoices = [];
    private readonly DispatcherTimer _sessionTimer;
    private bool _dirty;

    public AdminPanelWindow(AppController app, ECDsa orgKey)
    {
        InitializeComponent();
        _app = app;
        _orgKey = orgKey;

        RoleColumn.ItemsSource = new[]
        {
            new RoleChoice { Value = Role.User, Name = RoleNames.ToDisplay(Role.User) },
            new RoleChoice { Value = Role.UnitManager, Name = RoleNames.ToDisplay(Role.UnitManager) },
            new RoleChoice { Value = Role.Admin, Name = RoleNames.ToDisplay(Role.Admin) },
        };
        UnitColumn.ItemsSource = _unitChoices;
        UnitsList.ItemsSource = _units;
        MembersGrid.ItemsSource = _members;

        LoadData();

        _sessionTimer = new DispatcherTimer { Interval = SessionTimeout };
        _sessionTimer.Tick += (_, _) =>
        {
            _sessionTimer.Stop();
            _dirty = false;
            MessageBox.Show(this, "Admin oturumu hareketsizlik nedeniyle kapatıldı.", "V-Talk", MessageBoxButton.OK, MessageBoxImage.Information);
            Close();
        };
        _sessionTimer.Start();
        PreviewMouseDown += (_, _) => ResetSession();
        PreviewKeyDown += (_, _) => ResetSession();
    }

    private void ResetSession()
    {
        _sessionTimer.Stop();
        _sessionTimer.Start();
    }

    private void LoadData()
    {
        var chat = _app.Chat;
        var dir = chat.Directory.Current;

        _units.Clear();
        foreach (var u in dir?.Units ?? []) AddUnitRow(new UnitRow { Id = u.Id, Name = u.Name });
        RebuildUnitChoices();

        _members.Clear();
        var known = chat.Registry.All.ToDictionary(p => p.PeerId);
        var ids = new HashSet<string>(known.Keys) { chat.Identity.PeerId };
        foreach (var m in dir?.Members ?? []) ids.Add(m.PeerId);

        foreach (var id in ids)
        {
            var member = dir?.Members.FirstOrDefault(m => m.PeerId == id);
            known.TryGetValue(id, out var snap);
            var isMe = id == chat.Identity.PeerId;
            var row = new MemberRow
            {
                PeerId = id,
                User = isMe ? Environment.UserName : snap?.User ?? "",
                Machine = isMe ? Environment.MachineName : snap?.Machine ?? "",
                Address = isMe ? "" : snap?.Address?.ToString() ?? "",
                Fingerprint = isMe ? chat.Identity.Fingerprint : snap != null ? CryptoUtil.Fingerprint(snap.PublicKey) : "(bilinmiyor)",
                StateText = isMe ? "Bu bilgisayar" : snap is { IsOnline: true } ? "Çevrimiçi" : "Çevrimdışı",
                Include = member != null,
                Name = member?.Name is { Length: > 0 } n ? n : isMe ? chat.Settings.DisplayName : snap?.Name ?? id[..8],
                Role = member?.Role ?? Role.User,
                UnitKey = member?.UnitId ?? "",
            };
            row.PropertyChanged += (_, _) => { _dirty = true; UpdateUnitCounts(); };
            _members.Add(row);
        }
        foreach (var r in _members.OrderBy(r => r.Include ? 0 : 1).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList())
        {
            _members.Move(_members.IndexOf(r), _members.Count - 1);
        }
        UpdateUnitCounts();
        FlagDuplicateClaims();

        MembersOnlyCheck.IsChecked = dir?.MembersOnly == true;
        FingerprintRun.Text = CryptoUtil.Fingerprint(_orgKey.ExportSubjectPublicKeyInfo());
        VersionText.Text = $"Yayındaki dizin sürümü: {chat.Directory.Version}  •  {dir?.Members.Count ?? 0} üye, {dir?.Units.Count ?? 0} birim";
        StatusText.Text = "";
        _dirty = false;
    }

    private void AddUnitRow(UnitRow row)
    {
        row.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(UnitRow.Name)) return;
            _dirty = true;
            var choice = _unitChoices.FirstOrDefault(c => c.Id == row.Id);
            if (choice != null) choice.Name = row.Name;
        };
        _units.Add(row);
    }

    private void RebuildUnitChoices()
    {
        _unitChoices.Clear();
        _unitChoices.Add(new UnitRow { Id = "", Name = "(Birim yok)" });
        foreach (var u in _units) _unitChoices.Add(new UnitRow { Id = u.Id, Name = u.Name });
    }

    private void UpdateUnitCounts()
    {
        foreach (var u in _units)
        {
            var list = _members.Where(m => m.Include && m.UnitKey == u.Id)
                .OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            if (!list.SequenceEqual(u.Members)) u.Members = list;
        }
    }

    /// <summary>
    /// Kullanıcı ve bilgisayar adı kişinin kendi beyanıdır. Aynı Windows kullanıcı adını veya aynı bilgisayar adını
    /// iddia eden birden fazla kimlik varsa (olası taklit ya da yeniden kurulum) admin uyarılır.
    /// </summary>
    private void FlagDuplicateClaims()
    {
        foreach (var r in _members) r.Warning = null;
        foreach (var g in _members.Where(r => r.User.Length > 0).GroupBy(r => r.User, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            foreach (var r in g) r.Warning = $"\"{g.Key}\" kullanıcı adını {g.Count()} farklı kimlik iddia ediyor. Doğru kimliği IP adresi ve parmak iziyle kişiye teyit ettirin.";
        }
        foreach (var g in _members.Where(r => r.Machine.Length > 0).GroupBy(r => r.Machine, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            foreach (var r in g) r.Warning ??= $"\"{g.Key}\" bilgisayar adını {g.Count()} farklı kimlik iddia ediyor (yeniden kurulum veya taklit olabilir).";
        }
    }

    private void Policy_Changed(object sender, RoutedEventArgs e) => _dirty = true;

    // ------------------------------------------------------------------ Birimler

    private void AddUnit_Click(object sender, RoutedEventArgs e)
    {
        var name = TextLimits.Clean(NewUnitBox.Text, TextLimits.UnitNameMax);
        if (name.Length == 0) return;
        if (_units.Any(u => string.Equals(u.Name, name, StringComparison.CurrentCultureIgnoreCase)))
        {
            MessageBox.Show(this, "Bu isimde bir birim zaten var.", "V-Talk", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        AddUnitRow(new UnitRow { Id = CryptoUtil.NewId()[..12], Name = name });
        RebuildUnitChoices();
        NewUnitBox.Clear();
        _dirty = true;
    }

    private void DeleteUnit_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not UnitRow unit) return;
        var affected = _members.Count(m => m.UnitKey == unit.Id);
        if (affected > 0 && MessageBox.Show(this, $"\"{unit.Name}\" biriminde {affected} kişi var. Birim silinirse bu kişiler birimsiz kalır. Devam edilsin mi?",
                "Birimi sil", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        foreach (var m in _members.Where(m => m.UnitKey == unit.Id)) m.UnitKey = "";
        _units.Remove(unit);
        RebuildUnitChoices();
        _dirty = true;
    }

    /// <summary>Birime eklenecek kişiyi seçtirir. Doğrulanmamış biri seçilirse dizine de dahil edilir.</summary>
    private void AddToUnit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: UnitRow unit } button) return;
        var menu = new ContextMenu { PlacementTarget = button, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        var candidates = _members
            .Where(m => !(m.Include && m.UnitKey == unit.Id))
            .OrderBy(m => m.Include ? 0 : 1)
            .ThenBy(m => m.StateText == "Çevrimiçi" ? 0 : 1)
            .ThenBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (candidates.Count == 0)
            menu.Items.Add(new MenuItem { Header = new TextBlock { Text = "(Eklenecek kişi yok)" }, IsEnabled = false });
        foreach (var m in candidates)
        {
            var details = string.Join(" — ", new[] { m.Address, m.Machine, m.User }.Where(s => s.Length > 0));
            var header = new TextBlock();
            header.Inlines.Add(new System.Windows.Documents.Run(m.Name) { FontWeight = FontWeights.SemiBold });
            if (details.Length > 0) header.Inlines.Add(new System.Windows.Documents.Run("   " + details));
            if (!m.Include) header.Inlines.Add(new System.Windows.Documents.Run("   (doğrulanmamış)") { Foreground = System.Windows.Media.Brushes.DarkOrange });
            else if (_units.FirstOrDefault(u => u.Id == m.UnitKey) is { } current)
                header.Inlines.Add(new System.Windows.Documents.Run($"   (şu an: {current.Name})") { Foreground = System.Windows.Media.Brushes.Gray });
            var item = new MenuItem { Header = header, ToolTip = m.Tooltip };
            item.Click += (_, _) =>
            {
                m.Include = true;
                m.UnitKey = unit.Id;
                StatusText.Text = $"{m.Name} → {unit.Name}. Dağıtmak için \"Kaydet ve Yayınla\"ya basın.";
            };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void RemoveFromUnit_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not MemberRow m) return;
        m.UnitKey = "";
        StatusText.Text = $"{m.Name} birimden çıkarıldı. Dağıtmak için \"Kaydet ve Yayınla\"ya basın.";
    }

    private void IncludeAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var m in _members) m.Include = true;
    }

    // Girilen ağ(lar)daki (CIDR) kişileri topluca işaretler. Bu yalnızca bir seçim kolaylığıdır:
    // doğrulama yine yayınla adımında her kişinin kimlik anahtarı dizine yazılarak yapılır.
    private void IncludeSubnet_Click(object sender, RoutedEventArgs e)
    {
        var subnets = Net.NetworkTargets.Subnets(SubnetBox.Text);
        if (subnets.Count == 0)
        {
            MessageBox.Show(this, "Geçerli bir ağ girin. Örnek: 10.20.30.0/24", "V-Talk", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        int count = 0;
        foreach (var m in _members)
        {
            if (m.Include || m.Address.Length == 0) continue;
            if (!System.Net.IPAddress.TryParse(m.Address, out var ip)) continue;
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
            var value = Net.NetworkTargets.ToUInt(ip);
            if (subnets.Any(s => (value & s.Mask) == s.Net))
            {
                m.Include = true;
                count++;
            }
        }
        StatusText.Text = count > 0
            ? $"{count} kişi işaretlendi. Kontrol edip \"Yayınla\" ile kaydedin."
            : "Bu ağda dizinde olmayan kişi bulunamadı (kişilerin çevrimiçi olması gerekir).";
    }

    // ------------------------------------------------------------------ Yayınlama

    private void Publish_Click(object sender, RoutedEventArgs e)
    {
        MembersGrid.CommitEdit();
        MembersGrid.CommitEdit();

        var units = _units.Select(u => new OrgUnit { Id = u.Id, Name = TextLimits.Clean(u.Name, TextLimits.UnitNameMax) }).ToList();
        if (units.Any(u => u.Name.Length == 0))
        {
            MessageBox.Show(this, "Birim adları boş olamaz.", "V-Talk", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var unitIds = units.Select(u => u.Id).ToHashSet();
        var members = _members.Where(m => m.Include).Select(m => new OrgMember
        {
            PeerId = m.PeerId,
            Name = TextLimits.Clean(m.Name, TextLimits.NameMax),
            Role = m.Role,
            UnitId = m.UnitKey.Length > 0 && unitIds.Contains(m.UnitKey) ? m.UnitKey : null,
        }).ToList();

        var myId = _app.Chat.Identity.PeerId;
        if (!members.Any(m => m.Role == Role.Admin))
        {
            MessageBox.Show(this, "Dizinde en az bir Admin bulunmalı.", "V-Talk", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (members.FirstOrDefault(m => m.PeerId == myId)?.Role != Role.Admin &&
            MessageBox.Show(this, "Kendi Admin rolünüzü kaldırıyorsunuz. Anahtar bu bilgisayarda kaldığı sürece panele yine girebilirsiniz. Devam edilsin mi?",
                "Uyarı", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        var managersWithoutUnit = members.Where(m => m.Role == Role.UnitManager && m.UnitId == null).Select(m => m.Name).ToList();
        if (managersWithoutUnit.Count > 0 &&
            MessageBox.Show(this, $"Şu birim yöneticilerinin çalıştığı birim seçilmemiş, bu yüzden yönetecekleri bir birim yok:\n\n{string.Join(", ", managersWithoutUnit)}\n\nYine de yayınlansın mı?",
                "Birim seçilmemiş", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        var membersOnly = MembersOnlyCheck.IsChecked == true;
        var excludedOnline = _members.Count(m => !m.Include && m.StateText == "Çevrimiçi");
        if (membersOnly && excludedOnline > 0 &&
            MessageBox.Show(this, $"Sıkı mod açık ve şu anda çevrimiçi olan {excludedOnline} kişi dizinde değil. Bu kişiler yayından sonra mesajlaşamayacak. Devam edilsin mi?",
                "Sıkı mod", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        try
        {
            var dir = new OrgDirectory { Version = _app.Chat.Directory.Version, Units = units, Members = members, MembersOnly = membersOnly };
            _app.Chat.Directory.SignAndApply(dir, _orgKey);
            Log.Info($"Admin dizini yayınladı: sürüm {_app.Chat.Directory.Version}");
            LoadData();
            StatusText.Text = $"✔ Yayınlandı — sürüm {_app.Chat.Directory.Version}. Çevrimiçi kullanıcılar birkaç saniye içinde güncellenir.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Yayınlanamadı: " + ex.Message, "V-Talk", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ------------------------------------------------------------------ Organizasyon

    private void ExportPub_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { FileName = "org.pub", Filter = "Organizasyon açık anahtarı|*.pub" };
        if (dlg.ShowDialog(this) != true) return;
        File.WriteAllText(dlg.FileName, DirectoryService.ToPem(_orgKey.ExportSubjectPublicKeyInfo()));
        StatusText.Text = "✔ org.pub dışa aktarıldı.";
    }

    private void ExportKey_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this,
                "Admin anahtarına sahip olan ve parolayı bilen herkes tüm yetkileri değiştirebilir.\nDosyayı yalnızca güvendiğiniz yöneticiye verin. Devam edilsin mi?",
                "Güvenlik uyarısı", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var dlg = new SaveFileDialog { FileName = "vtalk-admin.vtkey", Filter = "V-Talk anahtarı|*.vtkey" };
        if (dlg.ShowDialog(this) != true) return;
        AdminKeyStore.Export(dlg.FileName);
        Log.Info("Admin anahtarı dışa aktarıldı.");
        StatusText.Text = "✔ Admin anahtarı dışa aktarıldı.";
    }

    private async void ChangePassword_Click(object sender, RoutedEventArgs e)
    {
        var p1 = NewPass1.Password;
        if (p1 != NewPass2.Password)
        {
            MessageBox.Show(this, "Parolalar eşleşmiyor.", "V-Talk", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var err = AdminKeyStore.ValidatePasswordStrength(p1);
        if (err != null)
        {
            MessageBox.Show(this, err, "V-Talk", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        IsEnabled = false;
        try
        {
            await Task.Run(() => AdminKeyStore.ChangePassword(_orgKey, p1));
            NewPass1.Clear();
            NewPass2.Clear();
            StatusText.Text = "✔ Parola değiştirildi. Daha önce dışa aktarılan anahtar dosyaları eski parolayla açılmaya devam eder.";
            Log.Info("Admin parolası değiştirildi.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Parola değiştirilemedi: " + ex.Message, "V-Talk", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_dirty && !_app.IsExiting &&
            MessageBox.Show(this, "Yayınlanmamış değişiklikler var. Yine de kapatılsın mı?", "V-Talk",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _sessionTimer.Stop();
        _orgKey.Dispose();
        Log.Info("Admin oturumu kapatıldı.");
        base.OnClosed(e);
    }
}
