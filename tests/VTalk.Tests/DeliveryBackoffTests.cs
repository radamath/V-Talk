using VTalk.Services;

namespace VTalk.Tests;

public class DeliveryBackoffTests
{
    private const string Peer = "e5af3e6c000000000000000000000000";
    private static readonly DateTime T0 = new(2026, 11, 20, 10, 41, 0, DateTimeKind.Utc);

    [Fact]
    public void RetriesThinOut_UpToTenMinutes()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), DeliveryBackoff.DelayFor(1));
        Assert.Equal(TimeSpan.FromMinutes(1), DeliveryBackoff.DelayFor(2));
        Assert.Equal(TimeSpan.FromMinutes(8), DeliveryBackoff.DelayFor(5));
        Assert.Equal(DeliveryBackoff.MaxDelay, DeliveryBackoff.DelayFor(6));
        Assert.Equal(DeliveryBackoff.MaxDelay, DeliveryBackoff.DelayFor(1000));
    }

    [Fact]
    public void TimerSkipsPeer_UntilBackoffElapses()
    {
        var backoff = new DeliveryBackoff();
        Assert.False(backoff.ShouldWait(Peer, T0));

        Assert.Equal(1, backoff.RecordFailure(Peer, T0));
        Assert.True(backoff.ShouldWait(Peer, T0.AddSeconds(29)));
        Assert.False(backoff.ShouldWait(Peer, T0.AddSeconds(31)));

        Assert.Equal(2, backoff.RecordFailure(Peer, T0.AddSeconds(31)));
        Assert.True(backoff.ShouldWait(Peer, T0.AddSeconds(31 + 59)));
    }

    [Fact]
    public void OnlyFirstFailureAndRecovery_AreReported()
    {
        var backoff = new DeliveryBackoff();
        Assert.False(backoff.RecordSuccess(Peer)); // hiç hata yokken kurtulma yazılmaz

        Assert.Equal(1, backoff.RecordFailure(Peer, T0));
        Assert.True(backoff.RecordFailure(Peer, T0.AddMinutes(1)) > 1);
        Assert.True(backoff.IsFailing(Peer));

        Assert.True(backoff.RecordSuccess(Peer));
        Assert.False(backoff.IsFailing(Peer));
        Assert.False(backoff.ShouldWait(Peer, T0.AddMinutes(1)));
        Assert.Equal(1, backoff.RecordFailure(Peer, T0.AddMinutes(2))); // yeni hata serisi yeniden yazılır
    }
}
