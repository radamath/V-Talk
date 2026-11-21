using VTalk.Services;

namespace VTalk.Tests;

public class VersionTextTests
{
    [Theory]
    [InlineData("1.3.32.0", "1.3.32")]
    [InlineData("1.3.32.7", "1.3.32.7")]
    [InlineData(" 1.4 ", "1.4")]
    [InlineData("2.0.0", "2.0")]
    public void ValidVersions_AreNormalized(string raw, string expected)
    {
        Assert.Equal(expected, AppUpdate.CleanVersionText(raw));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1")]
    [InlineData("1.3.32-beta")]
    [InlineData("1.-3")]
    [InlineData("Güncelleme tamamlandı ✔")]
    [InlineData("1.3‮gnp.exe")]
    [InlineData("1.3.32\r\nSahte satır")]
    public void NonVersionText_IsRejected(string? raw)
    {
        Assert.Null(AppUpdate.CleanVersionText(raw));
    }

    [Fact]
    public void OverlongText_IsRejected()
    {
        Assert.Null(AppUpdate.CleanVersionText(new string('1', 33)));
        Assert.Null(AppUpdate.CleanVersionText("1.2" + new string(' ', 40)));
    }
}
