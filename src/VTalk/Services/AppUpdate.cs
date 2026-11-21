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
using System.Text.Json.Serialization;
using VTalk.Core;
using VTalk.Net;
using VTalk.Security;

namespace VTalk.Services;

/// <summary>
/// Admin tetiklemeli güncelleme. Otomatik yoklama yok; sıralı push, istemci durum bildirimi, 5 dk × 3 deneme.
/// </summary>
public sealed partial class AppUpdate
{
    public const long MaxBytes = 120L * 1024 * 1024;
    private const int ChunkSize = 64 * 1024;
    private const int MaxAttempts = 3;
    private const int DownloadRetriesPerAttempt = 4;
    private const long MinPackageBytes = 1024;
    private const int MaxVersionFileBytes = 64;
    private const int InstallStartDelaySeconds = 3;
    /// <summary>Kurulum dizininin altındaki, yalnızca yöneticinin yazabildiği MSI hazırlık klasörü.</summary>
    private const string StagingDirName = "update-staging";
    private const string UpdateTaskName = "VTalkUpdate";
    private static readonly char[] CmdSpecialChars = ['"', '%', '!', '^', '&', '|', '<', '>'];
    private static readonly TimeSpan ChunkTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan PerClientWait = TimeSpan.FromMinutes(50);
    /// <summary>"started" bildiren istemci ilk denemeyi bu süre içinde başlatmalı (iş kaydı + "progress" anında gelir).</summary>
    private static readonly TimeSpan FirstAttemptWait = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Bu sürümlerin istemci tarafı güncelleme işi karartılmış derlemede çöküyordu: komutu alır, "started" der ve susar.
    /// Uzaktan güncellenemezler; sırayı bekletmeden "bir kez elle kurun" denir.
    /// </summary>
    private static readonly string[] RemotelyUnupdatableVersions = ["1.4"];
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static Version Current { get; } = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);
    public static string CurrentText => FormatVersion(Current);

    private readonly ChatService _chat;
    private readonly SemaphoreSlim _serveSlots = new(2, 2);
    private readonly SemaphoreSlim _pushGate = new(1, 1);
    private readonly ConcurrentDictionary<string, ClientUpdateInfo> _clientInfo = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ClientUpdateInfo>> _waiters = new();
    private int _jobRunning;
    private static readonly object HashGate = new();
    private static (DateTime write, long length, string sha)? _publishedHash;

    public event Action? ClientStatusChanged;

    public AppUpdate(ChatService chat) => _chat = chat;

    public static bool IsNewer(Version remote, Version local) => Normalize(remote) > Normalize(local);

    public static string FormatVersion(Version version)
    {
        var n = Normalize(version);
        return n.Revision > 0 ? n.ToString() : n.Build > 0 ? $"{n.Major}.{n.Minor}.{n.Build}" : $"{n.Major}.{n.Minor}";
    }

    private const int MaxVersionTextLength = 32;
    /// <summary>İstemcinin bildirdiği hata metni (istisna mesajı içerebilir) panelde bu uzunlukta kesilir.</summary>
    private const int MaxStatusMessageLength = 512;

    /// <summary>
    /// Ağdan (duyuru, Pong, durum paketi) veya diskten gelen sürüm metnini sürüm numarasına indirger.
    /// Sürüm olarak çözülemeyen ya da aşırı uzun metin null döner; yönetici panelinde ve durum dosyasında
    /// karşı tarafın seçtiği keyfi metin görünmez.
    /// </summary>
    public static string? CleanVersionText(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > MaxVersionTextLength) return null;
        return Version.TryParse(raw.Trim(), out var parsed) ? FormatVersion(parsed) : null;
    }

    private static Version Normalize(Version version) =>
        new(version.Major, version.Minor, Math.Max(version.Build, 0), Math.Max(version.Revision, 0));

    public ClientUpdateInfo GetClientInfo(string peerId) =>
        _clientInfo.GetOrAdd(peerId, id => new ClientUpdateInfo { PeerId = id });

    public void ObserveVersion(string peerId, string? version)
    {
        if (CleanVersionText(version) is not { } trimmed) return;
        var info = GetClientInfo(peerId);
        var changed = !string.Equals(info.AppVersion, trimmed, StringComparison.OrdinalIgnoreCase);
        info.AppVersion = trimmed;

        // Kurulum oldu ama "done" paketi gelmediyse (yeniden açılmama vb.) takılı Güncelleniyor kalmasın.
        if (_chat.IsThisAdminHost &&
            info.Phase is UpdatePhase.Updating or UpdatePhase.Queued &&
            Version.TryParse(trimmed, out var reported))
        {
            var target = ReadPublished(out var offer) && offer != null ? offer.Version : Current;
            if (Normalize(reported) >= Normalize(target))
            {
                info.Phase = UpdatePhase.Done;
                info.LastError = null;
                info.LastMessage = null;
                info.LastUpdatedUtc = DateTime.UtcNow;
                // Önce sıra ve panel: diske yazma yalnızca yeniden başlatma sonrası bilgi içindir.
                CompleteWaiter(peerId, info);
                PersistDone(peerId, info);
                changed = true;
            }
        }

        if (changed) ClientStatusChanged?.Invoke();
    }

    /// <summary>Sıralı güncelleme. onlyPeerIds null ise tüm çevrimiçi istemciler.</summary>
    public async Task<UpdatePushReport> PushAsync(IReadOnlyList<string>? onlyPeerIds = null)
    {
        if (!_chat.IsThisAdminHost) throw new InvalidOperationException("İstemciler yalnızca admin bilgisayarından güncellenir.");
        if (_chat.Directory.GetMember(_chat.Identity.PeerId)?.Role != Role.Admin)
            throw new InvalidOperationException("Bu işlem için admin yetkisi gerekir.");
        if (!ReadPublished(out var offer) || offer == null)
            throw new InvalidOperationException("Yayımlanmış kurulum paketi yok.");
        if (!await _pushGate.WaitAsync(0))
            throw new InvalidOperationException("Zaten bir güncelleme sırası çalışıyor.");

        try
        {
            IEnumerable<PeerSnapshot> peers = _chat.Registry.Online.Where(p => p.PeerId != _chat.Identity.PeerId);
            if (onlyPeerIds is { Count: > 0 })
            {
                var set = onlyPeerIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
                peers = peers.Where(p => set.Contains(p.PeerId));
            }
            var list = peers.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            if (list.Count == 0)
                return new UpdatePushReport(offer.Version, 0, 0, 0, 0, ["Çevrimiçi istemci yok."]);

            int ok = 0, failed = 0, outdated = 0, unreachable = 0;
            var notes = new List<string>();

            foreach (var peer in list)
            {
                var label = string.IsNullOrWhiteSpace(peer.Name) ? peer.PeerId[..8] : peer.Name;
                var info = GetClientInfo(peer.PeerId);
                if (CleanVersionText(peer.AppVersion ?? info.AppVersion) is { } reported &&
                    RemotelyUnupdatableVersions.Contains(reported))
                {
                    outdated++;
                    info.Phase = UpdatePhase.Outdated;
                    info.LastError = $"{reported} uzaktan güncellenemez; bir kez elle kurun";
                    notes.Add($"{label}: {reported} sürümü uzaktan güncellenemez, bir kez elle kurun");
                    ClientStatusChanged?.Invoke();
                    continue;
                }
                info.Phase = UpdatePhase.Queued;
                info.LastError = null;
                info.Attempt = 0;
                ClientStatusChanged?.Invoke();

                Packet? reply = null;
                Exception? last = null;
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    try
                    {
                        if (attempt > 0) await Task.Delay(1000);
                        var conn = await _chat.Pool.GetAsync(peer.PeerId);
                        reply = await conn.RequestAsync(new Packet { T = PacketTypes.UpdateNow }, TimeSpan.FromSeconds(20));
                        last = null;
                        break;
                    }
                    catch (Exception ex) when (ex is IOException or SocketException or InvalidOperationException or TimeoutException
                                               or OperationCanceledException or PeerUnavailableException or ProtocolException)
                    {
                        last = ex;
                    }
                }

                if (last != null || reply == null)
                {
                    unreachable++;
                    info.Phase = UpdatePhase.Failed;
                    info.LastError = "Ulaşılamadı";
                    notes.Add($"{label}: ulaşılamadı");
                    ClientStatusChanged?.Invoke();
                    continue;
                }

                if (reply.Err is "unsupported" or "forbidden" || reply.T == PacketTypes.Error && reply.Err is "unsupported" or "forbidden")
                {
                    outdated++;
                    info.Phase = UpdatePhase.Outdated;
                    info.LastError = "Sürüm güncelleme protokolünü desteklemiyor";
                    notes.Add($"{label}: eski sürüm, bir kez elle kurun");
                    ClientStatusChanged?.Invoke();
                    continue;
                }

                if (reply.T != PacketTypes.Ack)
                {
                    unreachable++;
                    info.Phase = UpdatePhase.Failed;
                    info.LastError = reply.Err ?? reply.T;
                    notes.Add($"{label}: yanıt={info.LastError}");
                    ClientStatusChanged?.Invoke();
                    continue;
                }

                info.Phase = UpdatePhase.Updating;
                ClientStatusChanged?.Invoke();

                var waiter = new TaskCompletionSource<ClientUpdateInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters[peer.PeerId] = waiter;
                try
                {
                    // Eski istemciler durum bildirmez; 45 sn içinde "started" yoksa sırayı kilitleme.
                    var first = await Task.WhenAny(waiter.Task, Task.Delay(TimeSpan.FromSeconds(45)));
                    if (first != waiter.Task && info.Phase == UpdatePhase.Updating && info.Attempt == 0 && info.LastMessage == null)
                    {
                        outdated++;
                        info.Phase = UpdatePhase.Outdated;
                        info.LastError = "Durum bildirimi yok (eski istemci)";
                        notes.Add($"{label}: durum yok — bir kez elle 1.2.1+ kurun");
                        continue;
                    }

                    // "started" geldi ama ilk deneme hiç başlamadıysa istemcinin işi çökmüştür: 50 dk bekleme.
                    if (!waiter.Task.IsCompleted && info.Attempt == 0)
                        await Task.WhenAny(waiter.Task, Task.Delay(FirstAttemptWait));
                    if (!waiter.Task.IsCompleted && info.Attempt == 0)
                    {
                        failed++;
                        info.Phase = UpdatePhase.Failed;
                        info.LastError = "İstemci güncellemeyi başlatamadı; bir kez elle kurun";
                        notes.Add($"{label}: güncelleme başlamadı, bir kez elle kurun");
                        continue;
                    }

                    var completed = waiter.Task.IsCompleted
                        ? waiter.Task
                        : await Task.WhenAny(waiter.Task, Task.Delay(PerClientWait));
                    if (completed != waiter.Task)
                    {
                        failed++;
                        info.Phase = UpdatePhase.Failed;
                        info.LastError = "Zaman aşımı (yanıt yok)";
                        notes.Add($"{label}: zaman aşımı");
                    }
                    else
                    {
                        var result = await waiter.Task;
                        if (result.Phase == UpdatePhase.Done)
                        {
                            ok++;
                            notes.Add($"{label}: güncellendi ({result.AppVersion})");
                        }
                        else
                        {
                            failed++;
                            notes.Add($"{label}: {result.LastError ?? result.Phase.ToString()}");
                        }
                    }
                }
                finally
                {
                    _waiters.TryRemove(peer.PeerId, out _);
                    ClientStatusChanged?.Invoke();
                }
            }

            return new UpdatePushReport(offer.Version, ok, failed, outdated, unreachable, notes);
        }
        finally
        {
            _pushGate.Release();
        }
    }

    internal bool IsTrustedAdmin(string peerId)
    {
        var host = _chat.Deployment.AdminHost;
        var peer = _chat.Registry.Get(peerId);
        if (host == null || peer == null || !peer.IsOnline || peer.PeerId == _chat.Identity.PeerId) return false;
        if (!HostEquals(peer.Machine, host)) return false;
        return _chat.Directory.GetMember(peerId)?.Role == Role.Admin;
    }

    internal void OnStatusPacket(string peerId, Packet packet)
    {
        if (!_chat.IsThisAdminHost) return;
        var code = packet.Err ?? "";
        var info = GetClientInfo(peerId);
        if (CleanVersionText(packet.Name) is { } version) info.AppVersion = version;
        info.Attempt = packet.St > 0 ? packet.St : info.Attempt;
        var message = TextLimits.Clean(packet.Text, MaxStatusMessageLength);
        info.LastMessage = message.Length == 0 ? null : message;

        switch (code)
        {
            case "started":
            case "progress":
                info.Phase = UpdatePhase.Updating;
                info.LastError = null;
                break;
            case "done":
                info.Phase = UpdatePhase.Done;
                info.LastError = null;
                info.LastUpdatedUtc = DateTime.UtcNow;
                CompleteWaiter(peerId, info);
                PersistDone(peerId, info);
                break;
            case "error":
                info.Phase = UpdatePhase.Updating;
                info.LastError = info.LastMessage ?? "Hata";
                break;
            case "exhausted":
                info.Phase = UpdatePhase.Failed;
                info.LastError = info.LastMessage ?? "3 deneme başarısız";
                CompleteWaiter(peerId, info);
                break;
            default:
                info.LastError = code.Length > 0 ? code : info.LastMessage;
                break;
        }
        ClientStatusChanged?.Invoke();
    }

    private void CompleteWaiter(string peerId, ClientUpdateInfo info)
    {
        if (_waiters.TryRemove(peerId, out var tcs))
            tcs.TrySetResult(info.Clone());
    }

    /// <summary>Admin UpdateNow sonrası istemci işi. Otomatik yoklama yok.</summary>
    public async Task RunAdminJobAsync(string adminPeerId)
    {
        if (Interlocked.Exchange(ref _jobRunning, 1) == 1) return;
        try
        {
            if (_chat.Network.LoopbackOnly || _chat.IsThisAdminHost || _chat.Deployment.AdminHost == null) return;

            await ReportAsync(adminPeerId, "started", 0, "Güncelleme başlatıldı");

            string? lastError = null;
            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                WriteJob(new UpdateJob { AdminPeerId = adminPeerId, Attempt = attempt, StartedUtc = DateTime.UtcNow });
                await ReportAsync(adminPeerId, "progress", attempt, $"Deneme {attempt}/{MaxAttempts}");

                using var cts = new CancellationTokenSource(AttemptTimeout);
                try
                {
                    var result = await TryDownloadAndApplyAsync(cts.Token);
                    if (result == ApplyOutcome.AlreadyCurrent)
                    {
                        await ReportAsync(adminPeerId, "done", attempt, "Zaten güncel", CurrentText);
                        ClearJob();
                        return;
                    }
                    if (result == ApplyOutcome.ApplyStarted)
                    {
                        // msiexec sonrası süreç yeniden başlar; ReportStartupAsync yalnızca sürüm yükselince done gönderir.
                        var target = TryReadStaged(out var tv, out _) ? FormatVersion(tv!) : null;
                        WriteJob(new UpdateJob
                        {
                            AdminPeerId = adminPeerId, Attempt = attempt, StartedUtc = DateTime.UtcNow,
                            Phase = "awaiting-restart", TargetVersion = target,
                        });
                        await ReportAsync(adminPeerId, "progress", attempt, "Kurulum başlatıldı, yeniden başlatılıyor…");
                        ScheduleExitForApply();
                        return;
                    }
                    lastError = result switch
                    {
                        ApplyOutcome.NoOffer => "Admin paket sunmuyor",
                        ApplyOutcome.DownloadFailed => "Paket indirilemedi",
                        ApplyOutcome.ApplyFailed => "Kurulum başlatılamadı (VTalkUpdate görevi / yetki)",
                        _ => "Güncelleme başarısız",
                    };
                }
                catch (OperationCanceledException)
                {
                    lastError = "5 dakika içinde tamamlanamadı";
                }
                catch (Exception ex) when (ex is IOException or SocketException or InvalidOperationException or PeerUnavailableException or ProtocolException)
                {
                    lastError = ex.Message;
                }

                await ReportAsync(adminPeerId, "error", attempt, lastError ?? "Hata");
                if (attempt < MaxAttempts)
                    await Task.Delay(TimeSpan.FromSeconds(8));
            }

            await ReportAsync(adminPeerId, "exhausted", MaxAttempts, lastError ?? "3 deneme başarısız");
            ClearJob();
        }
        catch (Exception ex)
        {
            // Beklenmeyen hata admin'e bildirilmezse admin sırası istemciyi 50 dk bekler (1.4.0'daki durum).
            Log.Error("Güncelleme işi beklenmedik hatayla durdu", ex);
            await ReportAsync(adminPeerId, "exhausted", 0, "Güncelleme işi hatayla durdu: " + ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _jobRunning, 0);
        }
    }

    /// <summary>Açılışta bekleyen MSI'yi dene; sahte "done" gönderme.</summary>
    public async Task ReportStartupAsync()
    {
        if (_chat.IsThisAdminHost) return;

        // Kurulum yükseltilmiş süreçte pending klasörüne dokunmadan yapılır; artık yeni olmayan paketi burada temizle.
        if (File.Exists(PendingVersionFile) && !TryReadPendingVersion(out _)) DeletePending(keepSha: false);

        // İndirme tamam ama schtasks kaçtıysa / msiexec düştüyse her açılışta yeniden dene.
        if (TryReadStaged(out var staged, out _) && IsNewer(staged!, Current))
        {
            Log.Warn($"Bekleyen güncelleme {FormatVersion(staged!)} var; kurulum yeniden tetikleniyor.");
            if (TryReadJob(out var pendingJob) && pendingJob != null)
            {
                WriteJob(pendingJob with
                {
                    Phase = "awaiting-restart",
                    StartedUtc = DateTime.UtcNow,
                    TargetVersion = FormatVersion(staged!),
                });
            }
            // Açılışta UAC gösterme; yalnızca SYSTEM görevini dene.
            if (TryStartApply(allowUac: false))
            {
                ScheduleExitForApply();
                return;
            }
            Log.Warn("Bekleyen güncelleme tetiklenemedi (VTalkUpdate). Admin'den Güncelle veya UAC'li deneme gerekir.");
        }

        if (!TryReadJob(out var job) || job == null) return;
        var admin = job.AdminPeerId;
        if (string.IsNullOrEmpty(admin)) admin = FindAdminPeer();
        if (admin == null) return;

        if (job.Phase is "awaiting-restart" or "retry-pending")
        {
            if (IsJobTargetMet(job))
            {
                await ReportAsync(admin, "done", job.Attempt, "Güncelleme tamamlandı", CurrentText);
                ClearJob();
                return;
            }

            // Eski sürümle açıldı: kurulumu bitmemiş say, admin'e yalan "done" gönderme.
            await ReportAsync(admin, "error", job.Attempt,
                $"Kurulum tamamlanmadı (hâlâ {CurrentText})", CurrentText);
            WriteJob(job with { Phase = "retry-pending", StartedUtc = DateTime.UtcNow });
            return;
        }

        // Yarım kalmış iş: admin tekrar tetikleyene kadar bekle (otomatik yoklama yok).
    }

    private static bool IsJobTargetMet(UpdateJob job)
    {
        if (!string.IsNullOrWhiteSpace(job.TargetVersion) &&
            Version.TryParse(job.TargetVersion, out var expected))
            return Normalize(Current) >= Normalize(expected);

        // Eski job.json (TargetVersion yok): pending MSI kalmadıysa yükseltme olmuş kabul edilir.
        return !TryReadStaged(out _, out _);
    }

    private static void ScheduleExitForApply()
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(1500);
            Environment.Exit(0);
        });
    }

    public async Task ServeAsync(SecureChannel channel, Packet request)
    {
        if (!_chat.IsThisAdminHost || !_chat.IsPeerAllowed(channel.RemotePeerId) || !ReadPublished(out var offer) || offer == null)
        {
            await TrySend(channel, new Packet { T = PacketTypes.Error, Re = request.Id, Err = "none" });
            await channel.DisposeAsync();
            return;
        }
        if (!string.Equals(request.Sha, offer.Sha256, StringComparison.OrdinalIgnoreCase) || request.Size != offer.Size)
        {
            await TrySend(channel, new Packet { T = PacketTypes.Error, Re = request.Id, Err = "mismatch" });
            await channel.DisposeAsync();
            return;
        }
        // St = devam ofseti (byte). Eski istemciler 0 gönderir.
        long offset = request.St > 0 ? request.St : 0;
        if (offset < 0 || offset > offer.Size)
        {
            await TrySend(channel, new Packet { T = PacketTypes.Error, Re = request.Id, Err = "mismatch" });
            await channel.DisposeAsync();
            return;
        }
        if (!await _serveSlots.WaitAsync(TimeSpan.FromSeconds(20)))
        {
            await TrySend(channel, new Packet { T = PacketTypes.Error, Re = request.Id, Err = "busy" });
            await channel.DisposeAsync();
            return;
        }
        try
        {
            await using var fs = new FileStream(PublishedMsi, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (fs.Length != offer.Size)
            {
                await TrySend(channel, new Packet { T = PacketTypes.Error, Re = request.Id, Err = "mismatch" });
                return;
            }
            if (offset > 0) fs.Seek(offset, SeekOrigin.Begin);
            await channel.SendAsync(new Packet
            {
                T = PacketTypes.UpdateOk,
                Re = request.Id,
                Size = fs.Length,
                Sha = offer.Sha256,
                St = (int)offset,
            }.Serialize());
            var buffer = new byte[ChunkSize];
            long sent = offset;
            while (sent < fs.Length)
            {
                int n = await fs.ReadAsync(buffer);
                if (n <= 0) throw new IOException("Paket beklenenden kısa.");
                using var cts = new CancellationTokenSource(ChunkTimeout);
                await channel.SendAsync(buffer.AsMemory(0, n), cts.Token);
                sent += n;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Güncelleme paketi gönderilemedi: " + ex.Message);
        }
        finally
        {
            _serveSlots.Release();
            await channel.DisposeAsync();
        }
    }

    internal static bool ReadPublished(out UpdateOffer? offer)
    {
        offer = null;
        if (!File.Exists(PublishedMsi) || !File.Exists(PublishedVersionFile)) return false;
        var info = new FileInfo(PublishedMsi);
        if (info.Length is < MinPackageBytes or > MaxBytes) return false;
        if (!Version.TryParse(File.ReadAllText(PublishedVersionFile).Trim(), out var version)) return false;
        string sha;
        lock (HashGate)
        {
            if (_publishedHash is { } cached && cached.write == info.LastWriteTimeUtc && cached.length == info.Length)
                sha = cached.sha;
            else
            {
                using var sha256 = SHA256.Create();
                using var fs = info.OpenRead();
                sha = Convert.ToHexString(sha256.ComputeHash(fs)).ToLowerInvariant();
                _publishedHash = (info.LastWriteTimeUtc, info.Length, sha);
            }
        }
        offer = new UpdateOffer(version, info.Length, sha);
        return true;
    }

    private async Task<ApplyOutcome> TryDownloadAndApplyAsync(CancellationToken ct)
    {
        var admin = FindAdminPeer();
        if (admin == null) return ApplyOutcome.NoOffer;

        if (TryReadStaged(out var stagedVer, out _) && IsNewer(stagedVer!, Current))
        {
            return TryStartApply() ? ApplyOutcome.ApplyStarted : ApplyOutcome.ApplyFailed;
        }

        var conn = await _chat.Pool.GetAsync(admin, ct);
        var reply = await conn.RequestAsync(new Packet { T = PacketTypes.UpdateInfo }, TimeSpan.FromSeconds(20), ct);
        if (reply.T != PacketTypes.UpdateOk || reply.Size is <= 0 or > MaxBytes) return ApplyOutcome.NoOffer;
        if (reply.Sha is not { Length: 64 } || !reply.Sha.All(Uri.IsHexDigit)) return ApplyOutcome.DownloadFailed;
        if (!Version.TryParse(reply.Name, out var remote)) return ApplyOutcome.DownloadFailed;
        if (!IsNewer(remote, Current)) return ApplyOutcome.AlreadyCurrent;

        if (TryReadStaged(out _, out var haveSha) &&
            string.Equals(haveSha, reply.Sha, StringComparison.OrdinalIgnoreCase))
        {
            return TryStartApply() ? ApplyOutcome.ApplyStarted : ApplyOutcome.ApplyFailed;
        }

        Version? staged = null;
        for (var dig = 0; dig < DownloadRetriesPerAttempt && staged == null; dig++)
        {
            ct.ThrowIfCancellationRequested();
            if (dig > 0) await Task.Delay(TimeSpan.FromSeconds(2 + dig * 2), ct);
            staged = await DownloadAsync(admin, reply.Size, reply.Sha, remote, ct);
        }
        if (staged == null) return ApplyOutcome.DownloadFailed;
        // İstemciye toast/uyarı yok — sessiz kurulum; admin panelinden izlenir.
        return TryStartApply() ? ApplyOutcome.ApplyStarted : ApplyOutcome.ApplyFailed;
    }

    private async Task ReportAsync(string adminPeerId, string code, int attempt, string message, string? version = null)
    {
        try
        {
            var conn = await _chat.Pool.GetAsync(adminPeerId);
            await conn.RequestAsync(new Packet
            {
                T = PacketTypes.UpdateStatus,
                Err = code,
                St = attempt,
                Text = message,
                Name = version ?? CurrentText,
            }, TimeSpan.FromSeconds(15));
        }
        catch (Exception ex) when (ex is IOException or SocketException or InvalidOperationException or TimeoutException
                                   or PeerUnavailableException or ProtocolException)
        {
            Log.Warn("Güncelleme durumu gönderilemedi: " + ex.Message);
        }
    }

    private string? FindAdminPeer()
    {
        var host = _chat.Deployment.AdminHost;
        if (host == null) return null;
        foreach (var peer in _chat.Registry.All)
        {
            if (!peer.IsOnline || peer.Endpoint == null) continue;
            if (!HostEquals(peer.Machine, host)) continue;
            if (_chat.Directory.GetMember(peer.PeerId)?.Role != Role.Admin) continue;
            return peer.PeerId;
        }
        return null;
    }

    private static bool HostEquals(string machine, string host)
    {
        if (machine.Length == 0) return false;
        var label = host.Split('.')[0];
        return machine.Equals(host, StringComparison.OrdinalIgnoreCase) ||
               machine.Equals(label, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<byte[]> Receive(SecureChannel channel, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        return await channel.ReceiveAsync(cts.Token);
    }

    private static bool TryReadStaged(out Version? version, out string? sha)
    {
        version = null;
        sha = null;
        try
        {
            if (!File.Exists(PendingMsi) || !File.Exists(PendingVersionFile)) return false;
            if (!Version.TryParse(File.ReadAllText(PendingVersionFile).Trim(), out var ver)) return false;
            if (!IsNewer(ver, Current)) return false;
            if (!PackageTrust.IsVTalkUpdate(PendingMsi, out var packaged) || Normalize(packaged) != Normalize(ver))
                return false;
            version = ver;
            sha = File.Exists(StagedShaFile) ? File.ReadAllText(StagedShaFile).Trim() : null;
            return true;
        }
        catch (Exception ex) when (IsFileError(ex))
        {
            Log.Warn("Bekleyen güncelleme okunamadı: " + ex.Message);
            return false;
        }
    }

    private static void DeletePending(bool keepSha)
    {
        TryDeleteFile(PendingMsi);
        TryDeleteFile(PendingVersionFile);
        TryDeleteFile(Path.Combine(PendingDir, "apply.lock"));
        if (!keepSha) TryDeleteFile(StagedShaFile);
        // Eski sürümlerin SYSTEM görevine çalıştırttığı betikler; artık kullanılmaz, kalırsa kafa karıştırır.
        foreach (var script in new[] { Path.Combine(PendingDir, "apply.cmd"), LegacyInstallScriptPath })
        {
            try
            {
                if (File.Exists(script)) File.Delete(script);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"Eski güncelleme betiği silinemedi ({Path.GetFileName(script)}): {ex.Message}");
            }
        }
    }

    private static void WriteJob(UpdateJob job)
    {
        try
        {
            Directory.CreateDirectory(PendingDir);
            File.WriteAllText(JobFile, JsonSerializer.Serialize(job, JsonOpts));
        }
        catch (Exception ex) when (IsStateStoreError(ex))
        {
            Log.Warn("Güncelleme işi kaydedilemedi: " + ex.Message);
        }
    }

    private static bool TryReadJob(out UpdateJob? job)
    {
        job = null;
        try
        {
            if (!File.Exists(JobFile)) return false;
            job = JsonSerializer.Deserialize<UpdateJob>(File.ReadAllText(JobFile), JsonOpts);
            return job != null;
        }
        catch (Exception ex) when (IsStateStoreError(ex))
        {
            Log.Warn("Güncelleme işi okunamadı: " + ex.Message);
            return false;
        }
    }

    private static void ClearJob()
    {
        TryDeleteFile(JobFile);
    }

    private void PersistDone(string peerId, ClientUpdateInfo info)
    {
        try
        {
            var path = StatusStoreFile;
            Dictionary<string, StoredClientStatus> map = [];
            if (File.Exists(path))
            {
                map = JsonSerializer.Deserialize<Dictionary<string, StoredClientStatus>>(File.ReadAllText(path), JsonOpts) ?? [];
            }
            map[peerId] = new StoredClientStatus { Version = info.AppVersion, LastUpdatedUtc = info.LastUpdatedUtc };
            File.WriteAllText(path, JsonSerializer.Serialize(map, JsonOpts));
        }
        catch (Exception ex) when (IsStateStoreError(ex))
        {
            Log.Warn("İstemci güncelleme durumu kaydedilemedi: " + ex.Message);
        }
    }

    public void LoadPersistedStatuses()
    {
        try
        {
            var path = File.Exists(StatusStoreFile) ? StatusStoreFile : LegacyStatusStoreFile;
            if (!File.Exists(path)) return;
            var map = JsonSerializer.Deserialize<Dictionary<string, StoredClientStatus>>(File.ReadAllText(path), JsonOpts);
            if (map == null) return;
            foreach (var (id, s) in map)
            {
                var info = GetClientInfo(id);
                if (CleanVersionText(s.Version) is { } version) info.AppVersion = version;
                if (s.LastUpdatedUtc is { } t) info.LastUpdatedUtc = t;
                if (info.Phase == UpdatePhase.Idle) info.Phase = UpdatePhase.Done;
            }
        }
        catch (Exception ex) when (IsStateStoreError(ex))
        {
            Log.Warn("İstemci güncelleme durumları okunamadı: " + ex.Message);
        }
    }

    /// <summary>
    /// İş/durum dosyaları yardımcı kayıttır: dosya veya JSON hatası (karartmanın bozduğu tür meta verisi dahil,
    /// InvalidOperationException/NotSupportedException) güncellemeyi ya da admin sırasını asla durdurmamalı.
    /// </summary>
    private static bool IsStateStoreError(Exception ex) =>
        IsFileError(ex) || ex is JsonException or NotSupportedException or InvalidOperationException;

    private static async Task TrySend(SecureChannel channel, Packet packet)
    {
        try
        {
            await channel.SendAsync(packet.Serialize());
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or ObjectDisposedException)
        {
            Log.Warn("Güncelleme yanıtı gönderilemedi: " + ex.Message);
        }
    }

    private static string UpdateRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "VTalk", "update");
    private static string PublishedMsi => Path.Combine(UpdateRoot, "published", "VTalk.msi");
    private static string PublishedVersionFile => Path.Combine(UpdateRoot, "published", "version.txt");
    private static string PendingDir => Path.Combine(UpdateRoot, "pending");
    private static string PendingMsi => Path.Combine(PendingDir, "VTalk.msi");
    private static string PendingVersionFile => Path.Combine(PendingDir, "version.txt");
    private static string StagedShaFile => Path.Combine(PendingDir, "staged.sha");
    private static string LegacyInstallScriptPath => Path.Combine(PendingDir, "install-pending.cmd");
    private static string JobFile => Path.Combine(PendingDir, "job.json");
    private static string DownloadLogFile => Path.Combine(PendingDir, "download.log");
    /// <summary>Admin bilgisayarındaki istemci durumları. Güncelleme kökü artık kullanıcılara salt okunur olduğundan kullanıcı verisinde.</summary>
    private static string StatusStoreFile => Path.Combine(AppPaths.DataDir, "update-client-status.json");
    private static string LegacyStatusStoreFile => Path.Combine(UpdateRoot, "client-status.json");

    private enum ApplyOutcome { AlreadyCurrent, NoOffer, DownloadFailed, ApplyFailed, ApplyStarted }

    // Diske yazılan durumlar: konumsal record kullanılmaz. Karartma kurucu parametre adlarını sildiği için
    // System.Text.Json konumsal record'u ne okuyabilir ne yazabilir (InvalidOperationException); sabit JSON adları ve
    // parametresiz kurucu, karartılmış derlemede de aynı biçimi korur.
    internal sealed record UpdateJob
    {
        [JsonPropertyName("admin")] public string AdminPeerId { get; init; } = "";
        [JsonPropertyName("attempt")] public int Attempt { get; init; }
        [JsonPropertyName("started")] public DateTime StartedUtc { get; init; }
        [JsonPropertyName("phase")] public string? Phase { get; init; }
        [JsonPropertyName("target")] public string? TargetVersion { get; init; }
    }

    internal sealed record StoredClientStatus
    {
        [JsonPropertyName("version")] public string? Version { get; init; }
        [JsonPropertyName("updated")] public DateTime? LastUpdatedUtc { get; init; }
    }

    /// <summary>Öz test: diske yazılan güncelleme durumları (karartılmış derlemede) gidiş-dönüş okunabiliyor mu.</summary>
    internal static bool PersistedStateRoundTrips(out string? error)
    {
        try
        {
            var job = new UpdateJob { AdminPeerId = "a", Attempt = 2, StartedUtc = DateTime.UtcNow, Phase = "awaiting-restart", TargetVersion = "1.4" };
            var jobBack = JsonSerializer.Deserialize<UpdateJob>(JsonSerializer.Serialize(job, JsonOpts), JsonOpts);
            var map = new Dictionary<string, StoredClientStatus> { ["p"] = new() { Version = "1.4", LastUpdatedUtc = DateTime.UtcNow } };
            var mapBack = JsonSerializer.Deserialize<Dictionary<string, StoredClientStatus>>(JsonSerializer.Serialize(map, JsonOpts), JsonOpts);
            error = jobBack == job && mapBack?["p"] == map["p"] ? null : "Okunan durum yazılanla aynı değil.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or JsonException)
        {
            error = ex.Message;
        }
        return error == null;
    }
}

public enum UpdatePhase
{
    Idle,
    Queued,
    Updating,
    Done,
    Failed,
    Outdated,
}

public sealed class ClientUpdateInfo
{
    public string PeerId { get; init; } = "";
    public string? AppVersion { get; set; }
    public DateTime? LastUpdatedUtc { get; set; }
    public UpdatePhase Phase { get; set; } = UpdatePhase.Idle;
    public int Attempt { get; set; }
    public string? LastError { get; set; }
    public string? LastMessage { get; set; }

    public string VersionText => string.IsNullOrWhiteSpace(AppVersion) ? "—" : AppVersion;
    public string LastUpdatedText => LastUpdatedUtc is { } t
        ? t.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.GetCultureInfo("tr-TR"))
        : "—";
    public string StatusText => Phase switch
    {
        UpdatePhase.Queued => "Sırada",
        UpdatePhase.Updating => Attempt > 0 ? $"Güncelleniyor ({Attempt}/3)" : "Güncelleniyor",
        UpdatePhase.Done => "Güncel",
        UpdatePhase.Failed => string.IsNullOrWhiteSpace(LastError) ? "Hata" : $"Hata: {LastError}",
        UpdatePhase.Outdated => "Eski istemci",
        _ => "—",
    };

    public ClientUpdateInfo Clone() => new()
    {
        PeerId = PeerId,
        AppVersion = AppVersion,
        LastUpdatedUtc = LastUpdatedUtc,
        Phase = Phase,
        Attempt = Attempt,
        LastError = LastError,
        LastMessage = LastMessage,
    };
}

public sealed record UpdateOffer(Version Version, long Size, string Sha256);

public sealed record UpdatePushReport(
    Version Version,
    int Succeeded,
    int Failed,
    int Outdated,
    int Unreachable,
    IReadOnlyList<string> Notes);
