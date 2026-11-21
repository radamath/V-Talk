using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using VTalk.Core;
using VTalk.Data;
using VTalk.Net;
using VTalk.Security;

namespace VTalk.Services;

/// <summary>Erişilebilirlik yoklaması: UDP duyurusu gelmeyen eşleri bilinen son uç noktadan TCP ile yoklar.</summary>
public sealed partial class ChatService
{
    // ------------------------------------------------------------------ Erişilebilirlik yoklaması

    // UDP duyurusu yalnızca bir yönde geçebilir (VPN yönlendirmesi, güvenlik duvarı profili, karşı tarafın ağ tanımı).
    // Bu durumda eş TCP ile ulaşılabilir olduğu halde çevrimdışı görünür; bilinen son uç noktaya kimlik doğrulamalı
    // bağlantı açılıp yoklanır. Uç nokta yalnızca imzalı duyurudan öğrenilir, kimlik el sıkışmada doğrulanır.
    private const int MaxProbesPerRound = 32;
    private const int MaxForcedProbes = 128;
    private const int ProbeParallelism = 8;
    private const int MaxRefreshTargets = 512;
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan SilentProbeAfter = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan OnlineProbeEvery = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan OfflineProbeEvery = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan OfflineProbeWindow = TimeSpan.FromDays(3);
    private static readonly TimeSpan ManualRefreshInterval = TimeSpan.FromSeconds(5);
    private readonly ConcurrentDictionary<string, DateTime> _lastProbe = new();
    private Timer? _probeTimer;
    private Timer? _presenceRefreshTimer;
    private int _probing;
    private volatile bool _forcedProbePending;
    private long _lastManualRefreshTicks;
    private static readonly TimeSpan PresenceRefreshInterval = TimeSpan.FromMinutes(3);

    /// <summary>
    /// "Kullanıcıları yenile": duyuruyu hemen yayınlar, bilinen tüm adreslere tekil duyuru gönderir ve
    /// bilinen uç noktası olan kişileri hemen yoklar. Arka arkaya tıklamalar sınırlanır.
    /// </summary>
    public bool RefreshPeers()
    {
        if (Network.LoopbackOnly) return false;
        var now = DateTime.UtcNow.Ticks;
        var last = Interlocked.Read(ref _lastManualRefreshTicks);
        if (now - last < ManualRefreshInterval.Ticks || Interlocked.CompareExchange(ref _lastManualRefreshTicks, now, last) != last)
            return false;

        _lastReply.Clear();
        _discovery.RequestSweep();
        var targets = Registry.All
            .Where(p => p.Address != null && p.PeerId != Identity.PeerId && IsPeerAllowed(p.PeerId))
            .OrderByDescending(p => p.IsOnline).ThenByDescending(p => p.LastSeenUtc)
            .Select(p => p.Address!)
            .Take(MaxRefreshTargets)
            .ToList();
        _ = Task.Run(() =>
        {
            _discovery.SendTo(targets);
            return ProbePeersAsync(force: true);
        });
        if (Deployment.AdminHost != null && !IsAdminHostThisMachine) _ = Task.Run(ResolveAdminHostAsync);
        return true;
    }

    internal Task ProbeNowAsync() => ProbePeersAsync(force: true);

    /// <summary>Periyodik varlık yenileme: keşif taraması + bilinen kişilere TCP ping.</summary>
    private async Task PeriodicPresenceRefreshAsync()
    {
        if (Network.LoopbackOnly) return;
        try
        {
            _discovery.RequestSweep();
            var targets = Registry.All
                .Where(p => p.Address != null && p.PeerId != Identity.PeerId && IsPeerAllowed(p.PeerId))
                .OrderByDescending(p => p.IsOnline).ThenByDescending(p => p.LastSeenUtc)
                .Select(p => p.Address!)
                .Distinct()
                .Take(MaxRefreshTargets)
                .ToList();
            if (targets.Count > 0) _discovery.SendTo(targets);
            await ProbePeersAsync(force: true);
        }
        catch (Exception ex)
        {
            Log.Warn("Periyodik varlık yenileme başarısız: " + ex.Message);
        }
    }

