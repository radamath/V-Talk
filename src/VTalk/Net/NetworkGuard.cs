using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using VTalk.Core;

namespace VTalk.Net;

/// <summary>
/// Windows'un ağ kategorisine (Ortak / Özel / Etki alanı) göre trafiği sınırlar.
/// Varsayılan olarak "Ortak" (kafe, otel, havalimanı Wi-Fi'ı gibi) ağlarda duyuru yayınlanmaz
/// (kullanıcı adı / bilgisayar adı sızmaz) ve bu ağların alt ağlarından gelen UDP/TCP trafiği yok sayılır.
/// Kategori okunamazsa uygulamanın çalışmaya devam etmesi için kısıtlama uygulanmaz (günlüğe yazılır).
/// </summary>
public sealed class NetworkGuard : IDisposable
{
    private const int CategoryPublic = 0;
    private static readonly Guid NetworkListManagerClsid = new("DCB00C01-570F-4A9B-8D69-199FDBA5723B");

    private sealed record Snapshot(HashSet<string> BlockedNicIds, List<(uint Net, uint Mask)> BlockedSubnets, List<string> BlockedNames)
    {
        public static readonly Snapshot Empty = new(new(StringComparer.OrdinalIgnoreCase), [], []);
    }

    private readonly Timer _timer;
    private volatile Snapshot _snapshot = Snapshot.Empty;
    private volatile bool _allowPublic;
    private volatile (uint Net, uint Mask)[] _allowedRemotes = [];
    private int _refreshing;

    public event Action? Changed;

    /// <summary>Yalnızca 127.0.0.1 ile konuşur, ağa hiçbir şey yayınlamaz (kendi kendini sınama modu).</summary>
    public bool LoopbackOnly { get; }

    public NetworkGuard(bool allowPublicNetworks, string? allowedRemoteSubnets = null, bool loopbackOnly = false)
    {
        _allowPublic = allowPublicNetworks;
        _allowedRemotes = NetworkTargets.Subnets(allowedRemoteSubnets).ToArray();
        LoopbackOnly = loopbackOnly;
        Refresh();
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
        _timer = new Timer(_ => Refresh(), null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
    }

    public bool AllowPublicNetworks
    {
        get => _allowPublic;
        set
        {
            if (_allowPublic == value) return;
            _allowPublic = value;
            Changed?.Invoke();
        }
    }

    /// <summary>Kısıtlanan (Ortak) ağ adları; kullanıcıya uyarı göstermek için.</summary>
    public IReadOnlyList<string> BlockedNetworkNames => _allowPublic ? [] : _snapshot.BlockedNames;

    public bool AnyBlockedInterface => LoopbackOnly || (!_allowPublic && _snapshot.BlockedNicIds.Count > 0);

    public bool IsBlockedInterface(string nicId) => LoopbackOnly || (!_allowPublic && _snapshot.BlockedNicIds.Contains(nicId));

    /// <summary>
    /// Yönetici tarafından tanımlanan VPN / uzak alt ağları güvenliye alır. Bu alt ağlardan gelen ve
    /// bunlara giden trafik, tünel bağdaştırıcısı Windows'ta "Ortak" görünse bile engellenmez.
    /// </summary>
    public void SetAllowedRemotes(string? allowedRemoteSubnets)
    {
        _allowedRemotes = NetworkTargets.Subnets(allowedRemoteSubnets).ToArray();
        Changed?.Invoke();
    }

    public bool IsAllowedRemote(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;
        if (LoopbackOnly) return false;
        if (_allowPublic) return true;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var ip = ToUInt(address);
        // Açıkça güvenilen VPN alt ağları, "Ortak" ağ engelinden muaftır.
        foreach (var (net, mask) in _allowedRemotes)
        {
            if ((ip & mask) == net) return true;
        }
        foreach (var (net, mask) in _snapshot.BlockedSubnets)
        {
            if ((ip & mask) == net) return false;
        }
        return true;
    }

    private void OnNetworkChanged(object? sender, EventArgs e) => ThreadPool.QueueUserWorkItem(_ => Refresh());

    public void Refresh()
    {
        if (Interlocked.Exchange(ref _refreshing, 1) == 1) return;
        try
        {
            var categories = ReadCategories();
            var nicIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var subnets = new List<(uint, uint)>();
            var names = new List<string>();
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                if (!categories.TryGetValue(nic.Id, out var info) || info.Category != CategoryPublic) continue;
                nicIds.Add(nic.Id);
                if (!names.Contains(info.Name)) names.Add(info.Name);
                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask == null) continue;
                    var mask = ToUInt(ua.IPv4Mask);
                    subnets.Add((ToUInt(ua.Address) & mask, mask));
                }
            }

