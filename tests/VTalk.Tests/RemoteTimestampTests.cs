using VTalk.Core;

namespace VTalk.Tests;

public class RemoteTimestampTests
{
    private const long Now = 1_790_000_000_000; // 2026
    private static readonly long Skew = (long)RemoteTimestamp.MaxClockSkew.TotalMilliseconds;

    [Fact]
    public void PastAndSlightlyAheadTimes_AreKept()
    {
        // Eşitlemeyle günler sonra yeniden gelen eski mesaj gönderildiği zamanı korur.
        var lastWeek = Now - (long)TimeSpan.FromDays(7).TotalMilliseconds;
        Assert.Equal(lastWeek, RemoteTimestamp.Sanitize(lastWeek, Now));
        Assert.Equal(Now + Skew, RemoteTimestamp.Sanitize(Now + Skew, Now));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(long.MinValue)]
    [InlineData(long.MaxValue)]
    public void InvalidTimes_FallBackToNow(long remote)
    {
        Assert.Equal(Now, RemoteTimestamp.Sanitize(remote, Now));
    }

    [Fact]
    public void FutureBeyondClockSkew_FallsBackToNow()
    {
        // Mesajı listede sürekli "en yeni" göstermek için ileri tarih atma girişimi.
        Assert.Equal(Now, RemoteTimestamp.Sanitize(Now + Skew + 1, Now));
    }

    [Theory]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    [InlineData(253402300800000)] // yıl 10000
    public void ToLocalTime_DoesNotThrowForCorruptStoredValues(long stored)
    {
        var local = RemoteTimestamp.ToLocalTime(stored);
        Assert.InRange(local, DateTime.MinValue, DateTime.MaxValue);
    }

    [Fact]
    public void ToLocalTime_MatchesFrameworkConversionForNormalValues()
    {
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(Now).LocalDateTime, RemoteTimestamp.ToLocalTime(Now));
    }
}
