using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using VTalk.Core;

namespace VTalk.UI;

/// <summary>Sohbet penceresinde seçime katılan metin kutularını işaretler (TextBox veya RichTextBox).</summary>
public static class ChatSelection
{
    public static readonly DependencyProperty SelectableProperty =
        DependencyProperty.RegisterAttached("Selectable", typeof(bool), typeof(ChatSelection), new PropertyMetadata(false));

    public static void SetSelectable(DependencyObject d, bool value) => d.SetValue(SelectableProperty, value);
    public static bool GetSelectable(DependencyObject d) => (bool)d.GetValue(SelectableProperty);
}

/// <summary>
/// Sohbet penceresinin tamamında belge gibi seçim: ad, saat, mesaj, teslim durumu, dosya bilgisi gibi ayrı metin
/// kutuları arasında sürükleyerek seçilir; boşluktan başlayan sürükleme en yakın metinden başlar. Ctrl+A tüm sohbeti
/// seçer, Ctrl+C birleşik düz metin kopyalar (aynı satırdaki parçalar boşlukla, satırlar satır sonuyla ayrılır).
/// </summary>
internal sealed class CrossBubbleSelection : IDisposable
{
    /// <summary>Bu kadar dikey farkın altındaki iki parça aynı satırda sayılır (ad ve saat gibi).</summary>
    private const double SameLineTolerance = 6;
    private const string SameLineSeparator = "  ";
    /// <summary>Sürükleme pencerenin üst/alt kenarını aşınca her harekette kaydırılan miktar.</summary>
    private const double AutoScrollStep = 24;

    private readonly Window _window;
    private readonly ScrollViewer _scroller;
    private readonly ItemsControl _messageList;
    private readonly RichTextBox _inputBox;
    private readonly CommandBinding _copyBinding;

    private bool _armed;
    private bool _multi;
    private bool _anchorDirect;
    private Segment? _anchor;
    private int _anchorOffset;
    private bool _disposed;

    public CrossBubbleSelection(Window window, ScrollViewer scroller, ItemsControl messageList, RichTextBox inputBox)
    {
        _window = window;
        _scroller = scroller;
        _messageList = messageList;
        _inputBox = inputBox;

        _scroller.PreviewMouseLeftButtonDown += OnDown;
        _scroller.PreviewMouseMove += OnMove;
        _scroller.PreviewMouseLeftButtonUp += OnUp;
        _scroller.LostMouseCapture += OnLostCapture;
        _scroller.AddHandler(TextBoxBase.SelectionChangedEvent, new RoutedEventHandler(OnSelectionChanged));
        _window.PreviewKeyDown += OnPreviewKeyDown;
        _copyBinding = new CommandBinding(ApplicationCommands.Copy, OnCopyExecuted, OnCopyCanExecute);
        _window.CommandBindings.Add(_copyBinding);
        _window.Closed += OnWindowClosed;
    }

