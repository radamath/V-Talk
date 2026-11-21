using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
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

    public DataTemplate? NewMessagesTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) => (item as MessageViewModel)?.Kind switch
    {
        MessageKind.File => FileTemplate,
        MessageKind.Nudge => NudgeTemplate,
        MessageKind.Announcement => BulletinTemplate,
        MessageKind.System => NewMessagesTemplate ?? TextTemplate,
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
    private readonly List<(string PeerId, string Name)> _mentionCandidates = [];
    private Button NudgeButton = null!;
    private int _mentionAt = -1;
    private bool _mentionSuppress;
    private MessageViewModel? _newMessagesMarker;
    private CrossBubbleSelection? _crossBubbleSelection;
    /// <summary>Yazıyor göstergesi: kişi → göstergenin söneceği an.</summary>
    private readonly Dictionary<string, DateTime> _typing = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _typingTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private static readonly TimeSpan TypingShownFor = TimeSpan.FromSeconds(5);
    private ReplyRef? _replyTo;
    /// <summary>Sağ tık menüsü açılırken tıklanan balon satırının düz metin ofseti (birleşik balonda hangi mesaj).</summary>
    private int _contextOffset;
    private const int MaxOlderPagesForJump = 10;

    public string PeerId { get; }
    public bool IsGroupChat => ChatGroup.IsConversationId(PeerId);

    public ChatWindow(AppController app, string peerId)
    {
        _app = app;
        PeerId = peerId;
        InitializeComponent();
        BuildComposerTools();
        BuildEmojiPicker();
        MessageList.ItemsSource = _messages;
        _typingTimer.Tick += (_, _) => RefreshTyping();
        Closed += (_, _) => _typingTimer.Stop();
        _crossBubbleSelection = new CrossBubbleSelection(this, Scroller, MessageList, InputBox);
        DataObject.AddPastingHandler(InputBox, OnPaste);
        CommandManager.AddPreviewExecutedHandler(InputBox, OnPreviewCommand);
        Activated += (_, _) =>
        {
            NativeMethods.Flash(this, stop: true);
            _app.Chat.SendReadReceipts(PeerId);
            if (_app.GetContact(PeerId) is { } c) c.Unread = 0;
        };
        // Son okunan konum pencere kapanınca / odaktan çıkınca kaydedilir; «Yeni iletiler» bir sonraki açılışta görünür.
        Closed += (_, _) => MarkReadNow();
        Deactivated += (_, _) => MarkReadNow();
        RefreshHeader();
        ReloadHistory();
    }

    public void FocusInput() => Dispatcher.BeginInvoke(() => InputBox.Focus(), DispatcherPriority.Input);

    public void RefreshHeader()
    {
        var c = _app.GetContact(PeerId);
        var name = _app.DisplayName(PeerId);
        var groupEarly = IsGroupChat ? _app.Chat.GetChatGroup(ChatGroup.TryParseGroupId(PeerId)!) : null;
        Title = IsGroupChat
            ? (groupEarly?.Temporary == true ? $"{name} — Geçici sohbet" : $"{name} — Grup")
            : $"{name} — Sohbet";
        PeerName.Text = name;
        if (IsGroupChat)
        {
            var group = groupEarly;
            var members = group?.MemberIds ?? [];
            var online = members.Count(id => id == _app.Chat.Identity.PeerId ||
                                             _app.Chat.Registry.Get(id) is { IsOnline: true });
            PeerStatus.Text = $"({online}/{members.Count} çevrimiçi)";
            PeerStatusDot.Fill = (System.Windows.Media.Brush)new StatusToBrushConverter().Convert(
                online > 1 ? PresenceStatus.Online : PresenceStatus.Offline, typeof(object), null!, null!);
            PeerNote.Text = "";
            PeerRole.Text = group?.Temporary == true
                ? "Geçici sohbet — davetli yalnızca bundan sonraki mesajları görür; çıkarınca oda dağılır"
                : "Grup sohbeti";
            PeerAvatar.Source = null;
            PeerAvatarBorder.Cursor = Cursors.Arrow;
            PeerAvatarBorder.ToolTip = null;
            UnverifiedBar.Visibility = Visibility.Collapsed;
            OfflineBar.Visibility = Visibility.Collapsed;
            NudgeButton.IsEnabled = online > 1;
            InputBox.IsEnabled = true;
            AllowDrop = false;
            InviteButton.Visibility = Visibility.Visible;
            RebuildGuestChips(members, group?.CreatedBy);
            return;
        }
        InviteButton.Visibility = Visibility.Visible;
        var status = c?.Status ?? PresenceStatus.Offline;
        PeerStatus.Text = "(" + StatusNames.ToDisplay(status) + ")";
        PeerStatusDot.Fill = (System.Windows.Media.Brush)new StatusToBrushConverter().Convert(status, typeof(object), null!, null!);
        PeerNote.Text = c?.Note ?? "";
        if (c == null)
        {
            PeerRole.Text = "";
        }
        else
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(c.RoleText)) parts.Add(c.RoleText);
            if (!string.IsNullOrWhiteSpace(c.UnitName)) parts.Add(c.UnitName);
            if (!string.IsNullOrWhiteSpace(c.User)) parts.Add(c.User);
            PeerRole.Text = string.Join(" • ", parts);
        }
        PeerAvatar.Source = c?.Avatar;
        PeerAvatarBorder.Cursor = Cursors.Hand;
        PeerAvatarBorder.ToolTip = "Büyütmek için tıklayın";
        var allowed = _app.Chat.IsPeerAllowed(PeerId);
        var unverified = _app.Chat.Directory.Current != null && _app.Chat.Directory.GetMember(PeerId) == null;
        UnverifiedBar.Visibility = unverified ? Visibility.Visible : Visibility.Collapsed;
        if (!allowed)
            UnverifiedText.Text = "⛔ Bu kişi organizasyon dizininde değil. Admin \"yalnızca dizindeki kişiler\" kuralını etkinleştirdiği için mesaj ve dosya alışverişi kapalı.";
        OfflineBar.Visibility = status == PresenceStatus.Offline && allowed ? Visibility.Visible : Visibility.Collapsed;
        NudgeButton.IsEnabled = status != PresenceStatus.Offline && allowed;
        InputBox.IsEnabled = allowed;
        AllowDrop = allowed;
        GuestPanel.Children.Clear();
    }

    private void RebuildGuestChips(IReadOnlyList<string> members, string? createdBy)
    {
        GuestPanel.Children.Clear();
        var me = _app.Chat.Identity.PeerId;
        var canRemove = string.Equals(createdBy, me, StringComparison.OrdinalIgnoreCase);
        foreach (var id in members.Where(id => !string.Equals(id, me, StringComparison.OrdinalIgnoreCase)))
        {
            var chip = new Border
            {
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xEE, 0xF4, 0xFB)),
                BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xC9, 0xDC, 0xF2)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(6, 1, 2, 1),
                Margin = new Thickness(0, 0, 4, 2),
                ToolTip = _app.DisplayName(id),
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock { Text = _app.DisplayName(id), VerticalAlignment = VerticalAlignment.Center });
            if (canRemove)
            {
                var peerId = id;
                var remove = new Button
                {
                    Content = "✕",
                    Margin = new Thickness(4, 0, 0, 0),
                    Padding = new Thickness(3, 0, 3, 0),
                    Background = System.Windows.Media.Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Cursor = Cursors.Hand,
                    ToolTip = "Sohbetten çıkar",
                    Tag = peerId,
                };
                remove.Click += RemoveGuest_Click;
                row.Children.Add(remove);
            }
            chip.Child = row;
            GuestPanel.Children.Add(chip);
        }
    }

    private void Invite_Click(object sender, RoutedEventArgs e)
    {
        if (IsGroupChat)
        {
            var groupId = ChatGroup.TryParseGroupId(PeerId);
            if (groupId == null) return;
            var group = _app.Chat.GetChatGroup(groupId);
            if (group == null) return;
            var picker = new InvitePickerWindow(_app, group.MemberIds, "Gruba eklenecek kişiyi seçin.") { Owner = this };
            if (picker.ShowDialog() != true || picker.Selected == null) return;
            _app.AddMembersToChatGroup(groupId, [picker.Selected.PeerId]);
            RefreshHeader();
            return;
        }

        // 1:1 → geçici oda (Gruplar'da kalıcı görünmez; "Geçici sohbetler" altında).
        var exclude = new List<string> { PeerId };
        var pick = new InvitePickerWindow(_app, exclude,
            "Bu sohbete geçici olarak dahil edilecek kişiyi seçin. Çıkarınca oda dağılır.")
        { Owner = this };
        if (pick.ShowDialog() != true || pick.Selected == null) return;
        var name = $"{_app.DisplayName(PeerId)}, {_app.DisplayName(pick.Selected.PeerId)}";
        if (name.Length > 64) name = name[..64];
        var created = _app.CreateChatGroup(name, [PeerId, pick.Selected.PeerId], temporary: true);
        if (created == null)
        {
            MessageBox.Show(this, "Sohbete davet edilemedi.", "V-Talk", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _app.OpenChat(created.ConversationId);
    }

    private void RemoveGuest_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string memberId }) return;
        var groupId = ChatGroup.TryParseGroupId(PeerId);
        if (groupId == null) return;
        var label = _app.DisplayName(memberId);
        if (MessageBox.Show(this, $"\"{label}\" sohbetten çıkarılsın mı?", "V-Talk",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var reopen = _app.RemoveMemberFromChatGroup(groupId, memberId);
        if (reopen != null) return; // pencere kapatılıp 1:1 açıldı
        RefreshHeader();
    }

    // ------------------------------------------------------------------ Geçmiş

    public void ReloadHistory()
    {
        _messages.Clear();
        _byRow.Clear();
        _newMessagesMarker = null;
        var lastRead = _app.Chat.Db.GetLastReadRowId(PeerId);
        var history = _app.Chat.Db.LoadHistory(PeerId, PageSize);
        foreach (var m in history) Append(m);
        InsertNewMessagesMarker(lastRead);
        LoadOlderButton.Visibility = history.Count == PageSize ? Visibility.Visible : Visibility.Collapsed;
        if (_newMessagesMarker != null)
            Dispatcher.BeginInvoke(ScrollToNewMessagesMarker, DispatcherPriority.Loaded);
        else
            ScrollToEnd();
    }

    private void ScrollToNewMessagesMarker()
    {
        if (_newMessagesMarker == null) { ScrollToEnd(); return; }
        var container = MessageList.ItemContainerGenerator.ContainerFromItem(_newMessagesMarker) as FrameworkElement;
        if (container == null) { ScrollToEnd(); return; }
        try
        {
            var y = container.TransformToAncestor(Scroller).Transform(new System.Windows.Point(0, 0)).Y;
            Scroller.ScrollToVerticalOffset(Math.Max(0, Scroller.VerticalOffset + y - 24));
        }
        catch
        {
            ScrollToEnd();
        }
    }

    private void InsertNewMessagesMarker(long lastReadRowId)
    {
        RemoveNewMessagesMarker();
        var firstNew = _messages.FirstOrDefault(m => m.MaxRowId > lastReadRowId && m.Kind != MessageKind.System);
        if (firstNew == null || lastReadRowId <= 0 && _messages.All(m => m.IsOutgoing || m.Kind == MessageKind.System))
            return;
        // Hiç okuma kaydı yoksa ve tüm geçmiş ilk açılışsa ayırıcı koyma.
        if (lastReadRowId <= 0) return;

        var idx = _messages.IndexOf(firstNew);
        if (idx < 0) return;
        _newMessagesMarker = CreateNewMessagesMarker();
        _messages.Insert(idx, _newMessagesMarker);
    }

    private static MessageViewModel CreateNewMessagesMarker() =>
        new(new StoredMessage
        {
            RowId = -1,
            PeerId = "",
            MsgId = "new-messages",
            Direction = MessageDirection.Incoming,
            Kind = MessageKind.System,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            State = MessageState.Delivered,
            Data = new MessageData { Text = "Yeni iletiler" },
        }, "") { ShowHeader = false };

    private void RemoveNewMessagesMarker()
    {
        if (_newMessagesMarker == null) return;
        _messages.Remove(_newMessagesMarker);
        _newMessagesMarker = null;
    }

    private void MarkReadNow()
    {
        var localMax = _messages.Where(m => m.MaxRowId > 0).Select(m => m.MaxRowId).DefaultIfEmpty(0).Max();
        var dbMax = _app.Chat.Db.GetMaxMessageRowId(PeerId);
        var max = Math.Max(localMax, dbMax);
        if (max > 0) _app.Chat.Db.SetLastReadRowId(PeerId, max);
        RemoveNewMessagesMarker();
        if (_app.GetContact(PeerId) is { } c) c.Unread = 0;
    }

    private void LoadOlder_Click(object sender, RoutedEventArgs e)
    {
        if (_messages.Count == 0) return;
        var older = _app.Chat.Db.LoadHistory(PeerId, PageSize, _messages[0].RowId);
        for (int i = older.Count - 1; i >= 0; i--)
        {
            var m = older[i];
            if (_byRow.ContainsKey(m.RowId)) continue;
            var vm = CreateVm(m);
            if (m.Kind == MessageKind.Text && _messages.Count > 0 && CanMergeTextBubble(vm, _messages[0]))
            {
                _messages[0].MergePrepend(m);
                _byRow[m.RowId] = _messages[0];
            }
            else
            {
                _messages.Insert(0, vm);
                _byRow[m.RowId] = vm;
            }
        }
        RecomputeHeaders();
        LoadOlderButton.Visibility = older.Count == PageSize ? Visibility.Visible : Visibility.Collapsed;
    }

    private MessageViewModel CreateVm(StoredMessage m)
    {
        var sender = m.Direction == MessageDirection.Outgoing
            ? _app.Chat.Settings.DisplayName
            : IsGroupChat
                ? _app.DisplayName(m.Data.SenderPeerId ?? "")
                : _app.DisplayName(m.PeerId);
        var vm = new MessageViewModel(m, sender);
        if (m.Data.ReplyToSender is { } replySender)
            vm.ReplySenderName = string.Equals(replySender, _app.Chat.Identity.PeerId, StringComparison.OrdinalIgnoreCase)
                ? _app.Chat.Settings.DisplayName
                : _app.DisplayName(replySender);
        return vm;
    }

    private static bool SameHeaderCluster(MessageViewModel a, MessageViewModel b) =>
        a.Kind is not MessageKind.System && b.Kind is not MessageKind.System &&
        a.IsOutgoing == b.IsOutgoing && a.SenderName == b.SenderName &&
        a.Kind is MessageKind.Text or MessageKind.File && b.Kind is MessageKind.Text or MessageKind.File &&
        (b.LocalTime - a.LocalTime) < TimeSpan.FromMinutes(3);

    /// <summary>Enter ile ardışık metinler tek RichTextBox'ta birleşsin → çok satır seçilebilsin.</summary>
    private static bool CanMergeTextBubble(MessageViewModel a, MessageViewModel b) =>
        a.Kind == MessageKind.Text && b.Kind == MessageKind.Text &&
        a.IsOutgoing == b.IsOutgoing &&
        // Yanıt içeren mesaj kendi balonunda durur: alıntı yalnızca balonun ilk mesajına aittir.
        !b.HasReply &&
        string.Equals(a.SenderName, b.SenderName, StringComparison.Ordinal) &&
        (b.LocalTime - a.LocalTime) < TimeSpan.FromMinutes(3);

    private void Append(StoredMessage m)
    {
        if (_byRow.ContainsKey(m.RowId)) return;
        var vm = CreateVm(m);
        if (m.Kind == MessageKind.Text && _messages.Count > 0 && CanMergeTextBubble(_messages[^1], vm))
        {
            _messages[^1].MergeAppend(m);
            _byRow[m.RowId] = _messages[^1];
            RefreshReceipt(_messages[^1]);
            return;
        }
        if (_messages.Count > 0)
            vm.ShowHeader = !SameHeaderCluster(_messages[^1], vm);
        _messages.Add(vm);
        _byRow[m.RowId] = vm;
        RefreshReceipt(vm);
    }

    /// <summary>Birleşik balonda tik, en geride kalan parçanın durumudur (hepsi teslim/okundu olmalı).</summary>
    private void RefreshReceipt(MessageViewModel vm)
    {
        if (!vm.IsOutgoing) return;
        var statuses = vm.Parts.Select(_app.Chat.GetReceiptStatus).ToList();
        if (statuses.Count == 0) return;
        var weakest = statuses.MinBy(s => s.State);
        vm.SetReceipt(weakest with { ReadCount = statuses[^1].ReadCount, Recipients = statuses[^1].Recipients });
    }

    private void RecomputeHeaders()
    {
        for (int i = 0; i < _messages.Count; i++)
            _messages[i].ShowHeader = i == 0 || !SameHeaderCluster(_messages[i - 1], _messages[i]);
        MessageList.Items.Refresh();
    }

    public void AddMessage(StoredMessage m)
    {
        bool atBottom = Scroller.VerticalOffset >= Scroller.ScrollableHeight - 40;
        Append(m);
        if (atBottom || m.Direction == MessageDirection.Outgoing) ScrollToEnd();
        // Pencereye bakarken gelen mesaj okunmuş sayılır.
        if (m.Direction == MessageDirection.Incoming && IsActive) _app.Chat.SendReadReceipts(PeerId);
        // Mesajı gelen kişi artık "yazıyor" değildir.
        if (m.Direction == MessageDirection.Incoming && _typing.Remove((IsGroupChat ? m.Data.SenderPeerId : m.PeerId) ?? ""))
            RefreshTyping();
    }

    public void UpdateMessage(StoredMessage m)
    {
        if (_byRow.TryGetValue(m.RowId, out var vm))
        {
            bool atBottom = Scroller.VerticalOffset >= Scroller.ScrollableHeight - 40;
            vm.Update(m);
            RefreshReceipt(vm);
            if (atBottom) ScrollToEnd();
        }
    }

    /// <summary>Karşı taraf yazıyor: birkaç saniye gösterilir, yeni paket geldikçe uzar, mesajı gelince kalkar.</summary>
    public void ShowTyping(string peerId)
    {
        _typing[peerId] = DateTime.UtcNow + TypingShownFor;
        RefreshTyping();
        _typingTimer.Start();
    }

    private void RefreshTyping()
    {
        var now = DateTime.UtcNow;
        foreach (var expired in _typing.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToList()) _typing.Remove(expired);
        if (_typing.Count == 0)
        {
            _typingTimer.Stop();
            TypingText.Visibility = Visibility.Collapsed;
            return;
        }
        var names = _typing.Keys.Select(id => _app.DisplayName(id)).ToList();
        TypingText.Text = names.Count switch
        {
            1 => $"{names[0]} yazıyor…",
            2 => $"{names[0]} ve {names[1]} yazıyor…",
            _ => "Birkaç kişi yazıyor…",
        };
        TypingText.Visibility = Visibility.Visible;
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
        if (MentionPopup.IsOpen)
        {
            if (e.Key == Key.Escape)
            {
                HideMentionPopup();
                e.Handled = true;
                return;
            }
            if (e.Key is Key.Up or Key.Down or Key.Tab)
            {
                CycleMention(e.Key == Key.Up ? -1 : 1);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
            {
                if (CommitMention()) e.Handled = true;
                return;
            }
        }

        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            SendCurrent();
        }
    }

    private void InputBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // InitializeComponent sırasında Document atanırken de tetiklenir; _app hazır değilse çık.
        if (_mentionSuppress || _app == null) return;
        // Gönderim sonrası kutu boşalınca "yazıyor" gönderilmez; ChatService kendi içinde 3 sn'de bire seyreltir.
        if (MentionRichText.GetPlainNormalized(InputBox).Length > 0) _app.Chat.NotifyTyping(PeerId);
        _mentionSuppress = true;
        try
        {
            // Text/Rebuild her tuşta → Emoji.Wpf + TextChanged sonsuz döngü / CPU spike.
            // Yalnızca mevcut Run'ları yerinde boya; emoji native kalsın.
            if (IsGroupChat)
            {
                var caret = MentionRichText.GetCaretOffset(InputBox);
                var plain = MentionRichText.GetPlainNormalized(InputBox);
                if (plain.Contains('@'))
                {
                    // rebuildInlines:false → Clear/Substitute yok; backspace imleci başa kaçmasın.
                    MentionRichText.FormatDocumentMentions(
                        InputBox.Document, CurrentMentionNames().ToList(), rebuildInlines: false);
                    MentionRichText.SetCaretFromPlainIndex(InputBox, caret);
                }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_mentionSuppress || !IsGroupChat) return;
                    RefreshMentionPopup();
                }), DispatcherPriority.Input);
            }
            else HideMentionPopup();
        }
        finally
        {
            _mentionSuppress = false;
        }
    }

    /// <summary>Mesaj boyama ve @ eşlemesi için grup üye görünen adları.</summary>
    public IReadOnlyList<string> MentionDisplayNames() => CurrentMentionNames().ToList();

    /// <summary>Sohbet geçmişindeki @etikete / isme tıklanınca input'a yazar.</summary>
    public void InsertMentionToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return;
        var insert = token.Trim();
        if (!insert.StartsWith('@')) insert = "@" + insert;
        if (!insert.EndsWith(' ')) insert += " ";
        MentionRichText.InsertAtCaret(InputBox, insert);
        // Focus'u geciktirme — mouse capture / sonraki tıklama kaymasın.
        InputBox.Focus();
    }

    /// <summary>Grup sohbetinde ada sürüklemeden tıklamak etiketler; sürükleyip seçmek (ad seçilebilir metindir) etiketlemez.</summary>
    private void SenderName_Click(object sender, MouseButtonEventArgs e)
    {
        if (!IsGroupChat) return;
        if (sender is not TextBox { DataContext: MessageViewModel vm } nameBox) return;
        if (nameBox.SelectionLength > 0 || _crossBubbleSelection?.HasMultiSelection == true) return;
        var name = (vm.SenderName ?? "").Trim();
        if (name.Length == 0) return;
        e.Handled = true;
        InsertMentionToken("@" + name);
    }

    private IEnumerable<string> CurrentMentionNames()
    {
        yield return "all";
        yield return "herkes";
        yield return "everyone";
        var groupId = ChatGroup.TryParseGroupId(PeerId);
        var group = groupId == null ? null : _app.Chat.GetChatGroup(groupId);
        if (group == null) yield break;
        foreach (var id in group.MemberIds)
        {
            var name = _app.DisplayName(id).Trim();
            if (name.Length > 0) yield return name;
        }
    }

    private void RefreshMentionPopup()
    {
        var plain = MentionRichText.GetPlainNormalized(InputBox);
        var caret = MentionRichText.GetCaretOffset(InputBox);
        var active = Mentions.ActiveQuery(plain, caret);
        if (active == null)
        {
            HideMentionPopup();
            return;
        }
        var (at, query) = active.Value;
        // Tamamlanmış @Ad Soyad + boşluk → listeyi kapat.
        if (query.EndsWith(' '))
        {
            var done = query.TrimEnd();
            if (CurrentMentionNames().Any(n => n.Equals(done, StringComparison.CurrentCultureIgnoreCase)) ||
                Mentions.IsEveryoneAlias(done))
            {
                HideMentionPopup();
                return;
            }
            query = done; // ara boşluklu yazımda filtrelemeye devam
        }
        var groupId = ChatGroup.TryParseGroupId(PeerId);
        var group = groupId == null ? null : _app.Chat.GetChatGroup(groupId);
        if (group == null) { HideMentionPopup(); return; }

        _mentionCandidates.Clear();
        if (query.Length == 0 ||
            "all".StartsWith(query, StringComparison.OrdinalIgnoreCase) ||
            "herkes".StartsWith(query, StringComparison.OrdinalIgnoreCase) ||
            "everyone".StartsWith(query, StringComparison.OrdinalIgnoreCase))
            _mentionCandidates.Add((Mentions.Everyone, "all"));

        foreach (var id in group.MemberIds)
        {
            if (string.Equals(id, _app.Chat.Identity.PeerId, StringComparison.OrdinalIgnoreCase)) continue;
            var name = _app.DisplayName(id).Trim();
            if (name.Length == 0) continue;
            if (query.Length == 0 || name.StartsWith(query, StringComparison.CurrentCultureIgnoreCase))
                _mentionCandidates.Add((id, name));
        }
        var people = _mentionCandidates.Where(c => c.PeerId != Mentions.Everyone)
            .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        var all = _mentionCandidates.Where(c => c.PeerId == Mentions.Everyone).ToList();
        _mentionCandidates.Clear();
        _mentionCandidates.AddRange(all);
        _mentionCandidates.AddRange(people);
        if (_mentionCandidates.Count == 0) { HideMentionPopup(); return; }

        _mentionAt = at;
        MentionList.ItemsSource = _mentionCandidates
            .Select(c => c.PeerId == Mentions.Everyone ? "all — herkes" : c.Name).ToList();
        MentionList.SelectedIndex = 0;
        MentionPopup.IsOpen = true;
    }

    private void CycleMention(int delta)
    {
        if (!MentionPopup.IsOpen || MentionList.Items.Count == 0) return;
        var n = MentionList.Items.Count;
        MentionList.SelectedIndex = ((MentionList.SelectedIndex + delta) % n + n) % n;
        MentionList.ScrollIntoView(MentionList.SelectedItem);
    }

    private bool CommitMention()
    {
        if (!MentionPopup.IsOpen || MentionList.SelectedIndex < 0 || MentionList.SelectedIndex >= _mentionCandidates.Count)
            return false;
        var pick = _mentionCandidates[MentionList.SelectedIndex];
        var token = pick.PeerId == Mentions.Everyone ? "all" : pick.Name;
        var caret = MentionRichText.GetCaretOffset(InputBox);
        if (_mentionAt < 0 || _mentionAt >= caret) return false;
        var at = _mentionAt;
        var names = CurrentMentionNames().ToList();
        HideMentionPopup(); // seçimden önce kapat; Rebuild sırasında listeyi bozma
        _mentionSuppress = true;
        try
        {
            var plain = MentionRichText.GetPlainNormalized(InputBox);
            if (at > plain.Length) return false;
            caret = Math.Clamp(caret, at, plain.Length);
            var before = plain[..at];
            var after = plain[caret..];
            var insert = "@" + token + " ";
            var next = before + insert + after;
            MentionRichText.Rebuild(InputBox, next, before.Length + insert.Length, names);
            MentionRichText.ResetTypingStyle(InputBox);
        }
        finally
        {
            _mentionSuppress = false;
            InputBox.Focus();
        }
        return true;
    }

    private void HideMentionPopup()
    {
        MentionPopup.IsOpen = false;
        _mentionAt = -1;
        _mentionCandidates.Clear();
    }

    private void MentionList_Click(object sender, MouseButtonEventArgs e)
    {
        // Tıklanan satırı seç; MouseUp’ta SelectedIndex bazen gecikir.
        if (e.OriginalSource is DependencyObject src &&
            ItemsControl.ContainerFromElement(MentionList, src) is ListBoxItem item)
        {
            item.IsSelected = true;
            MentionList.SelectedItem = item.Content;
        }
        if (CommitMention()) e.Handled = true;
    }

    private void Send_Click(object sender, RoutedEventArgs e) => SendCurrent();

    private void SendCurrent()
    {
        var text = MentionRichText.GetPlainNormalized(InputBox);
        if (string.IsNullOrWhiteSpace(text)) return;
        if (IsGroupChat)
        {
            var groupId = ChatGroup.TryParseGroupId(PeerId);
            if (groupId != null && _app.Chat.SendGroupText(groupId, text, _replyTo) != null) AfterSend();
        }
        else if (_app.Chat.SendText(PeerId, text, _replyTo) != null) AfterSend();
        InputBox.Focus();
    }

    private void AfterSend()
    {
        MentionRichText.Clear(InputBox);
        SetReply(null);
    }

    // ------------------------------------------------------------------ Yanıtla

    private void Message_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        _contextOffset = 0;
        if (sender is not RichTextBox box) return;
        var pointer = box.GetPositionFromPoint(Mouse.GetPosition(box), snapToText: true);
        if (pointer != null) _contextOffset = MentionRichText.GetPlainOffset(box, pointer);
    }

    private void Reply_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not MessageViewModel vm || vm.Parts.Count == 0) return;
        // Birleşik balonda sağ tıklanan satırın mesajı: parçalar metinde "\n" ile birleşiktir.
        var part = vm.Parts[^1];
        var seen = 0;
        foreach (var p in vm.Parts)
        {
            seen += (p.Data.Text ?? "").Length + 1;
            if (_contextOffset < seen) { part = p; break; }
        }
        var senderId = part.Direction == MessageDirection.Outgoing
            ? _app.Chat.Identity.PeerId
            : IsGroupChat ? part.Data.SenderPeerId ?? "" : part.PeerId;
        var quote = (part.Data.Text ?? "").ReplaceLineEndings(" ");
        SetReply(ReplyRef.TryCreate(part.MsgId, senderId, quote));
        InputBox.Focus();
    }

    private void CancelReply_Click(object sender, RoutedEventArgs e) => SetReply(null);

    private void SetReply(ReplyRef? reply)
    {
        _replyTo = reply;
        if (reply == null)
        {
            ReplyBanner.Visibility = Visibility.Collapsed;
            return;
        }
        ReplyBannerName.Text = "↩ " + (string.Equals(reply.SenderPeerId, _app.Chat.Identity.PeerId, StringComparison.OrdinalIgnoreCase)
            ? _app.Chat.Settings.DisplayName
            : _app.DisplayName(reply.SenderPeerId));
        ReplyBannerText.Text = reply.Text;
        ReplyBanner.Visibility = Visibility.Visible;
    }

    private void ReplyQuote_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is MessageViewModel { ReplyToMsgId: { } target }) JumpToMessage(target);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            new SearchWindow(_app, PeerId) { Owner = this }.Show();
            return;
        }
        base.OnPreviewKeyDown(e);
    }

    /// <summary>Arama sonucunu gösterir: mesaja kaydırır ve eşleşen sözcüğü seçili (vurgulu) yapar.</summary>
    public bool ShowSearchHit(string msgId, string query)
    {
        if (!JumpToMessage(msgId)) return false;
        var vm = _messages.First(m => m.ContainsMsgId(msgId));
        if (MessageList.ItemContainerGenerator.ContainerFromItem(vm) is not DependencyObject container) return true;
        var box = FindDescendant<RichTextBox>(container);
        if (box == null) return true;
        // Birleşik balonda parçalar "\n" ile birleşiktir: eşleşmenin balon metnindeki ofseti.
        var offset = 0;
        foreach (var part in vm.Parts)
        {
            var text = part.Data.Text ?? "";
            if (part.MsgId == msgId)
            {
                var at = Data.Database.MatchIndex(text, query);
                if (at < 0) return true;
                var from = MentionRichText.GetPointerAtPlainOffset(box, offset + at);
                var to = MentionRichText.GetPointerAtPlainOffset(box, offset + at + query.Trim().Length);
                if (from != null && to != null) box.Selection.Select(from, to);
                return true;
            }
            offset += text.Length + 1;
        }
        return true;
    }

    private static T? FindDescendant<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } nested) return nested;
        }
        return null;
    }

    /// <summary>Mesaja kaydırır; yüklü değilse eski sayfaları (sınırlı) yükleyerek arar.</summary>
    public bool JumpToMessage(string msgId)
    {
        for (var page = 0; page <= MaxOlderPagesForJump; page++)
        {
            var vm = _messages.FirstOrDefault(m => m.ContainsMsgId(msgId));
            if (vm != null)
            {
                MessageList.UpdateLayout();
                (MessageList.ItemContainerGenerator.ContainerFromItem(vm) as FrameworkElement)?.BringIntoView();
                return true;
            }
            if (LoadOlderButton.Visibility != Visibility.Visible) break;
            LoadOlder_Click(this, new RoutedEventArgs());
        }
        return false;
    }

    private void Nudge_Click(object sender, RoutedEventArgs e)
    {
        if (IsGroupChat)
        {
            var groupId = ChatGroup.TryParseGroupId(PeerId);
            if (groupId != null && _app.Chat.SendGroupNudge(groupId) != null) Shake();
            return;
        }
        if (_app.Chat.SendNudge(PeerId) != null) Shake();
    }

    private void SendFile_Click(object sender, RoutedEventArgs e) => PickAndSendFile();

    public void PickAndSendFile(bool imagesOnly = false)
    {
        if (IsGroupChat)
        {
            MessageBox.Show(this, "Grup sohbetinde şimdilik yalnızca metin gönderilebilir.", "V-Talk",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
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

    private static readonly string[] FrequentEmojis = ["😀", "😂", "😊", "😉", "😍", "👍", "🙏", "❤️", "✅", "🎉"];

    private static readonly string[] AllEmojis =
    [
        "😀", "😁", "😂", "🤣", "😊",
        "😉", "😍", "😘", "😎", "🤔",
        "😅", "😢", "😭", "😮", "😱",
        "😴", "😡", "🤗", "😇", "🤝",
        "👍", "👎", "👏", "🙏", "💪",
        "❤️", "🔥", "✅", "⭐", "🎉",
        "👋", "💯", "📌", "💬", "☕",
        "🎂", "🎁", "🌹", "👀", "✨",
    ];

    private void BuildComposerTools()
    {
        ComposerTools.Children.Add(MakeToolButton("📎", "Dosya", "Dosya gönder", SendFile_Click));
        ComposerTools.Children.Add(MakeToolButton("🖼", "Resim", "Resim gönder (Ctrl+V ile yapıştırabilirsiniz)", SendImage_Click));
        NudgeButton = MakeToolButton("📳", "Titret", "Titreşim gönder", Nudge_Click);
        ComposerTools.Children.Add(NudgeButton);
    }

    private Button MakeToolButton(string emoji, string label, string tip, RoutedEventHandler click)
    {
        var glyph = new Image
        {
            Width = 16,
            Height = 16,
            Stretch = System.Windows.Media.Stretch.Uniform,
            Margin = new Thickness(0, 0, 5, 0),
            IsHitTestVisible = false,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Emoji.Wpf.Image.SetSource(glyph, emoji);
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(glyph);
        panel.Children.Add(new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var button = new Button
        {
            Content = panel,
            Style = (Style)FindResource("ToolButton"),
            ToolTip = tip,
        };
        button.Click += click;
        return button;
    }

    private void BuildEmojiPicker()
    {
        foreach (var emoji in FrequentEmojis)
            FrequentEmojiPanel.Children.Add(MakeEmojiButton(emoji, closePopup: false));
        foreach (var emoji in AllEmojis)
            AllEmojiGrid.Children.Add(MakeEmojiButton(emoji, closePopup: true));
    }

    private Button MakeEmojiButton(string emoji, bool closePopup)
    {
        var glyph = new Image
        {
            Width = 20,
            Height = 20,
            Stretch = System.Windows.Media.Stretch.Uniform,
            IsHitTestVisible = false,
        };
        Emoji.Wpf.Image.SetSource(glyph, emoji);
        var button = new Button
        {
            Content = glyph,
            Style = (Style)FindResource("EmojiButton"),
            ToolTip = emoji,
        };
        button.Click += (_, _) =>
        {
            InsertEmoji(emoji);
            if (closePopup) EmojiPopup.IsOpen = false;
        };
        return button;
    }

    private void EmojiMore_Click(object sender, RoutedEventArgs e) => EmojiPopup.IsOpen = !EmojiPopup.IsOpen;

    private void InsertEmoji(string emoji)
    {
        if (!InputBox.IsEnabled) return;
        MentionRichText.InsertAtCaret(InputBox, emoji);
        InputBox.Focus();
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

    private void PeerAvatar_Click(object sender, MouseButtonEventArgs e)
    {
        if (IsGroupChat) return;
        System.Windows.Media.ImageSource? src = null;
        var hash = _app.Chat.Registry.Get(PeerId)?.AvatarHash;
        if (!string.IsNullOrEmpty(hash))
            src = ImageLoader.Load(ChatService.AvatarCachePath(hash), 512);
        src ??= PeerAvatar.Source;
        if (src == null) return;

        var preview = new Window
        {
            Title = _app.DisplayName(PeerId),
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = 520,
            Height = 520,
            Background = System.Windows.Media.Brushes.Black,
            ResizeMode = ResizeMode.CanResize,
            Content = new Image { Source = src, Stretch = System.Windows.Media.Stretch.Uniform, Margin = new Thickness(8) },
        };
        preview.MouseLeftButtonUp += (_, _) => preview.Close();
        preview.KeyDown += (_, ke) => { if (ke.Key == Key.Escape) preview.Close(); };
        preview.ShowDialog();
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
