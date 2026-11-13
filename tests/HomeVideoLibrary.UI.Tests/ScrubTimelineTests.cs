using HomeVideoLibrary.UI.Logic;
using Xunit;

namespace HomeVideoLibrary.UI.Tests;

public class ScrubTimelineTests
{
    private static readonly TimeSpan TenMinutes = TimeSpan.FromMinutes(10);

    private static TimeSpan Sec(double seconds) => TimeSpan.FromSeconds(seconds);

    private static ChapterSpan Span(double start, double end, string title) => new(Sec(start), Sec(end), title);

    [Fact]
    public void AdjacentRangesKeepIncumbentSegmentGapsAndTinyRangeWidth()
    {
        var extents = ScrubTimeline.LayoutSegments([Span(0, 300, "A"), Span(300, 300.5, "Tiny"), Span(300.5, 600, "B")],
            TenMinutes, 1000, 3, 2);
        Assert.Equal(2, extents[1].Width, 6);
        Assert.Equal(3, extents[1].Left - extents[0].Right, 6);
        Assert.Equal(3, extents[2].Left - extents[1].Right, 6);
    }

    [Theory]
    [InlineData(62.75, "1:02")]
    [InlineData(62, "1:02")]
    [InlineData(62.7501, "1:02")]
    [InlineData(62.0000002, "1:02")]
    public void MomentTimestampShowsWholeSeconds(double seconds, string expected)
    {
        Assert.Equal(expected, ScrubTimeline.FormatMomentTimestamp(Sec(seconds)));
    }

    [Fact]
    public void ExplicitRangesIgnoreMomentsAndPreserveGaps()
    {
        var spans = ScrubTimeline.BuildSpans([
            new ChapterMark(Sec(30), "Moment"),
            new ChapterMark(Sec(10), "Range") { End = Sec(50) },
            new ChapterMark(Sec(60), "Later") { End = Sec(90) }], TenMinutes);
        Assert.Equal([Span(10, 50, "Range"), Span(60, 90, "Later")], spans);
        Assert.Equal(-1, ScrubTimeline.SpanIndexAt(spans, Sec(55)));
        Assert.Equal(0, ScrubTimeline.SpanIndexAt(spans, Sec(30)));
        Assert.Empty(ScrubTimeline.BuildSpans([], TenMinutes));
        Assert.Empty(ScrubTimeline.BuildSpans([new ChapterMark(Sec(30), "Moment")], TenMinutes));
    }

    [Fact]
    public void LayoutUsesActualExtentWithoutFillingGaps()
    {
        var extents = ScrubTimeline.LayoutSegments([Span(60, 120, "Range"), Span(300, 360, "Range")], TenMinutes, 600, 3, 2);
        Assert.Equal([new SegmentExtent(60, 120), new SegmentExtent(300, 360)], extents);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(150, 250)]
    [InlineData(600, 1000)]
    [InlineData(-30, 0)]
    [InlineData(900, 1000)]
    public void TimeToX_MapsProportionallyAndClamps(double seconds, double expectedX)
    {
        Assert.Equal(expectedX, ScrubTimeline.TimeToX(Sec(seconds), TenMinutes, 1000), 6);
    }

    [Theory]
    [InlineData(250, 150)]
    [InlineData(-40, 0)]
    [InlineData(1400, 600)]
    public void XToTime_MapsProportionallyAndClamps(double x, double expectedSeconds)
    {
        Assert.Equal(Sec(expectedSeconds), ScrubTimeline.XToTime(x, TenMinutes, 1000));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1.25)]
    [InlineData(333.3)]
    [InlineData(599.9)]
    public void TimeAndX_RoundTrip(double seconds)
    {
        var x = ScrubTimeline.TimeToX(Sec(seconds), TenMinutes, 987);

        Assert.Equal(seconds, ScrubTimeline.XToTime(x, TenMinutes, 987).TotalSeconds, 3);
    }

    [Fact]
    public void Mapping_IsSafeForZeroDurationAndWidth()
    {
        Assert.Equal(0, ScrubTimeline.TimeToX(Sec(5), TimeSpan.Zero, 1000));
        Assert.Equal(0, ScrubTimeline.TimeToX(Sec(5), TenMinutes, 0));
        Assert.Equal(TimeSpan.Zero, ScrubTimeline.XToTime(500, TimeSpan.Zero, 1000));
        Assert.Equal(TimeSpan.Zero, ScrubTimeline.XToTime(500, TenMinutes, 0));
    }

    [Theory]
    [InlineData(-1, -1)]
    [InlineData(0, 0)]
    [InlineData(119.999, 0)]
    [InlineData(120, 1)]
    [InlineData(300, 2)]
    [InlineData(600, -1)]
    [InlineData(700, -1)]
    public void SpanIndexAt_AssignsBoundariesToTheSpanStartingThere(double seconds, int expected)
    {
        IReadOnlyList<ChapterSpan> spans = [Span(0, 120, "A"), Span(120, 300, "B"), Span(300, 600, "C")];

        Assert.Equal(expected, ScrubTimeline.SpanIndexAt(spans, Sec(seconds)));
    }

    [Fact]
    public void SpanIndexAt_NoSpansIsMinusOne()
    {
        Assert.Equal(-1, ScrubTimeline.SpanIndexAt([], Sec(1)));
    }

    [Fact]
    public void LayoutSegments_NeverProducesNegativeWidths()
    {
        IReadOnlyList<ChapterSpan> spans = [Span(0, 1, "A"), Span(1, 2, "B"), Span(2, 3, "C"), Span(3, 600, "D")];

        foreach (var width in new[] { 0.0, 1, 5, 11, 400 })
        {
            var extents = ScrubTimeline.LayoutSegments(spans, TenMinutes, width, gap: 3, minWidth: 2);

            Assert.All(extents, e => Assert.True(e.Width >= 0));
            Assert.All(extents.Zip(extents.Skip(1)), pair => Assert.True(pair.Second.Left >= pair.First.Right - 1e-9));
        }
    }

    [Fact]
    public void LayoutSegments_SingleSpanFillsTheWidth()
    {
        Assert.Equal([new SegmentExtent(0, 640)], ScrubTimeline.LayoutSegments([Span(0, 600, "")], TenMinutes, 640, 3, 2));
    }

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(65, "1:05")]
    [InlineData(2519, "41:59")]
    [InlineData(3600, "1:00:00")]
    [InlineData(4271, "1:11:11")]
    [InlineData(-3, "0:00")]
    public void FormatTimestamp_UsesHoursOnlyWhenNeeded(double seconds, string expected)
    {
        Assert.Equal(expected, ScrubTimeline.FormatTimestamp(Sec(seconds)));
    }
}
