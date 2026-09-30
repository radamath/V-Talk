using Microsoft.Win32;
using VTalk.Core;

namespace VTalk.Services;

/// <summary>Windows açılışında (yalnızca geçerli kullanıcı için, HKCU) otomatik başlatma.</summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "VTalk";

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key == null) return;
            var exe = Environment.ProcessPath;
            if (enabled && !string.IsNullOrEmpty(exe) && exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                key.SetValue(ValueName, $"\"{exe}\" --minimized");
            else if (!enabled)
                key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Log.Warn("Başlangıç kaydı güncellenemedi: " + ex.Message);
        }
    }
}
