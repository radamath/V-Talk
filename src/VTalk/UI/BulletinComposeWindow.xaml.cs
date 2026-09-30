using System.Windows;
using VTalk.Services;

namespace VTalk.UI;

public partial class BulletinComposeWindow : Window
{
    private readonly AppController _app;

    public BulletinComposeWindow(AppController app)
    {
        InitializeComponent();
        _app = app;
        var chat = app.Chat;
        var unit = chat.Directory.GetUnitName(chat.Directory.GetMember(chat.Identity.PeerId)?.UnitId);
        ScopeUnit.Content = $"Birim duyurusu — {unit ?? "birim atanmamış"}";
        ScopeAll.IsEnabled = chat.CanSendBulletin(ChatService.ScopeAll);
        ScopeUnit.IsEnabled = chat.CanSendBulletin(ChatService.ScopeUnit);
        if (ScopeUnit.IsEnabled) ScopeUnit.IsChecked = true;
        else ScopeAll.IsChecked = true;
        Loaded += (_, _) => TextBox.Focus();
    }

    private void Send_Click(object sender, RoutedEventArgs e)
    {
        var scope = ScopeAll.IsChecked == true ? ChatService.ScopeAll : ChatService.ScopeUnit;
        if (string.IsNullOrWhiteSpace(TextBox.Text)) return;
        try
        {
            var count = _app.Chat.SendBulletin(scope, TextBox.Text);
            MessageBox.Show(this, $"Duyuru {count} kişiye gönderildi. Çevrimdışı olanlara çevrimiçi olduklarında iletilecek.",
                "V-Talk", MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
        }
        catch (UnauthorizedAccessException ex)
        {
            MessageBox.Show(this, ex.Message, "V-Talk", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
