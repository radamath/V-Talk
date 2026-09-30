using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using VTalk.Core;
using VTalk.Services;

namespace VTalk.UI;

public sealed class MessageTemplateSelector : DataTemplateSelector
{
    public DataTemplate? TextTemplate { get; set; }
    public DataTemplate? FileTemplate { get; set; }
    public DataTemplate? NudgeTemplate { get; set; }
    public DataTemplate? BulletinTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) => (item as MessageViewModel)?.Kind switch
    {
        MessageKind.File => FileTemplate,
        MessageKind.Nudge => NudgeTemplate,
        MessageKind.Announcement => BulletinTemplate,
        _ => TextTemplate,
    };
}

public partial class ChatWindow : Window
{
    private const int PageSize = 200;
    private const int MaxFilesPerDrop = 20;

    private readonly AppController _app;
    private readonly ObservableCollection<MessageViewModel> _messages = [];
    private readonly Dictionary<long, MessageViewModel> _byRow = new();

    public string PeerId { get; }

    public ChatWindow(AppController app, string peerId)
    {
        InitializeComponent();
        _app = app;
        PeerId = peerId;
        MessageList.ItemsSource = _messages;
        DataObject.AddPastingHandler(InputBox, OnPaste);
        CommandManager.AddPreviewExecutedHandler(InputBox, OnPreviewCommand);
        Activated += (_, _) =>
        {
            NativeMethods.Flash(this, stop: true);
            if (_app.GetContact(PeerId) is { } c) c.Unread = 0;
        };
        RefreshHeader();
        ReloadHistory();
    }

    public void FocusInput() => Dispatcher.BeginInvoke(() => InputBox.Focus(), DispatcherPriority.Input);

    public void RefreshHeader()
    {
        var c = _app.GetContact(PeerId);
        var name = _app.DisplayName(PeerId);
        Title = $"{name} — Sohbet";
        PeerName.Text = name;
        var status = c?.Status ?? PresenceStatus.Offline;
        PeerStatus.Text = "(" + StatusNames.ToDisplay(status) + ")";
        PeerStatusDot.Fill = (System.Windows.Media.Brush)new StatusToBrushConverter().Convert(status, typeof(object), null!, null!);
        PeerNote.Text = c?.Note ?? "";
        PeerRole.Text = c == null ? "" : c.RoleText + (c.UnitName != null ? " • " + c.UnitName : "") + $" • {c.User}@{c.Machine}";
        PeerAvatar.Source = c?.Avatar;
        var allowed = _app.Chat.IsPeerAllowed(PeerId);
        var unverified = _app.Chat.Directory.Current != null && _app.Chat.Directory.GetMember(PeerId) == null;
        UnverifiedBar.Visibility = unverified ? Visibility.Visible : Visibility.Collapsed;
        if (!allowed)
            UnverifiedText.Text = "⛔ Bu kişi organizasyon dizininde değil. Admin \"yalnızca dizindeki kişiler\" kuralını etkinleştirdiği için mesaj ve dosya alışverişi kapalı.";
        OfflineBar.Visibility = status == PresenceStatus.Offline && allowed ? Visibility.Visible : Visibility.Collapsed;
        NudgeButton.IsEnabled = status != PresenceStatus.Offline && allowed;
        InputBox.IsEnabled = allowed;
        AllowDrop = allowed;
    }

    // ------------------------------------------------------------------ Geçmiş

    public void ReloadHistory()
    {
        _messages.Clear();
        _byRow.Clear();
        var history = _app.Chat.Db.LoadHistory(PeerId, PageSize);
        foreach (var m in history) Append(m);
        LoadOlderButton.Visibility = history.Count == PageSize ? Visibility.Visible : Visibility.Collapsed;
        ScrollToEnd();
    }

