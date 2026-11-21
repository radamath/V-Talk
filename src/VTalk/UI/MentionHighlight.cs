using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using VTalk.Core;

namespace VTalk.UI;

/// <summary>emoji:RichTextBox belgesinde @bahsetmeleri boyar; tıklanınca (sürüklemeden) input'a yazar.</summary>
public static class MentionHighlight
{
    private static readonly ConditionalWeakTable<RichTextBox, Box> Busy = new();
    private static long _lastInsertTicks;

    private sealed class Box
    {
        public bool Applying;
        public string? LastKey;
        public Point DownPos;
        public bool DownValid;
    }

    public static readonly DependencyProperty EnableProperty =
        DependencyProperty.RegisterAttached(
            "Enable", typeof(bool), typeof(MentionHighlight),
            new PropertyMetadata(false, OnEnableChanged));

    public static void SetEnable(DependencyObject d, bool value) => d.SetValue(EnableProperty, value);
    public static bool GetEnable(DependencyObject d) => (bool)d.GetValue(EnableProperty);

    private static void OnEnableChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not RichTextBox rtb) return;
        if ((bool)e.NewValue)
        {
            rtb.Loaded += OnLoaded;
            // MouseDown'ı yutma → çok satır seçimi bozuluyordu. Sadece kısa tıklamada ekle.
            rtb.PreviewMouseLeftButtonDown += OnMentionMouseDown;
            rtb.PreviewMouseLeftButtonUp += OnMentionMouseUp;
            rtb.MouseMove += OnMentionMouseMove;
            rtb.MouseLeave += OnMentionMouseLeave;
            rtb.LostMouseCapture += (_, _) => ResetCursor(rtb);
            TryHookTextProperty(rtb);
            Apply(rtb);
        }
        else
        {
            rtb.Loaded -= OnLoaded;
            rtb.PreviewMouseLeftButtonDown -= OnMentionMouseDown;
            rtb.PreviewMouseLeftButtonUp -= OnMentionMouseUp;
            rtb.MouseMove -= OnMentionMouseMove;
            rtb.MouseLeave -= OnMentionMouseLeave;
        }
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is RichTextBox rtb)
            rtb.Dispatcher.BeginInvoke(() => Apply(rtb, force: true), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private static void OnMentionMouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is RichTextBox rtb)
        {
            ResetCursor(rtb);
            Busy.GetOrCreateValue(rtb).DownValid = false;
        }
    }

    private static void ResetCursor(RichTextBox rtb) => rtb.Cursor = Cursors.IBeam;

    private static void OnMentionMouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not RichTextBox rtb) return;
        if (Window.GetWindow(rtb) is not ChatWindow { IsGroupChat: true } chat)
        {
            ResetCursor(rtb);
            return;
        }
        // Seçim sürerken imleç/etiket işleme karışmasın.
        if (rtb.Selection != null && !rtb.Selection.IsEmpty && e.LeftButton == MouseButtonState.Pressed)
        {
            ResetCursor(rtb);
            return;
        }
        var pos = rtb.GetPositionFromPoint(e.GetPosition(rtb), false);
        if (pos == null) { ResetCursor(rtb); return; }
        var token = HitMention(rtb, chat, pos);
        rtb.Cursor = token != null ? Cursors.Arrow : Cursors.IBeam;
    }

    private static void OnMentionMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not RichTextBox rtb) return;
        var box = Busy.GetOrCreateValue(rtb);
        box.DownPos = e.GetPosition(rtb);
        box.DownValid = true;
        // Handled etme — seçim/sürükleme başlayabilsin.
    }

    private static void OnMentionMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not RichTextBox rtb) return;
        if (Window.GetWindow(rtb) is not ChatWindow chat || !chat.IsGroupChat) return;

        var box = Busy.GetOrCreateValue(rtb);
        if (!box.DownValid) return;
        box.DownValid = false;

        // Sürükleyerek seçim yapıldıysa etiket ekleme.
        var up = e.GetPosition(rtb);
        if (Math.Abs(up.X - box.DownPos.X) > 4 || Math.Abs(up.Y - box.DownPos.Y) > 4) return;
        if (rtb.Selection != null && !rtb.Selection.IsEmpty)
        {
            // Gerçek bir seçim aralığı varsa kopyalamaya izin ver; etiket ekleme.
            var sel = rtb.Selection;
            if (sel.Start.CompareTo(sel.End) != 0) return;
        }

        var now = Environment.TickCount64;
        if (now - _lastInsertTicks < 400) return;

        var pos = rtb.GetPositionFromPoint(up, false);
        if (pos == null) return;
        var token = HitMention(rtb, chat, pos);
        if (token == null) return;

        _lastInsertTicks = now;
        e.Handled = true;
        rtb.ReleaseMouseCapture();
        Mouse.Capture(null);
        ResetCursor(rtb);
        chat.InsertMentionToken(token);
    }

    private static string? HitMention(RichTextBox rtb, ChatWindow chat, TextPointer pos)
    {
        var names = chat.MentionDisplayNames();
        var plain = MentionRichText.GetPlainNormalized(rtb);
        var offset = MentionRichText.GetPlainOffset(rtb, pos);
        return MentionRichText.FindMentionAt(plain, offset, names);
    }

    private static void TryHookTextProperty(RichTextBox rtb)
    {
        var type = rtb.GetType();
        var field = type.GetField("TextProperty",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.FlattenHierarchy);
        if (field?.GetValue(null) is not DependencyProperty dp) return;
        var desc = DependencyPropertyDescriptor.FromProperty(dp, type);
        desc?.AddValueChanged(rtb, (_, _) =>
        {
            var box = Busy.GetOrCreateValue(rtb);
            if (box.Applying) return;
            rtb.Dispatcher.BeginInvoke(() => Apply(rtb), System.Windows.Threading.DispatcherPriority.Background);
        });
    }

    private static string ReadText(RichTextBox rtb)
    {
        if (rtb is Emoji.Wpf.RichTextBox emoji) return emoji.Text ?? "";
        return new TextRange(rtb.Document.ContentStart, rtb.Document.ContentEnd).Text ?? "";
    }

    private static void Apply(RichTextBox rtb, bool force = false)
    {
        if (rtb.Document == null) return;
        // Seçim varken belgeyi yeniden kurma — çok satır seçimi silinmesin.
        if (rtb.Selection != null && !rtb.Selection.IsEmpty) return;

        var box = Busy.GetOrCreateValue(rtb);
        if (box.Applying) return;

        IReadOnlyList<string>? names = null;
        if (Window.GetWindow(rtb) is ChatWindow chat && chat.IsGroupChat)
            names = chat.MentionDisplayNames();

        var text = ReadText(rtb);
        var key = text + "\u001f" + (names == null ? "" : string.Join('\u001e', names));
        if (!force && key == box.LastKey && text.Length > 0) return;

        box.Applying = true;
        try
        {
            MentionRichText.FormatDocumentMentions(rtb.Document, names, rebuildInlines: true);
            box.LastKey = key;
        }
        catch (InvalidOperationException ex)
        {
            // Şablon yenilenirken belge değişebilir; bir sonraki düzende yeniden biçimlenir.
            Log.Info("Bahsetme vurgusu uygulanamadı: " + ex.Message);
        }
        finally { box.Applying = false; }
    }
}