            var old = _snapshot;
            _snapshot = new Snapshot(nicIds, subnets, names);
            if (!old.BlockedNicIds.SetEquals(nicIds))
            {
                if (nicIds.Count > 0) Log.Warn($"Ortak (Public) ağ algılandı, V-Talk bu ağda devre dışı: {string.Join(", ", names)}");
                Changed?.Invoke();
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Ağ kategorisi okunamadı: " + ex.Message);
        }
        finally
        {
            Volatile.Write(ref _refreshing, 0);
        }
    }

    /// <summary>Bağdaştırıcı kimliği ("{GUID}") → (kategori, ağ adı). Windows Network List Manager (COM) kullanılır.</summary>
    private static Dictionary<string, (int Category, string Name)> ReadCategories()
    {
        var result = new Dictionary<string, (int, string)>(StringComparer.OrdinalIgnoreCase);
        var type = Type.GetTypeFromCLSID(NetworkListManagerClsid, throwOnError: false);
        if (type == null) return result;
        var manager = (INetworkListManager)Activator.CreateInstance(type)!;
        try
        {
            var connections = manager.GetNetworkConnections();
            while (connections.Next(1, out var connection, out var fetched) == 0 && fetched == 1 && connection != null)
            {
                var network = connection.GetNetwork();
                var adapter = connection.GetAdapterId();
                result["{" + adapter.ToString().ToUpperInvariant() + "}"] =
                    (network.GetCategory(), TextLimits.Clean(network.GetName(), TextLimits.NameMax));
                Marshal.ReleaseComObject(network);
                Marshal.ReleaseComObject(connection);
            }
            Marshal.ReleaseComObject(connections);
        }
        finally
        {
            Marshal.ReleaseComObject(manager);
        }
        return result;
    }

    // ---- Network List Manager arabirimleri (netlistmgr.h; yalnızca kullanılan vtable başlangıcı tanımlıdır) ----

    [ComImport, Guid("DCB00000-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetworkListManager
    {
        [return: MarshalAs(UnmanagedType.IUnknown)] object GetNetworks(int flags);
        [return: MarshalAs(UnmanagedType.IUnknown)] object GetNetwork(Guid networkId);
        IEnumNetworkConnections GetNetworkConnections();
    }

    [ComImport, Guid("DCB00006-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IEnumNetworkConnections
    {
        [return: MarshalAs(UnmanagedType.IUnknown)] object NewEnum();
        [PreserveSig] int Next(uint count, out INetworkConnection? connection, out uint fetched);
    }

    [ComImport, Guid("DCB00005-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetworkConnection
    {
        INetwork GetNetwork();
        short IsConnectedToInternet();
        short IsConnected();
        int GetConnectivity();
        Guid GetConnectionId();
        Guid GetAdapterId();
    }

    [ComImport, Guid("DCB00002-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetwork
    {
        [return: MarshalAs(UnmanagedType.BStr)] string GetName();
        void SetName([MarshalAs(UnmanagedType.BStr)] string name);
        [return: MarshalAs(UnmanagedType.BStr)] string GetDescription();
        void SetDescription([MarshalAs(UnmanagedType.BStr)] string description);
        Guid GetNetworkId();
        int GetDomainType();
        [return: MarshalAs(UnmanagedType.IUnknown)] object GetNetworkConnections();
        void GetTimeCreatedAndConnected(out uint createdLow, out uint createdHigh, out uint connectedLow, out uint connectedHigh);
        short IsConnectedToInternet();
        short IsConnected();
        int GetConnectivity();
        int GetCategory();
    }

    private static uint ToUInt(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
    }

    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
        _timer.Dispose();
    }
}
