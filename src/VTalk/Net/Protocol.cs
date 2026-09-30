using System.Text.Json;
using System.Text.Json.Serialization;
using VTalk.Core;

namespace VTalk.Net;

public static class PacketTypes
{
    public const string Message = "msg";
    public const string Ack = "ack";
    public const string Nudge = "nudge";
    public const string FileOffer = "file-offer";
    public const string FileCancel = "file-cancel";
    public const string FileGet = "file-get";
    public const string FileOk = "file-ok";
    public const string FileDone = "file-done";
    public const string Bulletin = "bulletin";
    public const string DirectoryGet = "dir-get";
    public const string Directory = "dir";
    public const string AvatarGet = "avatar-get";
    public const string Avatar = "avatar";
    public const string Error = "err";
}

/// <summary>Şifreli kanal üzerinden taşınan uygulama paketi.</summary>
public sealed class Packet
{
    [JsonPropertyName("t")] public string T { get; set; } = "";
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("re")] public string? Re { get; set; }
    [JsonPropertyName("ts")] public long Ts { get; set; }
    [JsonPropertyName("text")] public string? Text { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("sha")] public string? Sha { get; set; }
    [JsonPropertyName("scope")] public string? Scope { get; set; }
    [JsonPropertyName("data")] public string? Data { get; set; }
    [JsonPropertyName("sig")] public string? Sig { get; set; }
    [JsonPropertyName("err")] public string? Err { get; set; }

    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        MaxDepth = 8,
    };

    public byte[] Serialize() => JsonSerializer.SerializeToUtf8Bytes(this, Options);

    public static Packet? TryParse(byte[] data)
    {
        try
        {
            var p = JsonSerializer.Deserialize<Packet>(data, Options);
            if (p == null || string.IsNullOrEmpty(p.T) || p.T.Length > 32) return null;
            if (p.Id is { Length: > 64 } || p.Re is { Length: > 64 }) return null;
            // Hata metni karşı taraftan gelir; günlüğe/arayüze ulaşmadan önce kısaltılıp temizlenir.
            if (p.Err != null) p.Err = TextLimits.Clean(p.Err, 200);
            return p;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static bool IsValidId(string? id) =>
        !string.IsNullOrEmpty(id) && id.Length <= 64 && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
}
