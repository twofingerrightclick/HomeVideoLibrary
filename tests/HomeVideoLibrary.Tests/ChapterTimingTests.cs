using HomeVideoLibrary.Core;
using Xunit;

namespace HomeVideoLibrary.Tests;

public sealed class ChapterTimingTests
{
    private static TimeSpan S(int seconds) => TimeSpan.FromSeconds(seconds);
    private static VideoChapter C(ulong uid, int start, int end) => new(uid, S(start), "", "", []) { End = S(end) };

    [Fact]
    public void MomentsCoexistWithRangesAndNeverShortenThem()
    {
        var chapters = ChapterTiming.Apply([C(1, 0, 90)], C(2, 30, 30), S(100));
        chapters = ChapterTiming.Apply(chapters, C(3, 0, 0), S(100));
        Assert.Equal(S(90), chapters.Single(c => c.Uid == 1).End);
        Assert.Throws<ArgumentException>(() => ChapterTiming.Apply(chapters, C(4, 30, 30), S(100)));
        Assert.Null(ChapterTiming.Conflict(chapters, S(100)));
    }

    [Fact]
    public void InsertingAndConvertingRangesShortensPredecessorAndCapsAtNextRange()
    {
        var chapters = ChapterTiming.Apply([C(1, 0, 80), C(2, 80, 100), C(3, 50, 50)], C(4, 20, 95), S(100));
        Assert.Equal(S(20), chapters.Single(c => c.Uid == 1).End);
        Assert.Equal(S(80), chapters.Single(c => c.Uid == 4).End);
        chapters = ChapterTiming.Apply(chapters, C(3, 50, 99), S(100));
        Assert.Equal(S(50), chapters.Single(c => c.Uid == 4).End);
        Assert.Equal(S(80), chapters.Single(c => c.Uid == 3).End);
        Assert.Null(ChapterTiming.Conflict(chapters, S(100)));
    }

    [Fact]
    public void RejectsInvalidTimesAndReadsOverlapsAsConflicts()
    {
        Assert.Throws<ArgumentException>(() => ChapterTiming.Apply([], C(1, 100, 100), S(100)));
        Assert.Throws<ArgumentException>(() => ChapterTiming.Apply([], C(1, 50, 40), S(100)));
        Assert.NotNull(ChapterTiming.Conflict([C(1, 0, 60), C(2, 50, 90)], S(100)));
        Assert.Equal(S(100), Assert.Single(ChapterTiming.Apply([], C(1, 90, 120), S(100))).End);
    }
}
