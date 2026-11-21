using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using VTalk.Core;

namespace VTalk.Net;

/// <summary>
/// "Ek adresler" ayarını (farklı VLAN / VPN alt ağlarına ulaşmak için) çözümler.
/// Her giriş ya tek bir IPv4 adresi (ör. 10.20.30.5) ya da CIDR alt ağıdır (ör. 10.20.30.0/24).
/// Yayın paketleri yönlendiriciyi/VPN tünelini geçmediğinden, alt ağlar tekil (unicast) "merhaba"
/// paketleriyle taranarak uzak eşler keşfedilir.
/// </summary>
public static class NetworkTargets
{
    /// <summary>Tek bir CIDR girişinin genişletilebileceği en fazla adres (/22 ≈ 1024). Daha büyük aralıklar atlanır.</summary>
    public const int MaxHostsPerEntry = 1024;
    /// <summary>Tüm girişlerden üretilecek toplam tarama hedefi üst sınırı (aşırı paket üretimini önler).</summary>
    public const int MaxExpandedTargets = 2048;

    private static readonly char[] Separators = [',', ';', ' ', '\t', '\r', '\n'];

    public static IEnumerable<string> SplitEntries(string? value) =>
        (value ?? "").Split(Separators, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Bir girişi IPv4 adresi + önek uzunluğuna ayrıştırır. Tek adres /32 kabul edilir.</summary>
    public static bool TryParse(string entry, out IPAddress address, out int prefix)
    {
        address = IPAddress.None;
        prefix = 32;
        var slash = entry.IndexOf('/');
        var host = slash < 0 ? entry : entry[..slash];
        if (!IPAddress.TryParse(host.Trim(), out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
            return false;
        if (slash >= 0)
        {
            if (!int.TryParse(entry[(slash + 1)..].Trim(), out prefix) || prefix is < 0 or > 32)
                return false;
        }
        address = ip;
        return true;
    }

    /// <summary>Keşif için tüm girişleri tekil hedef adreslere genişletir (üst sınır uygulanır).</summary>
    public static List<IPAddress> Expand(string? value)
    {
        var result = new List<IPAddress>();
        var seen = new HashSet<uint>();
        foreach (var entry in SplitEntries(value))
        {
            if (result.Count >= MaxExpandedTargets) break;
            if (!TryParse(entry, out var ip, out var prefix)) continue;
            uint mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
            uint net = ToUInt(ip) & mask;
            long count = prefix == 0 ? 0L : 1L << (32 - prefix); // /0 tüm interneti kapsar; taranmaz.
            if (count is 0 or > MaxHostsPerEntry)
            {
                Log.Warn($"Ek adres taranmayacak kadar geniş, atlandı: {entry}");
                continue;
            }
            for (long h = net; h < net + count && result.Count < MaxExpandedTargets; h++)
            {
                if (seen.Add((uint)h)) result.Add(FromUInt((uint)h));
            }
        }
        return result;
    }

    /// <summary>NetworkGuard'ın açıkça güveneceği (VPN) alt ağlar. Tek adresler /32 olarak döner.</summary>
    public static List<(uint Net, uint Mask)> Subnets(string? value)
    {
        var result = new List<(uint, uint)>();
        foreach (var entry in SplitEntries(value))
        {
            if (!TryParse(entry, out var ip, out var prefix)) continue;
            uint mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
            if (mask == 0) continue; // 0.0.0.0/0 beyaz listeye alınmaz.
            result.Add((ToUInt(ip) & mask, mask));
        }
        return result;
    }

    /// <summary>Etkin IPv4 arabirimlerinin doğrudan bağlı alt ağları.</summary>
    public static List<(uint Net, uint Mask)> LocalSubnets()
    {
        var result = new List<(uint, uint)>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask == null) continue;
                var mask = ToUInt(ua.IPv4Mask);
                if (mask != 0) result.Add((ToUInt(ua.Address) & mask, mask));
            }
        }
        return result;
    }

