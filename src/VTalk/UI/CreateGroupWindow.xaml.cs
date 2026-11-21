using System.Windows;
using System.Windows.Controls;
using VTalk.Core;

namespace VTalk.UI;

public partial class CreateGroupWindow : Window
{
    private readonly AppController _app;
    private readonly List<ContactViewModel> _all;

    public CreateGroupWindow(AppController app, string? preselectPeerId = null)
    {
        InitializeComponent();
        _app = app;
        _all = app.Contacts.Where(c => !c.IsGroupChat && c.IsVerified)
            .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        MemberList.ItemsSource = _all;
        Loaded += (_, _) =>
        {
            if (preselectPeerId != null)
            {
                var pre = _all.FirstOrDefault(c => c.PeerId == preselectPeerId);
                if (pre != null) MemberList.SelectedItems.Add(pre);
            }
            NameBox.Focus();
        };
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var q = FilterBox.Text.Trim();
        MemberList.ItemsSource = q.Length == 0
            ? _all
            : _all.Where(c => c.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase) ||
                              c.User.Contains(q, StringComparison.CurrentCultureIgnoreCase)).ToList();
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        var name = TextLimits.Clean(NameBox.Text, TextLimits.UnitNameMax);
        if (name.Length == 0)
        {
            MessageBox.Show(this, "Grup adı girin.", "V-Talk", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var members = MemberList.SelectedItems.OfType<ContactViewModel>().Select(c => c.PeerId).ToList();
        if (members.Count == 0)
        {
            MessageBox.Show(this, "En az bir kişi seçin.", "V-Talk", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var group = _app.CreateChatGroup(name, members);
        if (group == null)
        {
            MessageBox.Show(this, "Grup oluşturulamadı.", "V-Talk", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
        _app.OpenChat(group.ConversationId);
    }
}
