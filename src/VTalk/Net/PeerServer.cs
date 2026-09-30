using System.Net;
using System.Net.Sockets;
using VTalk.Core;

namespace VTalk.Net;

/// <summary>
/// Gelen şifreli TCP bağlantılarını kabul eder; IP başına eşzamanlı bağlantı, IP başına el sıkışma hızı
/// ve toplam bağlantı sınırı uygular. Ortak (Public) ağlardan gelen bağlantılar reddedilir.
/// </summary>
public sealed class PeerServer : IDisposable
{
    private const int MaxConnections = 256;
    private const int MaxPerAddress = 16;
    private const int MaxHandshakesPerWindow = 30;
    private static readonly TimeSpan HandshakeWindow = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(15);

    private readonly Identity _identity;
    private readonly PacketHandler _handler;
    private readonly Func<SecureChannel, Packet, Task> _fileRequestHandler;
    private readonly NetworkGuard _guard;
    private readonly object _gate = new();
    private readonly Dictionary<IPAddress, int> _perAddress = new();
    private readonly Dictionary<IPAddress, (DateTime window, int count)> _handshakes = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private int _total;

    public int Port { get; private set; }

    public PeerServer(Identity identity, PacketHandler handler, Func<SecureChannel, Packet, Task> fileRequestHandler, NetworkGuard guard)
    {
        _identity = identity;
        _handler = handler;
        _fileRequestHandler = fileRequestHandler;
        _guard = guard;
    }

    public void Start(int preferredPort)
    {
        var bind = _guard.LoopbackOnly ? IPAddress.Loopback : IPAddress.Any;
        _listener = TryListen(bind, preferredPort) ?? TryListen(bind, 0)
            ?? throw new InvalidOperationException("TCP dinleyici başlatılamadı.");
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => AcceptLoop(_cts.Token));
        Log.Info($"TCP dinleyici port {Port} üzerinde başladı.");
    }

    private static TcpListener? TryListen(IPAddress bind, int port)
    {
        try
        {
            var l = new TcpListener(bind, port);
            l.Server.ExclusiveAddressUse = true;
            l.Start(64);
            return l;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener != null)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; }

            var address = ((IPEndPoint)client.Client.RemoteEndPoint!).Address;
            if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
            if (!_guard.IsAllowedRemote(address) || !TryAcquire(address))
            {
                client.Dispose();
                continue;
            }
            _ = Task.Run(() => HandleClient(client, address, ct));
        }
    }

    /// <summary>Sınırlar içindeyse bağlantı yuvası ayırır; ayrılan her yuva için tam olarak bir kez <see cref="Release"/> çağrılmalıdır.</summary>
    private bool TryAcquire(IPAddress address)
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            var hs = _handshakes.GetValueOrDefault(address);
            hs = now - hs.window > HandshakeWindow ? (now, 1) : (hs.window, hs.count + 1);
            _handshakes[address] = hs;
            if (_handshakes.Count > 4096)
            {
                foreach (var key in _handshakes.Where(kv => now - kv.Value.window > HandshakeWindow).Select(kv => kv.Key).ToList())
                    _handshakes.Remove(key);
            }
            if (hs.count > MaxHandshakesPerWindow) return false;

            var current = _perAddress.GetValueOrDefault(address);
            if (_total >= MaxConnections || current >= MaxPerAddress) return false;
            _perAddress[address] = current + 1;
            _total++;
            return true;
        }
    }

    private async Task HandleClient(TcpClient client, IPAddress address, CancellationToken ct)
    {
        SecureChannel? channel = null;
        try
        {
            channel = await SecureChannel.AcceptAsync(client, _identity, ct);

            using var firstTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            firstTimeout.CancelAfter(TimeSpan.FromSeconds(30));
            var first = Packet.TryParse(await channel.ReceiveAsync(firstTimeout.Token));
            if (first == null) return;

            if (first.T == PacketTypes.FileGet)
            {
                await _fileRequestHandler(channel, first);
                return;
            }

            var connection = new PeerConnection(channel);
            channel = null; // Sahiplik PeerConnection'a geçti.
            await connection.RunAsync(_handler, first, IdleTimeout, ct);
        }
        catch (Exception ex) when (ex is ProtocolException or System.Security.Cryptography.CryptographicException)
        {
            Log.Warn($"Gelen bağlantı reddedildi ({address}): {ex.Message}");
        }
        catch
        {
            // Zaman aşımı / bağlantı kopması.
        }
        finally
        {
            if (channel != null) await channel.DisposeAsync();
            client.Dispose();
            Release(address);
        }
    }

    private void Release(IPAddress address)
    {
        lock (_gate)
        {
            _total--;
            var n = _perAddress.GetValueOrDefault(address) - 1;
            if (n <= 0) _perAddress.Remove(address);
            else _perAddress[address] = n;
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _listener?.Stop();
        _listener = null;
    }
}
