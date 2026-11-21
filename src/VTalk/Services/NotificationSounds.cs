using System.IO;

namespace VTalk.Services;

/// <summary>Seçilebilir yerleşik mesaj sesi. Ses dosyası dağıtılmaz; dalga biçimi çalışma anında üretilir (telif/lisans yok).</summary>
public sealed record NotificationSound(string Id, string Name, string Description, Func<byte[]> Generate);

/// <summary>
/// Hazır bildirim sesleri. Kimlikler ayarlara yazılır; değiştirilmemeli. Yeni ses eklenebilir, var olan kaldırılırsa
/// ayardaki kimlik bulunamaz ve <see cref="DefaultId"/> çalınır.
/// </summary>
public static class NotificationSounds
{
    public const string DefaultId = "classic";
    /// <summary>Kullanıcının kendi .wav dosyası (yol Settings.CustomSoundPath'te).</summary>
    public const string CustomId = "custom";

    public static IReadOnlyList<NotificationSound> All { get; } =
    [
        new(DefaultId, "Klasik", "Yükselen iki nota (varsayılan)",
            () => SoundSynth.Notes(0.55, (0.00, 1046.50, 0.40), (0.11, 1567.98, 0.42))),
        new("drop", "Damla", "Su damlası",
            () => SoundSynth.Sweep(0.22, 1400, 520, 0.06, 0.30)),
        new("bell", "Zil", "Tek, uzun çınlayan zil",
            () => SoundSynth.Bell(1.2, 880)),
        new("pop", "Pop", "Kısa ve yumuşak tık",
            () => SoundSynth.Sweep(0.09, 900, 300, 0.018, 0.34)),
        new("arpeggio", "Arpej", "Yükselen üç nota",
            () => SoundSynth.Notes(0.75, (0.00, 523.25, 0.35), (0.10, 659.25, 0.35), (0.20, 783.99, 0.45))),
        new("marimba", "Marimba", "Alçak, tahta tınılı iki vuruş",
            () => SoundSynth.Notes(0.5, (0.00, 392.00, 0.22), (0.12, 587.33, 0.26))),
        new("xylophone", "Ksilofon", "İnen üç parlak nota",
            () => SoundSynth.Notes(0.6, (0.00, 1567.98, 0.18), (0.08, 1318.51, 0.18), (0.16, 1046.50, 0.28))),
        new("crystal", "Kristal", "İnce, parıltılı çan",
            () => SoundSynth.Bell(0.9, 2093.0, shimmer: true)),
        new("soft", "Yumuşak", "Hafif, iki notalı uğultu",
            () => SoundSynth.Soft(0.8, 659.25, 880.0)),
        new("digital", "Dijital", "İki kısa elektronik bip",
            () => SoundSynth.Beeps(0.32, 1760, 2)),
        new("chirp", "Cıvıltı", "Kuş cıvıltısı gibi yükselen",
            () => SoundSynth.Chirp(0.35)),
        new("dingdong", "Ding-dong", "Kapı zili gibi inen iki nota",
            () => SoundSynth.Notes(1.1, (0.00, 783.99, 0.75), (0.30, 622.25, 0.90))),
    ];

    public static NotificationSound Find(string? id) =>
        All.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal)) ?? All[0];
}

/// <summary>Bildirim seslerinin sentezi: 44,1 kHz, 16 bit, tek kanal PCM WAV.</summary>
internal static class SoundSynth
{
    public const int SampleRate = 44100;

    /// <summary>Marimba/çan benzeri tını: temel frekans + üst harmonikler, üstel sönüm.</summary>
    public static byte[] Notes(double duration, params (double start, double freq, double decay)[] notes)
    {
        var samples = new double[(int)(SampleRate * duration)];
        foreach (var (start, freq, decay) in notes)
        {
            int s0 = (int)(start * SampleRate);
            for (int i = s0; i < samples.Length; i++)
            {
                double t = (i - s0) / (double)SampleRate;
                double env = Attack(t) * Math.Exp(-t / (decay * 0.35));
                double v = Math.Sin(2 * Math.PI * freq * t)
                         + 0.35 * Math.Sin(2 * Math.PI * freq * 2.0 * t) * Math.Exp(-t / 0.05)
                         + 0.15 * Math.Sin(2 * Math.PI * freq * 3.01 * t) * Math.Exp(-t / 0.03);
                samples[i] += v * env;
            }
        }
        return ToWav(samples, 0.28);
    }

