using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using VTalk.Net;
using VTalk.Security;

namespace VTalk.Core;

/// <summary>
/// Kurulum programının exe yanına (veya ProgramData\VTalk) yazdığı makine geneli ayarlar.
/// Uzak ağlar Public ağ engellemesinden muaf tutulduğu için dosya, org.pub ile aynı izin denetiminden geçmelidir.
/// </summary>
public sealed class DeploymentConfig
{
    private const int MaxFileBytes = 8 * 1024;
    public const int MaxHostLength = 253;

    [JsonPropertyName("adminHost")] public string? AdminHost { get; set; }
    [JsonPropertyName("networks")] public string? Networks { get; set; }

    public static DeploymentConfig Empty { get; } = new();

    public static DeploymentConfig Load()
    {
        foreach (var path in AppPaths.DeployConfigCandidates())
        {
            try
            {
                var file = new FileInfo(path);
                if (!file.Exists) continue;
                if (file.Length > MaxFileBytes)
                {
                    Log.Warn($"Kurulum ayarları yok sayıldı ({path}): dosya çok büyük.");
                    continue;
                }
                if (!FileTrust.IsProtectedFromOtherUsers(path, out var reason))
                {
                    Log.Warn($"Kurulum ayarları yok sayıldı ({path}): {reason}");
                    continue;
                }
                var cfg = JsonSerializer.Deserialize<DeploymentConfig>(File.ReadAllBytes(path)) ?? new DeploymentConfig();
                cfg.AdminHost = IsValidHostName(cfg.AdminHost) ? cfg.AdminHost!.Trim() : null;
                cfg.Networks = string.Join(", ", NetworkTargets.SplitEntries(cfg.Networks)
                    .Where(e => NetworkTargets.TryParse(e, out _, out _)));
                return cfg;
            }
            catch (Exception ex)
            {
                Log.Warn($"Kurulum ayarları okunamadı ({path}): {ex.Message}");
            }
        }
        return Empty;
    }

    /// <summary>NetBIOS / DNS adı: harf, rakam, tire ve nokta; etiket başına 1-63 karakter.</summary>
    public static bool IsValidHostName(string? host)
    {
        host = host?.Trim();
        if (string.IsNullOrEmpty(host) || host.Length > MaxHostLength) return false;
        foreach (var label in host.Split('.'))
        {
            if (label.Length is 0 or > 63 || label[0] == '-' || label[^1] == '-') return false;
            if (!label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')) return false;
        }
        return true;
    }
}
