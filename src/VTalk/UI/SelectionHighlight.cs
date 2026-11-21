using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;

namespace VTalk.UI;

/// <summary>
/// Sohbetteki seçilebilir kutuların seçimini odaktan bağımsız çizer. WPF yalnızca klavye odağındaki kutunun seçimini
/// boyar; balonlar arası (Ctrl+A, sürükleme) seçimde diğer kutular seçili olduğu halde seçilmemiş görünürdü.
/// Kutuların kendi seçim boyası stilde kapatılır (SelectionOpacity=0); tek boyama kaynağı bu sınıftır.
/// </summary>
internal static class SelectionHighlight
{
    private const double HighlightOpacity = 0.4;
    private static readonly ConditionalWeakTable<FrameworkElement, HighlightAdorner> Adorners = new();

    /// <summary>Kutunun seçimi değişti: vurgu katmanını (gerekirse oluşturup) yeniden çizdir.</summary>
    public static void Refresh(FrameworkElement box)
    {
        if (Adorners.TryGetValue(box, out var existing))
        {
            existing.InvalidateVisual();
            return;
        }
        if (!HasSelection(box)) return;
        // Kaydırıcının içerik sunucusunun katmanı: vurgu mesaj alanına kırpılır, başlık/yazma alanına taşmaz.
        var layer = AdornerLayer.GetAdornerLayer(box);
        if (layer == null) return;
        var adorner = new HighlightAdorner(box);
        layer.Add(adorner);
        Adorners.Add(box, adorner);
        box.SizeChanged += (_, _) => adorner.InvalidateVisual();
        box.Unloaded += (_, _) =>
        {
            AdornerLayer.GetAdornerLayer(box)?.Remove(adorner);
            Adorners.Remove(box);
        };
    }

    private static bool HasSelection(FrameworkElement box) => box switch
    {
        TextBox tb => tb.SelectionLength > 0,
        RichTextBox rtb => rtb.Selection is { IsEmpty: false },
        _ => false,
    };

    /// <summary>Seçili metnin satır satır dikdörtgenleri (kutunun kendi koordinatlarında).</summary>
    internal static List<Rect> SelectionRects(FrameworkElement box) => box switch
    {
        TextBox tb => TextBoxRects(tb),
        RichTextBox rtb => RichTextRects(rtb),
        _ => [],
    };

    private static List<Rect> TextBoxRects(TextBox box)
    {
        var rects = new List<Rect>();
        int start = box.SelectionStart, end = start + box.SelectionLength;
        if (end <= start) return rects;
        int firstLine = box.GetLineIndexFromCharacterIndex(start), lastLine = box.GetLineIndexFromCharacterIndex(end - 1);
        if (firstLine < 0 || lastLine < 0) return rects; // düzen henüz hazır değil; düzen sonrası SizeChanged yeniden çizer
        var text = box.Text;
        for (var line = firstLine; line <= lastLine; line++)
        {
            var lineStart = box.GetCharacterIndexFromLineIndex(line);
            int from = Math.Max(start, lineStart), to = Math.Min(end, lineStart + box.GetLineLength(line));
            while (to > from && text[to - 1] is '\r' or '\n') to--;
            if (to <= from) continue;
            var left = box.GetRectFromCharacterIndex(from);
            var right = box.GetRectFromCharacterIndex(to - 1, trailingEdge: true);
            if (left.IsEmpty || right.IsEmpty) continue;
            rects.Add(new Rect(left.Left, left.Top, Math.Max(0, right.Left - left.Left), left.Height));
        }
        return rects;
    }

    private static List<Rect> RichTextRects(RichTextBox box)
    {
        var rects = new List<Rect>();
        var selection = box.Selection;
        if (selection == null || selection.IsEmpty) return rects;
        var end = selection.End;
        var position = selection.Start;
        // Her görsel satır için seçili parçanın sol ve sağ kenarı.
        while (position != null && position.CompareTo(end) < 0)
        {
            var nextLine = position.GetLineStartPosition(1, out var moved);
            var lineEnd = moved == 0 || nextLine == null || nextLine.CompareTo(end) > 0 ? end : nextLine;
            var left = position.GetCharacterRect(LogicalDirection.Forward);
            var right = lineEnd.GetCharacterRect(LogicalDirection.Backward);
            if (!left.IsEmpty && !right.IsEmpty && right.Right > left.Left && Math.Abs(right.Top - left.Top) < left.Height)
                rects.Add(new Rect(left.Left, Math.Min(left.Top, right.Top), right.Right - left.Left,
                    Math.Max(left.Bottom, right.Bottom) - Math.Min(left.Top, right.Top)));
            if (moved == 0 || nextLine == null) break;
            position = nextLine;
        }
        return rects;
    }

    private sealed class HighlightAdorner : Adorner
    {
        private static readonly Brush Fill = CreateFill();

        public HighlightAdorner(FrameworkElement box) : base(box)
        {
            IsHitTestVisible = false;
        }

        private static Brush CreateFill()
        {
            var brush = new SolidColorBrush(SystemColors.HighlightColor) { Opacity = HighlightOpacity };
            brush.Freeze();
            return brush;
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            foreach (var rect in SelectionRects((FrameworkElement)AdornedElement))
                drawingContext.DrawRectangle(Fill, null, rect);
        }
    }
}
