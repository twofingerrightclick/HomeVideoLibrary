using System.Globalization;
using System.Text.RegularExpressions;

namespace HomeVideoLibrary.Infrastructure;

/// <summary>What an ffmpeg decode run (<c>-v level+verbose</c>) says about its input. Two independent measures are
/// read and the larger wins, because each misses damage the other sees:
/// <list type="bullet">
/// <item>error-level messages (<c>[error]</c>, <c>[fatal]</c>), the same lines <c>ffmpeg -v error -i f -f null -</c>
/// prints; they include demuxer damage such as a broken Matroska cluster, which loses packets without any decoder
/// noticing;</item>
/// <item>ffmpeg's own counters: the per-input-stream "N decode errors" summary (packets the decoder rejected, as
/// <c>-max_error_rate</c> counts them), plus warning-level "corrupt decoded frame" (frames carrying decode error flags,
/// what <c>-xerror</c> stops on) and "corrupt input packet" (packets the demuxer flagged).</item>
/// </list>
/// Per-line counts are a lower bound when the captured log was truncated.</summary>
/// <param name="FramesDecoded">The summary's decoded-frame total, or null when this ffmpeg does not print it.</param>
internal sealed partial record DecodeLog(int ErrorLines, int CountedErrors, long? FramesDecoded)
{
    public int Errors => Math.Max(ErrorLines, CountedErrors);

    public static DecodeLog Parse(string log)
    {
        var lines = log.Split('\n');
        var errorLines = lines.Count(line => line.Contains("[error] ", StringComparison.Ordinal) || line.Contains("[fatal] ", StringComparison.Ordinal));
        var summaries = StreamSummary().Matches(log);
        var counted = summaries.Sum(match => Number(match.Groups["errors"]))
            + lines.Count(line => line.Contains("corrupt decoded frame", StringComparison.Ordinal))
            + lines.Count(line => line.Contains("corrupt input packet in stream", StringComparison.Ordinal));
        long? frames = summaries.Count == 0 ? null : summaries.Sum(match => (long)Number(match.Groups["frames"]));
        return new DecodeLog(errorLines, counted, frames);
    }

    private static int Number(Group group) =>
        group.Success && int.TryParse(group.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;

    [GeneratedRegex(@"Input stream #\d+:\d+ \([a-z]+\): [^\n]*?(?<frames>\d+) frames decoded(?:; (?<errors>\d+) decode errors)?")]
    private static partial Regex StreamSummary();
}
