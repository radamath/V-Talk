using System.IO;
using System.Net;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VTalk.Core;
using VTalk.Data;
using VTalk.Net;
using VTalk.Security;
using VTalk.Services;

namespace VTalk.Tests;

public class ReplayGuardTests
{
    private static AnnouncePayload P(string sid, long ss, long seq) => new() { Id = new string('a', 32), Sid = sid, SessionStart = ss, Seq = seq };

    [Fact]
    public void SameSession_RequiresStrictlyIncreasingSequence()
    {
        var g = new AnnounceReplayGuard();
        var sid = new string('1', 32);
        Assert.True(g.IsFresh(P(sid, 100, 5)));
        g.Remember(P(sid, 100, 5));
        Assert.False(g.IsFresh(P(sid, 100, 5)));
        Assert.False(g.IsFresh(P(sid, 100, 4)));
        Assert.True(g.IsFresh(P(sid, 100, 6)));
    }

    [Fact]
    public void OldSessionReplay_RejectedWhileCurrentSessionIsActive()
    {
        var g = new AnnounceReplayGuard();
        var now = DateTime.UtcNow;
        string oldSid = new('1', 32), newSid = new('2', 32);
        g.Remember(P(newSid, 200, 1), now);

        Assert.False(g.IsFresh(P(oldSid, 100, 999), now.AddSeconds(5)));
        Assert.True(g.IsFresh(P(new string('3', 32), 300, 1), now.AddSeconds(5)));
        // Kişi kapandıktan (duyuru kesildikten) sonra farklı oturum kabul edilir.
        Assert.True(g.IsFresh(P(oldSid, 100, 999), now + AnnounceReplayGuard.SessionStale + TimeSpan.FromSeconds(1)));
    }
}

public class ImageSafetyTests
{
    private static string Write(string name, BitmapEncoder encoder, int w, int h)
    {
        var path = Path.Combine(AppPaths.DataDir, $"{Guid.NewGuid():N}-{name}");
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Gray8, null, new byte[w * h], w);
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        encoder.Save(fs);
        return path;
    }

    [Fact]
    public void AcceptsPng_RejectsOtherCodecsDisguisedAsPng()
    {
        Assert.True(ImageSafety.IsSafeImage(Write("ok.png", new PngBitmapEncoder(), 8, 8)));
        // İçeriği TIFF olan ".png": WIC normalde TIFF çözücüsünü seçerdi.
        Assert.False(ImageSafety.IsSafeImage(Write("tiff.png", new TiffBitmapEncoder(), 8, 8)));
        var junk = Path.Combine(AppPaths.DataDir, $"{Guid.NewGuid():N}.png");
        File.WriteAllBytes(junk, [0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4, 5, 6]);
        Assert.False(ImageSafety.IsSafeImage(junk));
    }

    [Fact]
    public void RejectsOversizedDimensions()
    {
        Assert.False(ImageSafety.IsSafeImage(Write("wide.png", new PngBitmapEncoder(), ImageSafety.MaxSide + 1, 1)));
        Assert.False(ImageSafety.IsSafeImage(File.ReadAllBytes(Write("avatar.png", new PngBitmapEncoder(), 2000, 2)), maxSide: 1024));
    }
}

public class HardeningTests
{
    [Theory]
    [InlineData("fatura.pdf                    .exe", "fatura.pdf.exe")]
    [InlineData("rap\u200Bor.txt", "rapor.txt")]
    [InlineData("\u3164\u3164.exe", "dosya.exe")]
    [InlineData("a\u00ADb.doc", "ab.doc")]
    public void SanitizeFileName_RemovesHidingTricks(string input, string expected) =>
        Assert.Equal(expected, FileTransferService.SanitizeFileName(input));

    [Fact]
    public void TextClean_LimitsZalgo_AndStripsInvisibleCharacters()
    {
        var zalgo = "a" + new string('\u0301', 50);
        Assert.Equal(4, TextLimits.Clean(zalgo, 100).Length);
        Assert.Equal("Ali Veli", TextLimits.Clean("Ali\u200B   Veli\uFEFF", 64));
        Assert.Equal("👨\u200D👩", TextLimits.Clean("👨\u200D👩", 64, allowNewLines: true));
        Assert.Equal("👨👩", TextLimits.Clean("👨\u200D👩", 64));
    }

    [Theory]
    [InlineData("setup.exe", true)]
    [InlineData("yardim.chm", true)]
    [InlineData("ayar.settingcontent-ms", true)]
    [InlineData("eklenti.xll", true)]
    [InlineData("belge.docm", true)]
    [InlineData("rapor.pdf", false)]
    [InlineData("tablo.xlsx", false)]
    [InlineData("foto.JPG", false)]
    public void OpenWarning_UsesAllowList(string name, bool warn) =>
        Assert.Equal(warn, FileTransferService.NeedsOpenWarning(name));

