using System.IO;
using System.Security.Cryptography;
using VTalk.Core;
using VTalk.Services;

namespace VTalk.Tests;

public class NotificationSoundTests
{
    [Fact]
    public void Catalog_HasTwelveDistinctSounds_WithStableDefault()
    {
        Assert.Equal(12, NotificationSounds.All.Count);
        Assert.Equal(NotificationSounds.All.Count, NotificationSounds.All.Select(s => s.Id).Distinct().Count());
        Assert.Equal(NotificationSounds.DefaultId, NotificationSounds.All[0].Id);
        Assert.DoesNotContain(NotificationSounds.All, s => s.Id == NotificationSounds.CustomId);
        Assert.Equal(NotificationSounds.DefaultId, NotificationSounds.Find("kaldırılmış-ses").Id);
    }

    [Fact]
    public void EverySound_IsPlayablePcm_Audible_AndUnique()
    {
        var hashes = new HashSet<string>();
        foreach (var sound in NotificationSounds.All)
        {
            var wav = sound.Generate();
            var path = Path.Combine(Path.GetTempPath(), $"vtalk-sound-{Guid.NewGuid():N}.wav");
            File.WriteAllBytes(path, wav);
            try
            {
                // Uygulamanın özel ses için kullandığı doğrulamanın aynısı: SoundPlayer'ın çalabileceği PCM.
                Assert.True(SoundService.TryLoadPlayableWav(path, out _, out var error), $"{sound.Id}: {error}");
            }
            finally
            {
                File.Delete(path);
            }

            var seconds = (wav.Length - 44) / 2.0 / SoundSynth.SampleRate;
            Assert.InRange(seconds, 0.05, 1.5);
            var peak = Enumerable.Range(0, (wav.Length - 44) / 2).Max(i => Math.Abs((int)BitConverter.ToInt16(wav, 44 + i * 2)));
            Assert.True(peak > short.MaxValue / 10, $"{sound.Id} neredeyse sessiz");
            Assert.True(peak < short.MaxValue, $"{sound.Id} kırpılıyor");
            Assert.True(hashes.Add(Convert.ToHexString(SHA256.HashData(wav))), $"{sound.Id} başka bir sesle aynı");
        }
    }

    [Theory]
    [InlineData("bell", null, "bell", null)]
    [InlineData("classic", null, "classic", null)]
    [InlineData("custom", @"C:\ses.wav", "classic", @"C:\ses.wav")]
    // Eski sürümden kalan ayar: kimlik yok/varsayılan ama özel dosya seçili → özel dosya çalmaya devam etsin.
    [InlineData("classic", @"C:\ses.wav", "classic", @"C:\ses.wav")]
    [InlineData("", @"C:\ses.wav", "classic", @"C:\ses.wav")]
    // Hazır bir ses açıkça seçildiyse eski özel yol yok sayılır.
    [InlineData("drop", @"C:\ses.wav", "drop", null)]
    [InlineData("bilinmeyen", null, "classic", null)]
    public void MessageSoundSelection_IsResolvedConsistently(string id, string? customPath, string expectedId, string? expectedPath)
    {
        var settings = new Settings { MessageSoundId = id, CustomSoundPath = customPath };
        var (builtIn, path) = SoundService.ResolveMessageSound(settings);
        Assert.Equal(expectedId, builtIn);
        Assert.Equal(expectedPath, path);
    }
}
