using System.Security.Cryptography;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using VTalk.Core;
using VTalk.Security;

namespace VTalk.UI;

public partial class AdminLoginWindow : Window
{
    private enum Mode { Create, Login, Import }

    private readonly AppController _app;
    private Mode _mode;

    /// <summary>Başarılı girişte kilidi açılmış organizasyon anahtarı. Panel kapanınca bellekten silinir.</summary>
    public ECDsa? UnlockedKey { get; private set; }

    public AdminLoginWindow(AppController app)
    {
        InitializeComponent();
        _app = app;
        var pinned = app.Chat.Directory.PinnedOrgKey;
        if (AdminKeyStore.HasLocalKey) SetMode(Mode.Login);
        else if (pinned == null) SetMode(Mode.Create);
        else SetMode(Mode.Import);
        Loaded += (_, _) => PasswordBox.Focus();
    }

    private void SetMode(Mode mode)
    {
        _mode = mode;
        ImportPanel.Visibility = mode == Mode.Import ? Visibility.Visible : Visibility.Collapsed;
        ConfirmPanel.Visibility = mode == Mode.Create ? Visibility.Visible : Visibility.Collapsed;
        SwitchImportButton.Visibility = mode == Mode.Login ? Visibility.Visible : Visibility.Collapsed;
        var fp = _app.Chat.Directory.OrgFingerprint;
        switch (mode)
        {
            case Mode.Create:
                TitleText.Text = "Organizasyonu oluştur";
                InfoText.Text = "Ağda henüz bir organizasyon (admin) yok. Bir admin parolası belirleyerek organizasyon imzalama anahtarını " +
                                "bu bilgisayarda oluşturacaksınız. Rol ve birim atamaları bu anahtarla imzalanır; diğer kullanıcılar " +
                                "yalnızca bu anahtarla imzalanmış yetkileri kabul eder. Siz otomatik olarak Admin olursunuz.";
                OkButton.Content = "Oluştur ve giriş yap";
                break;
            case Mode.Login:
                TitleText.Text = "Admin girişi";
                InfoText.Text = "Yetkilendirme paneline erişmek için admin parolasını girin." +
                                (fp != null ? $"\nOrganizasyon parmak izi: {fp}" : "");
                OkButton.Content = "Giriş yap";
                break;
            case Mode.Import:
                TitleText.Text = "Yönetici anahtarını içe aktar";
                InfoText.Text = "Bu bilgisayarda yönetici anahtarı bulunmuyor. Organizasyonu oluşturan admin bilgisayarından " +
                                "\"Admin anahtarını dışa aktar\" ile alınan dosyayı ve parolasını girin." +
                                (fp != null ? $"\nGüvenilen organizasyon: {fp}" : "");
                OkButton.Content = "İçe aktar ve giriş yap";
                break;
        }
        ErrorText.Visibility = Visibility.Collapsed;
    }

    private void SwitchImport_Click(object sender, RoutedEventArgs e) => SetMode(Mode.Import);

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "Yönetici anahtar dosyası", Filter = "V-Talk anahtarı|*.vtkey|Tüm dosyalar|*.*" };
        if (dlg.ShowDialog(this) == true) KeyFileBox.Text = dlg.FileName;
    }

    private void PasswordBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _mode != Mode.Create) Ok_Click(sender, e);
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private async void Ok_Click(object sender, RoutedEventArgs e)
    {
        var password = PasswordBox.Password;
        if (password.Length == 0)
        {
            ShowError("Parola girin.");
            return;
        }

        IsEnabled = false;
        Cursor = Cursors.Wait;
        try
        {
            var directory = _app.Chat.Directory;
            switch (_mode)
            {
                case Mode.Create:
                {
                    if (password != ConfirmBox.Password) throw new InvalidOperationException("Parolalar eşleşmiyor.");
                    var strength = AdminKeyStore.ValidatePasswordStrength(password);
                    if (strength != null) throw new InvalidOperationException(strength);
                    var key = await Task.Run(() => AdminKeyStore.CreateOrganizationKey(password));
                    directory.PinOrgKey(key.ExportSubjectPublicKeyInfo());
                    var chat = _app.Chat;
                    directory.SignAndApply(new OrgDirectory
                    {
                        Members =
                        [
                            new OrgMember { PeerId = chat.Identity.PeerId, Name = chat.Settings.DisplayName, Role = Role.Admin },
                        ],
                    }, key);
                    UnlockedKey = key;
                    break;
                }
                case Mode.Login:
                {
                    var localPub = AdminKeyStore.LocalPublicKey();
                    if (directory.PinnedOrgKey != null && localPub != null &&
                        !CryptographicOperations.FixedTimeEquals(localPub, directory.PinnedOrgKey))
                        throw new InvalidOperationException("Bu bilgisayardaki yönetici anahtarı, güvenilen organizasyona ait değil.");
                    var key = await Task.Run(() => AdminKeyStore.Unlock(password));
                    if (directory.PinnedOrgKey == null) directory.PinOrgKey(key.ExportSubjectPublicKeyInfo());
                    UnlockedKey = key;
                    break;
                }
                case Mode.Import:
                {
                    if (string.IsNullOrEmpty(KeyFileBox.Text)) throw new InvalidOperationException("Anahtar dosyasını seçin.");
                    var file = KeyFileBox.Text;
                    var pinned = directory.PinnedOrgKey;
                    await Task.Run(() => AdminKeyStore.Import(file, password, pinned));
                    var key = await Task.Run(() => AdminKeyStore.Unlock(password));
                    if (pinned == null) directory.PinOrgKey(key.ExportSubjectPublicKeyInfo());
                    UnlockedKey = key;
                    break;
                }
            }
            Log.Info("Admin girişi yapıldı.");
            DialogResult = true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or CryptographicException or System.IO.IOException)
        {
            ShowError(ex is CryptographicException ? "Parola hatalı veya anahtar dosyası bozuk." : ex.Message);
            PasswordBox.Clear();
            PasswordBox.Focus();
        }
        finally
        {
            IsEnabled = true;
            Cursor = null;
        }
    }
}