    [Fact]
    public void Packet_RemoteErrorText_IsSanitized()
    {
        var raw = System.Text.Encoding.UTF8.GetBytes(
            System.Text.Json.JsonSerializer.Serialize(new { t = "err", err = "satir1\r\n2026-01-01 [INFO] sahte" + new string('x', 5000) }));
        var p = Packet.TryParse(raw)!;
        Assert.DoesNotContain('\n', p.Err!);
        Assert.True(p.Err!.Length <= 200);
    }

    [Fact]
    public void FileTrust_RejectsFilesWritableByOtherUsers()
    {
        var dir = Path.Combine(AppPaths.DataDir, "trust-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "org.pub");
        File.WriteAllText(file, "x");
        Assert.True(FileTrust.IsProtectedFromOtherUsers(file, out var reason), reason);

        var info = new FileInfo(file);
        var acl = info.GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.Modify, AccessControlType.Allow));
        info.SetAccessControl(acl);
        Assert.False(FileTrust.IsProtectedFromOtherUsers(file, out _));
    }

    [Fact]
    public void NetworkGuard_BlocksSubnetsOfPublicNetworks()
    {
        using var guard = new NetworkGuard(allowPublicNetworks: false);
        Assert.True(guard.IsAllowedRemote(IPAddress.Loopback));

        // Ortak olarak sınıflandırılmış her arabirimin alt ağındaki adresler reddedilmeli.
        foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            if (!guard.IsBlockedInterface(nic.Id)) continue;
            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    Assert.False(guard.IsAllowedRemote(ua.Address));
            }
        }
        guard.AllowPublicNetworks = true;
        Assert.False(guard.AnyBlockedInterface);
        Assert.Empty(guard.BlockedNetworkNames);
    }

    [Fact]
    public void NetworkTargets_ExpandsCidrAndSingleHosts()
    {
        var expanded = NetworkTargets.Expand("10.20.30.0/30, 192.168.5.7");
        // /30 => 4 adres + tek host.
        Assert.Contains(IPAddress.Parse("10.20.30.0"), expanded);
        Assert.Contains(IPAddress.Parse("10.20.30.3"), expanded);
        Assert.Contains(IPAddress.Parse("192.168.5.7"), expanded);
        Assert.Equal(5, expanded.Count);

        // Aşırı geniş aralık (tüm internet) taranmaz.
        Assert.Empty(NetworkTargets.Expand("0.0.0.0/0"));
        Assert.Empty(NetworkTargets.Expand("10.0.0.0/8"));

        Assert.False(NetworkTargets.TryParse("not-an-ip", out _, out _));
        Assert.False(NetworkTargets.TryParse("10.0.0.1/33", out _, out _));
        Assert.True(NetworkTargets.TryParse("10.0.0.1/24", out _, out var prefix) && prefix == 24);
    }

    [Fact]
    public void NetworkTargets_ParsesSubnetsForAllowList()
    {
        var subnets = NetworkTargets.Subnets("10.77.0.0/16, 192.168.1.50");
        // /16 => mask 255.255.0.0 (0xFFFF0000); tek adres => /32 (0xFFFFFFFF).
        Assert.Contains((0x0A4D0000u, 0xFFFF0000u), subnets);   // 10.77.0.0 / 255.255.0.0
        Assert.Contains((0xC0A80132u, 0xFFFFFFFFu), subnets);   // 192.168.1.50 / 32
        // 0.0.0.0/0 beyaz listeye alınmaz.
        Assert.Empty(NetworkTargets.Subnets("0.0.0.0/0"));
    }

    [Fact]
    public void NetworkGuard_AllowsConfiguredVpnSubnet()
    {
        using var guard = new NetworkGuard(allowPublicNetworks: false, allowedRemoteSubnets: "10.77.0.0/16");
        Assert.True(guard.IsAllowedRemote(IPAddress.Parse("10.77.5.9")));
        Assert.True(guard.IsAllowedRemote(IPAddress.Loopback));
    }
}

public class AccessControlIntegrationTests
{
    private sealed class Peer : IDisposable
    {
        public Identity Id { get; } = Identity.CreateEphemeral();
        public Database Db { get; }
        public ChatService Chat { get; }
        public string Name { get; }
        private readonly LocalProtector _protector = LocalProtector.LoadOrCreate();

