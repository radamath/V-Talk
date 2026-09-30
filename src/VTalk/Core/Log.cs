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
        Write("ERROR", ex == null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}");

    private const int MaxLineLength = 2000;

    private static void Write(string level, string message)
    {
        // Mesajın bir kısmı karşı taraftan gelebilir: satır sonu / kontrol karakterleriyle sahte günlük satırı üretilemez.
        message = TextLimits.Clean(message, MaxLineLength);
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
        catch
        {
            // Günlük yazılamaması uygulamayı durdurmamalı.
        }
    }
}
