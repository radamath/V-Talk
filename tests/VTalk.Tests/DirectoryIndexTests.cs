using System.Security.Cryptography;
using VTalk.Core;
using VTalk.Security;

namespace VTalk.Tests;

public class DirectoryIndexTests
{
    private static string Id(int i) => i.ToString("x32");

    [Fact]
    public void GetMember_UsesIndex_AndFollowsNewlyAppliedDirectory()
    {
        var dirs = new DirectoryService();
        using var orgKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        dirs.PinOrgKey(orgKey.ExportSubjectPublicKeyInfo());

        dirs.SignAndApply(new OrgDirectory
        {
            Members = Enumerable.Range(1, 4000)
                .Select(i => new OrgMember { PeerId = Id(i), Name = "Kişi " + i, Role = Role.User })
                .ToList(),
        }, orgKey);

        Assert.Equal("Kişi 3999", dirs.GetMember(Id(3999))?.Name);
        Assert.Null(dirs.GetMember(Id(5000)));
        Assert.Null(dirs.GetMember(null));

        // Yeni imzalı dizin uygulanınca eski indeks kullanılmamalı.
        dirs.SignAndApply(new OrgDirectory
        {
            Members = [new OrgMember { PeerId = Id(5000), Name = "Yeni", Role = Role.Admin }],
        }, orgKey);

        Assert.Null(dirs.GetMember(Id(3999)));
        Assert.Equal(Role.Admin, dirs.GetRole(Id(5000)));
    }
}
