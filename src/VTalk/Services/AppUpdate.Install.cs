using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using VTalk.Core;
using VTalk.Net;
using VTalk.Security;

namespace VTalk.Services;

/// <summary>Yükseltilmiş kurulum: SYSTEM görevi / UAC, paket hazırlama ve doğrulama, msiexec ve kullanıcı oturumunda yeniden başlatma.</summary>
public sealed partial class AppUpdate
{
    /// <summary>
    /// <c>--apply-update</c>. Yükseltilmiş (SYSTEM görevi / UAC) süreçte bekleyen MSI'yi yalnızca yöneticinin yazabildiği
    /// hazırlık klasörüne kopyalar, kopyayı doğrular ve kopyayı kurar. Kullanıcıların yazabildiği pending klasöründen
    /// hiçbir şey doğrudan çalıştırılmaz ve oraya yazılmaz/silinmez (bağlantı noktası / sabit bağlantı saldırıları).
    /// </summary>
    public static int ApplyPending()
    {
        if (!IsElevatedProcess())
        {
            // Eski sürümlerin bıraktığı sahte kilit: kurulumu 25 dk sessizce iptal ediyordu.
            TryDeleteFile(Path.Combine(PendingDir, "apply.lock"));
            ApplyLog("Yükseltme yok; VTalkUpdate tetikleniyor.");
            TryEnsureUpdateTask();
            if (!TryRunScheduledTask() && !TryElevateApply())
            {
                ApplyLog("SYSTEM görevi ve UAC tetiklenemedi.");
                return 1;
            }
            return 0;
        }

        using var gate = new Mutex(false, @"Global\VTalk-ApplyUpdate");
        var owned = false;
        try
        {
            try { owned = gate.WaitOne(TimeSpan.FromSeconds(8)); }
            catch (AbandonedMutexException) { owned = true; }
            if (!owned)
            {
                ApplyLog("Başka kurulum kilidi var; çıkış kodu 1.");
                return 1;
            }

            if (!TryGetTrustedInstallExe(out var exe, out var stagingDir, out var why))
            {
                ApplyLog("Kurulum klasörü güvenilir değil: " + why);
                return 1;
            }
            Directory.CreateDirectory(stagingDir);
            // Hazırlık klasörü Program Files altındadır (yalnızca yönetici yazabilir); günlük artık oraya yazılır.
            _elevatedLogDir = stagingDir;
            ApplyLog($"ApplyPending start Current={CurrentText}");

            if (!TryReadPendingVersion(out var advertised))
            {
                ApplyLog("Pending sürüm yok, okunamadı veya güncel değil; çıkış.");
                return 0;
            }

            var stagedMsi = Path.Combine(stagingDir, "VTalk.msi");
            if (!TryCopyPendingMsi(stagedMsi))
            {
                ApplyLog("Pending MSI hazırlık klasörüne kopyalanamadı.");
                return 1;
            }
            // Doğrulama ve kurulum aynı, kullanıcıların değiştiremediği kopya üzerinde yapılır (TOCTOU yok).
            if (!PackageTrust.IsVTalkUpdate(stagedMsi, out var packaged, out var reason) ||
                Normalize(packaged) != Normalize(advertised) || !IsNewer(packaged, Current))
            {
                ApplyLog($"Paket doğrulanamadı (paket={packaged}, advertised={advertised}, neden={reason ?? "sürüm"}).");
                TryDeleteFile(stagedMsi);
                return 1;
            }

            // Yalnızca diğer VTalk süreçlerini öldür — taskkill /IM KENDİNİ de öldürürdü (kök bug).
            ForceKillOtherVTalk();
            Thread.Sleep(2000);
            ForceKillOtherVTalk();

            // msiexec bu süreç çıktıktan sonra çalışmalı; aksi halde çalışan VTalk.exe kilitli kalır ve kurulum
            // yeniden başlatmaya ertelenir. Betik dosyası yazılmaz: komut satırı yalnızca yönetici yollarını içerir.
            if (!TryStartDetachedInstall(stagedMsi, exe, Path.Combine(stagingDir, "apply.log")))
                return 1;
            ApplyLog($"Kurulum başlatıldı: {FormatVersion(packaged)}");
            return 0;
        }
        catch (Exception ex)
        {
            ApplyLog("ApplyPending hata: " + ex.Message);
            Log.Error("Güncelleme kurulamadı", ex);
            return 1;
        }
        finally
        {
            if (owned)
            {
                try
                {
                    gate.ReleaseMutex();
                }
                catch (ApplicationException ex)
                {
                    ApplyLog("Kurulum kilidi bırakılamadı: " + ex.Message);
                }
            }
        }
    }

