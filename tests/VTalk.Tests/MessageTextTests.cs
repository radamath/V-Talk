using System.ComponentModel;
using System.Windows.Data;
using VTalk.UI;

namespace VTalk.Tests;

public class MessageTextTests
{
    private sealed class Source : INotifyPropertyChanged
    {
        private string _text = "";
        public string Text { get => _text; set { _text = value; PropertyChanged?.Invoke(this, new(nameof(Text))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) throw new Exception("STA testi başarısız", failure);
    }

    [Fact]
    public void MergedMessages_ReachTheBubble_IncludingEmojiOnly()
    {
        // Emoji.Wpf kutusu Text bağlamasını ilk atamada siler; aynı balona birleşen mesajlar ekrana düşmüyordu.
        OnSta(() =>
        {
            var source = new Source { Text = "Önce yazı" };
            var box = new Emoji.Wpf.RichTextBox { IsReadOnly = true };
            BindingOperations.SetBinding(box, MessageText.TextProperty, new Binding(nameof(Source.Text)) { Source = source, Mode = BindingMode.OneWay });
            Assert.Equal("Önce yazı", MentionRichText.GetPlainNormalized(box));

            source.Text = "Önce yazı\n😀";
            Assert.Equal("Önce yazı\n😀", MentionRichText.GetPlainNormalized(box));

            source.Text = "Önce yazı\n😀\nsonra yazı";
            Assert.Equal("Önce yazı\n😀\nsonra yazı", MentionRichText.GetPlainNormalized(box));
        });
    }
}
