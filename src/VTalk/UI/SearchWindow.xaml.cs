using System.Windows;
using System.Windows.Input;
using VTalk.Core;
using VTalk.Data;

namespace VTalk.UI;

/// <summary>Mesajlarda arama: tek sohbette (sohbet penceresinden Ctrl+F) veya tüm sohbetlerde (ana pencereden).</summary>
public partial class SearchWindow : Window
{
    private const int MaxResults = 200;
    private const int MaxScanned = 50_000;
    private const int SnippetContext = 40;

    public sealed record SearchHit(string ConversationId, string MsgId, string Title, string When, string Before, string Match, string After);

    private readonly AppController _app;
    private readonly string? _conversationId;
    private CancellationTokenSource? _search;
    private string _lastQuery = "";

    public SearchWindow(AppController app, string? conversationId, string? initialQuery = null)
    {
        InitializeComponent();
        _app = app;
        _conversationId = conversationId;
        ScopeText.Text = conversationId == null ? "Tüm sohbetlerde ara" : $"«{app.DisplayName(conversationId)}» sohbetinde ara";
        QueryBox.Text = initialQuery ?? "";
        Loaded += (_, _) => { QueryBox.Focus(); QueryBox.SelectAll(); };
        Closed += (_, _) => _search?.Cancel();
    }

    private async void Search_Click(object sender, RoutedEventArgs e)
    {
        var query = QueryBox.Text.Trim();
        if (query.Length < 2)
        {
            StatusText.Text = "En az iki karakter yazın.";
            return;
        }
        _search?.Cancel();
        var cts = _search = new CancellationTokenSource();
        _lastQuery = query;
        StatusText.Text = "Aranıyor…";
        Results.ItemsSource = null;

        // Şifre çözme ve süzme arka planda: büyük geçmişte pencere donmaz.
        var found = await Task.Run(() => _app.Chat.Db.SearchMessages(_conversationId, query, MaxResults, MaxScanned, cts.Token));
        if (cts.IsCancellationRequested) return;
        var hits = found.Select(m => ToHit(m, query)).ToList();
        Results.ItemsSource = hits;
        StatusText.Text = hits.Count == 0 ? "Sonuç bulunamadı."
            : hits.Count >= MaxResults ? $"İlk {MaxResults} sonuç gösteriliyor; aramayı daraltın." : $"{hits.Count} sonuç";
    }

    private SearchHit ToHit(StoredMessage m, string query)
    {
        var text = (m.Kind == MessageKind.File ? m.Data.FileName : m.Data.Text) ?? "";
        text = text.ReplaceLineEndings(" ");
        var at = Math.Max(0, Database.MatchIndex(text, query));
        var length = Math.Min(query.Length, text.Length - at);
        var start = Math.Max(0, at - SnippetContext);
        var end = Math.Min(text.Length, at + length + SnippetContext * 2);
        var before = (start > 0 ? "…" : "") + text[start..at];
        var after = text[(at + length)..end] + (end < text.Length ? "…" : "");

        var sender = m.Direction == MessageDirection.Outgoing
            ? _app.Chat.Settings.DisplayName
            : _app.DisplayName(ChatGroup.IsConversationId(m.PeerId) ? m.Data.SenderPeerId ?? "" : m.PeerId);
        var title = _conversationId == null ? $"{_app.DisplayName(m.PeerId)} — {sender}" : sender;
        var when = RemoteTimestamp.ToLocalTime(m.Timestamp).ToString("dd.MM.yyyy HH:mm");
        return new SearchHit(m.PeerId, m.MsgId, title, when, before, text.Substring(at, length), after);
    }

    private void Results_MouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenSelected();

    private void Results_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        OpenSelected();
    }

    private void OpenSelected()
    {
        if (Results.SelectedItem is not SearchHit hit) return;
        var chat = _app.OpenChat(hit.ConversationId);
        if (!chat.ShowSearchHit(hit.MsgId, _lastQuery))
            StatusText.Text = "Mesaj sohbet geçmişinin çok gerisinde; pencerede «Daha eski mesajları göster» ile ulaşabilirsiniz.";
    }
}
