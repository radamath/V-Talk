using System.Windows;

namespace VTalk.UI;

/// <summary>
/// Mesaj balonunun metnini Emoji.Wpf kutusuna aktarır. Emoji.Wpf.RichTextBox kendi Text özelliğine ilk atamada yerel
/// değer yazdığı için o özelliğe kurulan bağlama hemen silinir; aynı balona sonradan birleşen mesajlar (ardışık mesajlar,
/// yalnız emoji) pencere yeniden açılana kadar görünmezdi. Bağlama bu ekli özellikte durur ve her değişiklik açıkça aktarılır.
/// </summary>
public static class MessageText
{
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.RegisterAttached("Text", typeof(string), typeof(MessageText), new PropertyMetadata("", OnTextChanged));

    public static void SetText(DependencyObject d, string value) => d.SetValue(TextProperty, value);
    public static string GetText(DependencyObject d) => (string)d.GetValue(TextProperty);

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Emoji.Wpf.RichTextBox box) return;
        var text = e.NewValue as string ?? "";
        if (!string.Equals(box.Text, text, StringComparison.Ordinal)) box.Text = text;
    }
}