    /// <summary>Yükseltilmiş süreçte günlük klasörü; yalnızca yöneticinin yazabildiği hazırlık klasörü.</summary>
    private static string? _elevatedLogDir;

    private static void ApplyLog(string message)
    {
        Log.Warn("Güncelleme apply: " + message);
        // Yükseltilmiş süreç kullanıcıların yazabildiği pending klasörüne asla yazmaz: oradaki bir bağlantı noktası
        // veya sabit bağlantı, SYSTEM yazmasını başka bir dosyaya yönlendirebilir.
        var dir = _elevatedLogDir ?? (IsElevatedProcess() ? null : PendingDir);
        if (dir == null) return;
        try
        {
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "apply.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Dosya günlüğü yardımcıdır; mesaj yukarıda uygulama günlüğüne zaten yazıldı.
        }
    }

    /// <summary>
    /// Çalışan exe'nin ve klasörünün yalnızca SYSTEM / Administrators / geçerli kullanıcı tarafından yazılabildiğini
    /// doğrular (ör. Program Files). Hazırlık klasörü bu klasörün altındadır.
    /// </summary>
    private static bool TryGetTrustedInstallExe(out string exe, out string stagingDir, out string? reason)
    {
        exe = Environment.ProcessPath ?? "";
        stagingDir = "";
        var installDir = Path.GetDirectoryName(exe);
        if (exe.Length == 0 || installDir == null || !File.Exists(exe))
        {
            reason = "exe yolu bulunamadı";
            return false;
        }
        if (!FileTrust.IsProtectedFromOtherUsers(exe, out reason)) return false;
        stagingDir = Path.Combine(installDir, StagingDirName);
        return true;
    }

    private static bool TryReadPendingVersion(out Version advertised)
    {
        advertised = new Version(0, 0);
        var info = new FileInfo(PendingVersionFile);
        if (!info.Exists || info.Length > MaxVersionFileBytes) return false;
        return Version.TryParse(File.ReadAllText(info.FullName).Trim(), out advertised!) && IsNewer(advertised, Current);
    }

