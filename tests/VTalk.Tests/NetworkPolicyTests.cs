using System.IO;
using System.Security.Cryptography;
using VTalk.Core;
using VTalk.Data;
using VTalk.Security;
using VTalk.Services;

namespace VTalk.Tests;

public class NetworkPolicyTests
{
    [Fact]
    public void Directory_RejectsInvalidNetworks_AndNormalizesValidOnes()
    {
        var bad = new OrgDirectory { Version = 1, Networks = ["10.20.40.0/23", "999.1.1.1"] };
        Assert.NotNull(bad.Validate());

        var tooMany = new OrgDirectory { Version = 1, Networks = Enumerable.Range(0, OrgDirectory.MaxNetworks + 1).Select(i => $"10.0.{i}.0/24").ToList() };
        Assert.NotNull(tooMany.Validate());

        var good = new OrgDirectory { Version = 1, Networks = [" 10.20.40.0/23 ", "10.20.30.0/24", "10.20.30.0/24"] };
        Assert.Null(good.Validate());
        Assert.Equal(["10.20.40.0/23", "10.20.30.0/24"], good.Networks);
    }

    [Fact]
    public void PublishedNetworks_ReachClients_AndReplaceLegacyLocalSetting()
    {
        using var identity = Identity.CreateEphemeral();
        using var protector = LocalProtector.LoadOrCreate();
        using var db = new Database(Path.Combine(AppPaths.DataDir, $"net-{Guid.NewGuid():N}.db"), protector);
        var directory = new DirectoryService();
        using var orgKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        directory.PinOrgKey(orgKey.ExportSubjectPublicKeyInfo());
        var settings = new Settings { DisplayName = "Test", PreferredTcpPort = 0, ExtraAddresses = "192.168.11.0/24" };
        using var chat = new ChatService(identity, settings, db, directory, loopbackOnly: true);

        // Admin henüz ağ yayınlamadı: eski sürümde kullanıcının girdiği ağ geçerli kalır.
        Assert.Contains("192.168.11.0/24", chat.EffectiveNetworks);

        directory.SignAndApply(new OrgDirectory { Networks = ["10.20.40.0/23", "10.20.30.0/24"] }, orgKey);
        Assert.Contains("10.20.40.0/23", chat.EffectiveNetworks);
        Assert.Contains("10.20.30.0/24", chat.EffectiveNetworks);
        Assert.DoesNotContain("192.168.11.0/24", chat.EffectiveNetworks);
    }
}
