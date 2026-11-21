using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using VTalk.Core;

namespace VTalk.Net;

/// <summary>İmzalı UDP duyuru içeriği.</summary>
public sealed class AnnouncePayload
{
    public const int CurrentVersion = 2;

    [JsonPropertyName("v")] public int V { get; set; } = CurrentVersion;
    [JsonPropertyName("k")] public string Kind { get; set; } = "hello";
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    /// <summary>Süreç başına rastgele oturum kimliği.</summary>
    [JsonPropertyName("sid")] public string Sid { get; set; } = "";
    /// <summary>Oturumun başladığı an (unix ms).</summary>
    [JsonPropertyName("ss")] public long SessionStart { get; set; }
    /// <summary>Oturum içinde kesin artan sıra numarası.</summary>
    [JsonPropertyName("seq")] public long Seq { get; set; }
    [JsonPropertyName("pk")] public string Pk { get; set; } = "";
    [JsonPropertyName("n")] public string Name { get; set; } = "";
    [JsonPropertyName("u")] public string User { get; set; } = "";
    [JsonPropertyName("m")] public string Machine { get; set; } = "";
    [JsonPropertyName("st")] public int Status { get; set; }
    [JsonPropertyName("no")] public string Note { get; set; } = "";
    [JsonPropertyName("port")] public int Port { get; set; }
    [JsonPropertyName("ts")] public long Ts { get; set; }
    [JsonPropertyName("dv")] public int DirVersion { get; set; }
    [JsonPropertyName("of")] public string? OrgFp { get; set; }
    [JsonPropertyName("av")] public string? AvatarHash { get; set; }
    /// <summary>Uygulama sürümü (ör. 1.2.1.0). Eski istemciler yok sayar.</summary>
    [JsonPropertyName("ver")] public string? AppVer { get; set; }
    /// <summary>
    /// Tercih edilen yerel IPv4'ler (geçitsiz VLAN önce). UDP kaynak adresi yanlış çıkarsa TCP için kullanılır.
    /// Eski istemciler yok sayar.
    /// </summary>
    [JsonPropertyName("ips")] public List<string>? Ips { get; set; }
}

internal sealed class AnnounceWire
{
    [JsonPropertyName("vt")] public int Vt { get; set; } = 1;
    [JsonPropertyName("p")] public string P { get; set; } = "";
    [JsonPropertyName("s")] public string S { get; set; } = "";
}

/// <summary>
/// Duyuru tekrar oynatma (replay) koruması. Aynı oturumdan yalnızca daha büyük sıra numarası kabul edilir;
/// farklı oturum ancak daha yeni başlamışsa ya da mevcut oturum sessizleşmişse (süreç kapanmış) kabul edilir.
/// Eşzamanlı kullanım için tasarlanmamıştır (tek alım iş parçacığı).
/// </summary>
public sealed class AnnounceReplayGuard
{
    public const int MaxTrackedPeers = 20_000;
    /// <summary>Bu süre boyunca duyurusu gelmeyen oturum sona ermiş sayılır.</summary>
    public static readonly TimeSpan SessionStale = TimeSpan.FromSeconds(30);

    private sealed record SessionState(string Sid, long SessionStart, long Seq, DateTime LastHeardUtc);

    private readonly Dictionary<string, SessionState> _sessions = new();

    public bool IsFresh(AnnouncePayload p, DateTime? nowUtc = null)
    {
        if (!_sessions.TryGetValue(p.Id, out var st)) return true;
        if (p.Sid == st.Sid) return p.Seq > st.Seq;
        return p.SessionStart > st.SessionStart || (nowUtc ?? DateTime.UtcNow) - st.LastHeardUtc > SessionStale;
    }

    public void Remember(AnnouncePayload p, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        if (_sessions.Count >= MaxTrackedPeers && !_sessions.ContainsKey(p.Id))
        {
            var limit = now - TimeSpan.FromHours(1);
            foreach (var key in _sessions.Where(kv => kv.Value.LastHeardUtc < limit).Select(kv => kv.Key).ToList())
                _sessions.Remove(key);
            if (_sessions.Count >= MaxTrackedPeers) return;
        }
        _sessions[p.Id] = new SessionState(p.Sid, p.SessionStart, p.Seq, now);
    }