    private async Task ProbePeersAsync(bool force)
    {
        if (Interlocked.Exchange(ref _probing, 1) == 1)
        {
            if (force) _forcedProbePending = true;
            return;
        }
        try
        {
            var now = DateTime.UtcNow;
            var local = NetworkTargets.LocalSubnets();
            var candidates = Registry.All
                .Where(p => p.Endpoint != null && p.PeerId != Identity.PeerId && IsPeerAllowed(p.PeerId) &&
                            Network.IsAllowedRemote(p.Address!) && (force || NeedsProbe(p, now, local)))
                .OrderByDescending(p => p.IsOnline).ThenByDescending(p => p.LastSeenUtc)
                .Take(force ? MaxForcedProbes : MaxProbesPerRound)
                .ToList();
            foreach (var p in candidates) _lastProbe[p.PeerId] = now;
            await Parallel.ForEachAsync(candidates, new ParallelOptions { MaxDegreeOfParallelism = ProbeParallelism },
                async (p, _) => await ProbeAsync(p));
        }
        catch (Exception ex)
        {
            Log.Warn("Erişilebilirlik yoklaması başarısız: " + ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _probing, 0);
            if (_forcedProbePending)
            {
                _forcedProbePending = false;
                _ = Task.Run(() => ProbePeersAsync(force: true));
            }
        }
    }

    private bool NeedsProbe(PeerSnapshot p, DateTime now, List<(uint Net, uint Mask)> localSubnets)
    {
        var sinceProbe = now - _lastProbe.GetValueOrDefault(p.PeerId);
        // Yeni düşmüş kayıt da sık yoklanır; tek bir geç kalan yanıt kişiyi dakikalarca çevrimdışı bırakmasın.
        if (p.IsOnline || now - p.LastSeenUtc < PresenceTimeout * 3)
            return now - p.LastSeenUtc > SilentProbeAfter && sinceProbe > OnlineProbeEvery;
        if (now - p.LastSeenUtc > OfflineProbeWindow || sinceProbe < OfflineProbeEvery) return false;
        // Aynı alt ağdaki eşler yayınla duyulur; sessizlerse gerçekten kapalıdırlar.
        return !NetworkTargets.InSubnets(p.Address!, localSubnets);
    }

    private async Task ProbeAsync(PeerSnapshot p)
    {
        var endpoint = p.Endpoint!;
        Packet reply;
        try
        {
            using var cts = new CancellationTokenSource(ProbeTimeout);
            var conn = await _pool.GetAsync(p.PeerId, cts.Token, endpoint);
            reply = await conn.RequestAsync(new Packet { T = PacketTypes.Ping }, ProbeTimeout);
        }
        catch (Exception)
        {
            // Ulaşılamıyor; yarı açık kalmış bir havuz bağlantısı bir sonraki denemeyi de engellemesin.
            _pool.Drop(p.PeerId);
            return;
        }

        var (prev, updated) = Registry.Update(p.PeerId, old =>
        {
            var status = reply.T == PacketTypes.Pong && Enum.IsDefined(typeof(PresenceStatus), reply.St) &&
                         reply.St != (int)PresenceStatus.Offline
                ? (PresenceStatus)reply.St
                : old.Status == PresenceStatus.Offline ? PresenceStatus.Online : old.Status;
            var appVer = reply.T == PacketTypes.Pong && AppUpdate.CleanVersionText(reply.Name) is { } reported
                ? reported
                : old.AppVersion;
            // Çevrimiçiyken adres duyurudan gelir; yalnızca çevrimdışı kayıt yoklanan uç noktayla canlandırılır.
            return old.IsOnline
                ? old with { Status = status, LastSeenUtc = DateTime.UtcNow, AppVersion = appVer }
                : old with { Status = status, Address = endpoint.Address, Port = endpoint.Port, LastSeenUtc = DateTime.UtcNow, AppVersion = appVer };
        });
        if (prev == null || updated == null) return;
        if (updated.AppVersion != null) _update.ObserveVersion(p.PeerId, updated.AppVersion);

        if (!prev.IsOnline)
        {
            Log.Info($"{p.PeerId[..8]} UDP duyurusu olmadan yoklamayla çevrimiçi ({endpoint}).");
            PersistContact(updated);
            _discovery.SendTo(endpoint.Address);
            _ = Task.Run(() => FlushPendingAsync(p.PeerId));
            PeerChanged?.Invoke(updated, true);
        }
        else if (prev.Status != updated.Status)
        {
            PeerChanged?.Invoke(updated, false);
        }
    }
}
