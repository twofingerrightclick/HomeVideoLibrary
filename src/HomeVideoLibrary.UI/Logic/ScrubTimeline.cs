namespace HomeVideoLibrary.UI.Logic;

/// <summary>A chapter's extent on the timeline, [Start, End). An empty title marks untitled footage.</summary>
public readonly record struct ChapterSpan(TimeSpan Start, TimeSpan End, string Title);

/// <summary>Horizontal extent of one drawn segment, in the units of the layout width.</summary>
public readonly record struct SegmentExtent(double Left, double Right)
{
    public double Width => Right - Left;
}

/// <summary>Chapter span building, time/position mapping, and segment layout for a scrub bar.</summary>
public static class ScrubTimeline
{
    /// <summary>
    /// Builds the actual explicit range spans, retaining gaps and ignoring moments. Ranges are clipped to
    /// the duration for display without changing their stored times. Duplicate external entries are retained.
    /// </summary>
    public static IReadOnlyList<ChapterSpan> BuildSpans(IEnumerable<IChapterMark> marks, TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(marks);
        if (duration <= TimeSpan.Zero)
        {
            return [];
        }

        return marks.Where(m => m.End > m.Start && m.Start < duration && m.End > TimeSpan.Zero)
            .OrderBy(m => m.Start)
            .Select(m => new ChapterSpan(m.Start < TimeSpan.Zero ? TimeSpan.Zero : m.Start,
                m.End > duration ? duration : m.End, m.Title ?? string.Empty)).ToArray();
    }

    /// <summary>Position of <paramref name="time"/> on a track <paramref name="width"/> wide, clamped to [0, width].</summary>
    public static double TimeToX(TimeSpan time, TimeSpan duration, double width)
    {
        if (duration <= TimeSpan.Zero || !(width > 0))
        {
            return 0;
        }

        return Math.Clamp(time / duration, 0, 1) * width;
    }

    /// <summary>Time at <paramref name="x"/> on a track <paramref name="width"/> wide, clamped to [0, duration].</summary>
    public static TimeSpan XToTime(double x, TimeSpan duration, double width)
    {
        if (duration <= TimeSpan.Zero || !(width > 0) || double.IsNaN(x))
        {
            return TimeSpan.Zero;
        }

        return duration * Math.Clamp(x / width, 0, 1);
    }

    /// <summary>
    /// Index of the span containing <paramref name="time"/>. A time on a boundary belongs to the span that
    /// starts there. Returns -1 for gaps, points outside all ranges, and an empty timeline.
    /// </summary>
    /// <param name="spans">Spans in start order, as built by <see cref="BuildSpans"/>.</param>
    public static int SpanIndexAt(IReadOnlyList<ChapterSpan> spans, TimeSpan time)
    {
        ArgumentNullException.ThrowIfNull(spans);
        for (var i = 0; i < spans.Count; i++)
            if (time >= spans[i].Start && time < spans[i].End) return i;
        return -1;
    }

    /// <summary>
    /// Maps actual endpoints proportionally. Gaps and tiny chapters retain their true extent; the legacy
    /// gap and minimum-width arguments remain for source compatibility with gallery callers.
    /// </summary>
    public static IReadOnlyList<SegmentExtent> LayoutSegments(
        IReadOnlyList<ChapterSpan> spans, TimeSpan duration, double width, double gap, double minWidth)
    {
        ArgumentNullException.ThrowIfNull(spans);
        return spans.Select(span => new SegmentExtent(TimeToX(span.Start, duration, width),
            TimeToX(span.End, duration, width))).ToArray();
    }

    /// <summary>
    /// Extent of a selected range [<paramref name="start"/>, <paramref name="end"/>] on a track
    /// <paramref name="width"/> wide, both ends clamped to the track. Null unless both ends are set and
    /// <paramref name="end"/> is after <paramref name="start"/>.
    /// </summary>
    public static SegmentExtent? LayoutRange(TimeSpan? start, TimeSpan? end, TimeSpan duration, double width)
    {
        if (start is not { } from || end is not { } to || to <= from || duration <= TimeSpan.Zero)
        {
            return null;
        }

        return new SegmentExtent(TimeToX(from, duration, width), TimeToX(to, duration, width));
    }

    /// <summary>Formats as <c>m:ss</c>, or <c>h:mm:ss</c> from one hour; negative times format as zero.</summary>
    public static string FormatTimestamp(TimeSpan time)
    {
        if (time < TimeSpan.Zero)
        {
            time = TimeSpan.Zero;
        }

        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{time.Minutes}:{time.Seconds:00}";
    }
}

/// <summary>Where a chapter added at the playhead belongs in an ordered chapter list.</summary>
public static class ChapterPlacement
{
    /// <summary>
    /// The list index a chapter inserted at <paramref name="playhead"/> takes: after every chapter starting
    /// at or before it.
    /// </summary>
    /// <param name="sortedStarts">Chapter starts in ascending order.</param>
    public static int PlaceholderIndex(IReadOnlyList<TimeSpan> sortedStarts, TimeSpan playhead)
    {
        ArgumentNullException.ThrowIfNull(sortedStarts);
        int low = 0, high = sortedStarts.Count;
        while (low < high)
        {
            var mid = (low + high) / 2;
            if (sortedStarts[mid] <= playhead)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }

    /// <summary>Whether any chapter starts within <paramref name="tolerance"/> (inclusive) of the playhead.</summary>
    public static bool HasChapterAt(IEnumerable<TimeSpan> starts, TimeSpan playhead, TimeSpan tolerance)
    {
        ArgumentNullException.ThrowIfNull(starts);
        return starts.Any(start => (start - playhead).Duration() <= tolerance);
    }
}
