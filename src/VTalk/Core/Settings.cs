using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace VTalk.Core;

public sealed class Settings
{
    public string DisplayName { get; set; } = Environment.UserName;
    public string Note { get; set; } = "";
    public PresenceStatus Status { get; set; } = PresenceStatus.Online;
    public bool SoundEnabled { get; set; } = true;
    public string? CustomSoundPath { get; set; }
    public bool ShowNotifications { get; set; } = true;
    /// <summary>Bildirimde mesaj içeriğini göster (kapalıysa yalnızca "yeni mesaj" yazar; ekran paylaşımı / kilit ekranı için).</summary>
    public bool ShowMessagePreview { get; set; } = true;
    /// <summary>Windows'ta "Ortak" olarak sınıflandırılmış ağlarda (kafe, otel Wi-Fi'ı) da çalış.</summary>
    public bool AllowPublicNetworks { get; set; }
    public bool NotifyContactOnline { get; set; } = true;
    public bool AutoAcceptImages { get; set; } = true;
    public int AutoAcceptMaxMb { get; set; } = 20;
    public string DownloadFolder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "V-Talk");
    public bool StartWithWindows { get; set; } = true;
    public bool StartMinimized { get; set; }
    public int AutoAwayMinutes { get; set; } = 10;
    public int PreferredTcpPort { get; set; } = 50506;
    /// <summary>Farklı alt ağlara (VLAN) ulaşmak için ek IP / yayın adresleri, virgülle ayrılmış.</summary>
    public string ExtraAddresses { get; set; } = "";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(AppPaths.SettingsFile, Encoding.UTF8), JsonOptions);
                if (s != null)
                {
                    s.Normalize();
                    return s;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Ayarlar okunamadı, varsayılanlar kullanılacak: " + ex.Message);
        }
        var fresh = new Settings();
        fresh.Normalize();
        return fresh;
    }

    public void Save()
    {
        Normalize();
        LocalProtector.WriteAtomic(AppPaths.SettingsFile, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this, JsonOptions)));
    }

    private void Normalize()
    {
        DisplayName = TextLimits.Clean(DisplayName, TextLimits.NameMax);
        if (string.IsNullOrWhiteSpace(DisplayName)) DisplayName = TextLimits.Clean(Environment.UserName, TextLimits.NameMax);
        Note = TextLimits.Clean(Note, TextLimits.NoteMax);
        if (Status == PresenceStatus.Offline) Status = PresenceStatus.Online;
        AutoAcceptMaxMb = Math.Clamp(AutoAcceptMaxMb, 1, 200);
        AutoAwayMinutes = Math.Clamp(AutoAwayMinutes, 0, 240);
        if (PreferredTcpPort is < 1024 or > 65535) PreferredTcpPort = 50506;
        if (string.IsNullOrWhiteSpace(DownloadFolder))
            DownloadFolder = new Settings().DownloadFolder;
    }
}

public static class TextLimits
{
    public const int NameMax = 64;
    public const int NoteMax = 128;
    public const int MessageMax = 16000;
    public const int UnitNameMax = 64;

    private const int MaxCombiningRun = 3;

    /// <summary>
    /// Kontrol karakterlerini, görünmez biçim karakterlerini (RTL override, sıfır genişlikli boşluk, soft hyphen vb.),
    /// "boş görünen" harfleri ve aşırı birleştirici işaret yığınlarını (zalgo) temizler.
    /// Böylece görünüşte aynı iki ad veya gizli karakter içeren dosya adları üretilemez.
    /// Mesajlarda (allowNewLines) emoji dizileri için sıfır genişlikli birleştirici (ZWJ) korunur.
    /// </summary>
    public static string Clean(string? value, int max, bool allowNewLines = false)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var sb = new StringBuilder(Math.Min(value.Length, max));
        int combiningRun = 0;
        foreach (var ch in value)
        {
            if (sb.Length >= max) break;
            if (ch == '\n' && allowNewLines) { sb.Append(ch); combiningRun = 0; continue; }
            if (ch == '\t') { sb.Append(' '); combiningRun = 0; continue; }
            if (char.IsControl(ch)) continue;
            var category = char.GetUnicodeCategory(ch);
            if (category == UnicodeCategory.Format && !(allowNewLines && ch == '\u200D')) continue;
            if (ch is '\u115F' or '\u1160' or '\u3164' or '\uFFA0' or '\u2800') continue;
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark)
            {
                if (++combiningRun > MaxCombiningRun) continue;
            }
            else
            {
                combiningRun = 0;
            }
            if (!allowNewLines && char.IsWhiteSpace(ch))
            {
                if (sb.Length > 0 && sb[^1] == ' ') continue;
                sb.Append(' ');
                continue;
            }
            sb.Append(ch);
        }
        return sb.ToString().Trim();
    }
}
