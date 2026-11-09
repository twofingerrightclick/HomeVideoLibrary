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
    /// Builds contiguous spans covering [0, <paramref name="duration"/>). Marks are ordered by start; starts
    /// before zero clamp to zero, marks at or beyond the duration are dropped, and a later mark repeating an
    /// earlier start is dropped (the first in input order wins). When no mark starts at zero, an untitled
    /// leading span covers the footage before the first chapter. Returns no spans when the duration is not
    /// positive.
    /// </summary>
    public static IReadOnlyList<ChapterSpan> BuildSpans(IEnumerable<IChapterMark> marks, TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(marks);
        if (duration <= TimeSpan.Zero)
        {
            return [];
        }

        var starts = new List<(TimeSpan Start, string Title)>();
        foreach (var mark in marks
            .Select(m => (Start: m.Start < TimeSpan.Zero ? TimeSpan.Zero : m.Start, Title: m.Title ?? string.Empty))
            .Where(m => m.Start < duration)
            .OrderBy(m => m.Start))
        {
            if (starts.Count == 0 || starts[^1].Start != mark.Start)
            {
                starts.Add(mark);
            }
        }

        if (starts.Count == 0 || starts[0].Start > TimeSpan.Zero)
        {
            starts.Insert(0, (TimeSpan.Zero, string.Empty));
        }

        var spans = new ChapterSpan[starts.Count];
        for (var i = 0; i < starts.Count; i++)
        {
            var end = i + 1 < starts.Count ? starts[i + 1].Start : duration;
            spans[i] = new ChapterSpan(starts[i].Start, end, starts[i].Title);
        }

        return spans;
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
    /// starts there; times before the first span map to it and times past the end map to the last.
    /// Returns -1 when there are no spans.
    /// </summary>
    /// <param name="spans">Spans in start order, as built by <see cref="BuildSpans"/>.</param>
    public static int SpanIndexAt(IReadOnlyList<ChapterSpan> spans, TimeSpan time)
    {
        ArgumentNullException.ThrowIfNull(spans);
        if (spans.Count == 0)
        {
            return -1;
        }

        int low = 0, high = spans.Count - 1;
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            if (spans[mid].Start <= time)
            {
                low = mid;
            }
            else
            {
                high = mid - 1;
            }
        }

        return low;
    }

    /// <summary>
    /// Lays spans out across [0, <paramref name="width"/>], one extent per span, with exactly
    /// <paramref name="gap"/> between neighbours and none at the outer ends. A span too short to show
    /// <paramref name="minWidth"/> after its gaps pushes its boundaries outward, borrowing width from its
    /// neighbours, so every segment stays at least <paramref name="minWidth"/> wide and gaps never shrink to
    /// fit it. Only when the width cannot hold every segment at <paramref name="minWidth"/> plus all gaps do
    /// the gaps shrink, and then the minimum, until everything fits.
    /// </summary>
    public static IReadOnlyList<SegmentExtent> LayoutSegments(
        IReadOnlyList<ChapterSpan> spans, TimeSpan duration, double width, double gap, double minWidth)
    {
        ArgumentNullException.ThrowIfNull(spans);
        var count = spans.Count;
        if (count == 0)
        {
            return [];
        }

        width = Math.Max(0, width);
        gap = count > 1 ? Math.Max(0, gap) : 0;
        minWidth = Math.Max(0, minWidth);
        if (count * minWidth + (count - 1) * gap > width)
        {
            gap = count > 1 ? Math.Max(0, (width - count * minWidth) / (count - 1)) : 0;
            minWidth = Math.Min(minWidth, width / count);
        }

        double Required(int i) => minWidth + gap / 2 * ((i > 0 ? 1 : 0) + (i < count - 1 ? 1 : 0));

        var bounds = new double[count + 1];
        bounds[count] = width;
        for (var i = 1; i < count; i++)
        {
            bounds[i] = TimeToX(spans[i].Start, duration, width);
        }

        for (var i = 1; i < count; i++)
        {
            bounds[i] = Math.Max(bounds[i], bounds[i - 1] + Required(i - 1));
        }

        for (var i = count - 1; i > 0; i--)
        {
            bounds[i] = Math.Min(bounds[i], bounds[i + 1] - Required(i));
        }

        var extents = new SegmentExtent[count];
        for (var i = 0; i < count; i++)
        {
            var left = bounds[i] + (i > 0 ? gap / 2 : 0);
            var right = bounds[i + 1] - (i < count - 1 ? gap / 2 : 0);
            extents[i] = new SegmentExtent(left, Math.Max(left, right));
        }

        return extents;
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