    /// <summary>Kullanıcıların yazabildiği pending MSI'yi yönetici klasörüne kopyalar; sonraki her işlem kopya üzerindedir.</summary>
    private static bool TryCopyPendingMsi(string destination)
    {
        try
        {
            TryDeleteFile(destination);
            using var source = new FileStream(PendingMsi, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (source.Length is < MinPackageBytes or > MaxBytes)
            {
                ApplyLog($"Pending MSI boyutu geçersiz ({source.Length}).");
                return false;
            }
            using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            source.CopyTo(target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ApplyLog("MSI kopyalanamadı: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Kısa bir beklemeden sonra msiexec'i ve ardından yeni sürümü kullanıcı oturumunda başlatan bağımsız cmd süreci.
    /// Bütün yollar yöneticiye ait klasörlerdedir; cmd özel karakteri içeren yol reddedilir.
    /// </summary>
    private static bool TryStartDetachedInstall(string msi, string exe, string log)
    {
        var sys = Environment.SystemDirectory;
        if (BuildInstallCommandLine(sys, msi, exe, log) is not { } arguments)
        {
            ApplyLog("Kurulum yolu cmd için güvenli değil.");
            return false;
        }
        try
        {
            using var process = Process.Start(new ProcessStartInfo(Path.Combine(sys, "cmd.exe"), arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = sys,
            });
            if (process != null) return true;
            ApplyLog("Kurulum süreci başlatılamadı.");
            return false;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ApplyLog("Kurulum süreci başlatılamadı: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// cmd.exe argümanlarını üretir. Yollardan biri cmd'nin yorumladığı bir karakter içeriyorsa (tırnak, %, !, ^, &amp;,
    /// |, &lt;, &gt;) null döner: SYSTEM olarak çalışan satıra komut eklenemesin.
    /// </summary>
    internal static string? BuildInstallCommandLine(string systemDir, string msi, string exe, string log)
    {
        if (new[] { msi, exe, log, systemDir }.Any(p => string.IsNullOrEmpty(p) || p.IndexOfAny(CmdSpecialChars) >= 0))
            return null;
        string ping = Path.Combine(systemDir, "PING.EXE"), msiexec = Path.Combine(systemDir, "msiexec.exe");
        var line = $"\"{ping}\" -n {InstallStartDelaySeconds + 1} 127.0.0.1 >nul" +
                   $" & \"{msiexec}\" /i \"{msi}\" /qn /norestart REBOOT=ReallySuppress" +
                   $" & echo !DATE! !TIME! msiexec=!ERRORLEVEL!>>\"{log}\"" +
                   $" & del /f /q \"{msi}\"" +
                   $" & \"{exe}\" --launch-in-session";
        return $"/d /v:on /s /c \"{line}\"";
    }

    /// <param name="log">Varsayılan <see cref="ApplyLog"/>; indirme akışında indirme günlüğü verilir.</param>
    private static void TryDeleteFile(string path, Action<string>? log = null)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (IsFileError(ex))
        {
            (log ?? ApplyLog)($"Dosya silinemedi ({Path.GetFileName(path)}): {ex.Message}");
        }
    }

    private static bool IsFileError(Exception ex) => ex is IOException or UnauthorizedAccessException;

    private static void ForceKillOtherVTalk()
    {
        // ASLA "taskkill /IM VTalk.exe" kullanma: --apply-update süreci de VTalk.exe'dir, kendini öldürür.
        var self = Environment.ProcessId;
        foreach (var process in Process.GetProcessesByName("VTalk"))
        {
            try
            {
                if (process.Id == self) continue;
                ApplyLog($"VTalk PID {process.Id} kapatılıyor.");
                process.Kill(entireProcessTree: true);
                process.WaitForExit(8000);
            }
            catch (Exception ex)
            {
                ApplyLog("VTalk kapatılamadı: " + ex.Message);
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    /// <summary>
    /// SYSTEM sürecinden (`--launch-in-session`): aktif konsol oturumunda VTalk --show.
    /// PowerShell kullanılmaz (ExecutionPolicy engeli yok).
    /// </summary>
    public static int LaunchInUserSession()
    {
        try
        {
            if (!TryGetTrustedInstallExe(out var exe, out var stagingDir, out var why))
            {
                ApplyLog("launch-in-session: kurulum klasörü güvenilir değil: " + why);
                return 1;
            }
            if (Directory.Exists(stagingDir)) _elevatedLogDir = stagingDir;
            if (SessionProcess.TryStartInActiveSession(exe, "--show", out var detail))
            {
                ApplyLog("launch-in-session OK: " + detail);
                return 0;
            }
            ApplyLog("launch-in-session FAILED: " + detail);
            return 1;
        }
        catch (Exception ex)
        {
            ApplyLog("launch-in-session hata: " + ex.Message);
            return 1;
        }
    }

    private static bool TryStartApply(bool allowUac = true)
    {
        // SYSTEM görevi "VTalk.exe --apply-update" çalıştırır; paket orada yönetici klasörüne kopyalanıp doğrulanır.
        TryEnsureUpdateTask();
        if (TryRunScheduledTask()) return true;
        return allowUac && TryElevateApply();
    }

    private static bool TryRunScheduledTask()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("schtasks.exe", $"/Run /TN {UpdateTaskName}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            });
            if (process == null) return false;
            process.WaitForExit(15000);
            if (process.ExitCode == 0)
            {
                ApplyLog($"schtasks /Run {UpdateTaskName} OK");
                return true;
            }
            var err = process.StandardError.ReadToEnd().Trim();
            ApplyLog("schtasks /Run başarısız: " + process.ExitCode + (err.Length > 0 ? " " + err : ""));
            return false;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ApplyLog("schtasks /Run hata: " + ex.Message);
            return false;
        }
    }

    /// <summary>Görev yok/yetkisizse UAC ile aynı doğrulamalı yol: "VTalk.exe --apply-update" (kullanıcı onaylarsa).</summary>
    private static bool TryElevateApply()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe) || !File.Exists(PendingMsi)) return false;
            ApplyLog("UAC runas --apply-update deneniyor.");
            using var process = Process.Start(new ProcessStartInfo(exe, "--apply-update")
            {
                UseShellExecute = true,
                Verb = "runas",
            });
            return process != null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ApplyLog("UAC runas reddedildi/başarısız: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Tetikleyicisiz SYSTEM görevi: eylem = korumalı kurulum klasöründeki "VTalk.exe --apply-update".
    /// Yalnızca yükseltilmiş süreç kaydedebilir; normalde MSI CustomAction (register-update.cmd) kaydeder.
    /// Eylem asla kullanıcıların yazabildiği bir dosyayı (ör. pending klasöründe betik) göstermez.
    /// </summary>
    private static void TryEnsureUpdateTask()
    {
        if (!IsElevatedProcess()) return;
        if (!TryGetTrustedInstallExe(out var exe, out _, out var why))
        {
            ApplyLog("Güncelleme görevi kaydedilmedi: " + why);
            return;
        }
        try
        {
            var psi = new ProcessStartInfo("powershell.exe",
                "-NoProfile -ExecutionPolicy Bypass -Command \"" +
                "try { $ErrorActionPreference='Stop'; $exe=$env:VTALK_UPDATE_EXE; " +
                $"Unregister-ScheduledTask -TaskName '{UpdateTaskName}' -Confirm:$false -ErrorAction SilentlyContinue; " +
                "$a=New-ScheduledTaskAction -Execute $exe -Argument '--apply-update'; " +
                "$p=New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest; " +
                "$s=New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable " +
                "-ExecutionTimeLimit (New-TimeSpan -Minutes 30) -MultipleInstances IgnoreNew; " +
                $"Register-ScheduledTask -TaskName '{UpdateTaskName}' -Action $a -Principal $p -Settings $s -Force | Out-Null; " +
                "$svc=New-Object -ComObject Schedule.Service; $svc.Connect(); " +
                $"$svc.GetFolder('\').GetTask('{UpdateTaskName}').SetSecurityDescriptor('D:(A;;FA;;;BA)(A;;FA;;;SY)(A;;FRFX;;;AU)',0) " +
                "} catch { exit 1 }\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            psi.Environment["VTALK_UPDATE_EXE"] = exe;
            using var process = Process.Start(psi);
            process?.WaitForExit(20000);
            if (process is { ExitCode: not 0 })
                ApplyLog($"{UpdateTaskName} görevi kaydedilemedi (PowerShell çıkış={process.ExitCode}).");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ApplyLog($"{UpdateTaskName} görevi kaydedilemedi: " + ex.Message);
        }
    }

    private static bool IsElevatedProcess()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            // SYSTEM görevinden gelen --apply-update: Administrator rolü her ortamda true olmayabilir.
            if (id.User?.IsWellKnown(WellKnownSidType.LocalSystemSid) == true) return true;
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (System.Security.SecurityException ex)
        {
            Log.Warn("Süreç yetkisi okunamadı: " + ex.Message);
            return false;
        }
    }
}
