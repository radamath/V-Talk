using System.Text.Json.Serialization;
using VTalk.Core;

namespace VTalk.Security;

public sealed class OrgUnit
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
}

public sealed class OrgMember
{
    [JsonPropertyName("peer")] public string PeerId { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("role")] public Role Role { get; set; }
    [JsonPropertyName("unit")] public string? UnitId { get; set; }
}

/// <summary>Admin'in organizasyon anahtarıyla imzaladığı rol/birim dizini.</summary>
public sealed class OrgDirectory
{
    [JsonPropertyName("v")] public int Version { get; set; }
    [JsonPropertyName("iat")] public long IssuedAt { get; set; }
    [JsonPropertyName("org")] public string OrgKey { get; set; } = "";
    [JsonPropertyName("units")] public List<OrgUnit> Units { get; set; } = [];
    [JsonPropertyName("members")] public List<OrgMember> Members { get; set; } = [];
    /// <summary>
    /// Sıkı mod: yalnızca dizindeki kimlikler mesaj/dosya/duyuru alışverişi yapabilir.
    /// Dizin dışı kimlikler (sahte, yabancı veya dizinden çıkarılmış) reddedilir ve listelerde gizlenir;
    /// yalnızca Admin rolündekiler onları görür (kayıt için).
    /// </summary>
    [JsonPropertyName("mo")] public bool MembersOnly { get; set; }

    public const int MaxUnits = 500;
    public const int MaxMembers = 4000;

    /// <summary>İmzalı içerik doğrulandıktan sonra bile alanları katı biçimde denetler.</summary>
    public string? Validate()
    {
        if (Version is < 1 or int.MaxValue) return "Geçersiz sürüm.";
        if (Units.Count > MaxUnits || Members.Count > MaxMembers) return "Dizin çok büyük.";
        var unitIds = new HashSet<string>();
        foreach (var u in Units)
        {
            if (!IsValidToken(u.Id) || !unitIds.Add(u.Id)) return "Geçersiz birim kimliği.";
            u.Name = TextLimits.Clean(u.Name, TextLimits.UnitNameMax);
            if (u.Name.Length == 0) return "Boş birim adı.";
        }
        var peers = new HashSet<string>();
        foreach (var m in Members)
        {
            if (!IsValidPeerId(m.PeerId) || !peers.Add(m.PeerId)) return "Geçersiz üye kimliği.";
            if (!Enum.IsDefined(m.Role)) return "Geçersiz rol.";
            if (m.UnitId != null && !unitIds.Contains(m.UnitId)) return "Üye bilinmeyen bir birime atanmış.";
            m.Name = TextLimits.Clean(m.Name, TextLimits.NameMax);
        }
        return null;
    }

    public static bool IsValidPeerId(string? id) =>
        id is { Length: 32 } && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static bool IsValidToken(string? id) =>
        id is { Length: > 0 and <= 40 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
}

public sealed class SignedDirectory
{
    [JsonPropertyName("p")] public string Payload { get; set; } = "";
    [JsonPropertyName("s")] public string Signature { get; set; } = "";
}
