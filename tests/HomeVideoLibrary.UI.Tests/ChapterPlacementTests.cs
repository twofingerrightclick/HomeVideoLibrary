using HomeVideoLibrary.UI.Logic;
using Xunit;

namespace HomeVideoLibrary.UI.Tests;

public class ChapterPlacementTests
{
    private static readonly IReadOnlyList<TimeSpan> Starts = [Sec(0), Sec(60), Sec(180)];

    private static TimeSpan Sec(double seconds) => TimeSpan.FromSeconds(seconds);

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 1)]
    [InlineData(30, 1)]
    [InlineData(60, 2)]
    [InlineData(179.9, 2)]
    [InlineData(180, 3)]
    [InlineData(900, 3)]
    public void PlaceholderIndex_GoesAfterEveryChapterStartingAtOrBeforeThePlayhead(double playhead, int expected)
    {
        Assert.Equal(expected, ChapterPlacement.PlaceholderIndex(Starts, Sec(playhead)));
    }

    [Fact]
    public void PlaceholderIndex_EmptyListIsZero()
    {
        Assert.Equal(0, ChapterPlacement.PlaceholderIndex([], Sec(42)));
    }

    [Theory]
    [InlineData(60, true)]
    [InlineData(60.5, true)]
    [InlineData(59.5, true)]
    [InlineData(60.51, false)]
    [InlineData(120, false)]
    public void HasChapterAt_MatchesWithinToleranceInclusive(double playhead, bool expected)
    {
        Assert.Equal(expected, ChapterPlacement.HasChapterAt(Starts, Sec(playhead), Sec(0.5)));
    }

    [Fact]
    public void HasChapterAt_EmptyListIsFalse()
    {
        Assert.False(ChapterPlacement.HasChapterAt([], Sec(0), Sec(1)));
    }
}
