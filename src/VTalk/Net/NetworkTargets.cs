using System.Net;
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

    internal static uint ToUInt(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
    }

    private static IPAddress FromUInt(uint value) =>
        new(new[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value });
}
