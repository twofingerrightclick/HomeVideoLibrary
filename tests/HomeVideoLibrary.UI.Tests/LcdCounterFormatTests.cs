using HomeVideoLibrary.UI.Controls;
using Xunit;

namespace HomeVideoLibrary.UI.Tests;

public sealed class LcdCounterFormatTests
{
    [Theory]
    [InlineData("00:01:37", LcdHours.Auto, false, "01:37")]
    [InlineData("00:00:00", LcdHours.Auto, false, "00:00")]
    [InlineData("01:02:03", LcdHours.Auto, false, "1:02:03")]
    [InlineData("00:00:09", LcdHours.Always, false, "0:00:09")]
    [InlineData("01:15:00", LcdHours.Never, false, "75:00")]
    [InlineData("02:00:00", LcdHours.Never, false, "120:00")]
    [InlineData("12:34:56", LcdHours.Auto, false, "12:34:56")]
    [InlineData("-00:03:42", LcdHours.Auto, false, "-03:42")]
    [InlineData("-01:00:00", LcdHours.Auto, false, "-1:00:00")]
    [InlineData("00:01:37", LcdHours.Auto, true, " 01:37")]
    [InlineData("-00:01:37", LcdHours.Auto, true, "-01:37")]
    public void Format_ProducesClockText(string value, LcdHours hours, bool reserveSign, string expected)
    {
        Assert.Equal(expected, LcdCounter.Format(TimeSpan.Parse(value), hours, reserveSign));
    }

    [Fact]
    public void Format_TruncatesPartialSeconds()
    {
        Assert.Equal("00:01", LcdCounter.Format(TimeSpan.FromMilliseconds(1999)));
        Assert.Equal("-00:01", LcdCounter.Format(TimeSpan.FromMilliseconds(-1999)));
    }

    [Theory]
    [InlineData("01:37", "01:37", "88:88")]
    [InlineData("-03:42", "-03:42", "888:88")]
    [InlineData(" 1:02:03", "!1:02:03", "88:88:88")]
    [InlineData("--:--", "--:--", "88:88")]
    [InlineData("12.5", "12.5", "88.8")]
    public void ToSegments_KeepsSeparatorsAlignedWithGhost(string text, string lit, string ghost)
    {
        Assert.Equal((lit, ghost), LcdCounter.ToSegments(text));
    }

    [Fact]
    public void ToSegments_GhostIsStableAcrossValuesOfTheSameShape()
    {
        var ghosts = new[] { "00:00", "07:21", "59:59" }
            .Select(t => LcdCounter.ToSegments(LcdCounter.Format(TimeSpan.Parse("00:" + t))).Ghost)
            .Distinct();

        Assert.Single(ghosts);
    }
}
