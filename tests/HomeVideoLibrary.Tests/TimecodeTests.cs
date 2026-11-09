using HomeVideoLibrary.Core;
using Xunit;

namespace HomeVideoLibrary.Tests;

public sealed class TimecodeTests
{
    [Theory]
    [InlineData("90", 90_000)]
    [InlineData("1:30", 90_000)]
    [InlineData("1:02:03.5", 3_723_500)]
    [InlineData(".5", 500)]
    [InlineData(" 0:00:01.250 ", 1_250)]
    [InlineData("90:00", 5_400_000)]
    public void ParsesSecondsMinutesAndHours(string text, int expectedMilliseconds)
    {
        Assert.True(Timecode.TryParse(text, out var value));
        Assert.Equal(TimeSpan.FromMilliseconds(expectedMilliseconds), value);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("1:60")]
    [InlineData("1:00:60")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("1:2:3:4")]
    [InlineData("1.")]
    [InlineData("1:")]
    [InlineData("9999999")]
    public void RejectsInvalidText(string text) => Assert.False(Timecode.TryParse(text, out _));

    [Fact]
    public void FormatParsesBackToTheSameMillisecond()
    {
        var value = new TimeSpan(0, 26, 3, 4, 567);
        Assert.Equal("26:03:04.567", Timecode.Format(value));
        Assert.True(Timecode.TryParse(Timecode.Format(value), out var parsed));
        Assert.Equal(value, parsed);
    }
}
