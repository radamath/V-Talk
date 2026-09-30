using System.Collections.Concurrent;
using System.Net;
using VTalk.Core;

namespace VTalk.Net;

/// <summary>Ağda görülen bir eşin değişmez anlık görüntüsü.</summary>
public sealed record PeerSnapshot
{
    public required string PeerId { get; init; }
    public required byte[] PublicKey { get; init; }
    public string Name { get; init; } = "";
    public string User { get; init; } = "";
    public string Machine { get; init; } = "";
    public string Note { get; init; } = "";
    public PresenceStatus Status { get; init; } = PresenceStatus.Offline;
    public IPAddress? Address { get; init; }
    public int Port { get; init; }
    public DateTime LastSeenUtc { get; init; }
    public int DirectoryVersion { get; init; }
    public string? OrgFingerprint { get; init; }
    public string? AvatarHash { get; init; }
    public bool IsOnline => Status != PresenceStatus.Offline && Address != null;
    public IPEndPoint? Endpoint => Address == null || Port == 0 ? null : new IPEndPoint(Address, Port);
}

public sealed class PeerRegistry
{
    private readonly ConcurrentDictionary<string, PeerSnapshot> _peers = new();

    public PeerSnapshot? Get(string peerId) => _peers.TryGetValue(peerId, out var p) ? p : null;

    public IReadOnlyCollection<PeerSnapshot> All => _peers.Values.ToList();

    public int Count => _peers.Count;

    public IEnumerable<PeerSnapshot> Online => _peers.Values.Where(p => p.IsOnline);

    /// <summary>Günceller ve önceki durumu döndürür.</summary>
    public PeerSnapshot? Upsert(PeerSnapshot snapshot)
    {
        PeerSnapshot? previous = null;
        _peers.AddOrUpdate(snapshot.PeerId, snapshot, (_, old) =>
        {
            previous = old;
            return snapshot;
        });
        return previous;
    }

    public PeerSnapshot? MarkOffline(string peerId)
    {
        while (_peers.TryGetValue(peerId, out var old))
        {
            if (!old.IsOnline) return null;
            var updated = old with { Status = PresenceStatus.Offline };
            if (_peers.TryUpdate(peerId, updated, old)) return updated;
        }
        return null;
    }

    public List<PeerSnapshot> ExpireOlderThan(TimeSpan age)
    {
        var expired = new List<PeerSnapshot>();
        var limit = DateTime.UtcNow - age;
        foreach (var p in _peers.Values.Where(p => p.IsOnline && p.LastSeenUtc < limit))
        {
            var updated = MarkOffline(p.PeerId);
            if (updated != null) expired.Add(updated);
        }
        return expired;
    }
}
