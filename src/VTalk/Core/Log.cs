using System.IO;

namespace VTalk.Core;

/// <summary>Basit, boyut sınırlı dosya günlüğü. Mesaj içerikleri asla günlüğe yazılmaz.</summary>
public static class Log
{
    private static readonly object Gate = new();
    private const long MaxBytes = 2 * 1024 * 1024;

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex == null ? message : $"{message}: {Describe(ex)}", MaxErrorLength);

    private const int MaxLineLength = 2000;
    private const int MaxErrorLength = 8000;

    /// <summary>Tür, mesaj ve iç hatalarla birlikte yığın izi; satır sonları tek satıra indirilir.</summary>
    private static string Describe(Exception ex)
    {
        string detail;
        try { detail = ex.ToString(); }
        catch { detail = $"{ex.GetType().FullName}: {ex.Message}"; }
        return detail.Replace("\r\n", " | ").Replace('\n', '|');
    }

    private static void Write(string level, string message, int maxLength = MaxLineLength)
    {
        // Mesajın bir kısmı karşı taraftan gelebilir: satır sonu / kontrol karakterleriyle sahte günlük satırı üretilemez.
        message = TextLimits.Clean(message, maxLength);
        try
        {
            lock (Gate)
            {
                var file = AppPaths.LogFile;
                var fi = new FileInfo(file);
                if (fi.Exists && fi.Length > MaxBytes)
                {
                    File.Copy(file, file + ".old", overwrite: true);
                    File.Delete(file);
                }
                File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Günlük yazılamaması uygulamayı durdurmamalı; günlüğe yazılamayan hata yalnızca izleyiciye gider.
            System.Diagnostics.Trace.TraceWarning($"V-Talk günlüğü yazılamadı: {ex.Message}");
        }
    }
}