    /// <summary>
    /// Duyuru / TCP için tercih edilen yerel IPv4'ler (en iyisi önce).
    /// Elle eklenmiş geçitsiz VLAN IP'leri, varsayılan ağ geçidi olan (internete çıkan) IP'ye tercih edilir.
    /// </summary>
    public static List<IPAddress> PreferredLanAddresses(string? preferredSubnets = null, int max = 4)
    {
        var preferred = Subnets(preferredSubnets);
        var scored = new List<(int Score, IPAddress Ip)>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;
            var props = nic.GetIPProperties();
            var gateways = props.GatewayAddresses
                .Where(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(g.Address))
                .Select(g => g.Address)
                .ToList();
            var hasAnyGateway = gateways.Count > 0;

            foreach (var ua in props.UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask == null) continue;
                if (IPAddress.IsLoopback(ua.Address)) continue;
                var bytes = ua.Address.GetAddressBytes();
                if (bytes[0] == 169 && bytes[1] == 254) continue; // APIPA

                var ip = ua.Address;
                var mask = ToUInt(ua.IPv4Mask);
                var net = ToUInt(ip) & mask;
                var score = 0;

                // Yapılandırılmış kurumsal / ek ağlar en yüksek öncelik.
                if (InSubnets(ip, preferred)) score += 200;

                // Bu IP'nin alt ağında ağ geçidi yoksa (ikincil/elle VLAN IP) yükselt.
                var gatewayOnThisSubnet = gateways.Any(g =>
                {
                    var gv = ToUInt(g);
                    return mask != 0 && (gv & mask) == net;
                });
                if (!gatewayOnThisSubnet) score += 100;
                if (!hasAnyGateway) score += 40;

                // Özel aralıklar.
                if (bytes[0] == 10) score += 30;
                else if (bytes[0] == 192 && bytes[1] == 168) score += 30;
                else if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) score += 30;
                else score -= 20; // genel / internete bakan

                // Varsayılan geçitli arabirimdeki birincil (geçitli) IP: duyuru kaynağı olarak zayıf.
                if (gatewayOnThisSubnet) score -= 80;

                scored.Add((score, ip));
            }
        }

        return scored
            .OrderByDescending(s => s.Score)
            .Select(s => s.Ip)
            .Distinct()
            .Take(Math.Clamp(max, 1, 8))
            .ToList();
    }

    /// <summary>
    /// Duyurulan IP listesi + UDP kaynağından adres seçer.
    /// Önce gerçek paket yolu (UDP source): kurumsal/ek ağdaysa ona güven.
    /// Yalnızca UDP internete çıkan yanlış IP'den geliyorsa duyurulan VLAN IP'sine geç.
    /// </summary>
    public static IPAddress PickReachableAddress(IPAddress udpSource, IEnumerable<IPAddress>? advertised, string? preferredSubnets)
    {
        if (udpSource.IsIPv4MappedToIPv6) udpSource = udpSource.MapToIPv4();
        var preferred = Subnets(preferredSubnets);
        var adv = new List<IPAddress>();
        if (advertised != null)
        {
            foreach (var a0 in advertised)
            {
                var a = a0.IsIPv4MappedToIPv6 ? a0.MapToIPv4() : a0;
                if (a.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(a)) continue;
                adv.Add(a);
            }
        }

        var udpOk = udpSource.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(udpSource);
        // preferred boşsa veya UDP zaten ek/kurumsal ağdaysa gerçek yolu koru (1.3.11 regresyonu).
        if (udpOk && (preferred.Count == 0 || InSubnets(udpSource, preferred)))
            return udpSource;

        // UDP yanlış ağdan (ör. varsayılan geçit IP'si) geliyorsa duyurulan ek-ağ IP'si.
        var advPreferred = adv.FirstOrDefault(a => InSubnets(a, preferred));
        if (advPreferred != null) return advPreferred;

        if (udpOk) return udpSource;
        return adv.FirstOrDefault() ?? udpSource;
    }

    public static bool InSubnets(IPAddress address, IEnumerable<(uint Net, uint Mask)> subnets)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var v = ToUInt(address);
        return subnets.Any(n => (v & n.Mask) == n.Net);
    }

    /// <summary>İki adres aynı /24 içinde mi? (adres zıplamasını yumuşatmak için).</summary>
    public static bool SameSlash24(IPAddress a, IPAddress b)
    {
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (b.IsIPv4MappedToIPv6) b = b.MapToIPv4();
        if (a.AddressFamily != AddressFamily.InterNetwork || b.AddressFamily != AddressFamily.InterNetwork)
            return false;
        const uint mask = 0xFFFFFF00u;
        return (ToUInt(a) & mask) == (ToUInt(b) & mask);
    }

    internal static uint ToUInt(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
    }

    private static IPAddress FromUInt(uint value) =>
        new(new[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value });
}