    /// <summary>Birden fazla parçaya yayılan bir seçim var mı (ad tıklamasıyla etiketlemeyi ayırt etmek için).</summary>
    public bool HasMultiSelection => _multi && CollectSegments().Count(s => s.HasSelection) > 1;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _scroller.PreviewMouseLeftButtonDown -= OnDown;
        _scroller.PreviewMouseMove -= OnMove;
        _scroller.PreviewMouseLeftButtonUp -= OnUp;
        _scroller.LostMouseCapture -= OnLostCapture;
        _scroller.RemoveHandler(TextBoxBase.SelectionChangedEvent, new RoutedEventHandler(OnSelectionChanged));
        _window.PreviewKeyDown -= OnPreviewKeyDown;
        _window.Closed -= OnWindowClosed;
        _window.CommandBindings.Remove(_copyBinding);
        EndDrag();
    }

    private void OnWindowClosed(object? sender, EventArgs e) => Dispose();

    private void OnLostCapture(object sender, MouseEventArgs e) => EndDrag();

    /// <summary>Hangi yolla değişirse değişsin (fare, Ctrl+A, balonlar arası sürükleme) seçimi odaktan bağımsız boya.</summary>
    private void OnSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement box && ChatSelection.GetSelectable(box) && !ReferenceEquals(box, _inputBox))
            SelectionHighlight.Refresh(box);
    }

    // ------------------------------------------------------------------ Fare

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.ClickCount > 1) return;
        var source = e.OriginalSource as DependencyObject;
        // Kaydırma çubuğu, düğme ve yazma alanı kendi davranışını korur.
        if (IsUnder(_inputBox, source) || HasAncestor<ScrollBar>(source) || HasAncestor<ButtonBase>(source)) return;

        ClearAll();
        _multi = false;
        _armed = false;
        _anchor = null;

        var segments = CollectSegments();
        if (segments.Count == 0) return;

        var point = e.GetPosition(_scroller);
        var direct = HitDirect(segments, point);
        var hit = direct ?? HitNearest(segments, point);
        if (hit == null) return;

        _anchor = hit.Value.Segment;
        _anchorOffset = hit.Value.Offset;
        _anchorDirect = direct != null;
        _armed = true;
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (!_armed || _anchor == null || e.LeftButton != MouseButtonState.Pressed) return;

        var segments = CollectSegments();
        var anchorIndex = IndexOf(segments, _anchor.Element);
        if (anchorIndex < 0)
        {
            EndDrag();
            return;
        }
        var anchor = segments[anchorIndex];

        var point = e.GetPosition(_scroller);
        AutoScroll(point);
        var hit = HitDirect(segments, point) ?? HitNearest(segments, point);
        if (hit == null) return;

        // Aynı kutu içindeki sürüklemeyi kutunun kendi seçimine bırak (çift tıklama, kelime seçimi vb. korunur).
        if (!_multi && _anchorDirect && ReferenceEquals(hit.Value.Segment.Element, anchor.Element)) return;

        _multi = true;
        if (!ReferenceEquals(Mouse.Captured, _scroller)) _scroller.CaptureMouse();
        ApplyRange(segments, anchorIndex, _anchorOffset, IndexOf(segments, hit.Value.Segment.Element), hit.Value.Offset);
        e.Handled = true;
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        EndDrag();
    }

    private void EndDrag()
    {
        _armed = false;
        if (ReferenceEquals(Mouse.Captured, _scroller)) _scroller.ReleaseMouseCapture();
    }

    private void AutoScroll(Point point)
    {
        if (point.Y < 0) _scroller.ScrollToVerticalOffset(_scroller.VerticalOffset - AutoScrollStep);
        else if (point.Y > _scroller.ActualHeight) _scroller.ScrollToVerticalOffset(_scroller.VerticalOffset + AutoScrollStep);
    }

    // ------------------------------------------------------------------ Klavye ve kopyalama

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        if (IsUnder(_inputBox, e.OriginalSource as DependencyObject)) return;

        if (e.Key == Key.A)
        {
            SelectAll();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.C && TryCopyMulti()) e.Handled = true;
    }

    private void OnCopyCanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        if (IsUnder(_inputBox, Keyboard.FocusedElement as DependencyObject)) return;
        if (CollectSegments().Count(s => s.HasSelection) > 1)
        {
            e.CanExecute = true;
            e.Handled = true;
        }
    }

    private void OnCopyExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        if (IsUnder(_inputBox, Keyboard.FocusedElement as DependencyObject)) return;
        if (TryCopyMulti()) e.Handled = true;
    }

    /// <summary>Birden fazla parçada seçim varsa birleşik metni panoya koyar; tek parçada kutunun kendi kopyalaması çalışır.</summary>
    private bool TryCopyMulti()
    {
        var text = BuildSelectedText(CollectSegments());
        if (text == null) return false;
        Clipboard.SetText(text);
        return true;
    }

    private string? BuildSelectedText(IReadOnlyList<Segment> segments)
    {
        var selected = segments.Where(s => s.HasSelection).ToList();
        if (selected.Count < 2) return null;
        var parts = new List<(Segment Segment, string Text)>();
        foreach (var segment in selected)
        {
            var text = segment.SelectedText;
            if (text.Length > 0) parts.Add((segment, text));
        }
        if (parts.Count == 0) return null;

        var sb = new System.Text.StringBuilder(parts[0].Text);
        for (var i = 1; i < parts.Count; i++)
        {
            sb.Append(parts[i].Segment.Line == parts[i - 1].Segment.Line ? SameLineSeparator : "\n");
            sb.Append(parts[i].Text);
        }
        return sb.ToString();
    }

    private void SelectAll()
    {
        var segments = CollectSegments();
        if (segments.Count == 0) return;
        foreach (var s in segments) s.Select(0, s.Length);
        _multi = true;
    }

    private void ClearAll()
    {
        foreach (var s in CollectSegments()) s.Clear();
    }

    private static void ApplyRange(IReadOnlyList<Segment> segments, int anchorIndex, int anchorOffset, int currentIndex, int currentOffset)
    {
        if (anchorIndex < 0 || currentIndex < 0) return;
        var (lo, loOffset, hi, hiOffset) = anchorIndex < currentIndex || (anchorIndex == currentIndex && anchorOffset <= currentOffset)
            ? (anchorIndex, anchorOffset, currentIndex, currentOffset)
            : (currentIndex, currentOffset, anchorIndex, anchorOffset);

        for (var i = 0; i < segments.Count; i++)
        {
            var s = segments[i];
            if (i < lo || i > hi) s.Clear();
            else if (lo == hi) s.Select(loOffset, hiOffset);
            else if (i == lo) s.Select(loOffset, s.Length);
            else if (i == hi) s.Select(0, hiOffset);
            else s.Select(0, s.Length);
        }
    }

    // ------------------------------------------------------------------ Parçaları toplama ve isabet

    /// <summary>Görünen sırayla (yukarıdan aşağı, aynı satırda soldan sağa) seçilebilir parçalar.</summary>
    private List<Segment> CollectSegments()
    {
        var all = new List<(FrameworkElement Element, Point TopLeft)>();
        for (var i = 0; i < _messageList.Items.Count; i++)
        {
            if (_messageList.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement container) continue;
            foreach (var element in FindVisualChildren<FrameworkElement>(container))
            {
                if (!ChatSelection.GetSelectable(element) || ReferenceEquals(element, _inputBox)) continue;
                if (!element.IsVisible || element.ActualHeight <= 0) continue;
                if (TryTopLeft(element, out var topLeft)) all.Add((element, topLeft));
            }
        }

        all.Sort((a, b) => a.TopLeft.Y.CompareTo(b.TopLeft.Y));
        var result = new List<Segment>(all.Count);
        var line = -1;
        var lineY = double.NegativeInfinity;
        var lineStart = 0;
        foreach (var (element, topLeft) in all)
        {
            if (topLeft.Y - lineY > SameLineTolerance)
            {
                SortLineByX(result, lineStart);
                line++;
                lineY = topLeft.Y;
                lineStart = result.Count;
            }
            if (Segment.Create(element, topLeft, line) is { } segment) result.Add(segment);
        }
        SortLineByX(result, lineStart);
        return result;
    }

    private static void SortLineByX(List<Segment> segments, int start)
    {
        if (segments.Count - start > 1)
            segments.Sort(start, segments.Count - start, Comparer<Segment>.Create((a, b) => a.TopLeft.X.CompareTo(b.TopLeft.X)));
    }

    private bool TryTopLeft(FrameworkElement element, out Point topLeft)
    {
        try
        {
            topLeft = element.TransformToAncestor(_scroller).Transform(new Point(0, 0));
            return true;
        }
        catch (InvalidOperationException)
        {
            // Görsel ağaçta kaydırıcının altında olmayan (henüz yerleşmemiş) öğe seçime katılmaz.
            topLeft = default;
            return false;
        }
    }

    private static int IndexOf(IReadOnlyList<Segment> segments, FrameworkElement element)
    {
        for (var i = 0; i < segments.Count; i++)
            if (ReferenceEquals(segments[i].Element, element)) return i;
        return -1;
    }

    private (Segment Segment, int Offset)? HitDirect(IReadOnlyList<Segment> segments, Point point)
    {
        var result = VisualTreeHelper.HitTest(_scroller, point);
        for (var d = result?.VisualHit as DependencyObject; d != null; d = GetParentSafe(d))
        {
            if (d is not FrameworkElement fe) continue;
            var index = IndexOf(segments, fe);
            if (index < 0) continue;
            var segment = segments[index];
            return (segment, segment.OffsetAt(_scroller.TranslatePoint(point, segment.Element)));
        }
        return null;
    }

    /// <summary>Boşluğa basıldığında en yakın parçanın başı (imleç üstündeyse) veya sonu (altındaysa).</summary>
    private (Segment Segment, int Offset)? HitNearest(IReadOnlyList<Segment> segments, Point point)
    {
        Segment? best = null;
        var bestDistance = double.MaxValue;
        foreach (var s in segments)
        {
            var top = s.TopLeft.Y;
            var bottom = top + s.Element.ActualHeight;
            var distance = point.Y < top ? top - point.Y : point.Y > bottom ? point.Y - bottom : 0;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = s;
            }
        }
        if (best == null) return null;

        var local = _scroller.TranslatePoint(point, best.Element);
        if (local.Y < 0) return (best, 0);
        if (local.Y > best.Element.ActualHeight) return (best, best.Length);
        // Aynı satır hizasında (ör. ad ile saat arası boşluk): satırdaki en yakın karakter.
        return (best, best.OffsetAt(local));
    }

    private static bool IsUnder(DependencyObject root, DependencyObject? node)
    {
        for (var d = node; d != null; d = GetParentSafe(d))
            if (ReferenceEquals(d, root)) return true;
        return false;
    }

    private static bool HasAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        for (var d = node; d != null; d = GetParentSafe(d))
            if (d is T) return true;
        return false;
    }

    private static DependencyObject? GetParentSafe(DependencyObject d)
    {
        // OriginalSource çoğu zaman Run (FrameworkContentElement) → VisualTreeHelper.GetParent istisna atar.
        if (d is Visual or System.Windows.Media.Media3D.Visual3D)
            return VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d);
        return LogicalTreeHelper.GetParent(d);
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            // Seçilebilir kutunun kendi iç şablonunda başka seçilebilir öğe yoktur; içine inmeye gerek yok.
            if (child is TextBoxBase) continue;
            foreach (var nested in FindVisualChildren<T>(child))
                yield return nested;
        }
    }

    // ------------------------------------------------------------------ Parça soyutlaması

    /// <summary>Seçime katılan tek metin kutusu; konumlar düz metin ofsetidir (belge yeniden kurulsa da geçerli kalır).</summary>
    private abstract class Segment(FrameworkElement element, Point topLeft, int line)
    {
        public FrameworkElement Element { get; } = element;
        public Point TopLeft { get; } = topLeft;
        public int Line { get; } = line;

        public abstract int Length { get; }
        public abstract bool HasSelection { get; }
        public abstract string SelectedText { get; }
        public abstract int OffsetAt(Point local);
        public abstract void Select(int start, int end);
        public abstract void Clear();

        public static Segment? Create(FrameworkElement element, Point topLeft, int line) => element switch
        {
            TextBox tb => new TextBoxSegment(tb, topLeft, line),
            RichTextBox rtb when rtb.Document != null => new RichTextSegment(rtb, topLeft, line),
            _ => null,
        };
    }

    private sealed class TextBoxSegment(TextBox box, Point topLeft, int line) : Segment(box, topLeft, line)
    {
        public override int Length => box.Text.Length;
        public override bool HasSelection => box.SelectionLength > 0;
        public override string SelectedText => box.SelectedText;

        public override int OffsetAt(Point local)
        {
            var index = box.GetCharacterIndexFromPoint(local, snapToText: true);
            if (index < 0) return local.X <= 0 ? 0 : Length;
            // Karakterin sağ yarısına basıldıysa imleç karakterden sonra konur.
            var leading = box.GetRectFromCharacterIndex(index);
            var trailing = box.GetRectFromCharacterIndex(index, trailingEdge: true);
            return local.X > (leading.Left + trailing.Left) / 2 ? Math.Min(index + 1, Length) : index;
        }

        public override void Select(int start, int end)
        {
            start = Math.Clamp(start, 0, Length);
            end = Math.Clamp(end, start, Length);
            box.Select(start, end - start);
        }

        public override void Clear()
        {
            if (box.SelectionLength > 0) box.Select(box.SelectionStart, 0);
        }
    }

    private sealed class RichTextSegment(RichTextBox box, Point topLeft, int line) : Segment(box, topLeft, line)
    {
        public override int Length => MentionRichText.GetPlainNormalized(box).Length;
        public override bool HasSelection => box.Selection is { IsEmpty: false };

        public override string SelectedText => box.Selection is { IsEmpty: false } selection
            ? MentionRichText.NormalizeSelection(MentionRichText.ExtractEmojiAwareText(selection.Start, selection.End))
            : "";

        public override int OffsetAt(Point local)
        {
            var pointer = box.GetPositionFromPoint(local, snapToText: true);
            return pointer == null ? 0 : MentionRichText.GetPlainOffset(box, pointer);
        }

        public override void Select(int start, int end)
        {
            var length = Length;
            var from = start <= 0 ? box.Document.ContentStart : MentionRichText.GetPointerAtPlainOffset(box, start) ?? box.Document.ContentStart;
            var to = end >= length ? box.Document.ContentEnd : MentionRichText.GetPointerAtPlainOffset(box, end) ?? box.Document.ContentEnd;
            if (from.CompareTo(to) > 0) (from, to) = (to, from);
            box.Selection.Select(from, to);
        }

        public override void Clear()
        {
            if (box.Selection is not { IsEmpty: false }) return;
            var caret = box.CaretPosition ?? box.Document.ContentStart;
            box.Selection.Select(caret, caret);
        }
    }
}
