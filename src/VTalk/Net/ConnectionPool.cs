using System.Collections.Concurrent;
using VTalk.Core;

namespace VTalk.Net;

/// <summary>Eş başına tek, yeniden kullanılan giden bağlantı. Boşta kalan bağlantılar kapatılır.</summary>
public sealed class ConnectionPool : IDisposable
{
    private static readonly TimeSpan IdleClose = TimeSpan.FromMinutes(5);

    private readonly Identity _identity;
    private readonly PeerRegistry _registry;
    private readonly PacketHandler _handler;
    private readonly NetworkGuard _guard;
    private readonly ConcurrentDictionary<string, PeerConnection> _connections = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Timer _sweeper;

    public ConnectionPool(Identity identity, PeerRegistry registry, PacketHandler handler, NetworkGuard guard)
    {
        _identity = identity;
        _registry = registry;
        _handler = handler;
        _guard = guard;
        _sweeper = new Timer(_ => Sweep(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    /// <summary>Eşe halen açık, kullanılabilir bir bağlantı var mı? (Çok-arayüzlü ortamda adres kararlılığı için.)</summary>
    public bool HasLiveConnection(string peerId) =>
        _connections.TryGetValue(peerId, out var c) && !c.IsClosed;

    public async Task<PeerConnection> GetAsync(string peerId, CancellationToken ct = default)
    {
        if (_connections.TryGetValue(peerId, out var existing) && !existing.IsClosed) return existing;

        var gate = _locks.GetOrAdd(peerId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (_connections.TryGetValue(peerId, out existing) && !existing.IsClosed) return existing;

            var channel = await SecureChannel.ConnectAsync(ResolveEndpoint(peerId), _identity, peerId, ct);
            var connection = new PeerConnection(channel);
            connection.Closed += c => _connections.TryRemove(new KeyValuePair<string, PeerConnection>(c.RemotePeerId, c));
            _connections[peerId] = connection;
            _ = Task.Run(() => connection.RunAsync(_handler, null, TimeSpan.FromHours(12), _cts.Token));
            return connection;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Dosya aktarımı gibi uzun işlemler için havuz dışı, ayrı bir kanal açar.</summary>
    public async Task<SecureChannel> OpenDedicatedAsync(string peerId, CancellationToken ct) =>
        await SecureChannel.ConnectAsync(ResolveEndpoint(peerId), _identity, peerId, ct);

    private System.Net.IPEndPoint ResolveEndpoint(string peerId)
    {
        var peer = _registry.Get(peerId);
        var endpoint = peer?.Endpoint;
        if (peer == null || !peer.IsOnline || endpoint == null || !_guard.IsAllowedRemote(endpoint.Address))
            throw new PeerUnavailableException();
        return endpoint;
    }

    public void Drop(string peerId)
    {
        if (_connections.TryRemove(peerId, out var c)) _ = c.DisposeAsync().AsTask();
    }

    private void Sweep()
    {
        var limit = DateTime.UtcNow - IdleClose;
        foreach (var kv in _connections)
        {
            if (kv.Value.LastActivityUtc < limit) Drop(kv.Key);
        }
    }

    public void Dispose()
    {
        _sweeper.Dispose();
        _cts.Cancel();
        foreach (var key in _connections.Keys.ToList()) Drop(key);
    }
}

public sealed class PeerUnavailableException() : Exception("Kişi çevrimdışı veya erişilemiyor.");
