using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using VTalk.Core;

namespace VTalk.Net;

public delegate Task PacketHandler(PeerConnection connection, Packet packet);

/// <summary>Şifreli kanal üzerinde istek/yanıt (RPC) katmanı.</summary>
public sealed class PeerConnection : IAsyncDisposable
{
    private readonly SecureChannel _channel;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<Packet>> _pending = new();
    private readonly CancellationTokenSource _cts = new();
    private int _closed;

    public string RemotePeerId => _channel.RemotePeerId;
    public byte[] RemotePublicKey => _channel.RemotePublicKey;
    public System.Net.IPAddress RemoteAddress => _channel.RemoteAddress;
    public DateTime LastActivityUtc { get; private set; } = DateTime.UtcNow;
    public bool IsClosed => Volatile.Read(ref _closed) == 1;
    public event Action<PeerConnection>? Closed;

    public PeerConnection(SecureChannel channel) => _channel = channel;

    public async Task RunAsync(PacketHandler handler, Packet? firstPacket, TimeSpan idleTimeout, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        try
        {
            if (firstPacket != null) await Dispatch(handler, firstPacket);
            while (!linked.IsCancellationRequested)
            {
                using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
                readTimeout.CancelAfter(idleTimeout);
                var data = await _channel.ReceiveAsync(readTimeout.Token);
                LastActivityUtc = DateTime.UtcNow;
                var packet = Packet.TryParse(data);
                if (packet == null) throw new ProtocolException("Geçersiz paket.");
                await Dispatch(handler, packet);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException
                                       or ProtocolException or CryptographicException or ObjectDisposedException
                                       or EndOfStreamException)
        {
            if (ex is ProtocolException or CryptographicException)
                Log.Warn($"Bağlantı kapatıldı ({RemotePeerId[..8]}): {ex.Message}");
        }
        finally
        {
            await DisposeAsync();
        }
    }

    private async Task Dispatch(PacketHandler handler, Packet packet)
    {
        if (packet.Re != null && _pending.TryRemove(packet.Re, out var tcs))
        {
            tcs.TrySetResult(packet);
            return;
        }
        try
        {
            await handler(this, packet);
        }
        catch (Exception ex) when (ex is not (IOException or SocketException or ObjectDisposedException))
        {
            Log.Error($"Paket işlenemedi ({packet.T})", ex);
        }
    }

    public async Task SendAsync(Packet packet, CancellationToken ct = default)
    {
        LastActivityUtc = DateTime.UtcNow;
        await _channel.SendAsync(packet.Serialize(), ct);
    }

    public async Task<Packet> RequestAsync(Packet packet, TimeSpan timeout, CancellationToken ct = default)
    {
        packet.Id ??= CryptoUtil.NewId();
        var tcs = new TaskCompletionSource<Packet>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(packet.Id, tcs)) throw new InvalidOperationException("Yinelenen istek kimliği.");
        try
        {
            await SendAsync(packet, ct);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
            cts.CancelAfter(timeout);
            await using (cts.Token.Register(() => tcs.TrySetCanceled()))
            {
                return await tcs.Task;
            }
        }
        finally
        {
            _pending.TryRemove(packet.Id, out _);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 1) return;
        try { _cts.Cancel(); } catch { }
        foreach (var kv in _pending) kv.Value.TrySetCanceled();
        await _channel.DisposeAsync();
        Closed?.Invoke(this);
    }
}
