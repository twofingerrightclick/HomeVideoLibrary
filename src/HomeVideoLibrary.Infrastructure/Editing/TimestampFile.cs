using System.Globalization;

namespace HomeVideoLibrary.Infrastructure.Editing;

/// <summary>mkvmerge timestamp files (format v2): one presentation time per frame, in milliseconds. mkvmerge assigns
/// them in presentation order, which is how raw video joins keep the source's real frame timing.</summary>
internal static class TimestampFile
{
    public static Task WriteAsync(string path, IEnumerable<TimeSpan> presentationTimes, CancellationToken cancellationToken) =>
        File.WriteAllLinesAsync(path, presentationTimes.Order()
            .Select(time => time.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture))
            .Prepend("# timestamp format v2"), cancellationToken);
}
