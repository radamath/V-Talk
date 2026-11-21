using System.IO;
using VTalk.Security;
using VTalk.Services;
using Xunit.Abstractions;

namespace VTalk.Tests;

public class UpdateTrustTests(ITestOutputHelper output)
{
    private const string Sys = @"C:\Windows\System32";
    private const string Msi = @"C:\Program Files\V-Talk\update-staging\VTalk.msi";
    private const string Exe = @"C:\Program Files\V-Talk\VTalk.exe";
    private const string LogFile = @"C:\Program Files\V-Talk\update-staging\apply.log";

    // ------------------------------------------------------------ Kurulum komut satırı (SYSTEM olarak çalışır)

    [Fact]
    public void InstallCommand_QuotesEveryPath_AndRunsInstallThenRelaunch()
    {
        var args = AppUpdate.BuildInstallCommandLine(Sys, Msi, Exe, LogFile);

        Assert.NotNull(args);
        Assert.StartsWith("/d /v:on /s /c \"", args);
        Assert.Contains($"\"{Sys}\\msiexec.exe\" /i \"{Msi}\" /qn", args);
        Assert.Contains($">>\"{LogFile}\"", args);
        Assert.Contains($"del /f /q \"{Msi}\"", args);
        Assert.EndsWith($"\"{Exe}\" --launch-in-session\"", args);
    }

    [Theory]
    [InlineData("\"")]
    [InlineData("%")]
    [InlineData("!")]
    [InlineData("^")]
    [InlineData("&")]
    [InlineData("|")]
    [InlineData("<")]
    [InlineData(">")]
    public void InstallCommand_RejectsCmdMetacharacters_InAnyPath(string ch)
    {
        // Ör. "C:\x&calc.exe" yolu, SYSTEM olarak çalışan satıra ek komut sokardı.
        Assert.Null(AppUpdate.BuildInstallCommandLine(Sys, @"C:\up" + ch + "x.msi", Exe, LogFile));
        Assert.Null(AppUpdate.BuildInstallCommandLine(Sys, Msi, @"C:\V" + ch + @"\VTalk.exe", LogFile));
        Assert.Null(AppUpdate.BuildInstallCommandLine(Sys, Msi, Exe, @"C:\l" + ch + "og.txt"));
        Assert.Null(AppUpdate.BuildInstallCommandLine(@"C:\Win" + ch, Msi, Exe, LogFile));
    }

    [Fact]
    public void InstallCommand_RejectsEmptyPath()
    {
        Assert.Null(AppUpdate.BuildInstallCommandLine(Sys, "", Exe, LogFile));
    }

    [Fact]
    public void VersionComparison_IgnoresTrailingZeroComponents()
    {
        Assert.False(AppUpdate.IsNewer(new Version(1, 4, 0, 0), new Version(1, 4)));
        Assert.True(AppUpdate.IsNewer(new Version(1, 4, 0, 1), new Version(1, 4)));
        Assert.False(AppUpdate.IsNewer(new Version(1, 3, 32), new Version(1, 4)));
        Assert.Equal("1.4", AppUpdate.FormatVersion(new Version(1, 4, 0, 0)));
        Assert.Equal("1.3.32", AppUpdate.FormatVersion(new Version(1, 3, 32, 0)));
    }

    // ------------------------------------------------------------ Diske yazılan güncelleme durumu

    [Theory]
    [InlineData("UpdateJob")]
    [InlineData("StoredClientStatus")]
    public void PersistedUpdateState_SurvivesObfuscation(string typeName)
    {
        // Karartma kurucu parametre adlarını siler: konumsal record JSON'a ne yazılabilir ne okunabilir. 1.4.0'da bu
        // yüzden admin paneli "Güncelleniyor (1/3)" durumunda kalıyordu. Parametresiz kurucu ve sabit JSON adları şart.
        var type = typeof(AppUpdate).GetNestedType(typeName, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)!;
        Assert.NotNull(type.GetConstructor(Type.EmptyTypes));
        foreach (var property in type.GetProperties().Where(p => p.Name != "EqualityContract"))
            Assert.True(property.GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonPropertyNameAttribute), false).Length == 1,
                $"{typeName}.{property.Name} sabit JSON adı taşımıyor");
    }

    [Fact]
    public void PersistedUpdateState_RoundTrips()
    {
        Assert.True(AppUpdate.PersistedStateRoundTrips(out var error), error);
    }

    // ------------------------------------------------------------ Paket doğrulama

    [Fact]
    public void Package_MissingFile_IsRejected()
    {
        Assert.False(PackageTrust.IsVTalkUpdate(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".msi"), out _, out var why));
        Assert.Equal("Dosya yok", why);
    }

    [Fact]
    public void Package_NonMsiFile_IsRejected()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".msi");
        File.WriteAllBytes(path, System.Security.Cryptography.RandomNumberGenerator.GetBytes(8192));
        try
        {
            Assert.False(PackageTrust.IsVTalkUpdate(path, out var version, out var why));
            Assert.Equal(new Version(0, 0), version);
            Assert.NotNull(why);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Gerçek imzalı paket depoda değil (installer\out .gitignore'da); yalnızca derlenmiş bir paket varsa çalışır.
    /// Paket yoksa test, neyin denetlenmediğini çıktıya yazarak geçer.
    /// </summary>
    [Fact]
    public void Package_SignedBuild_IsAccepted_AndTamperedCopy_IsRejected()
    {
        var signed = FindLatestBuiltMsi();
        if (signed == null)
        {
            output.WriteLine("installer\\out altında derlenmiş MSI yok; imzalı paket denetimi atlandı.");
            return;
        }

        Assert.True(PackageTrust.IsVTalkUpdate(signed, out var version, out var why), why);
        Assert.True(version > new Version(1, 0));

        var tampered = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".msi");
        var bytes = File.ReadAllBytes(signed);
        // İmza bloğu dosyanın sonundadır; içerik ortasındaki tek bayt değişikliği imza özetini bozmalı.
        bytes[bytes.Length / 2] ^= 0xFF;
        File.WriteAllBytes(tampered, bytes);
        try
        {
            Assert.False(PackageTrust.IsVTalkUpdate(tampered, out _, out var tamperedWhy));
            output.WriteLine($"Değiştirilmiş paket reddedildi: {tamperedWhy}");
        }
        finally
        {
            File.Delete(tampered);
        }
    }

    private static string? FindLatestBuiltMsi()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var outDir = Path.Combine(dir.FullName, "installer", "out");
            if (!Directory.Exists(outDir)) continue;
            return new DirectoryInfo(outDir).GetFiles("VTalk-*.msi")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault()?.FullName;
        }
        return null;
    }
}
