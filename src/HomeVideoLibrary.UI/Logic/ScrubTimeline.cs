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

    /// <summary>Applies the incumbent segment gaps and minimum widths within each contiguous run,
    /// without extending ranges across genuine gaps or outside their explicit endpoints.</summary>
    public static IReadOnlyList<SegmentExtent> LayoutSegments(
        IReadOnlyList<ChapterSpan> spans, TimeSpan duration, double width, double gap, double minWidth)
    {
        ArgumentNullException.ThrowIfNull(spans);
        var result = new List<SegmentExtent>(spans.Count);
        for (var first = 0; first < spans.Count;)
        {
            var last = first;
            while (last + 1 < spans.Count && spans[last].End == spans[last + 1].Start) last++;
            var start = spans[first].Start;
            var end = spans[last].End;
            var left = TimeToX(start, duration, width);
            var right = TimeToX(end, duration, width);
            var local = Enumerable.Range(first, last - first + 1)
                .Select(i => new ChapterSpan(spans[i].Start - start, spans[i].End - start, spans[i].Title)).ToArray();
            result.AddRange(LayoutContiguousSegments(local, end - start, right - left, gap, minWidth)
                .Select(extent => new SegmentExtent(left + extent.Left, left + extent.Right)));
            first = last + 1;
        }
        return result;
    }

    /// <summary>Drawing-only intervals for the full playable track. Empty footage remains distinct from
    /// annotations: it supplies playback progress, but never a chapter title or current-range state.</summary>
    public static IReadOnlyList<ChapterSpan> TrackSpans(IReadOnlyList<ChapterSpan> ranges, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) return [];
        var result = new List<ChapterSpan>();
        var cursor = TimeSpan.Zero;
        foreach (var range in ranges)
        {
            if (range.Start > cursor) result.Add(new ChapterSpan(cursor, range.Start, ""));
            result.Add(range);
            if (range.End > cursor) cursor = range.End;
        }
        if (cursor < duration) result.Add(new ChapterSpan(cursor, duration, ""));
        return result;
    }

    private static IReadOnlyList<SegmentExtent> LayoutContiguousSegments(
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

    /// <summary>A moment's exact timestamp, retaining stored subsecond precision.</summary>
    public static string FormatMomentTimestamp(TimeSpan time)
    {
        var whole = FormatTimestamp(time);
        var fraction = time.Ticks % TimeSpan.TicksPerSecond;
        return fraction <= 0 ? whole : whole + "." + fraction.ToString("D7", System.Globalization.CultureInfo.InvariantCulture)
            .TrimEnd('0').PadRight(3, '0');
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
