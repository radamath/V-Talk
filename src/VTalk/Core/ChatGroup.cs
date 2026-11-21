namespace VTalk.Core;

/// <summary>Çok kişili sohbet odası. Konuşma kimliği veritabanında g:{Id} biçimindedir.</summary>
public sealed class ChatGroup
{
    public const string IdPrefix = "g:";
    public const int MaxMembers = 32;
    public const int MaxGroups = 80;

    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string CreatedBy { get; set; } = "";
    public long CreatedUtc { get; set; }
    public List<string> MemberIds { get; set; } = [];
    /// <summary>1:1 sohbete davet ile oluşan geçici oda; son davetli çıkınca dağılır.</summary>
    public bool Temporary { get; set; }

    public string ConversationId => ToConversationId(Id);

    public static bool IsConversationId(string? id) =>
        !string.IsNullOrEmpty(id) && id.StartsWith(IdPrefix, StringComparison.Ordinal);

    public static string ToConversationId(string groupId) => IdPrefix + groupId;

    public static string? TryParseGroupId(string? conversationId) =>
        IsConversationId(conversationId) ? conversationId![IdPrefix.Length..] : null;

    public static bool IsValidGroupId(string? id) =>
        !string.IsNullOrEmpty(id) && id.Length is >= 16 and <= 64 && id.All(Uri.IsHexDigit);

    /// <summary>Uzaktan gelen üye listesini geçerli, tekil, küçük harfli PeerId'lere indirger.</summary>
    public static List<string> NormalizeMembers(IEnumerable<string>? members) =>
        (members ?? [])
            .Select(id => (id ?? "").Trim().ToLowerInvariant())
            .Where(VTalk.Security.OrgDirectory.IsValidPeerId)
            .Distinct(StringComparer.Ordinal)
            .Take(MaxMembers)
            .ToList();

    /// <summary>
    /// Uzaktan gelen üyelik güncellemesinin (davet veya mesajla taşınan liste) yetkisini denetler.
    /// Kurallar: bilinmeyen grup yalnızca bizi içeriyorsa kabul edilir (yeni davet); bilinen grubu yalnızca mevcut
    /// bir üye güncelleyebilir; herhangi bir üye kişi ekleyebilir ve kendini çıkarabilir; başkalarını (biz dahil)
    /// yalnızca grubu kuran çıkarabilir. Arayüzdeki "yalnızca kuran çıkarır" kuralının alıcı tarafındaki karşılığıdır.
    /// </summary>
    public static GroupUpdateDecision EvaluateUpdate(ChatGroup? existing, string fromPeerId, string myPeerId,
        IReadOnlyCollection<string> proposedMembers)
    {
        bool includesMe = proposedMembers.Contains(myPeerId, StringComparer.OrdinalIgnoreCase);
        if (existing == null) return includesMe ? GroupUpdateDecision.Apply : GroupUpdateDecision.Ignore;

        if (!existing.MemberIds.Contains(fromPeerId, StringComparer.OrdinalIgnoreCase)) return GroupUpdateDecision.Forbidden;
        bool fromCreator = string.Equals(existing.CreatedBy, fromPeerId, StringComparison.OrdinalIgnoreCase);
        if (!includesMe) return fromCreator ? GroupUpdateDecision.RemovedMe : GroupUpdateDecision.Forbidden;

        bool removesOthers = existing.MemberIds.Any(id =>
            !string.Equals(id, fromPeerId, StringComparison.OrdinalIgnoreCase) &&
            !proposedMembers.Contains(id, StringComparer.OrdinalIgnoreCase));
        return removesOthers && !fromCreator ? GroupUpdateDecision.Forbidden : GroupUpdateDecision.Apply;
    }
}

public enum GroupUpdateDecision
{
    /// <summary>Bilmediğimiz ve bizi içermeyen grup: yapılacak bir şey yok.</summary>
    Ignore,
    /// <summary>Gönderenin bu değişikliği yapma yetkisi yok; yerel kayıt değişmez.</summary>
    Forbidden,
    /// <summary>Üye listesi ve ad güncellenir (gerekirse grup oluşturulur).</summary>
    Apply,
    /// <summary>Grubu kuran bizi çıkardı veya grubu dağıttı.</summary>
    RemovedMe,
}
