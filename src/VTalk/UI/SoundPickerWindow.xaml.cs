using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using VTalk.Services;

namespace VTalk.UI;

/// <summary>Yeni mesaj sesini hazır seslerden veya kullanıcının kendi .wav dosyasından seçtirir; seçilen ses çalınarak dinletilir.</summary>
public partial class SoundPickerWindow : Window
{
    public sealed record SoundOption(string Id, string Name, string Description, string? CustomPath);

    private readonly SoundService _sounds;
    private readonly ObservableCollection<SoundOption> _options = [];
    private bool _initializing = true;

    public string SelectedId { get; private set; }
    public string? SelectedCustomPath { get; private set; }

    public SoundPickerWindow(SoundService sounds, string currentId, string? currentCustomPath)
    {
        InitializeComponent();
        _sounds = sounds;
        foreach (var s in NotificationSounds.All) _options.Add(new SoundOption(s.Id, s.Name, s.Description, null));
        if (currentId == NotificationSounds.CustomId && !string.IsNullOrWhiteSpace(currentCustomPath))
            _options.Add(CustomOption(currentCustomPath));
        SoundList.ItemsSource = _options;

        SelectedId = currentId;
        SelectedCustomPath = currentCustomPath;
        SoundList.SelectedItem = _options.FirstOrDefault(o => o.Id == currentId) ?? _options[0];
        SoundList.ScrollIntoView(SoundList.SelectedItem);
        _initializing = false;
    }

    private static SoundOption CustomOption(string path) =>
        new(NotificationSounds.CustomId, "Özel: " + Path.GetFileNameWithoutExtension(path), path, path);

    private void SoundList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Pencere açılırken mevcut seçimi çalma; kullanıcı her seçimde sesi duysun.
        if (!_initializing && SoundList.SelectedItem is SoundOption option) Play(option);
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SoundOption option) return;
        // Başka bir satırın düğmesiyse seçim değişir ve SelectionChanged çalar; aynı satırsa burada çal.
        if (ReferenceEquals(SoundList.SelectedItem, option)) Play(option);
        else SoundList.SelectedItem = option;
    }

    private void Play(SoundOption option) => _sounds.Preview(option.Id, option.CustomPath);

    private void PickCustom_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "Bildirim sesi (.wav)", Filter = "WAV ses dosyası|*.wav" };
        if (dlg.ShowDialog(this) != true) return;
        if (!SoundService.TryLoadPlayableWav(dlg.FileName, out _, out var error))
        {
            MessageBox.Show(this, error, "V-Talk", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var existing = _options.FirstOrDefault(o => o.Id == NotificationSounds.CustomId);
        if (existing != null) _options.Remove(existing);
        var custom = CustomOption(dlg.FileName);
        _options.Add(custom);
        SoundList.SelectedItem = custom;
        SoundList.ScrollIntoView(custom);
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (SoundList.SelectedItem is not SoundOption option) return;
        SelectedId = option.Id;
        SelectedCustomPath = option.CustomPath;
        DialogResult = true;
    }

    /// <summary>Ayarlar penceresinde gösterilecek ad.</summary>
    public static string DisplayName(string id, string? customPath) =>
        id == NotificationSounds.CustomId && !string.IsNullOrWhiteSpace(customPath)
            ? "Özel: " + Path.GetFileName(customPath)
            : NotificationSounds.Find(id).Name;
}