    /// <summary>Frekansı üstel olarak kayan kısa ton (damla, pop).</summary>
    public static byte[] Sweep(double duration, double fromHz, double toHz, double decay, double volume)
    {
        var samples = new double[(int)(SampleRate * duration)];
        double phase = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            double t = i / (double)SampleRate;
            double freq = fromHz * Math.Pow(toHz / fromHz, t / duration);
            phase += 2 * Math.PI * freq / SampleRate;
            samples[i] = Math.Sin(phase) * Attack(t) * Math.Exp(-t / decay);
        }
        return ToWav(samples, volume);
    }

    /// <summary>Uyumsuz (inharmonik) kısmi seslerle çan; "shimmer" ince titreşim ekler.</summary>
    public static byte[] Bell(double duration, double baseHz, bool shimmer = false)
    {
        (double ratio, double amp, double decay)[] partials =
            [(1.0, 1.0, 0.45), (2.76, 0.55, 0.25), (5.40, 0.30, 0.14), (8.93, 0.15, 0.08)];
        var samples = new double[(int)(SampleRate * duration)];
        for (int i = 0; i < samples.Length; i++)
        {
            double t = i / (double)SampleRate;
            double v = 0;
            foreach (var (ratio, amp, decay) in partials)
                v += amp * Math.Sin(2 * Math.PI * baseHz * ratio * t) * Math.Exp(-t / (decay * duration));
            if (shimmer) v *= 1 + 0.15 * Math.Sin(2 * Math.PI * 7 * t);
            samples[i] = v * Attack(t);
        }
        return ToWav(samples, 0.26);
    }

    /// <summary>Yavaş yükselip sönen iki sinüs; sessiz ortamlar için.</summary>
    public static byte[] Soft(double duration, double firstHz, double secondHz)
    {
        var samples = new double[(int)(SampleRate * duration)];
        double half = duration / 2;
        for (int i = 0; i < samples.Length; i++)
        {
            double t = i / (double)SampleRate;
            double freq = t < half ? firstHz : secondHz;
            double local = t < half ? t : t - half;
            double env = Math.Sin(Math.PI * Math.Min(1.0, local / half));
            samples[i] = Math.Sin(2 * Math.PI * freq * t) * env;
        }
        return ToWav(samples, 0.22);
    }

    /// <summary>Yumuşatılmış kare dalga bipleri (yalnızca tek harmonikler, kulak tırmalamasın).</summary>
    public static byte[] Beeps(double duration, double freq, int count)
    {
        var samples = new double[(int)(SampleRate * duration)];
        double slot = duration / count;
        double beep = slot * 0.6;
        for (int i = 0; i < samples.Length; i++)
        {
            double t = i / (double)SampleRate;
            double local = t % slot;
            if (local > beep) continue;
            double env = Math.Min(1.0, local / 0.004) * Math.Min(1.0, (beep - local) / 0.01);
            double v = Math.Sin(2 * Math.PI * freq * t) + Math.Sin(2 * Math.PI * freq * 3 * t) / 3 + Math.Sin(2 * Math.PI * freq * 5 * t) / 5;
            samples[i] = v * env;
        }
        return ToWav(samples, 0.20);
    }

    /// <summary>Hızlı frekans modülasyonlu iki yükselen cıvıltı.</summary>
    public static byte[] Chirp(double duration)
    {
        var samples = new double[(int)(SampleRate * duration)];
        double phase = 0;
        double each = duration / 2;
        for (int i = 0; i < samples.Length; i++)
        {
            double t = i / (double)SampleRate;
            double local = t % each;
            double freq = 2200 + 1800 * (local / each) + 250 * Math.Sin(2 * Math.PI * 45 * t);
            phase += 2 * Math.PI * freq / SampleRate;
            double env = Math.Sin(Math.PI * local / each);
            samples[i] = Math.Sin(phase) * env * env;
        }
        return ToWav(samples, 0.22);
    }

    /// <summary>Titreşim (nudge) için kısa, tremolo'lu vızıltı.</summary>
    public static byte[] Buzz()
    {
        var samples = new double[(int)(SampleRate * 0.6)];
        for (int i = 0; i < samples.Length; i++)
        {
            double t = i / (double)SampleRate;
            double tremolo = 0.5 + 0.5 * Math.Sign(Math.Sin(2 * Math.PI * 14 * t));
            double env = Math.Min(1.0, t / 0.01) * Math.Min(1.0, (0.6 - t) / 0.05);
            double v = Math.Sin(2 * Math.PI * 140 * t) + 0.5 * Math.Sin(2 * Math.PI * 280 * t) + 0.25 * Math.Sin(2 * Math.PI * 420 * t);
            samples[i] = v * tremolo * env;
        }
        return ToWav(samples, 0.30);
    }

    /// <summary>Tık sesini önleyen 4 ms'lik yumuşak başlangıç.</summary>
    private static double Attack(double t) => Math.Min(1.0, t / 0.004);

    public static byte[] ToWav(double[] samples, double volume)
    {
        double peak = samples.Length == 0 ? 1 : samples.Max(Math.Abs);
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
