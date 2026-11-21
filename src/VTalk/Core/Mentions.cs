namespace VTalk.Core;

/// <summary>Grup mesajlarında @etiket çözümleme. @all / @herkes tüm üyeleri hedefler.</summary>
public static class Mentions
{
    /// <summary>Paket Mentions listesinde «herkes» anlamı (gerçek peer id değil).</summary>
    public const string Everyone = "*";

    private static readonly string[] EveryoneAliases = ["all", "herkes", "everyone"];

    public static bool IsEveryoneAlias(string? raw) =>
        !string.IsNullOrWhiteSpace(raw) &&
        EveryoneAliases.Any(a => a.Equals(raw.Trim(), StringComparison.OrdinalIgnoreCase));

    public static List<string> ResolvePeerIds(string? text, IEnumerable<string> memberPeerIds, Func<string, string> displayName)
    {
        if (string.IsNullOrEmpty(text)) return [];
        var catalog = memberPeerIds
            .Select(id => (Id: id, Name: (displayName(id) ?? "").Trim()))
            .Where(x => x.Name.Length > 0)
            .OrderByDescending(x => x.Name.Length)
            .ToList();
        var names = catalog.Select(c => c.Name).ToList();

        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (mention, segment) in SplitSegments(text, names))
        {
            if (!mention || segment.Length < 2 || segment[0] != '@') continue;
            var raw = segment[1..].Trim().TrimEnd(',', '.', '!', '?', ':', ';');
            if (raw.Length == 0) continue;
            if (IsEveryoneAlias(raw))
            {
                found.Add(Everyone);
                continue;
            }
            var hit = catalog.FirstOrDefault(c => raw.Equals(c.Name, StringComparison.CurrentCultureIgnoreCase));
            if (hit.Id != null) found.Add(hit.Id);
        }
        return found.ToList();
    }

    public static bool Includes(IReadOnlyList<string>? mentionPeerIds, string peerId) =>
        mentionPeerIds is { Count: > 0 } &&
        (mentionPeerIds.Contains(Everyone, StringComparer.OrdinalIgnoreCase) ||
         mentionPeerIds.Contains(peerId, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Metni düz / @bahsetme parçalarına ayırır.
    /// Boşluklu adlar için katalogdaki en uzun eşleşme kullanılır.
    /// </summary>
    public static List<(bool Mention, string Text)> SplitSegments(string? text, IEnumerable<string>? validNames = null)
    {
        var list = new List<(bool, string)>();
        if (string.IsNullOrEmpty(text)) return list;

        var names = (validNames ?? [])
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .Concat(EveryoneAliases)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderByDescending(n => n.Length)
            .ToList();

        var i = 0;
        while (i < text.Length)
        {
            if (text[i] != '@' || !IsMentionStart(text, i))
            {
                var next = text.IndexOf('@', i + 1);
                if (next < 0)
                {
                    list.Add((false, text[i..]));
                    break;
                }
                // Sonraki @ mention başlangıcı değilse düz metin olarak yut.
                if (!IsMentionStart(text, next))
                {
                    list.Add((false, text[i..(next + 1)]));
                    i = next + 1;
                    continue;
                }
                if (next > i) list.Add((false, text[i..next]));
                i = next;
                continue;
            }

            var matched = TryMatchName(text, i + 1, names);
            if (matched != null)
            {
                list.Add((true, text.Substring(i, 1 + matched.Length)));
                i += 1 + matched.Length;
                continue;
            }

            // Katalogda yok: tek sözcük (eski davranış) — boşluksuz token.
            var end = i + 1;
            while (end < text.Length && text[end] != '@' && text[end] != '\n' && !char.IsWhiteSpace(text[end]))
                end++;
            if (end > i + 1)
            {
                list.Add((false, text[i..end])); // geçerli isim değil → düz
                i = end;
            }
            else
            {
                list.Add((false, "@"));
                i++;
            }
        }
        return list;
    }

    /// <summary>Yazım sırasında @ sonrası süzülecek ad parçası; yoksa null. Boşluklu adları destekler.</summary>
    public static (int Start, string Query)? ActiveQuery(string text, int caret)
    {
        if (caret < 0 || caret > text.Length) return null;
        var i = caret - 1;
        while (i >= 0 && text[i] != '@' && text[i] != '\n') i--;
        if (i < 0 || text[i] != '@') return null;
        if (!IsMentionStart(text, i)) return null;
        var query = text[(i + 1)..caret];
        if (query.Contains('\n')) return null;
        // Tamamlanmış etiket + boşluktan sonra yeni yazım → popup kapansın.
        if (query.Length > 0 && query[^1] == ' ')
        {
            var trimmed = query.TrimEnd();
            if (trimmed.Length > 0 && (IsEveryoneAlias(trimmed) || !trimmed.Contains(' ')))
                return null;
            // Boşluklu isimde ara boşluklar OK; yalnızca sondaki tamamlanma boşluğunda kapatmak için
            // TrimEnd sonrası sorguyu kullan — popup filtrelemesi trimmed ile.
            // Tam isim + trailing space: çağıran isim listesine bakmadan kapatamayız; bırak.
        }
        return (i, query);
    }

    private static bool IsMentionStart(string text, int atIndex)
    {
        if (atIndex < 0 || atIndex >= text.Length || text[atIndex] != '@') return false;
        if (atIndex == 0) return true;
        var prev = text[atIndex - 1];
        return !(char.IsLetterOrDigit(prev) || prev is '.' or '_');
    }

    private static string? TryMatchName(string text, int start, List<string> namesOrderedLongestFirst)
    {
        if (start > text.Length) return null;
        foreach (var name in namesOrderedLongestFirst)
        {
            var consumed = TryConsumeName(text, start, name);
            if (consumed == null) continue;
            var end = start + consumed.Length;
            if (end < text.Length && char.IsLetterOrDigit(text[end])) continue;
            return consumed;
        }
        return null;
    }

    /// <summary>Katalog adını metinde eşle; boşluk farklarını tolere et, metindeki orijinal yazımı döndür.</summary>
    private static string? TryConsumeName(string text, int start, string name)
    {
        var ti = start;
        var ni = 0;
        while (ni < name.Length && ti < text.Length)
        {
            if (char.IsWhiteSpace(name[ni]))
            {
                if (!char.IsWhiteSpace(text[ti])) return null;
                while (ni < name.Length && char.IsWhiteSpace(name[ni])) ni++;
                while (ti < text.Length && char.IsWhiteSpace(text[ti])) ti++;
                continue;
            }
            if (char.ToLowerInvariant(text[ti]) != char.ToLowerInvariant(name[ni]))
                return null;
            ti++;
            ni++;
        }
        if (ni < name.Length) return null;
        return text[start..ti];
    }
}