    /// <summary>Bu kimliğin imzalı bir duyurusu daha önce doğrulandı mı.</summary>
    public bool IsKnown(string peerId) => _sessions.ContainsKey(peerId);
}

/// <summary>
/// Duyuru işleme bütçeleri. Kaynak IP başına paket sınırı ve saniyelik imza doğrulama bütçesi uygular.
/// Daha önce doğrulanmış kimlikler bütçenin tamamını kullanabilir; yeni/bilinmeyen kimlikler yalnızca bir kısmını.
/// Her pakette yeni anahtar üreten sahte duyuru seli böylece gerçek eşlerin duyurularını düşüremez.
/// Eşzamanlı kullanım için tasarlanmamıştır (tek alım iş parçacığı).
/// </summary>
public sealed class AnnounceRateLimiter
{
    public const int PerSourcePerSecond = 30;
    public const int MaxTrackedSources = 4096;
    public const int VerifiesPerSecond = 400;
    public const int UnknownVerifiesPerSecond = 100;
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(1);

    private readonly Dictionary<IPAddress, (DateTime window, int count)> _sources = new();
    private DateTime _verifyWindow = DateTime.MinValue;
    private int _verifyCount;
    private int _unknownVerifyCount;

    public bool AllowSource(IPAddress source, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        if (_sources.TryGetValue(source, out var e) && now - e.window <= Window)
        {
            _sources[source] = (e.window, e.count + 1);
            return e.count + 1 <= PerSourcePerSecond;
        }
        if (!_sources.ContainsKey(source) && _sources.Count >= MaxTrackedSources)
        {
            // Tabloyu tamamen sıfırlamak, sahte kaynak IP'leriyle dolduran saldırganın kendi sayacını da
            // sıfırlardı. Yalnızca penceresi geçmiş kayıtlar atılır; yer açılmazsa yeni kaynak reddedilir.
            foreach (var key in _sources.Where(kv => now - kv.Value.window > Window).Select(kv => kv.Key).ToList())
                _sources.Remove(key);
            if (_sources.Count >= MaxTrackedSources) return false;
        }
        _sources[source] = (now, 1);
        return true;
    }

    public bool AllowVerify(bool knownIdentity, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        if (now - _verifyWindow > Window)
        {
            _verifyWindow = now;
            _verifyCount = 0;
            _unknownVerifyCount = 0;
        }
        if (_verifyCount >= VerifiesPerSecond) return false;
        if (!knownIdentity)
        {
            if (_unknownVerifyCount >= UnknownVerifiesPerSecond) return false;
            _unknownVerifyCount++;
        }
        _verifyCount++;
        return true;
    }
}

public sealed class VerifiedAnnounce
{
    public required AnnouncePayload Payload { get; init; }
    public required byte[] PublicKey { get; init; }
    public required IPAddress Source { get; init; }
}

/// <summary>
/// UDP yayın ile eş keşfi. Her duyuru gönderenin kimlik anahtarıyla imzalanır ve PeerId = H(açık anahtar) doğrulanır.
/// Tekrar oynatma (replay) koruması: duyurular (oturum kimliği, oturum başlangıcı, kesin artan sıra no) taşır.
/// Aynı oturumdan yalnızca daha büyük sıra numarası, yeni oturumdan yalnızca daha yeni başlangıç kabul edilir;
/// böylece eski bir duyuru başka bir IP'den yeniden gönderilerek kişinin adresi yanlış yöne çevrilemez.
/// Kaynak IP başına ve toplamda imza doğrulama bütçesi uygulanır; genel (Public) ağlardan gelen trafik yok sayılır.
/// </summary>
public sealed class DiscoveryService : IDisposable
{
    /// <summary>
    /// 49152 altı seçilir. Windows geçici port rezervasyonları (Hyper-V / NAT) bu sınırın
    /// üstünden blok ayırır; 50505 bu bloklara girip bağlanmayı 10013 ile reddediyordu.
    /// </summary>
    public const int Port = 49050;
    private const int MaxPacket = 4096;
    private const int MaxPeerTargets = 512;
    /// <summary>Yakalanmış eski duyuruların geçerlilik penceresi (makineler arası saat farkı toleransı dahil).</summary>
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(15);

