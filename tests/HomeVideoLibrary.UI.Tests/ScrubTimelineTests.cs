using HomeVideoLibrary.UI.Logic;
using Xunit;

namespace HomeVideoLibrary.UI.Tests;

public class ScrubTimelineTests
{
    private static readonly TimeSpan TenMinutes = TimeSpan.FromMinutes(10);

    private static TimeSpan Sec(double seconds) => TimeSpan.FromSeconds(seconds);

    private static ChapterSpan Span(double start, double end, string title) => new(Sec(start), Sec(end), title);

    [Fact]
    public void BuildSpans_SortsMarksAndEndsEachAtTheNextStart()
    {
        var spans = ScrubTimeline.BuildSpans(
            [new ChapterMark(Sec(300), "Cake"), new ChapterMark(Sec(0), "Arrival"), new ChapterMark(Sec(120), "Gifts")],
            TenMinutes);

        Assert.Equal([Span(0, 120, "Arrival"), Span(120, 300, "Gifts"), Span(300, 600, "Cake")], spans);
    }

    [Fact]
    public void BuildSpans_KeepsTheFirstMarkAtARepeatedStart()
    {
        var spans = ScrubTimeline.BuildSpans(
            [new ChapterMark(Sec(0), "Intro"), new ChapterMark(Sec(60), "First"), new ChapterMark(Sec(60), "Second")],
            TenMinutes);

        Assert.Equal([Span(0, 60, "Intro"), Span(60, 600, "First")], spans);
    }

    [Fact]
    public void BuildSpans_DropsMarksAtOrBeyondTheDurationAndClampsNegativeStarts()
    {
        var spans = ScrubTimeline.BuildSpans(
            [new ChapterMark(Sec(-5), "Pre-roll"), new ChapterMark(Sec(600), "At end"), new ChapterMark(Sec(900), "Past end")],
            TenMinutes);

        Assert.Equal([Span(0, 600, "Pre-roll")], spans);
    }

    [Fact]
    public void BuildSpans_AddsAnUntitledLeadingSpanBeforeALateFirstChapter()
    {
        var spans = ScrubTimeline.BuildSpans([new ChapterMark(Sec(45), "Opening")], TenMinutes);

        Assert.Equal([Span(0, 45, ""), Span(45, 600, "Opening")], spans);
    }

    [Fact]
    public void BuildSpans_WithoutMarksCoversTheWholeDuration()
    {
        Assert.Equal([Span(0, 600, "")], ScrubTimeline.BuildSpans([], TenMinutes));
    }

    [Fact]
    public void BuildSpans_ZeroDurationHasNoSpans()
    {
        Assert.Empty(ScrubTimeline.BuildSpans([new ChapterMark(Sec(0), "Anything")], TimeSpan.Zero));
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
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(119.999, 0)]
    [InlineData(120, 1)]
    [InlineData(300, 2)]
    [InlineData(600, 2)]
    [InlineData(700, 2)]
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
    public void LayoutSegments_SplitsTheGapAcrossInteriorBoundariesOnly()
    {
        IReadOnlyList<ChapterSpan> spans = [Span(0, 300, "A"), Span(300, 600, "B")];

        var extents = ScrubTimeline.LayoutSegments(spans, TenMinutes, 1000, gap: 4, minWidth: 2);

        Assert.Equal([new SegmentExtent(0, 498), new SegmentExtent(502, 1000)], extents);
    }

    [Fact]
    public void LayoutSegments_WidensATinySegmentByBorrowingFromItsNeighbours()
    {
        IReadOnlyList<ChapterSpan> spans = [Span(0, 300, "A"), Span(300, 300.5, "Blink"), Span(300.5, 600, "C")];

        var extents = ScrubTimeline.LayoutSegments(spans, TenMinutes, 1000, gap: 3, minWidth: 2);

        Assert.Equal(3, extents.Count);
        Assert.Equal(2, extents[1].Width, 6);
        Assert.Equal(3, extents[1].Left - extents[0].Right, 6);
        Assert.Equal(3, extents[2].Left - extents[1].Right, 6);
        Assert.Equal(0, extents[0].Left);
        Assert.Equal(1000, extents[2].Right);
    }

    [Fact]
    public void LayoutSegments_PushesTinySegmentsAtTheEndsInward()
    {
        IReadOnlyList<ChapterSpan> spans = [Span(0, 0.1, "Head"), Span(0.1, 599.9, "Body"), Span(599.9, 600, "Tail")];

        var extents = ScrubTimeline.LayoutSegments(spans, TenMinutes, 1000, gap: 3, minWidth: 2);

        Assert.Equal(new SegmentExtent(0, 2), extents[0]);
        Assert.Equal(new SegmentExtent(5, 995), extents[1]);
        Assert.Equal(new SegmentExtent(998, 1000), extents[2]);
    }

    [Fact]
    public void LayoutSegments_ShrinksGapsWhenTheWidthCannotHoldThem()
    {
        IReadOnlyList<ChapterSpan> spans = [Span(0, 1, "A"), Span(1, 2, "B"), Span(2, 600, "C")];

        var extents = ScrubTimeline.LayoutSegments(spans, TenMinutes, 8, gap: 3, minWidth: 2);

        Assert.All(extents, e => Assert.True(e.Width >= 2 - 1e-9));
        Assert.Equal(1, extents[1].Left - extents[0].Right, 6);
        Assert.Equal(8, extents[2].Right, 6);
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
