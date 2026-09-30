using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VTalk.Core;
using VTalk.Data;
using VTalk.Net;
using VTalk.Security;
using VTalk.Services;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace VTalk.Tests;

internal static class TestSetup
{
    [ModuleInitializer]
    internal static void Init()
    {
        // Testler gerçek kullanıcı verisine dokunmasın.
        Environment.SetEnvironmentVariable("VTALK_PROFILE", "unittests");
        AppPaths.EnsureCreated();
    }
}

public class SecureChannelTests
{
    private static async Task<(SecureChannel client, SecureChannel server)> Connect(Identity clientId, Identity serverId, string expected)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var acceptTask = Task.Run(async () =>
        {
            var tcp = await listener.AcceptTcpClientAsync();
            return await SecureChannel.AcceptAsync(tcp, serverId, CancellationToken.None);
        });
        try
        {
            var client = await SecureChannel.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), clientId, expected, CancellationToken.None);
            var server = await acceptTask;
            return (client, server);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Handshake_AuthenticatesBothSides_AndEncryptsBothDirections()
    {
        using var a = Identity.CreateEphemeral();
        using var b = Identity.CreateEphemeral();
        var (client, server) = await Connect(a, b, b.PeerId);
        await using var _ = client;
        await using var __ = server;

        Assert.Equal(b.PeerId, client.RemotePeerId);
        Assert.Equal(a.PeerId, server.RemotePeerId);

        await client.SendAsync(Encoding.UTF8.GetBytes("merhaba"));
        Assert.Equal("merhaba", Encoding.UTF8.GetString(await server.ReceiveAsync()));

        var big = RandomNumberGenerator.GetBytes(SecureChannel.MaxPlaintext);
        await server.SendAsync(big);
        Assert.Equal(big, await client.ReceiveAsync());
    }

    [Fact]
    public async Task Handshake_RejectsUnexpectedServerIdentity()
    {
        using var a = Identity.CreateEphemeral();
        using var b = Identity.CreateEphemeral();
        using var impostorTarget = Identity.CreateEphemeral();
        await Assert.ThrowsAsync<ProtocolException>(() => Connect(a, b, impostorTarget.PeerId));
    }

    [Fact]
    public async Task Send_RejectsOversizedFrame()
    {
        using var a = Identity.CreateEphemeral();
        using var b = Identity.CreateEphemeral();
        var (client, server) = await Connect(a, b, b.PeerId);
        await using var _ = client;
        await using var __ = server;
        await Assert.ThrowsAsync<ProtocolException>(() => client.SendAsync(new byte[SecureChannel.MaxPlaintext + 1]));
    }
}

public class DirectoryTests
{
    private static SignedDirectory Sign(OrgDirectory dir, ECDsa key)
    {
        dir.OrgKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        var payload = JsonSerializer.SerializeToUtf8Bytes(dir);
        return new SignedDirectory
        {
            Payload = Convert.ToBase64String(payload),
            Signature = Convert.ToBase64String(key.SignData(payload, HashAlgorithmName.SHA256)),
        };
    }

    private static DirectoryService FreshService(byte[] pinned)
    {
        File.Delete(AppPaths.DirectoryFile);
        File.Delete(AppPaths.PinnedOrgKeyFile);
        var svc = new DirectoryService();
        svc.PinOrgKey(pinned);
        return svc;
    }

    private static OrgDirectory Sample(int version) => new()
    {
        Version = version,
        Units = [new OrgUnit { Id = "muhasebe", Name = "Muhasebe" }],
        Members = [new OrgMember { PeerId = new string('a', 32), Name = "Ali", Role = Role.UnitManager, UnitId = "muhasebe" }],
    };

    [Fact]
    public void AcceptsValidSignature_AndEnforcesMonotonicVersion()
    {
        using var org = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var svc = FreshService(org.ExportSubjectPublicKeyInfo());

        Assert.True(svc.TryApply(Sign(Sample(2), org), out _));
        Assert.Equal(Role.UnitManager, svc.GetRole(new string('a', 32)));
        Assert.False(svc.TryApply(Sign(Sample(1), org), out var err));
        Assert.Equal("Eski sürüm.", err);
    }

    [Fact]
    public void RejectsDirectorySignedByOtherKey()
    {
        using var org = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var svc = FreshService(org.ExportSubjectPublicKeyInfo());
        Assert.False(svc.TryApply(Sign(Sample(99), attacker), out _));
        Assert.Null(svc.Current);
    }