    private readonly Identity _identity;
    private readonly Func<AnnouncePayload> _payloadFactory;
    private readonly Func<IEnumerable<IPAddress>> _extraTargets;
    private readonly Func<IEnumerable<IPAddress>> _peerTargets;
    private readonly NetworkGuard _guard;
    private readonly string _sid = CryptoUtil.NewId();
    private readonly long _sessionStart = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private long _seq;
    private int _broadcasting;
    private int _broadcastRequested;
    /// <summary>Tanımlı ağların tam tarama aralığı. Her istemcinin her 5 sn'de binlerce paket üretmesini önler.</summary>
    public static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(60);
    private long _lastSweepTicks;
    private volatile bool _sweepRequested = true;
    private readonly AnnounceReplayGuard _replay = new();
    private readonly AnnounceRateLimiter _limits = new();
    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private Timer? _timer;

    public event Action<VerifiedAnnounce>? Announced;

    /// <summary>Keşif yuvası açılamazsa uygulamanın kapanmaması için doldurulur; boşsa keşif çalışıyordur.</summary>
    public string? StartError { get; private set; }

    /// <param name="extraTargets">
    /// Ayarlarda / kurulumda tanımlı ağların tüm adresleri (VPN / farklı alt ağlar). Binlerce adres olabildiği için
    /// her duyuruda değil, <see cref="SweepInterval"/> aralıkla ve istendiğinde (<see cref="RequestSweep"/>) taranır.
    /// </param>
    /// <param name="peerTargets">
    /// Duyurusu alınmış çevrimiçi eşlerin adresleri ve admin bilgisayarı. Her duyuruda tekil olarak gönderilir;
    /// böylece ağ tanımı yalnızca bir tarafta olsa da, yayın engellense de iki taraf birbirini çevrimiçi görür.
    /// </param>
    public DiscoveryService(Identity identity, Func<AnnouncePayload> payloadFactory, Func<IEnumerable<IPAddress>> extraTargets,
        NetworkGuard guard, Func<IEnumerable<IPAddress>>? peerTargets = null)
    {
        _identity = identity;
        _payloadFactory = payloadFactory;
        _extraTargets = extraTargets;
        _peerTargets = peerTargets ?? (() => []);
        _guard = guard;
    }

