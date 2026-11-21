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
/// Bildirim seslerini çalışma anında sentezler (ses dosyası dağıtılmaz). Yeni mesaj sesi hazır seslerden
/// (<see cref="NotificationSounds"/>) seçilir; kullanıcı isterse kendi .wav dosyasını da kullanabilir.
/// </summary>
public sealed class SoundService
{
    private readonly Settings _settings;
    private readonly Dictionary<string, byte[]> _cache = new();
    private DateTime _lastPlay = DateTime.MinValue;

    private string? _warnedCustomSound;

    public SoundService(Settings settings) => _settings = settings;

    /// <summary>Seçili ses dosyası silinmişse her mesajda değil, dosya başına bir kez uyarılır (varsayılan ses çalar).</summary>
    private void WarnCustomSoundOnce(string path, string? error)
    {
        if (string.Equals(_warnedCustomSound, path, StringComparison.OrdinalIgnoreCase)) return;
        _warnedCustomSound = path;
        Log.Warn($"Özel bildirim sesi kullanılamadı ({Path.GetFileName(path)}): {error}. Varsayılan ses çalınıyor; Ayarlar'dan yeni ses seçilebilir.");
    }

    /// <summary>
    /// Sesi arka planda çalar. Ses aygıtının açılması (uzak masaüstü / AnyDesk ses yönlendirmesi, sürücü) saniyeler
    /// sürebildiğinden arayüz iş parçacığı beklemez.
    /// </summary>
    /// <param name="force">true ise ayarlardaki «Sesleri çal» kapalı olsa da çalar (@bahsetme).</param>
    public void Play(SoundKind kind, bool force = false)
    {
        if (!force && !_settings.SoundEnabled) return;
        if (DateTime.UtcNow - _lastPlay < TimeSpan.FromMilliseconds(400)) return;
        _lastPlay = DateTime.UtcNow;
        if (Interlocked.Exchange(ref _playing, 1) == 1) return;
        byte[]? custom = null;
        string key = kind.ToString();
        if (kind == SoundKind.Message)
        {
            var (builtInId, customPath) = ResolveMessageSound(_settings);
            if (customPath != null)
            {
                if (TryLoadPlayableWav(customPath, out var wav, out var error)) custom = wav;
                else WarnCustomSoundOnce(customPath, error);
            }
            key = "message:" + builtInId;
        }
        StartPlayback(key, custom, () => kind == SoundKind.Message ? NotificationSounds.Find(key["message:".Length..]).Generate() : Generate(kind));
    }

    /// <summary>Ses seçim penceresinde dinletme: «Sesleri çal» ayarından bağımsızdır.</summary>
    public void Preview(string soundId, string? customPath)
    {
        if (soundId == NotificationSounds.CustomId)
        {
            if (TryLoadPlayableWav(customPath, out var wav, out _)) StartPlayback("preview:custom", wav, () => wav!);
            return;
        }
        var sound = NotificationSounds.Find(soundId);
        StartPlayback("message:" + sound.Id, null, sound.Generate);
    }

    /// <summary>
    /// Yeni mesaj sesinin kaynağı. Seçim yoksa ama eski sürümden kalan özel dosya varsa o çalar (geriye uyumluluk);
    /// hazır bir ses seçilince ayar penceresi özel yolu temizler.
    /// </summary>
    public static (string BuiltInId, string? CustomPath) ResolveMessageSound(Settings settings)
    {
        var id = settings.MessageSoundId;
        var hasCustom = !string.IsNullOrWhiteSpace(settings.CustomSoundPath);
        if (id == NotificationSounds.CustomId || (hasCustom && (string.IsNullOrEmpty(id) || id == NotificationSounds.DefaultId)))
            return (NotificationSounds.DefaultId, hasCustom ? settings.CustomSoundPath : null);
        return (NotificationSounds.Find(id).Id, null);
    }

    private int _playing;

    private void StartPlayback(string cacheKey, byte[]? ready, Func<byte[]> generate)
    {
        Task.Run(() =>
        {
            try { PlaySync(cacheKey, ready, generate); }
            finally { Volatile.Write(ref _playing, 0); }
        });
    }

    private void PlaySync(string cacheKey, byte[]? ready, Func<byte[]> generate)
    {
        try
        {
            byte[] wav;
            if (ready != null)
                wav = ready;
            else
            {
                lock (_cache)
                {
                    if (!_cache.TryGetValue(cacheKey, out wav!))
                        _cache[cacheKey] = wav = generate();
                }
            }
            using var player = new SoundPlayer(new MemoryStream(wav));
            player.PlaySync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException)
        {
            Log.Warn("Ses çalınamadı: " + ex.Message);
        }
    }

    public static bool IsUsableCustomSound(string? path) => TryLoadPlayableWav(path, out _, out _);