    [Fact]
    public void RejectsTamperedPayload()
    {
        using var org = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var svc = FreshService(org.ExportSubjectPublicKeyInfo());
        var signed = Sign(Sample(3), org);
        var dir = JsonSerializer.Deserialize<OrgDirectory>(Convert.FromBase64String(signed.Payload))!;
        dir.Members[0].Role = Role.Admin;
        signed.Payload = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(dir));
        Assert.False(svc.TryApply(signed, out _));
    }

    [Fact]
    public void RejectsMemberInUnknownUnit()
    {
        using var org = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var svc = FreshService(org.ExportSubjectPublicKeyInfo());
        var dir = Sample(4);
        dir.Members[0].UnitId = "yok";
        Assert.False(svc.TryApply(Sign(dir, org), out _));
    }
}

public class AdminKeyStoreTests
{
    [Fact]
    public void CreateUnlock_WrongPasswordFails()
    {
        File.Delete(AppPaths.AdminKeyFile);
        using var key = AdminKeyStore.CreateOrganizationKey("Guclu.Parola123");
        using var unlocked = AdminKeyStore.Unlock("Guclu.Parola123");
        Assert.Equal(key.ExportSubjectPublicKeyInfo(), unlocked.ExportSubjectPublicKeyInfo());
        Assert.Throws<UnauthorizedAccessException>(() => AdminKeyStore.Unlock("yanlis-parola-1A"));
    }

    [Fact]
    public void RejectsWeakPassword()
    {
        Assert.NotNull(AdminKeyStore.ValidatePasswordStrength("kisa"));
        Assert.NotNull(AdminKeyStore.ValidatePasswordStrength("sadecekucukharf"));
        Assert.Null(AdminKeyStore.ValidatePasswordStrength("Guclu.Parola123"));
    }
}

public class StorageTests
{
    [Fact]
    public void LocalProtector_DetectsWrongContext()
    {
        using var p = LocalProtector.LoadOrCreate();
        var blob = p.EncryptString("gizli", "ctx-1");
        Assert.Equal("gizli", p.DecryptString(blob, "ctx-1"));
        Assert.ThrowsAny<CryptographicException>(() => p.DecryptString(blob, "ctx-2"));
    }

    [Fact]
    public void Database_EncryptsContent_AndDeduplicates()
    {
        var path = Path.Combine(AppPaths.DataDir, $"test-{Guid.NewGuid():N}.db");
        using var p = LocalProtector.LoadOrCreate();
        using (var db = new Database(path, p))
        {
            var m = new StoredMessage
            {
                PeerId = new string('b', 32), MsgId = "m1", Direction = MessageDirection.Incoming, Kind = MessageKind.Text,
                Timestamp = 1, State = MessageState.Delivered, Data = new MessageData { Text = "çok gizli mesaj" },
            };
            Assert.True(db.InsertMessage(m));
            Assert.False(db.InsertMessage(m));
            var history = db.LoadHistory(m.PeerId, 10);
            Assert.Single(history);
            Assert.Equal("çok gizli mesaj", history[0].Data.Text);
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var raw = File.ReadAllBytes(path);
        Assert.DoesNotContain("gizli", Encoding.UTF8.GetString(raw));
    }
}

public class SanitizeTests
{
    [Theory]
    [InlineData("..\\..\\Windows\\System32\\evil.dll", "evil.dll")]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("CON.txt", "_CON.txt")]
    [InlineData("com1", "_com1")]
    [InlineData("rapor.pdf:Zone.Identifier", "rapor.pdf_Zone.Identifier")]
    [InlineData("fatura\u202Efdp.exe", "faturafdp.exe")]
    [InlineData("...", "dosya")]
    [InlineData("", "dosya")]
    [InlineData("normal dosya.xlsx", "normal dosya.xlsx")]
    public void SanitizeFileName(string input, string expected) =>
        Assert.Equal(expected, FileTransferService.SanitizeFileName(input));

    [Fact]
    public void TextClean_RemovesControlAndBidiCharacters()
    {
        Assert.Equal("abc", TextLimits.Clean("a\u0000b\u202Ec", 10));
        Assert.Equal("a\nb", TextLimits.Clean("a\r\nb", 10, allowNewLines: true));
    }

    [Fact]
    public void PeerId_IsDerivedFromPublicKey()
    {
        using var id = Identity.CreateEphemeral();
        Assert.Equal(CryptoUtil.PeerIdFromPublicKey(id.PublicKey), id.PeerId);
        Assert.Equal(32, id.PeerId.Length);
    }
}