    public void Start()
    {
        // Loopback (öz sınama) modunda ağa hiçbir şey yayınlanmaz; eşler doğrudan Registry'ye eklenir.
        if (_guard.LoopbackOnly) return;
        UdpClient? udp = null;
        try
        {
            udp = new UdpClient(AddressFamily.InterNetwork);
            // Windows'ta ExclusiveAddressUse varsayılan olarak açıktır. Kapatılmazsa ikinci
            // bağlama "adres kullanımda" olur; ReuseAddress bind'dan önce verilmezse bağlama
            // 10013 (erişim reddedildi) ile düşer ve tüm uygulama açılmaz.
            udp.Client.ExclusiveAddressUse = false;
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, Port));
            try
            {
                udp.EnableBroadcast = true;
            }
            catch (SocketException ex)
            {
                // Yayın izni güvenlik yazılımınca reddedilse de doğrudan duyuru çalışabilir.
                Log.Warn($"Yayın izni yok ({ex.SocketErrorCode}); duyuru yalnızca doğrudan adreslere gidecek.");
            }
            _udp = udp;
            udp = null;
            _cts = new CancellationTokenSource();
            _ = Task.Run(() => ReceiveLoop(_cts.Token));
            _timer = new Timer(_ => Broadcast(), null, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        }
        catch (SocketException ex)
        {
            Log.Error("Keşif soketi açılamadı", ex);
            StartError = ex.SocketErrorCode == SocketError.AccessDenied
                ? $"Keşif portu (UDP {Port}) bu bilgisayarda engellenmiş. Windows bu portu başka bir hizmete ayırmış olabilir ya da güvenlik yazılımı V-Talk'ın ağa çıkmasını reddediyor. Uygulama açık kalır; çevrimiçi kişiler görünmeyebilir."
                : $"Keşif portu (UDP {Port}) açılamadı: " + ex.Message;
        }
        finally
        {
            udp?.Dispose();
        }
    }

    /// <summary>
    /// Duyuruyu arka planda gönderir; çağıranı (arayüz iş parçacığı dahil) bekletmez. Uzak ağlar binlerce
    /// hedefe genişleyebildiği ve güvenlik yazılımları her hedefi denetleyebildiği için gönderim saniyeler sürebilir.
    /// Gönderim sürerken gelen istekler tek bir ek gönderimde birleştirilir.
    /// </summary>
    public void RequestBroadcast()
    {
        if (Interlocked.Exchange(ref _broadcastRequested, 1) == 1) return;
        ThreadPool.QueueUserWorkItem(_ => Broadcast());
    }

    /// <summary>Tanımlı ağların tamamını bir sonraki duyuruda tarar (yenile komutu, ağ / ayar değişikliği).</summary>
    public void RequestSweep()
    {
        _sweepRequested = true;
        RequestBroadcast();
    }

    private void Broadcast()
    {
        while (true)
        {
            if (Interlocked.Exchange(ref _broadcasting, 1) == 1) return;
            try
            {
                Volatile.Write(ref _broadcastRequested, 0);
                var packet = BuildPacket("hello");
                var now = DateTime.UtcNow.Ticks;
                bool sweep = _sweepRequested || now - Interlocked.Read(ref _lastSweepTicks) >= SweepInterval.Ticks;
                if (sweep)
                {
                    _sweepRequested = false;
                    Interlocked.Exchange(ref _lastSweepTicks, now);
                }
                foreach (var target in BroadcastTargets(sweep)) Send(packet, target);
            }
            catch (Exception ex)
            {
                Log.Warn("Duyuru gönderilemedi: " + ex.Message);
            }
            finally
            {
                Volatile.Write(ref _broadcasting, 0);
            }
            // Gönderim sırasında gelen istek, meşgul olduğu için çıkan iş öğesinin yerine burada karşılanır.
            if (Volatile.Read(ref _broadcastRequested) == 0) return;
        }
    }

    public void SendTo(IPAddress address)
    {
        if (!_guard.IsAllowedRemote(address)) return;
        try
        {
            Send(BuildPacket("hello"), new IPEndPoint(address, Port));
        }
        catch (Exception ex)
        {
            Log.Warn("Tekil duyuru gönderilemedi: " + ex.Message);
        }
    }

    /// <summary>Tek bir imzalı duyuruyu verilen adreslere tekil olarak gönderir. Arayüz iş parçacığından çağrılmamalı.</summary>
    public void SendTo(IEnumerable<IPAddress> addresses)
    {
        if (_udp == null) return;
        try
        {
            byte[]? packet = null;
            foreach (var address in addresses.Distinct().Take(MaxPeerTargets))
            {
                if (address.AddressFamily != AddressFamily.InterNetwork || !_guard.IsAllowedRemote(address)) continue;
                packet ??= BuildPacket("hello");
                Send(packet, new IPEndPoint(address, Port));
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Tekil duyuru gönderilemedi: " + ex.Message);
        }
    }

    public void SendBye()
    {
        try
        {
            var packet = BuildPacket("bye");
            foreach (var target in BroadcastTargets(sweep: false)) Send(packet, target);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            Log.Info("Kapanış duyurusu gönderilemedi: " + ex.Message);
        }
    }

    private void Send(byte[] packet, IPEndPoint target)
    {
        try
        {
            _udp?.Send(packet, packet.Length, target);
        }
        catch (SocketException)
        {
            // Erişilemeyen arabirimler olağandır.
        }
    }

    private byte[] BuildPacket(string kind)
    {
        var payload = _payloadFactory();
        payload.Kind = kind;
        payload.V = AnnouncePayload.CurrentVersion;
        payload.Id = _identity.PeerId;
        payload.Pk = Convert.ToBase64String(_identity.PublicKey);
        payload.Sid = _sid;
        payload.SessionStart = _sessionStart;
        payload.Seq = Interlocked.Increment(ref _seq);
        payload.Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        var wire = new AnnounceWire
        {
            P = Convert.ToBase64String(payloadBytes),
            S = Convert.ToBase64String(_identity.Sign(payloadBytes)),
        };
        return JsonSerializer.SerializeToUtf8Bytes(wire);
    }

    private IEnumerable<IPEndPoint> BroadcastTargets(bool sweep)
    {
        var set = new HashSet<IPAddress>();
        // Sınırlı yayın (255.255.255.255) hangi arabirimden çıkacağı seçilemediği için yalnızca genel ağ yokken kullanılır.
        if (!_guard.AnyBlockedInterface) set.Add(IPAddress.Broadcast);
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            if (_guard.IsBlockedInterface(nic.Id)) continue;
            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask == null) continue;
                var ip = ua.Address.GetAddressBytes();
                var mask = ua.IPv4Mask.GetAddressBytes();
                if (mask.All(b => b == 0)) continue;
                var bc = new byte[4];
                for (int i = 0; i < 4; i++) bc[i] = (byte)(ip[i] | ~mask[i]);
                set.Add(new IPAddress(bc));
            }
        }
        if (sweep)
        {
            foreach (var extra in _extraTargets())
            {
                if (_guard.IsAllowedRemote(extra)) set.Add(extra);
            }
        }
        // Bilinen eşlere her turda tekil gönderim: tarama seyrekleştiği için ve yayının engellendiği ağlarda
        // (ör. istemci yalıtımlı Wi-Fi, VPN) çevrimiçi durumun düşmemesi buna dayanır.
        int learned = 0;
        foreach (var peer in _peerTargets())
        {
            if (learned >= MaxPeerTargets) break;
            if (peer.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(peer) || !_guard.IsAllowedRemote(peer)) continue;
            if (set.Add(peer)) learned++;
        }
        return set.Select(a => new IPEndPoint(a, Port));
    }

    private async Task ReceiveLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _udp != null)
        {
            UdpReceiveResult result;
            try
            {
                result = await _udp.ReceiveAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; }

            try
            {
                Handle(result.Buffer, result.RemoteEndPoint.Address);
            }
            catch (Exception ex)
            {
                Log.Warn("Duyuru işlenemedi: " + ex.Message);
            }
        }
    }

    private void Handle(byte[] buffer, IPAddress source)
    {
        if (buffer.Length > MaxPacket || !_guard.IsAllowedRemote(source) || !_limits.AllowSource(source)) return;

        AnnounceWire? wire;
        try { wire = JsonSerializer.Deserialize<AnnounceWire>(buffer); }
        catch (JsonException) { return; }
        if (wire == null || wire.Vt != 1 || string.IsNullOrEmpty(wire.P) || string.IsNullOrEmpty(wire.S)) return;

        byte[] payloadBytes, sig;
        try
        {
            payloadBytes = Convert.FromBase64String(wire.P);
            sig = Convert.FromBase64String(wire.S);
        }
        catch (FormatException) { return; }

        AnnouncePayload? payload;
        try { payload = JsonSerializer.Deserialize<AnnouncePayload>(payloadBytes); }
        catch (JsonException) { return; }
        if (payload == null || payload.V != AnnouncePayload.CurrentVersion || payload.Id == _identity.PeerId) return;
        if (payload.Port is <= 0 or > 65535 || payload.Kind is not ("hello" or "bye")) return;
        if (payload.Sid is not { Length: 32 } || !payload.Sid.All(char.IsAsciiHexDigit) || payload.Seq <= 0) return;

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (Math.Abs(now - payload.Ts) > MaxAge.TotalMilliseconds) return;

        byte[] pk;
        try { pk = Convert.FromBase64String(payload.Pk); }
        catch (FormatException) { return; }

        if (CryptoUtil.PeerIdFromPublicKey(pk) != payload.Id) return;
        if (!_replay.IsFresh(payload)) return;
        if (!_limits.AllowVerify(_replay.IsKnown(payload.Id)) || !CryptoUtil.Verify(pk, payloadBytes, sig)) return;
        _replay.Remember(payload);

        payload.Name = TextLimits.Clean(payload.Name, TextLimits.NameMax);
        payload.User = TextLimits.Clean(payload.User, TextLimits.NameMax);
        payload.Machine = TextLimits.Clean(payload.Machine, TextLimits.NameMax);
        payload.Note = TextLimits.Clean(payload.Note, TextLimits.NoteMax);
        if (payload.AvatarHash is { } av && (av.Length != 64 || !av.All(char.IsAsciiHexDigit))) payload.AvatarHash = null;
        if (payload.OrgFp is { Length: > 64 }) payload.OrgFp = null;
        if (!Enum.IsDefined(typeof(PresenceStatus), payload.Status)) payload.Status = (int)PresenceStatus.Online;

        Announced?.Invoke(new VerifiedAnnounce { Payload = payload, PublicKey = pk, Source = source });
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _cts?.Cancel();
        _udp?.Dispose();
        _udp = null;
    }
}
