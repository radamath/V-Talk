using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace VTalk.Core;

public static class AppPaths
{
    /// <summary>
    /// Ayrı profil (ör. aynı bilgisayarda test için ikinci örnek): VTALK_PROFILE ortam değişkeni.
    /// Yalnızca harf/rakam/tire kabul edilir; yol enjeksiyonu engellenir.
    /// </summary>
    public static string Profile { get; } = SanitizeProfile(Environment.GetEnvironmentVariable("VTALK_PROFILE"));

    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Profile.Length == 0 ? "VTalk" : "VTalk-" + Profile);

    private static string SanitizeProfile(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var clean = new string(value.Where(c => char.IsAsciiLetterOrDigit(c) || c == '-').Take(32).ToArray());
        return clean;
    }

    public static string IdentityFile => Path.Combine(DataDir, "identity.bin");
    public static string LocalKeyFile => Path.Combine(DataDir, "localkey.bin");
    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string DatabaseFile => Path.Combine(DataDir, "history.db");
    public static string DirectoryFile => Path.Combine(DataDir, "directory.json");
    public static string PinnedOrgKeyFile => Path.Combine(DataDir, "org.pub");
    public static string AdminKeyFile => Path.Combine(DataDir, "orgkey.bin");
    public static string AvatarFile => Path.Combine(DataDir, "avatar.png");
    public static string AvatarCacheDir => Path.Combine(DataDir, "avatars");
    public static string SentImagesDir => Path.Combine(DataDir, "sent");
    public static string LogFile => Path.Combine(DataDir, "vtalk.log");

    /// <summary>BT tarafından dağıtılan, değiştirilemez organizasyon açık anahtarı (exe yanında veya ProgramData).</summary>
    public static IEnumerable<string> DeployedOrgKeyCandidates()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "org.pub");
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "VTalk", "org.pub");
    }

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(AvatarCacheDir);
        Directory.CreateDirectory(SentImagesDir);
        RestrictToCurrentUser(DataDir);
    }

    /// <summary>Veri klasörünü yalnızca mevcut kullanıcı ve SYSTEM erişebilecek şekilde kısıtlar.</summary>
    private static void RestrictToCurrentUser(string dir)
    {
        try
        {
            var info = new DirectoryInfo(dir);
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            var user = WindowsIdentity.GetCurrent().User;
            if (user == null) return;
            const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, inherit,
                PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            info.SetAccessControl(security);
        }
        catch (Exception ex)
        {
            Log.Warn("Veri klasörü izinleri ayarlanamadı: " + ex.Message);
        }
    }
}