        public Peer(string name)
        {
            Name = name;
            Db = new Database(Path.Combine(AppPaths.DataDir, $"ac-{name}-{Guid.NewGuid():N}.db"), _protector);
            // Testler yerel makinenin ağ yapılandırmasından bağımsız olmalı: yalnızca 127.0.0.1, eşler elle tanıtılır.
            var settings = new Settings
            {
                DisplayName = name, PreferredTcpPort = 0, AutoAcceptImages = false,
                DownloadFolder = Path.Combine(AppPaths.DataDir, "dl-" + Guid.NewGuid().ToString("N")),
            };
            Chat = new ChatService(Id, settings, Db, new DirectoryService(), loopbackOnly: true);
            Chat.Start();
        }

        public void Dispose()
        {
            Chat.Dispose();
            Db.Dispose();
            _protector.Dispose();
            Id.Dispose();
        }
    }

    private static async Task<T> WaitFor<T>(Func<T?> probe, int timeoutMs = 20000) where T : class
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < until)
        {
            if (probe() is { } v) return v;
            await Task.Delay(100);
        }
        throw new TimeoutException("Beklenen durum oluşmadı.");
    }

    // Eşleri birbirine 127.0.0.1 üzerinden elle tanıtır (ortamdan bağımsız test için).
    private static void Introduce(params Peer[] peers)
    {
        foreach (var a in peers)
        foreach (var b in peers)
        {
            if (ReferenceEquals(a, b)) continue;
            a.Chat.Registry.Upsert(new PeerSnapshot
            {
                PeerId = b.Id.PeerId, PublicKey = b.Id.PublicKey, Name = b.Name, Status = PresenceStatus.Online,
                Address = IPAddress.Loopback, Port = b.Chat.TcpPort, LastSeenUtc = DateTime.UtcNow,
            });
        }
    }

    private static async Task<Packet> SendRaw(Peer target, Identity from, Packet packet)
    {
        var endpoint = new IPEndPoint(IPAddress.Loopback, target.Chat.TcpPort);
        await using var ch = await SecureChannel.ConnectAsync(endpoint, from, target.Id.PeerId, CancellationToken.None);
        await ch.SendAsync(packet.Serialize());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        return Packet.TryParse(await ch.ReceiveAsync(cts.Token))!;
    }

    [Fact]
    public async Task MembersOnlyPolicy_RejectsOutsiders_AndHidesThem()
    {
        using var admin = new Peer("Admin");
        using var member = new Peer("Uye");
        using var outsider = new Peer("Yabanci");
        Introduce(admin, member, outsider);

        using var orgKey = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        admin.Chat.Directory.PinOrgKey(orgKey.ExportSubjectPublicKeyInfo());
        var signed = admin.Chat.Directory.SignAndApply(new OrgDirectory
        {
            MembersOnly = true,
            Members =
            [
                new OrgMember { PeerId = admin.Id.PeerId, Name = "Admin", Role = Role.Admin },
                new OrgMember { PeerId = member.Id.PeerId, Name = "Üye", Role = Role.User },
            ],
        }, orgKey);

        // İmzalı dizin her iki eşte de doğrulanıp uygulanabilmeli (dizin dışı cihaz da yetkisini öğrenir).
        Assert.True(member.Chat.Directory.TryApply(signed, out _));
        Assert.True(outsider.Chat.Directory.TryApply(signed, out _));

        Assert.False(member.Chat.IsVisible(outsider.Id.PeerId));
        Assert.True(admin.Chat.IsVisible(outsider.Id.PeerId));

        var sent = outsider.Chat.SendText(member.Id.PeerId, "parolanı söyler misin")!;
        await WaitFor(() => outsider.Db.GetMessage(member.Id.PeerId, sent.MsgId, MessageDirection.Outgoing) is { State: MessageState.Failed } m ? m : null);
        Assert.Empty(member.Db.LoadHistory(outsider.Id.PeerId, 10));

        // Üyeler arası iletişim çalışmaya devam eder.
        var ok = admin.Chat.SendText(member.Id.PeerId, "merhaba")!;
        await WaitFor(() => admin.Db.GetMessage(member.Id.PeerId, ok.MsgId, MessageDirection.Outgoing) is { State: MessageState.Delivered } m ? m : null);
    }

    [Fact]
    public async Task DirectTcp_NewIdentitiesPerSource_AreRateLimited()
    {
        using var host = new Peer("Alici");
        int accepted = 0, rejected = 0;
        for (int i = 0; i < 25; i++)
        {
            using var other = Identity.CreateEphemeral();
            var reply = await SendRaw(host, other, new Packet { T = PacketTypes.Message, Id = CryptoUtil.NewId(), Text = "deneme" });
            if (reply.T == PacketTypes.Ack) accepted++;
            else if (reply.Err == "forbidden") rejected++;
        }
        // Aynı kaynaktan yeni kimlik açma sayısı sınırlıdır (IP başına üst sınır).
        Assert.Equal(20, accepted);
        Assert.Equal(5, rejected);
    }
}
