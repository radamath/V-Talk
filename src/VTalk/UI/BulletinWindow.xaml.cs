using System.Windows;

namespace VTalk.UI;

public partial class BulletinWindow : Window
{
    public BulletinWindow(string title, string sender, string text, long timestamp)
    {
        InitializeComponent();
        TitleText.Text = title;
        SenderText.Text = $"{sender} • {DateTimeOffset.FromUnixTimeMilliseconds(timestamp).LocalDateTime:dd.MM.yyyy HH:mm}";
        BodyText.Text = text;
        Loaded += (_, _) =>
        {
            var area = SystemParameters.WorkArea;
            Left = area.Right - ActualWidth - 16;
            Top = area.Bottom - ActualHeight - 16;
        };
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => Close();
}
