namespace HomeVideoLibrary.Core;

/// <summary>A range the user asked to cut that cannot be cut; <see cref="Exception.Message"/> says why in plain words.</summary>
public sealed class ClipRangeException(string message) : Exception(message);

/// <summary>The one rule for the range of a video an edit keeps: it starts inside the video, ends after it starts, and
/// keeps at least <see cref="MinimumLength"/>. An end at or past the video's end means "to the end", because catalog and
/// container durations include audio that outlasts the picture, so a whole-video range typed from them must still
/// resolve.</summary>
public static class ClipRange
{
    public static readonly TimeSpan MinimumLength = TimeSpan.FromMilliseconds(100);

    // Matroska stores millisecond timestamps, so times this close are the same frame.
    private static readonly TimeSpan Epsilon = TimeSpan.FromMilliseconds(1);

    /// <summary>Why [<paramref name="start"/>, <paramref name="end"/>) cannot be kept from a video <paramref name="length"/>
    /// long, or null when it can.</summary>
    public static string? Problem(TimeSpan start, TimeSpan end, TimeSpan length)
    {
        if (start < TimeSpan.Zero) return "The in point can't be before the start of the video.";
        if (start >= length - Epsilon) return $"The in point is at or past the end of the video ({Timecode.Format(length)}).";
        var resolved = Resolve(end, length);
        if (resolved <= start) return "The out point must come after the in point.";
        if (resolved - start < MinimumLength) return "Keep at least 0.1 seconds between the in and out points.";
        return null;
    }

    /// <summary>The range to cut, with an end at or past <paramref name="length"/> resolved to it.</summary>
    /// <exception cref="ClipRangeException">The range cannot be kept; see <see cref="Problem"/>.</exception>
    public static (TimeSpan Start, TimeSpan End) Validate(TimeSpan start, TimeSpan end, TimeSpan length) =>
        Problem(start, end, length) is { } problem ? throw new ClipRangeException(problem) : (start, Resolve(end, length));

    private static TimeSpan Resolve(TimeSpan end, TimeSpan length) => end >= length - Epsilon ? length : end;
}