    private void LoadOlder_Click(object sender, RoutedEventArgs e)
    {
        if (_messages.Count == 0) return;
        var older = _app.Chat.Db.LoadHistory(PeerId, PageSize, _messages[0].RowId);
        for (int i = older.Count - 1; i >= 0; i--)
        {
            var vm = CreateVm(older[i]);
            _messages.Insert(0, vm);
            _byRow[vm.RowId] = vm;
        }
        RecomputeHeaders();
        LoadOlderButton.Visibility = older.Count == PageSize ? Visibility.Visible : Visibility.Collapsed;
    }

    private MessageViewModel CreateVm(StoredMessage m) =>
        new(m, m.Direction == MessageDirection.Outgoing ? _app.Chat.Settings.DisplayName : _app.DisplayName(m.PeerId));

    private void Append(StoredMessage m)
    {
        if (_byRow.ContainsKey(m.RowId)) return;
        var vm = CreateVm(m);
        if (_messages.Count > 0)
        {
            var prev = _messages[^1];
            vm.ShowHeader = !(prev.IsOutgoing == vm.IsOutgoing && prev.Kind is MessageKind.Text or MessageKind.File &&
                              (vm.LocalTime - prev.LocalTime) < TimeSpan.FromMinutes(3));
        }
        _messages.Add(vm);
        _byRow[vm.RowId] = vm;
    }

    private void RecomputeHeaders()
    {
        for (int i = 0; i < _messages.Count; i++)
        {
            var vm = _messages[i];
            vm.ShowHeader = i == 0 || !(_messages[i - 1].IsOutgoing == vm.IsOutgoing &&
                                        _messages[i - 1].Kind is MessageKind.Text or MessageKind.File &&
                                        (vm.LocalTime - _messages[i - 1].LocalTime) < TimeSpan.FromMinutes(3));
        }
        MessageList.Items.Refresh();
    }

    public void AddMessage(StoredMessage m)
    {
        bool atBottom = Scroller.VerticalOffset >= Scroller.ScrollableHeight - 40;
        Append(m);
        if (atBottom || m.Direction == MessageDirection.Outgoing) ScrollToEnd();
    }

    public void UpdateMessage(StoredMessage m)
    {
        if (_byRow.TryGetValue(m.RowId, out var vm))
        {
            bool atBottom = Scroller.VerticalOffset >= Scroller.ScrollableHeight - 40;
            vm.Update(m);
            if (atBottom) ScrollToEnd();
        }
    }

    public void UpdateProgress(string msgId, long done, long total)
    {
        foreach (var vm in _messages)
        {
            if (vm.MsgId == msgId) vm.SetProgress(done, total);
        }
    }

    private void ScrollToEnd() => Dispatcher.BeginInvoke(() => Scroller.ScrollToEnd(), DispatcherPriority.Background);

    // ------------------------------------------------------------------ Gönderme

