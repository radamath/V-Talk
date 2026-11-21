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

/// <summary>Admin bilgisayarından güncelleme paketinin indirilmesi, devam ettirilmesi ve bekleyen pakete yükseltilmesi.</summary>
public sealed partial class AppUpdate
{
    private async Task<Version?> DownloadAsync(string peerId, long size, string sha, Version remote, CancellationToken ct)
    {
        SecureChannel? channel = null;
        var part = PendingMsi + ".part";
        try
        {
            Directory.CreateDirectory(PendingDir);
            // Aynı paketin yarım/tam .part dosyasını değerlendir; tamamsa yeniden indirme.
            var priorSha = File.Exists(StagedShaFile) ? File.ReadAllText(StagedShaFile).Trim() : null;
            long resumeAt = 0;
            if (File.Exists(part))
            {
                var len = new FileInfo(part).Length;
                var same = string.Equals(priorSha, sha, StringComparison.OrdinalIgnoreCase);
                if (same && len > 0 && len == size && len <= MaxBytes)
                {
                    if (TryPromotePart(part, size, sha, remote))
                    {
                        LogDownload("Tam .part doğrulandı, yeniden indirilmedi.");
                        return remote;
                    }
                    LogDownload("Tam .part doğrulanamadı; silinip yeniden indirilecek.");
                    TryDeleteFile(part, LogDownload);
                }
                else if (same && len > 0 && len < size && len <= MaxBytes)
                {
                    resumeAt = len;
                    LogDownload($"Resume ofset={resumeAt}/{size}");
                }
                else
                {
                    LogDownload($"Part uyumsuz (len={len}, size={size}, sameSha={same}); baştan.");
                    TryDeleteFile(part, LogDownload);
                    resumeAt = 0;
                }
            }
            File.WriteAllText(StagedShaFile, sha.ToLowerInvariant());

            Packet? response = null;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                if (channel != null)
                {
                    await channel.DisposeAsync();
                    channel = null;
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                }
                channel = await _chat.Pool.OpenDedicatedAsync(peerId, ct);
                await channel.SendAsync(new Packet
                {
                    T = PacketTypes.UpdateGet,
                    Size = size,
                    Sha = sha,
                    St = resumeAt > 0 && resumeAt <= int.MaxValue ? (int)resumeAt : 0,
                }.Serialize());
                response = Packet.TryParse(await Receive(channel, TimeSpan.FromSeconds(30)));
                if (response?.T == PacketTypes.Error && response.Err == "busy") continue;
                break;
            }
            if (channel == null || response == null || response.T != PacketTypes.UpdateOk || response.Size != size ||
                !string.Equals(response.Sha, sha, StringComparison.OrdinalIgnoreCase))
            {
                Log.Warn($"Güncelleme teklifi reddedildi ({response?.Err ?? response?.T ?? "yok"}).");
                return null;
            }

            // Sunucu ofseti onaylamadıysa (eski admin) baştan indir.
            var serverOffset = response.St > 0 ? response.St : 0;
            if (serverOffset != resumeAt) resumeAt = 0;

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var mode = resumeAt > 0 ? FileMode.OpenOrCreate : FileMode.Create;
            await using (var fs = new FileStream(part, mode, FileAccess.ReadWrite, FileShare.None, 1 << 16, useAsync: true))
            {
                if (resumeAt > 0)
                {
                    if (fs.Length < resumeAt) resumeAt = 0;
                    if (resumeAt > 0)
                    {
                        fs.Seek(0, SeekOrigin.Begin);
                        var buf = new byte[ChunkSize];
                        long hashed = 0;
                        while (hashed < resumeAt)
                        {
                            int n = await fs.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, resumeAt - hashed)), ct);
                            if (n <= 0) break;
                            hash.AppendData(buf.AsSpan(0, n));
                            hashed += n;
                        }
                        fs.Seek(resumeAt, SeekOrigin.Begin);
                        fs.SetLength(resumeAt);
                    }
                    else
                    {
                        fs.SetLength(0);
                    }
                }

                long received = resumeAt;
                while (received < size)
                {
                    ct.ThrowIfCancellationRequested();
                    var chunk = await Receive(channel, ChunkTimeout);
                    if (chunk.Length == 0 || received + chunk.Length > size)
                        throw new IOException($"Beklenmeyen güncelleme verisi ({received}/{size}).");
                    await fs.WriteAsync(chunk, ct);
                    hash.AppendData(chunk);
                    received += chunk.Length;
                }
                await fs.FlushAsync(ct);
            }

            var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (!CryptoUtil.FixedEquals(actual, sha))
                throw new IOException($"Güncelleme bütünlük denetimi başarısız (sha mismatch).");
            if (!TryPromotePart(part, size, sha, remote))
                throw new IOException("Güncelleme paketi imzası veya ürün kimliği uyuşmuyor.");
            LogDownload($"İndirme tamam: {FormatVersion(remote)} ({size} bayt)");
            return remote;
        }
        catch (OperationCanceledException)
        {
            LogDownload("İndirme iptal/zaman aşımı (part korunur).");
            throw;
        }
        catch (Exception ex)
        {
            // .part dosyasını silme — sonraki denemede devam edilir.
            LogDownload("İndirme hatası: " + ex.Message);
            Log.Warn($"Güncelleme indirilemedi: {ex.Message}");
            return null;
        }
        finally
        {
            if (channel != null) await channel.DisposeAsync();
        }
    }

    /// <summary>
    /// Tamamlanmış .part → VTalk.msi. Authenticode MSI SIP yalnızca .msi uzantısında çalışır;
    /// .part üzerinde imza doğrulaması yanlışlıkla başarısız olurdu.
    /// </summary>
    private static bool TryPromotePart(string part, long size, string sha, Version remote)
    {
        try
        {
            if (!File.Exists(part) || new FileInfo(part).Length != size) return false;
            using (var sha256 = SHA256.Create())
            using (var fs = File.OpenRead(part))
            {
                var actual = Convert.ToHexString(sha256.ComputeHash(fs)).ToLowerInvariant();
                if (!CryptoUtil.FixedEquals(actual, sha))
                {
                    LogDownload("Promote: SHA uyuşmuyor.");
                    return false;
                }
            }

            if (File.Exists(PendingMsi)) File.Delete(PendingMsi);
            File.Move(part, PendingMsi);

            if (!PackageTrust.IsVTalkUpdate(PendingMsi, out var packaged, out var why) ||
                Normalize(packaged) != Normalize(remote))
            {
                LogDownload($"Promote: imza/kimlik reddedildi (paket={packaged}, beklenen={remote}, neden={why ?? "sürüm"}).");
                try
                {
                    File.Move(PendingMsi, part);
                }
                catch (Exception ex) when (IsFileError(ex))
                {
                    LogDownload("Reddedilen paket .part'a taşınamadı, siliniyor: " + ex.Message);
                    TryDeleteFile(PendingMsi, LogDownload);
                }
                return false;
            }

            File.WriteAllText(PendingVersionFile, Normalize(remote).ToString());
            File.WriteAllText(StagedShaFile, sha.ToLowerInvariant());
            return true;
        }
        catch (Exception ex)
        {
            LogDownload("Promote başarısız: " + ex.Message);
            return false;
        }
    }

    private static void LogDownload(string message)
    {
        try
        {
            Directory.CreateDirectory(PendingDir);
            File.AppendAllText(DownloadLogFile,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch (Exception ex) when (IsFileError(ex))
        {
            Log.Warn("Güncelleme indirme günlüğü yazılamadı: " + ex.Message);
        }
        Log.Warn("Güncelleme: " + message);
    }
}
