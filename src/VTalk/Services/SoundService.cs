using System.IO;
using System.Media;
using VTalk.Core;

namespace VTalk.Services;

public enum SoundKind
{
    Message,
    Nudge,
    Online,
    Bulletin,
}

/// <summary>
/// MSN Messenger'ı anımsatan bildirim seslerini çalışma anında sentezler (telifli ses dosyası dağıtılmaz).
/// Kullanıcı isterse kendi .wav dosyasını seçebilir.
/// </summary>
public sealed class SoundService
{
    private const int SampleRate = 44100;
    private readonly Settings _settings;
    private readonly Dictionary<SoundKind, byte[]> _cache = new();
    private DateTime _lastPlay = DateTime.MinValue;

    public SoundService(Settings settings) => _settings = settings;

    public void Play(SoundKind kind)
    {
        if (!_settings.SoundEnabled) return;
        if (DateTime.UtcNow - _lastPlay < TimeSpan.FromMilliseconds(400)) return;
        _lastPlay = DateTime.UtcNow;
        try
        {
            byte[] wav;
            if (kind == SoundKind.Message && IsUsableCustomSound(_settings.CustomSoundPath))
                wav = File.ReadAllBytes(_settings.CustomSoundPath!);
            else
            {
                lock (_cache)
                {
                    if (!_cache.TryGetValue(kind, out wav!))
                        _cache[kind] = wav = Generate(kind);
                }
            }
            var player = new SoundPlayer(new MemoryStream(wav));
            player.Play();
        }
        catch (Exception ex)
        {
            Log.Warn("Ses çalınamadı: " + ex.Message);
        }
    }

    public static bool IsUsableCustomSound(string? path) =>
        !string.IsNullOrWhiteSpace(path) && File.Exists(path) &&
        string.Equals(Path.GetExtension(path), ".wav", StringComparison.OrdinalIgnoreCase) &&
        new FileInfo(path).Length is > 44 and < 5 * 1024 * 1024;

    private static byte[] Generate(SoundKind kind) => kind switch
    {
        // Yükselen iki notalı parlak "di-dıng" (yeni mesaj)
        SoundKind.Message => Render(0.55, (0.00, 1046.50, 0.40), (0.11, 1567.98, 0.42)),
        // Üç notalı yumuşak yükselen arpej (kişi çevrimiçi)
        SoundKind.Online => Render(0.75, (0.00, 523.25, 0.35), (0.12, 659.25, 0.35), (0.24, 783.99, 0.40)),
        // Duyuru: dört notalı zil
        SoundKind.Bulletin => Render(0.9, (0.00, 783.99, 0.35), (0.14, 1046.50, 0.35), (0.28, 1318.51, 0.35), (0.42, 1567.98, 0.40)),
        _ => RenderBuzz(),
    };

    /// <summary>Marimba/çan benzeri tını: temel frekans + üst harmonikler, üstel sönüm.</summary>
    private static byte[] Render(double duration, params (double start, double freq, double decay)[] notes)
    {
        int n = (int)(SampleRate * duration);
        var samples = new double[n];
        foreach (var (start, freq, decay) in notes)
        {
            int s0 = (int)(start * SampleRate);
            for (int i = s0; i < n; i++)
            {
                double t = (i - s0) / (double)SampleRate;
                double attack = Math.Min(1.0, t / 0.004);
                double env = attack * Math.Exp(-t / (decay * 0.35));
                double v = Math.Sin(2 * Math.PI * freq * t)
                         + 0.35 * Math.Sin(2 * Math.PI * freq * 2.0 * t) * Math.Exp(-t / 0.05)
                         + 0.15 * Math.Sin(2 * Math.PI * freq * 3.01 * t) * Math.Exp(-t / 0.03);
                samples[i] += v * env;
            }
        }
        return ToWav(samples, 0.28);
    }

    /// <summary>Titreşim (nudge) için kısa, tremolo'lu vızıltı.</summary>
    private static byte[] RenderBuzz()
    {
        int n = (int)(SampleRate * 0.6);
        var samples = new double[n];
        for (int i = 0; i < n; i++)
        {
            double t = i / (double)SampleRate;
            double tremolo = 0.5 + 0.5 * Math.Sign(Math.Sin(2 * Math.PI * 14 * t));
            double env = Math.Min(1.0, t / 0.01) * Math.Min(1.0, (0.6 - t) / 0.05);
            double v = Math.Sin(2 * Math.PI * 140 * t) + 0.5 * Math.Sin(2 * Math.PI * 280 * t) + 0.25 * Math.Sin(2 * Math.PI * 420 * t);
            samples[i] = v * tremolo * env;
        }
        return ToWav(samples, 0.30);
    }

    private static byte[] ToWav(double[] samples, double volume)
    {
        double peak = samples.Max(Math.Abs);
        if (peak <= 0) peak = 1;
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        int dataLen = samples.Length * 2;
        w.Write("RIFF"u8.ToArray());
        w.Write(36 + dataLen);
        w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray());
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(SampleRate);
        w.Write(SampleRate * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write("data"u8.ToArray());
        w.Write(dataLen);
        foreach (var s in samples)
            w.Write((short)Math.Clamp(s / peak * volume * short.MaxValue, short.MinValue, short.MaxValue));
        w.Flush();
        return ms.ToArray();
    }
}
