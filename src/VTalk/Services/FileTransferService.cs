using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using VTalk.Core;
using VTalk.Net;

namespace VTalk.Services;

/// <summary>
/// Dosya aktarımı. Gönderen yalnızca kendi veritabanında o eşe teklif ettiği dosyaları sunar
/// (kimlik şifreli kanalla doğrulanır). Alıcı SHA-256 bütünlüğünü denetler, dosya adını temizler,
/// ".vtpart" geçici dosyasına yazar ve tamamlanınca Windows "Mark of the Web" (Zone.Identifier) ekler.
/// </summary>
public sealed class FileTransferService
{
    public const long MaxFileSize = 20L * 1024 * 1024 * 1024;
    private const int ChunkSize = 256 * 1024;
    private const int MaxConcurrentServes = 8;
    private const int MaxConcurrentDownloads = 4;
    private const int AutoAcceptMaxCountPerPeer = 30;
    private const long AutoAcceptMaxBytesPerPeer = 200L * 1024 * 1024;
    private const long AutoAcceptMaxBytesTotal = 500L * 1024 * 1024;
    private static readonly TimeSpan AutoAcceptWindow = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ChunkTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Sohbet içinde önizlenen resim türleri; içerik ayrıca <see cref="ImageSafety"/> ile doğrulanır.</summary>
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".gif", ".bmp" };

    /// <summary>
    /// Uyarı gösterilmeden açılabilecek türler (beyaz liste). Diğer her şey (exe, betik, kısayol, makrolu belge,
    /// disk görüntüsü, .chm, .xll, .settingcontent-ms, bilinmeyen yeni türler vb.) açılmadan önce onay ister.
    /// </summary>
    private static readonly HashSet<string> SafeOpenExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".txt", ".log", ".csv", ".pdf", ".docx", ".xlsx", ".pptx",
        ".zip", ".mp3", ".mp4", ".wav", ".m4a",
    };

    private readonly ChatService _chat;
    private readonly ConnectionPool _pool;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _downloads = new();
    private readonly SemaphoreSlim _serveSlots = new(MaxConcurrentServes, MaxConcurrentServes);
    private readonly SemaphoreSlim _downloadSlots = new(MaxConcurrentDownloads, MaxConcurrentDownloads);
    private readonly object _quotaGate = new();
    private readonly List<(DateTime at, string peerId, long bytes)> _autoAccepted = [];

    /// <summary>(peerId, msgId, alınan bayt, toplam bayt)</summary>
    public event Action<string, string, long, long>? Progress;

    public FileTransferService(ChatService chat, ConnectionPool pool)
    {
        _chat = chat;
        _pool = pool;
    }

    public static bool IsImageName(string name) => ImageExtensions.Contains(Path.GetExtension(name));

    public static bool NeedsOpenWarning(string name) => !SafeOpenExtensions.Contains(Path.GetExtension(name));

    public static string SanitizeFileName(string? name)
    {
        name = (name ?? "").Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        name = TextLimits.Clean(name, 255);
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            var c = invalid.Contains(ch) ? '_' : char.IsWhiteSpace(ch) ? ' ' : ch;
            // Art arda boşluklar daraltılır: "fatura.pdf          .exe" gibi uzantı gizleme hilesi engellenir.
            if (c == ' ' && sb.Length > 0 && sb[^1] == ' ') continue;
            sb.Append(c);
        }
        name = sb.ToString().Replace(" .", ".").Trim().TrimEnd('.', ' ');

        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        var device = stem.Split('.')[0].ToUpperInvariant();
        if (device is "CON" or "PRN" or "AUX" or "NUL" or "CLOCK$" ||
            (device.Length == 4 && (device.StartsWith("COM") || device.StartsWith("LPT")) && char.IsDigit(device[3])))
            stem = "_" + stem;
        if (ext.Length > 20) ext = ext[..20];
        if (stem.Length > 150) stem = stem[..150];
        name = (stem + ext).Trim();
        return string.IsNullOrWhiteSpace(stem) ? "dosya" + ext : name;
    }

    private static string UniquePath(string folder, string fileName)
    {
        var path = Path.Combine(folder, fileName);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (int i = 1; File.Exists(path) || File.Exists(path + ".vtpart"); i++)
            path = Path.Combine(folder, $"{stem} ({i}){ext}");
        return path;
    }

    // ------------------------------------------------------------------ Alıcı tarafı

    /// <summary>Otomatik resim alımı için kota: kişi başına adet/bayt ve toplam bayt, kayan pencere içinde.</summary>
    public bool TryReserveAutoAccept(string peerId, long bytes)
    {
        lock (_quotaGate)
        {
            var limit = DateTime.UtcNow - AutoAcceptWindow;
            _autoAccepted.RemoveAll(e => e.at < limit);
            var mine = _autoAccepted.Where(e => e.peerId == peerId).ToList();
            if (mine.Count >= AutoAcceptMaxCountPerPeer ||
                mine.Sum(e => e.bytes) + bytes > AutoAcceptMaxBytesPerPeer ||
                _autoAccepted.Sum(e => e.bytes) + bytes > AutoAcceptMaxBytesTotal)
            {
                Log.Warn($"Otomatik resim alma kotası doldu ({peerId[..8]}); kullanıcı onayı beklenecek.");
                return false;
            }
            _autoAccepted.Add((DateTime.UtcNow, peerId, bytes));
            return true;
        }
    }

    public void Accept(StoredMessage m)
    {
        if (m.Direction != MessageDirection.Incoming || m.Kind != MessageKind.File) return;
        if (m.Data.FileState is not (FileState.Offered or FileState.Failed)) return;
        var cts = new CancellationTokenSource();
        if (!_downloads.TryAdd(m.MsgId, cts)) return;
        _ = Task.Run(() => DownloadAsync(m, cts.Token));
    }

    public void Reject(StoredMessage m)
    {
        if (m.Direction != MessageDirection.Incoming || m.Data.FileState != FileState.Offered) return;
        SetState(m, FileState.Rejected);
        _ = _chat.SendControlAsync(m.PeerId, new Packet { T = PacketTypes.FileCancel, Id = m.MsgId });
    }

    public void Cancel(StoredMessage m)
    {
        if (m.Direction == MessageDirection.Incoming)
        {
            if (_downloads.TryRemove(m.MsgId, out var cts)) cts.Cancel();
            else if (m.Data.FileState == FileState.Offered) Reject(m);
        }
        else if (m.Data.FileState is FileState.Offered or FileState.Transferring)
        {
            SetState(m, FileState.Cancelled);
            _ = _chat.SendControlAsync(m.PeerId, new Packet { T = PacketTypes.FileCancel, Id = m.MsgId });
        }
    }

    /// <summary>Karşı taraf iptal etti: gönderen teklifini geri çektiyse ya da alıcı reddettiyse.</summary>
    public void OnRemoteCancel(string peerId, string msgId)
    {
        var incoming = _chat.Db.GetMessage(peerId, msgId, MessageDirection.Incoming);
        if (incoming is { Kind: MessageKind.File } && incoming.Data.FileState is FileState.Offered or FileState.Transferring)
        {
            if (_downloads.TryRemove(msgId, out var cts)) cts.Cancel();
            SetState(incoming, FileState.Cancelled);
        }
        var outgoing = _chat.Db.GetMessage(peerId, msgId, MessageDirection.Outgoing);
        if (outgoing is { Kind: MessageKind.File } && outgoing.Data.FileState is FileState.Offered or FileState.Transferring)
            SetState(outgoing, FileState.Rejected);
    }

    private void SetState(StoredMessage m, FileState state)
    {
        m.Data.FileState = state;
        _chat.Db.UpdateMessage(m);
        _chat.RaiseUpdated(m);
    }

    private async Task DownloadAsync(StoredMessage m, CancellationToken ct)
    {
        string? partPath = null;
        SecureChannel? channel = null;
        bool slot = false;
        try
        {
            SetState(m, FileState.Transferring);
            await _downloadSlots.WaitAsync(ct);
            slot = true;
            var peer = _chat.Registry.Get(m.PeerId);
            var peerName = _chat.Directory.GetMember(m.PeerId)?.Name is { Length: > 0 } dn ? dn : peer?.Name ?? m.PeerId[..8];
            var folder = Path.Combine(_chat.Settings.DownloadFolder, SanitizeFileName(peerName));
            Directory.CreateDirectory(folder);

            var root = Path.GetPathRoot(Path.GetFullPath(folder));
            if (root != null && new DriveInfo(root).AvailableFreeSpace < m.Data.FileSize + 100L * 1024 * 1024)
                throw new IOException("Diskte yeterli boş alan yok.");

            var finalPath = UniquePath(folder, SanitizeFileName(m.Data.FileName));
            partPath = finalPath + ".vtpart";

            channel = await _pool.OpenDedicatedAsync(m.PeerId, ct);
            await channel.SendAsync(new Packet { T = PacketTypes.FileGet, Id = m.MsgId }.Serialize(), ct);
            var response = Packet.TryParse(await ReceiveWithTimeout(channel, TimeSpan.FromSeconds(30), ct));
            if (response == null || response.T != PacketTypes.FileOk)
                throw new IOException(response?.Err is { Length: > 0 } err ? "Gönderen: " + err : "Gönderen dosyayı sunmadı.");
            if (response.Size != m.Data.FileSize) throw new IOException("Dosya boyutu teklifle uyuşmuyor.");

            long received = 0;
            var lastReport = DateTime.MinValue;
            using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            await using (var fs = new FileStream(partPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                while (received < m.Data.FileSize)
                {
                    var chunk = await ReceiveWithTimeout(channel, ChunkTimeout, ct);
                    if (chunk.Length == 0 || received + chunk.Length > m.Data.FileSize) throw new IOException("Beklenmeyen veri.");
                    await fs.WriteAsync(chunk, ct);
                    sha.AppendData(chunk);
                    received += chunk.Length;
                    if (DateTime.UtcNow - lastReport > TimeSpan.FromMilliseconds(200))
                    {
                        lastReport = DateTime.UtcNow;
                        Progress?.Invoke(m.PeerId, m.MsgId, received, m.Data.FileSize);
                    }
                }
                var actual = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
                if (!CryptoUtil.FixedEquals(actual, m.Data.FileSha256 ?? "")) throw new IOException("Dosya bütünlük denetimi başarısız.");
            }

            if (File.Exists(finalPath)) finalPath = UniquePath(folder, Path.GetFileName(finalPath));
            File.Move(partPath, finalPath);
            partPath = null;
            MarkOfTheWeb(finalPath);

            try { await channel.SendAsync(new Packet { T = PacketTypes.FileDone, Id = m.MsgId }.Serialize(), ct); } catch { }

            m.Data.FilePath = finalPath;
            if (m.Data.IsImage && !ImageSafety.IsSafeImage(finalPath)) m.Data.IsImage = false;
            Progress?.Invoke(m.PeerId, m.MsgId, m.Data.FileSize, m.Data.FileSize);
            SetState(m, FileState.Completed);
        }
        catch (Exception ex)
        {
            var cancelled = ct.IsCancellationRequested;
            if (!cancelled) Log.Warn($"Dosya indirilemedi: {ex.Message}");
            var current = _chat.Db.GetMessage(m.PeerId, m.MsgId, MessageDirection.Incoming);
            if (current?.Data.FileState != FileState.Cancelled)
                SetState(m, cancelled ? FileState.Cancelled : FileState.Failed);
            if (cancelled)
                _ = _chat.SendControlAsync(m.PeerId, new Packet { T = PacketTypes.FileCancel, Id = m.MsgId });
        }
        finally
        {
            if (slot) _downloadSlots.Release();
            if (channel != null) await channel.DisposeAsync();
            if (partPath != null) try { File.Delete(partPath); } catch { }
            if (_downloads.TryRemove(m.MsgId, out var cts)) cts.Dispose();
        }
    }

    private static async Task<byte[]> ReceiveWithTimeout(SecureChannel channel, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        return await channel.ReceiveAsync(cts.Token);
    }

    private static void MarkOfTheWeb(string path)
    {
        try
        {
            File.WriteAllText(path + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\nReferrerUrl=vtalk://lan\r\n");
        }
        catch (Exception ex)
        {
            Log.Warn("Zone.Identifier yazılamadı: " + ex.Message);
        }
    }

    // ------------------------------------------------------------------ Gönderen tarafı

    public async Task ServeAsync(SecureChannel channel, Packet request)
    {
        var peerId = channel.RemotePeerId;
        if (!Packet.IsValidId(request.Id))
        {
            await TrySend(channel, new Packet { T = PacketTypes.Error, Re = request.Id, Err = "Geçersiz istek." });
            return;
        }
        if (!_chat.IsPeerAllowed(peerId))
        {
            await TrySend(channel, new Packet { T = PacketTypes.Error, Re = request.Id, Err = "Yetkiniz yok." });
            return;
        }
        if (!await _serveSlots.WaitAsync(TimeSpan.FromSeconds(30)))
        {
            await TrySend(channel, new Packet { T = PacketTypes.Error, Re = request.Id, Err = "Gönderen meşgul, sonra tekrar deneyin." });
            return;
        }
        StoredMessage? m = null;
        try
        {
            // Yalnızca bu eşe teklif edilmiş, iptal edilmemiş dosyalar sunulur.
            m = _chat.Db.GetMessage(peerId, request.Id!, MessageDirection.Outgoing);
            if (m is not { Kind: MessageKind.File } || m.Data.FileState is FileState.Cancelled || m.Data.FilePath == null)
            {
                await TrySend(channel, new Packet { T = PacketTypes.Error, Re = request.Id, Err = "Dosya teklifi bulunamadı." });
                return;
            }
            if (!File.Exists(m.Data.FilePath))
            {
                await TrySend(channel, new Packet { T = PacketTypes.Error, Re = request.Id, Err = "Dosya gönderenin bilgisayarında artık yok." });
                return;
            }

            await using var fs = new FileStream(m.Data.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
            if (fs.Length != m.Data.FileSize)
            {
                await TrySend(channel, new Packet { T = PacketTypes.Error, Re = request.Id, Err = "Dosya teklif edildikten sonra değiştirilmiş." });
                return;
            }

            SetState(m, FileState.Transferring);
            await channel.SendAsync(new Packet { T = PacketTypes.FileOk, Re = request.Id, Size = fs.Length }.Serialize());

            var buffer = new byte[ChunkSize];
            long sent = 0;
            var lastReport = DateTime.MinValue;
            while (sent < fs.Length)
            {
                int n = await fs.ReadAsync(buffer);
                if (n == 0) throw new IOException("Dosya beklenenden kısa.");
                using var cts = new CancellationTokenSource(ChunkTimeout);
                await channel.SendAsync(buffer.AsMemory(0, n), cts.Token);
                sent += n;
                if (DateTime.UtcNow - lastReport > TimeSpan.FromMilliseconds(200))
                {
                    lastReport = DateTime.UtcNow;
                    Progress?.Invoke(peerId, m.MsgId, sent, fs.Length);
                }
            }

            using var doneCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var done = Packet.TryParse(await channel.ReceiveAsync(doneCts.Token));
            if (done?.T == PacketTypes.FileDone)
            {
                Progress?.Invoke(peerId, m.MsgId, fs.Length, fs.Length);
                SetState(m, FileState.Completed);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Dosya sunulamadı: " + ex.Message);
            if (m != null)
            {
                var current = _chat.Db.GetMessage(peerId, m.MsgId, MessageDirection.Outgoing);
                if (current?.Data.FileState == FileState.Transferring) SetState(current, FileState.Offered);
            }
        }
        finally
        {
            _serveSlots.Release();
            await channel.DisposeAsync();
        }
    }

    private static async Task TrySend(SecureChannel channel, Packet p)
    {
        try { await channel.SendAsync(p.Serialize()); } catch { }
    }
}