    /// <summary>
    /// SoundPlayer yalnızca sıkıştırılmamış PCM çalar. MP3, OGG ve 24/32 bit wav burada reddedilir.
    /// </summary>
    public static bool TryLoadPlayableWav(string? path, out byte[]? wav, out string? error)
    {
        wav = null;
        error = null;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            error = "Dosya bulunamadı.";
            return false;
        }
        if (!string.Equals(Path.GetExtension(path), ".wav", StringComparison.OrdinalIgnoreCase))
        {
            error = "Yalnızca .wav seçilebilir.";
            return false;
        }
        var length = new FileInfo(path).Length;
        if (length is <= 44 or >= 5 * 1024 * 1024)
        {
            error = "Dosya 44 bayttan büyük ve 5 MB'tan küçük olmalı.";
            return false;
        }
        try
        {
            wav = File.ReadAllBytes(path);
        }
        catch (Exception ex)
        {
            error = "Dosya okunamadı: " + ex.Message;
            wav = null;
            return false;
        }
        if (!TryNormalizePcm(wav, out var pcm))
        {
            wav = null;
            error = "Sıkıştırılmamış PCM .wav olmalı: 8 veya 16 bit, tek ya da çift kanal. MP3, OGG ve 24/32 bit dosyalar çalınmaz.";
            return false;
        }
        wav = pcm;
        return true;
    }

    /// <summary>Klasik PCM'i olduğu gibi bırakır. Modern araçların yazdığı genişletilmiş PCM başlığını SoundPlayer'ın çaldığı biçime çevirir.</summary>
    private static bool TryNormalizePcm(byte[] data, out byte[]? pcm)
    {
        pcm = null;
        if (data.Length < 44 || data[0] != (byte)'R' || data[1] != (byte)'I' || data[2] != (byte)'F' || data[3] != (byte)'F')
            return false;
        if (data[8] != (byte)'W' || data[9] != (byte)'A' || data[10] != (byte)'V' || data[11] != (byte)'E')
            return false;
        int pos = 12;
        short format = -1, channels = 0, bits = 0;
        int sampleRate = 0;
        int dataAt = -1, dataLen = 0;
        while (pos + 8 <= data.Length)
        {
            int size = BitConverter.ToInt32(data, pos + 4);
            if (size < 0 || pos + 8 + size > data.Length) return false;
            bool fmt = data[pos] == (byte)'f' && data[pos + 1] == (byte)'m' && data[pos + 2] == (byte)'t' && data[pos + 3] == (byte)' ';
            bool samples = data[pos] == (byte)'d' && data[pos + 1] == (byte)'a' && data[pos + 2] == (byte)'t' && data[pos + 3] == (byte)'a';
            if (fmt && size >= 16)
            {
                format = BitConverter.ToInt16(data, pos + 8);
                channels = BitConverter.ToInt16(data, pos + 10);
                sampleRate = BitConverter.ToInt32(data, pos + 12);
                bits = BitConverter.ToInt16(data, pos + 22);
                if (format == -2 && size >= 40)
                {
                    int guid = pos + 32;
                    bool pcmGuid = data[guid] == 1 && data[guid + 1] == 0 && data[guid + 2] == 0 && data[guid + 3] == 0;
                    if (!pcmGuid) return false;
                    format = 1;
                }
            }
            else if (samples)
            {
                dataAt = pos + 8;
                dataLen = size;
            }
            pos += 8 + size + (size & 1);
        }
        if (format != 1 || channels is not (1 or 2) || bits is not (8 or 16) || sampleRate < 8000 || dataAt < 0 || dataLen <= 0)
            return false;
        pcm = data;
        if (BitConverter.ToInt16(data, 20) == 1) return true;
        int block = channels * (bits / 8);
        using var ms = new MemoryStream(44 + dataLen);
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8.ToArray());
        w.Write(36 + dataLen);
        w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray());
        w.Write(16);
        w.Write((short)1);
        w.Write(channels);
        w.Write(sampleRate);
        w.Write(sampleRate * block);
        w.Write((short)block);
        w.Write(bits);
        w.Write("data"u8.ToArray());
        w.Write(dataLen);
        w.Write(data, dataAt, dataLen);
        w.Flush();
        pcm = ms.ToArray();
        return true;
    }

    private static byte[] Generate(SoundKind kind) => kind switch
    {
        // Üç notalı yumuşak yükselen arpej (kişi çevrimiçi)
        SoundKind.Online => SoundSynth.Notes(0.75, (0.00, 523.25, 0.35), (0.12, 659.25, 0.35), (0.24, 783.99, 0.40)),
        // Duyuru: dört notalı zil
        SoundKind.Bulletin => SoundSynth.Notes(0.9, (0.00, 783.99, 0.35), (0.14, 1046.50, 0.35), (0.28, 1318.51, 0.35), (0.42, 1567.98, 0.40)),
        SoundKind.Nudge => SoundSynth.Buzz(),
        _ => NotificationSounds.Find(NotificationSounds.DefaultId).Generate(),
    };
}
