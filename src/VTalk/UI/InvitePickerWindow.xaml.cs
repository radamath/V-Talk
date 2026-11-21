using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace VTalk.UI;

public partial class InvitePickerWindow : Window
{
    private readonly List<ContactViewModel> _all;
    public ContactViewModel? Selected { get; private set; }

    public InvitePickerWindow(AppController app, IEnumerable<string> excludePeerIds, string? hint = null)
    {
        InitializeComponent();
        var exclude = excludePeerIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        exclude.Add(app.Chat.Identity.PeerId);
        _all = app.Contacts
            .Where(c => !c.IsGroupChat && c.IsVerified && !exclude.Contains(c.PeerId))
            .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        MemberList.ItemsSource = _all;
        if (!string.IsNullOrWhiteSpace(hint)) HintText.Text = hint;
        Loaded += (_, _) => FilterBox.Focus();
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var q = FilterBox.Text.Trim();
        MemberList.ItemsSource = q.Length == 0
            ? _all
            : _all.Where(c => c.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase) ||
                              c.User.Contains(q, StringComparison.CurrentCultureIgnoreCase)).ToList();
    }

    private void MemberList_DoubleClick(object sender, MouseButtonEventArgs e) => Ok_Click(sender, e);

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (MemberList.SelectedItem is not ContactViewModel c)
        {
            MessageBox.Show(this, "Bir kişi seçin.", "V-Talk", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Selected = c;
        DialogResult = true;
    }
}