    private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            SendCurrent();
        }
    }

    private void Send_Click(object sender, RoutedEventArgs e) => SendCurrent();

    private void SendCurrent()
    {
        var text = InputBox.Text;
        if (string.IsNullOrWhiteSpace(text)) return;
        if (_app.Chat.SendText(PeerId, text) != null) InputBox.Clear();
        InputBox.Focus();
    }

    private void Nudge_Click(object sender, RoutedEventArgs e)
    {
        if (_app.Chat.SendNudge(PeerId) != null) Shake();
    }

    private void SendFile_Click(object sender, RoutedEventArgs e) => PickAndSendFile();

    public void PickAndSendFile(bool imagesOnly = false)
    {
        var dlg = new OpenFileDialog
        {
            Title = imagesOnly ? "Gönderilecek resmi seçin" : "Gönderilecek dosyayı seçin",
            Multiselect = true,
            Filter = imagesOnly ? "Resimler|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.tif;*.tiff" : "Tüm dosyalar|*.*",
        };
        if (dlg.ShowDialog(this) == true) _ = SendFilesAsync(dlg.FileNames);
    }

    private void SendImage_Click(object sender, RoutedEventArgs e) => PickAndSendFile(imagesOnly: true);

    private async Task SendFilesAsync(IEnumerable<string> paths)
    {
        foreach (var path in paths.Take(MaxFilesPerDrop))
        {
            try
            {
                if (Directory.Exists(path))
                {
                    MessageBox.Show(this, "Klasörler gönderilemez. Lütfen klasörü sıkıştırıp (.zip) gönderin.", "V-Talk",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    continue;
                }
                await _app.Chat.SendFileAsync(PeerId, path);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"{Path.GetFileName(path)} gönderilemedi: {ex.Message}", "V-Talk", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files) _ = SendFilesAsync(files);
    }

    private void OnPreviewCommand(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.Command != ApplicationCommands.Paste) return;
        if (Clipboard.ContainsFileDropList())
        {
            e.Handled = true;
            _ = SendFilesAsync(Clipboard.GetFileDropList().Cast<string>().ToList());
        }
        else if (Clipboard.ContainsImage())
        {
            e.Handled = true;
            PasteImage();
        }
    }

    private void OnPaste(object sender, DataObjectPastingEventArgs e)
    {
        // Metin yapıştırmada biçimlendirme alınmaz; yalnızca düz metin.
        if (!e.DataObject.GetDataPresent(DataFormats.UnicodeText)) e.CancelCommand();
    }

    private void PasteImage()
    {
        try
        {
            var image = Clipboard.GetImage();
            if (image == null) return;
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            var path = Path.Combine(AppPaths.SentImagesDir, $"Ekran görüntüsü {DateTime.Now:yyyy-MM-dd HH.mm.ss}.png");
            using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write)) encoder.Save(fs);
            _ = SendFilesAsync([path]);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Pano resmi gönderilemedi: " + ex.Message, "V-Talk", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ------------------------------------------------------------------ Dosya işlemleri

    private static MessageViewModel? Vm(object sender) => (sender as FrameworkElement)?.DataContext as MessageViewModel;

    private void Accept_Click(object sender, RoutedEventArgs e)
    {
        if (Vm(sender) is { } vm) _app.Chat.Files.Accept(vm.Model);
    }

    private void Reject_Click(object sender, RoutedEventArgs e)
    {
        if (Vm(sender) is { } vm) _app.Chat.Files.Reject(vm.Model);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (Vm(sender) is { } vm) _app.Chat.Files.Cancel(vm.Model);
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (Vm(sender) is { } vm) OpenFile(vm);
    }

    private void Image_Click(object sender, MouseButtonEventArgs e)
    {
        if (Vm(sender) is { } vm) OpenFile(vm);
    }

    private void OpenFile(MessageViewModel vm)
    {
        var path = vm.FilePath;
        if (path == null || !File.Exists(path)) return;
        if (!vm.IsOutgoing && FileTransferService.NeedsOpenWarning(path))
        {
            var ext = Path.GetExtension(path);
            var sender = _app.DisplayName(PeerId);
            if (MessageBox.Show(this,
                    $"Dosya: {Path.GetFileName(path)}\nGerçek dosya türü: {(ext.Length > 0 ? ext.ToUpperInvariant() : "(uzantısız)")}\nGönderen: {sender}\n\n" +
                    "Bu dosya türü program çalıştırabilir veya bilgisayarınıza zarar verebilir.\n" +
                    "Yalnızca gönderene güveniyor ve bu dosyayı bekliyorsanız açın. Açmak istiyor musunuz?",
                    "Güvenlik uyarısı", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Dosya açılamadı: " + ex.Message, "V-Talk", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ShowInFolder_Click(object sender, RoutedEventArgs e)
    {
        if (Vm(sender)?.FilePath is not { } path || !File.Exists(path)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
    }

    // ------------------------------------------------------------------ Titreşim

    public void Shake()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        if (!IsVisible) Show();
        var originLeft = Left;
        var originTop = Top;
        int step = 0;
        var rnd = new Random();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
        timer.Tick += (_, _) =>
        {
            if (++step > 24)
            {
                timer.Stop();
                Left = originLeft;
                Top = originTop;
                return;
            }
            Left = originLeft + rnd.Next(-9, 10);
            Top = originTop + rnd.Next(-6, 7);
        };
        timer.Start();
    }
}
