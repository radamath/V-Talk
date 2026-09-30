using System.IO;
using System.Net;
using System.Windows;
using Microsoft.Win32;
using VTalk.Core;
using VTalk.Services;

namespace VTalk.UI;

public partial class SettingsWindow : Window
{
    private readonly AppController _app;

    public SettingsWindow(AppController app)
    {
        InitializeComponent();
        _app = app;
        var s = app.Chat.Settings;
        NameBox.Text = s.DisplayName;
        SoundCheck.IsChecked = s.SoundEnabled;
        SoundPathBox.Text = s.CustomSoundPath ?? "";
        NotifyCheck.IsChecked = s.ShowNotifications;
        PreviewCheck.IsChecked = s.ShowMessagePreview;
        PublicNetCheck.IsChecked = s.AllowPublicNetworks;
        OnlineCheck.IsChecked = s.NotifyContactOnline;
        FolderBox.Text = s.DownloadFolder;
        AutoImageCheck.IsChecked = s.AutoAcceptImages;
        AutoImageMbBox.Text = s.AutoAcceptMaxMb.ToString();
        StartupCheck.IsChecked = s.StartWithWindows;
        MinimizedCheck.IsChecked = s.StartMinimized;
        AwayBox.Text = s.AutoAwayMinutes.ToString();
        ExtraBox.Text = s.ExtraAddresses;

        var chat = app.Chat;
        NetInfo.Text = $"Bu bilgisayar: TCP {chat.TcpPort}, UDP {Net.DiscoveryService.Port}. Güvenlik duvarında bu portlara yerel ağdan izin verilmelidir.";
        SecurityInfo.Text = $"Kimlik:        {chat.Identity.PeerId[..8]}\nParmak izi:    {chat.Identity.Fingerprint}\n" +
                            $"Organizasyon:  {chat.Directory.OrgFingerprint ?? "-"}" +
                            (chat.Directory.IsDeployedTrustRoot ? "\n               (BT tarafından dağıtılmış güven kökü)" : "");
    }

    private void PickSound_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "Bildirim sesi (.wav)", Filter = "WAV ses dosyası|*.wav" };
        if (dlg.ShowDialog(this) != true) return;
        if (!SoundService.IsUsableCustomSound(dlg.FileName))
        {
            MessageBox.Show(this, "Geçerli bir .wav dosyası seçin (en fazla 5 MB).", "V-Talk", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        SoundPathBox.Text = dlg.FileName;
    }

    private void ResetSound_Click(object sender, RoutedEventArgs e) => SoundPathBox.Text = "";

    private void TestSound_Click(object sender, RoutedEventArgs e)
    {
        var s = _app.Chat.Settings;
        var (enabled, path) = (s.SoundEnabled, s.CustomSoundPath);
        s.SoundEnabled = true;
        s.CustomSoundPath = SoundPathBox.Text.Length > 0 ? SoundPathBox.Text : null;
        _app.Sounds.Play(SoundKind.Message);
        (s.SoundEnabled, s.CustomSoundPath) = (enabled, path);
    }

    private void PickFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Alınan dosyalar klasörü", InitialDirectory = FolderBox.Text };
        if (dlg.ShowDialog(this) == true) FolderBox.Text = dlg.FolderName;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = TextLimits.Clean(NameBox.Text, TextLimits.NameMax);
        if (name.Length == 0)
        {
            MessageBox.Show(this, "Görünen ad boş olamaz.", "V-Talk", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!int.TryParse(AutoImageMbBox.Text, out var mb) || mb is < 1 or > 200)
        {
            MessageBox.Show(this, "Otomatik resim boyutu 1–200 MB arasında olmalı.", "V-Talk", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!int.TryParse(AwayBox.Text, out var away) || away is < 0 or > 240)
        {
            MessageBox.Show(this, "Boşta kalma süresi 0–240 dakika arasında olmalı.", "V-Talk", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var extras = Net.NetworkTargets.SplitEntries(ExtraBox.Text).ToArray();
        var invalid = extras.FirstOrDefault(x => !Net.NetworkTargets.TryParse(x, out _, out _));
        if (invalid != null)
        {
            MessageBox.Show(this, $"Geçersiz IPv4 adresi veya alt ağ: {invalid}\nÖrnek: 10.20.30.5 veya 10.20.30.0/24",
                "V-Talk", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var folder = FolderBox.Text;
        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Klasör kullanılamıyor: " + ex.Message, "V-Talk", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var s = _app.Chat.Settings;
        s.SoundEnabled = SoundCheck.IsChecked == true;
        s.CustomSoundPath = SoundPathBox.Text.Length > 0 ? SoundPathBox.Text : null;
        s.ShowNotifications = NotifyCheck.IsChecked == true;
        s.ShowMessagePreview = PreviewCheck.IsChecked == true;
        s.AllowPublicNetworks = PublicNetCheck.IsChecked == true;
        _app.Chat.Network.AllowPublicNetworks = s.AllowPublicNetworks;
        s.NotifyContactOnline = OnlineCheck.IsChecked == true;
        s.DownloadFolder = folder;
        s.AutoAcceptImages = AutoImageCheck.IsChecked == true;
        s.AutoAcceptMaxMb = mb;
        s.StartWithWindows = StartupCheck.IsChecked == true;
        s.StartMinimized = MinimizedCheck.IsChecked == true;
        s.AutoAwayMinutes = away;
        s.ExtraAddresses = string.Join(", ", extras);
        _app.Chat.Network.SetAllowedRemotes(s.ExtraAddresses);
        _app.Chat.UpdateProfile(displayName: name);
        StartupRegistration.Apply(s.StartWithWindows);
        DialogResult = true;
    }
}
