using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using VTalk.Core;

namespace VTalk.UI;

/// <summary>@bahsetmeleri kırmızı+kalın gösterir; sonrası düz metin stiline döner.</summary>
public static class MentionRichText
{
    public static readonly Brush MentionBrush = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B));

    public static string GetPlainNormalized(RichTextBox box)
    {
        // Düz TextRange emoji InlineUIContainer'ları atlar → yalnızca yazı kalır, tek emoji gönderilemez.
        return Normalize(ExtractEmojiAwareText(box.Document.ContentStart, box.Document.ContentEnd));
    }

    public static int GetCaretOffset(RichTextBox box)
    {
        var plain = GetPlainNormalized(box);
        var caret = box.CaretPosition ?? box.Document.ContentEnd;
        var before = NormalizeKeep(ExtractEmojiAwareText(box.Document.ContentStart, caret));
        if (before.Length > plain.Length) return plain.Length;
        if (before.EndsWith('\n') && before.TrimEnd('\n') == plain) return plain.Length;
        return before.Length;
    }

    public static int GetPlainOffset(RichTextBox box, TextPointer position)
    {
        var plain = GetPlainNormalized(box);
        var before = NormalizeKeep(ExtractEmojiAwareText(box.Document.ContentStart, position));
        return Math.Clamp(before.EndsWith('\n') && before.TrimEnd('\n') == plain ? plain.Length : before.Length, 0, plain.Length);
    }

    public static TextPointer? GetPointerAtPlainOffset(RichTextBox box, int plainIndex) =>
        PointerAtPlainOffset(box.Document.ContentStart, box.Document.ContentEnd, plainIndex);

    public static string NormalizeSelection(string? text) => Normalize(text ?? "");

    /// <summary>Verilen düz metin ofsetindeki @bahsetme token'ı (@Ad Soyad); yoksa null.</summary>
    public static string? FindMentionAt(string plain, int offset, IEnumerable<string>? validNames)
    {
        if (string.IsNullOrEmpty(plain) || offset < 0 || offset > plain.Length) return null;
        var at = 0;
        foreach (var (mention, segment) in Mentions.SplitSegments(plain, validNames))
        {
            var end = at + segment.Length;
            // Token içi (bitiş sınırı hariç — boşluğa tıklayınca yanlış eşleşmesin).
            if (mention && offset >= at && offset < end)
                return segment;
            at = end;
        }
        return null;
    }

    /// <summary>EmojiInline → UTF-16; paragraf/LineBreak → \n (gönderimde satır sonları korunsun).</summary>
    public static string ExtractEmojiAwareText(TextPointer start, TextPointer end)
    {
        var buf = new StringBuilder();
        for (TextPointer p = start, next = null; p != null && p.CompareTo(end) < 0; p = next)
        {
            next = p.GetNextContextPosition(LogicalDirection.Forward);
            if (next == null) break;
            switch (p.GetPointerContext(LogicalDirection.Forward))
            {
                case TextPointerContext.ElementStart:
                {
                    var el = p.GetAdjacentElement(LogicalDirection.Forward);
                    if (el is Emoji.Wpf.EmojiInline emoji && emoji.ContentStart.CompareTo(end) < 0)
                        buf.Append(emoji.Text ?? "");
                    else if (el is LineBreak)
                        buf.Append('\n');
                    else if (el is Paragraph && buf.Length > 0 && (buf[^1] != '\n'))
                        buf.Append('\n');
                    break;
                }
                case TextPointerContext.Text:
                    var segEnd = next.CompareTo(end) < 0 ? next : end;
                    buf.Append(new TextRange(p, segEnd).Text);
                    break;
            }
        }
        return buf.ToString();
    }

    private static string NormalizeKeep(string text) =>
        (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');

    private static string Normalize(string text) => NormalizeKeep(text).TrimEnd('\n');

    public static void Clear(RichTextBox box)
    {
        if (box is Emoji.Wpf.RichTextBox emojiBox)
        {
            emojiBox.Text = "";
            ResetTypingStyle(box);
            return;
        }
        box.Document.Blocks.Clear();
        box.Document.Blocks.Add(new Paragraph(new Run("")) { Margin = new Thickness(0) });
        ResetTypingStyle(box);
    }

    public static void SetPlainText(RichTextBox box, string text, int caretPlainIndex)
    {
        Rebuild(box, text ?? "", caretPlainIndex);
    }

    public static void InsertAtCaret(RichTextBox box, string insert)
    {
        if (string.IsNullOrEmpty(insert)) return;
        var plain = GetPlainNormalized(box);
        var at = Math.Clamp(GetCaretOffset(box), 0, plain.Length);
        if (plain.Length + insert.Length > 16000) return;
        var next = plain[..at] + insert + plain[at..];
        Rebuild(box, next, at + insert.Length);
    }

    public static void Rebuild(RichTextBox box, string plain, int caretPlainIndex, IEnumerable<string>? validNames = null)
    {
        // Emoji.Wpf.Text → renkli emoji; sonra @bahsetmeleri boya.
        if (box is Emoji.Wpf.RichTextBox emojiBox)
        {
            emojiBox.Text = plain ?? "";
            if (plain != null && plain.Contains('@'))
                FormatDocumentMentions(emojiBox.Document, validNames);
            SetCaretFromPlainIndex(box, caretPlainIndex);
            ResetTypingStyle(box);
            return;
        }

        box.Document.Blocks.Clear();
        var para = new Paragraph { Margin = new Thickness(0) };
        foreach (var (mention, segment) in Mentions.SplitSegments(plain, validNames))
        {
            var run = new Run(segment);
            if (mention)
            {
                run.Foreground = MentionBrush;
                run.FontWeight = FontWeights.Bold;
            }
            para.Inlines.Add(run);
        }
        if (para.Inlines.Count == 0) para.Inlines.Add(new Run(""));
        box.Document.Blocks.Add(para);
        SetCaretFromPlainIndex(box, caretPlainIndex);
        ResetTypingStyle(box);
    }

    public static void ResetTypingStyle(RichTextBox box)
    {
        // ApplyPropertyValue(UnsetValue) → NamedObject/Foreground ArgumentException.
        // Emoji.Wpf'de Foreground zorlamak renkli emojiyi bozar; yalnızca kalınlığı sıfırla.
        if (box.CaretPosition == null) return;
        box.Selection.Select(box.CaretPosition, box.CaretPosition);
        box.Selection.ApplyPropertyValue(TextElement.FontWeightProperty, FontWeights.Normal);
        if (box is Emoji.Wpf.RichTextBox) return;
        box.Selection.ApplyPropertyValue(TextElement.ForegroundProperty, Brushes.Black);
    }

    public static void SetCaretFromPlainIndex(RichTextBox box, int plainIndex)
    {
        var pointer = PointerAtPlainOffset(box.Document.ContentStart, box.Document.ContentEnd, plainIndex);
        box.CaretPosition = pointer ?? box.Document.ContentEnd;
    }

    /// <summary>
    /// @bahsetmeleri boyar.
    /// <paramref name="rebuildInlines"/> true: mesaj baloncukları (sağlam boyama).
    /// false: yazım kutusu — inline'ları silmez, imleç/backspace bozulmaz.
    /// </summary>
    public static void FormatDocumentMentions(FlowDocument doc, IEnumerable<string>? validNames = null, bool rebuildInlines = true)
    {
        var names = validNames?.ToList();
        foreach (var block in doc.Blocks.ToList())
        {
            if (block is not Paragraph para) continue;
            if (rebuildInlines) FormatParagraphMentionsRebuild(para, names);
            else FormatParagraphMentionsInPlace(para, names);
        }
    }

    private static void FormatParagraphMentionsRebuild(Paragraph para, IReadOnlyList<string>? validNames)
    {
        var plain = Normalize(ExtractEmojiAwareText(para.ContentStart, para.ContentEnd));
        if (plain.Length == 0 || !plain.Contains('@')) return;

        var segs = Mentions.SplitSegments(plain, validNames);
        if (segs.All(s => !s.Mention)) return;

        para.Inlines.Clear();
        foreach (var (mention, segment) in segs)
        {
            if (segment.Length == 0) continue;
            var run = new Run(segment);
            if (mention)
            {
                run.Foreground = MentionBrush;
                run.FontWeight = FontWeights.Bold;
            }
            para.Inlines.Add(run);
        }
        if (para.Inlines.Count == 0) para.Inlines.Add(new Run(""));

        foreach (var run in para.Inlines.OfType<Run>().ToList())
        {
            try { Emoji.Wpf.FlowDocumentExtensions.SubstituteGlyphs(run); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                // Emoji görseli yerleştirilemezse metin karakter olarak kalır.
                Log.Info("Emoji yerleştirilemedi: " + ex.Message);
            }
        }
    }

    private static void FormatParagraphMentionsInPlace(Paragraph para, IReadOnlyList<string>? validNames)
    {
        var plain = Normalize(ExtractEmojiAwareText(para.ContentStart, para.ContentEnd));
        if (plain.Length == 0) return;

        foreach (var inline in para.Inlines.OfType<Run>())
        {
            inline.FontWeight = FontWeights.Normal;
            inline.ClearValue(TextElement.ForegroundProperty);
        }

        if (!plain.Contains('@')) return;
        var segs = Mentions.SplitSegments(plain, validNames);
        var offset = 0;
        foreach (var (mention, segment) in segs)
        {
            if (mention && segment.Length > 0)
            {
                var a = PointerAtPlainOffset(para.ContentStart, para.ContentEnd, offset);
                var b = PointerAtPlainOffset(para.ContentStart, para.ContentEnd, offset + segment.Length);
                if (a != null && b != null && a.CompareTo(b) < 0)
                {
                    var range = new TextRange(a, b);
                    range.ApplyPropertyValue(TextElement.ForegroundProperty, MentionBrush);
                    range.ApplyPropertyValue(TextElement.FontWeightProperty, FontWeights.Bold);
                }
            }
            offset += segment.Length;
        }
    }

    private static TextPointer? PointerAtPlainOffset(TextPointer start, TextPointer end, int plainIndex)
    {
        plainIndex = Math.Max(0, plainIndex);
        var seen = 0;
        for (TextPointer p = start, next = null; p != null && p.CompareTo(end) < 0; p = next)
        {
            next = p.GetNextContextPosition(LogicalDirection.Forward);
            if (next == null) break;
            switch (p.GetPointerContext(LogicalDirection.Forward))
            {
                case TextPointerContext.ElementStart:
                    if (p.GetAdjacentElement(LogicalDirection.Forward) is Emoji.Wpf.EmojiInline emoji)
                    {
                        var emojiText = emoji.Text ?? "";
                        if (seen + emojiText.Length >= plainIndex)
                            return plainIndex <= seen ? emoji.ContentStart : emoji.ContentEnd;
                        seen += emojiText.Length;
                    }
                    break;
                case TextPointerContext.Text:
                    var runText = p.GetTextInRun(LogicalDirection.Forward) ?? "";
                    if (seen + runText.Length >= plainIndex)
                        return p.GetPositionAtOffset(plainIndex - seen, LogicalDirection.Forward) ?? next;
                    seen += runText.Length;
                    break;
            }
        }
        return end;
    }
}
