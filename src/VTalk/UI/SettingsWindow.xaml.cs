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
    private string _soundId;
    private string? _customSoundPath;

    public SettingsWindow(AppController app)
    {
        InitializeComponent();
        _app = app;
        var s = app.Chat.Settings;
        NameBox.Text = s.DisplayName;
        SoundCheck.IsChecked = s.SoundEnabled;
        // Eski sürümde yalnızca özel dosya seçilmişse o seçili görünsün (SoundService ile aynı kural).
        var (builtInId, customPath) = SoundService.ResolveMessageSound(s);
        _soundId = customPath != null ? NotificationSounds.CustomId : builtInId;
        _customSoundPath = customPath;
        SoundNameText.Text = SoundPickerWindow.DisplayName(_soundId, _customSoundPath);
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
        var networks = app.Chat.EffectiveNetworks;
        NetworksInfo.Text = "Etkin uzak ağlar: " + (networks.Length > 0 ? networks : "(yok)");

        var chat = app.Chat;
        NetInfo.Text = $"Bu bilgisayar: TCP {chat.TcpPort}, UDP {Net.DiscoveryService.Port}. Güvenlik duvarında bu portlara yerel ağdan izin verilmelidir.";
        SecurityInfo.Text = $"Kimlik:        {chat.Identity.PeerId[..8]}\nParmak izi:    {chat.Identity.Fingerprint}\n" +
                            $"Organizasyon:  {chat.Directory.OrgFingerprint ?? "-"}" +
                            (chat.Directory.IsDeployedTrustRoot ? "\n               (BT tarafından dağıtılmış güven kökü)" : "");
    }

    private void ChooseSound_Click(object sender, RoutedEventArgs e)
    {
        var picker = new SoundPickerWindow(_app.Sounds, _soundId, _customSoundPath) { Owner = this };
        if (picker.ShowDialog() != true) return;
        _soundId = picker.SelectedId;
        _customSoundPath = picker.SelectedCustomPath;
        SoundNameText.Text = SoundPickerWindow.DisplayName(_soundId, _customSoundPath);
    }

    private void TestSound_Click(object sender, RoutedEventArgs e) => _app.Sounds.Preview(_soundId, _customSoundPath);

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
        s.MessageSoundId = _soundId;
        // Hazır ses seçilince eski özel yol temizlenir; aksi halde geriye uyumluluk kuralı onu çalardı.
        s.CustomSoundPath = _soundId == NotificationSounds.CustomId ? _customSoundPath : null;
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
        _app.Chat.RefreshAllowedRemotes();
        _app.Chat.UpdateProfile(displayName: name);
        StartupRegistration.Apply(s.StartWithWindows);
        DialogResult = true;
    }
}
