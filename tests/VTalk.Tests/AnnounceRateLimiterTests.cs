using System.Net;
using VTalk.Net;

namespace VTalk.Tests;

public class AnnounceRateLimiterTests
{
    private static readonly DateTime T0 = new(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);
    private static readonly IPAddress Attacker = IPAddress.Parse("10.0.0.66");

    private static IPAddress Spoofed(int i) => new(BitConverter.GetBytes(0x0A000000 + 1000 + i).Reverse().ToArray());

    [Fact]
    public void Source_IsLimitedPerSecond_AndRecoversInNextWindow()
    {
        var limiter = new AnnounceRateLimiter();
        for (var i = 0; i < AnnounceRateLimiter.PerSourcePerSecond; i++)
            Assert.True(limiter.AllowSource(Attacker, T0));
        Assert.False(limiter.AllowSource(Attacker, T0));
        Assert.True(limiter.AllowSource(Attacker, T0.AddSeconds(1.5)));
    }

    [Fact]
    public void FillingTableWithSpoofedSources_DoesNotResetAttackersCounter()
    {
        var limiter = new AnnounceRateLimiter();
        for (var i = 0; i < AnnounceRateLimiter.PerSourcePerSecond; i++) limiter.AllowSource(Attacker, T0);
        Assert.False(limiter.AllowSource(Attacker, T0));

        // Eski davranış: 4096'yı aşınca tablo tamamen siliniyor ve saldırganın sayacı sıfırlanıyordu.
        for (var i = 0; i < AnnounceRateLimiter.MaxTrackedSources + 10; i++) limiter.AllowSource(Spoofed(i), T0);

        Assert.False(limiter.AllowSource(Attacker, T0));
    }

    [Fact]
    public void FullTable_RejectsNewSources_UntilEntriesExpire()
    {
        var limiter = new AnnounceRateLimiter();
        for (var i = 0; i < AnnounceRateLimiter.MaxTrackedSources; i++) Assert.True(limiter.AllowSource(Spoofed(i), T0));

        Assert.False(limiter.AllowSource(Attacker, T0));
        Assert.True(limiter.AllowSource(Attacker, T0.AddSeconds(2)));
    }

    [Fact]
    public void UnknownIdentityFlood_CannotStarveKnownPeers()
    {
        var limiter = new AnnounceRateLimiter();
        var accepted = 0;
        for (var i = 0; i < AnnounceRateLimiter.VerifiesPerSecond * 2; i++)
            if (limiter.AllowVerify(knownIdentity: false, T0)) accepted++;
        Assert.Equal(AnnounceRateLimiter.UnknownVerifiesPerSecond, accepted);

        // Sahte kimlik seline rağmen daha önce doğrulanmış eşlerin duyuruları için bütçe kalır.
        for (var i = 0; i < AnnounceRateLimiter.VerifiesPerSecond - AnnounceRateLimiter.UnknownVerifiesPerSecond; i++)
            Assert.True(limiter.AllowVerify(knownIdentity: true, T0));
        Assert.False(limiter.AllowVerify(knownIdentity: true, T0));

        Assert.True(limiter.AllowVerify(knownIdentity: false, T0.AddSeconds(1.5)));
    }
}
