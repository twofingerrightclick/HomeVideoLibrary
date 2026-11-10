using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using HomeVideoLibrary.Core;
using HomeVideoLibrary.Infrastructure.Editing;

namespace HomeVideoLibrary.Infrastructure;

/// <summary>How far a decoded duration may fall short of the expected one.</summary>
public sealed record DecodeTolerance(TimeSpan Minimum, double Fraction)
{
    public TimeSpan For(TimeSpan expected) =>
        TimeSpan.FromTicks(Math.Max(Minimum.Ticks, (long)(expected.Ticks * Fraction)));

    /// <summary>Edit outputs: <c>max(2 frames, 0.25 s)</c>.</summary>
    public static DecodeTolerance ForEdit(TimeSpan frameDuration) =>
        new(TimeSpan.FromTicks(Math.Max(frameDuration.Ticks * 2, TimeSpan.FromSeconds(0.25).Ticks)), 0);

    /// <summary>Imports: <c>max(1 s, 0.5%)</c>, or <c>max(2 s, 2%)</c> for MPEG transport and program streams,
    /// whose header durations are estimates.</summary>
    public static DecodeTolerance ForImport(bool isMpegStream) => isMpegStream
        ? new(TimeSpan.FromSeconds(2), 0.02)
        : new(TimeSpan.FromSeconds(1), 0.005);
}

/// <param name="ErrorCount">Damaged packets or frames found; see <see cref="DecodeLog"/>.</param>
public sealed record DecodeReport(TimeSpan Decoded, int ErrorCount);

/// <summary>A track an edit output must contain. Codec is mkvmerge's codec name.</summary>
public sealed record ExpectedTrack(string Type, string Codec, string Language);

/// <summary>A span of a file in its own absolute timestamps.</summary>
public sealed record DecodeWindow(string Path, TimeSpan Start, TimeSpan Length);

/// <summary>A position in an edit output where the edit joined or cut streams, with the source spans that end up
/// around it. Decoder errors the sources already had there are not the edit's fault.</summary>
public sealed record EditBoundary(TimeSpan Position, IReadOnlyList<DecodeWindow> Sources);

/// <summary>The one place that judges whether a media file is intact. Every check decodes with ffmpeg to its null sink
/// (frames keep their own timestamps) and reads the log with <see cref="DecodeLog"/>: damage is what the demuxer
/// reports at error level plus ffmpeg's own decode-error counters.
/// <list type="table">
/// <listheader><term>What</term><description>Checks, and what a failure means</description></listheader>
/// <item><term>Import (<see cref="ValidateDecodedAsync"/>)</term><description>Full decode of the staged file. Damage is
/// a warning; the video is imported as it is (damaged tapes are still family memories). Nothing decodable, or a decoded
/// duration short of the source's by more than <see cref="DecodeTolerance.ForImport"/>, rejects the import.</description></item>
/// <item><term>Edit output (<see cref="ValidateStructureAsync"/>)</term><description>Structure: the track layout
/// (mkvmerge -J), the video's length against the plan within <see cref="DecodeTolerance.ForEdit"/>, packets on every
/// audio and video track. Each boundary the edit made (its start, end, and every join or smart-cut seam): per-track
/// timestamp order, and a decode of the window around it, which may show no more damage than the source spans it came
/// from. Any failure stops the edit before publishing; damage inherited from a source is a warning. The metadata
/// written afterwards is verified by its own round trip (<see cref="IMatroskaFileEditor.WriteStagedAsync"/>).</description></item>
/// </list></summary>
public sealed class MediaValidator(MediaTools tools, MediaProcess process)
{
    /// <summary>How far on each side of a boundary decoding is checked.</summary>
    public static readonly TimeSpan BoundaryRadius = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PacketWindow = TimeSpan.FromSeconds(5);

    /// <summary>Decodes every video and audio stream without stopping at errors, which are counted instead, so a
    /// tape with a few damaged packets still passes. Throws <see cref="InvalidDataException"/> when nothing decodes
    /// or the decoded duration falls short of <paramref name="expectedDuration"/> by more than the tolerance.</summary>
    /// <param name="progress">Receives the decoded fraction of <paramref name="expectedDuration"/>, 0..1.</param>
    public async Task<DecodeReport> ValidateDecodedAsync(string path, TimeSpan expectedDuration, DecodeTolerance tolerance,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var report = await DecodeAsync(path, null, null, expectedDuration, progress, cancellationToken);
        if (expectedDuration - report.Decoded > tolerance.For(expectedDuration))
            throw new InvalidDataException(
                $"Only {report.Decoded.TotalSeconds:0.#} of {expectedDuration.TotalSeconds:0.#} seconds could be decoded. The file may be truncated.");
        return report;
    }

    /// <summary>Decodes a span of a file, given in its own absolute timestamps.</summary>
    internal async Task<DecodeReport> DecodeWindowAsync(DecodeWindow window, CancellationToken cancellationToken = default)
    {
        var timeline = await MediaProbe.ReadTimelineAsync(process, tools, window.Path, cancellationToken);
        return await DecodeAsync(window.Path, timeline.Seek(window.Start), window.Length, window.Length, null, cancellationToken);
    }

    /// <summary>Validates an edit output as the policy above describes. Throws <see cref="InvalidDataException"/> on any
    /// failure; returns warnings about damage the sources already had.</summary>
    /// <param name="boundaries">Every position the edit cut or joined, including the output's start and end.</param>
    /// <param name="plannedDuration">The planned video length; the output's video must end within
    /// <c>max(2 frames, 0.25 s)</c> of it.</param>
    public async Task<IReadOnlyList<string>> ValidateStructureAsync(string path, IReadOnlyList<ExpectedTrack> expectedTracks,
        TimeSpan plannedDuration, IReadOnlyList<EditBoundary> boundaries, CancellationToken cancellationToken = default)
    {
        var identify = await process.RunAsync(tools.MkvMerge, ["-J", path], TimeSpan.FromMinutes(5), cancellationToken);
        if (identify.ExitCode > 1)
            throw new InvalidDataException("The edited video is not a readable MKV. " + MediaErrors.Summarize(identify.StandardOutput));
        var actual = MkvInfo.Parse(identify.StandardOutput).Tracks.Select(track => Key(track.Expected)).ToList();
        foreach (var track in expectedTracks.Select(Key))
            if (!actual.Remove(track))
                throw new InvalidDataException($"The edited video lost its {track} track.");
        if (actual.Count > 0)
            throw new InvalidDataException($"The edited video has an unexpected {actual[0]} track.");

        var timeline = await MediaProbe.ReadTimelineAsync(process, tools, path, cancellationToken);
        var length = timeline.VideoEnd - Max(TimeSpan.Zero, timeline.StartTime);
        if ((length - plannedDuration).Duration() > DecodeTolerance.ForEdit(timeline.FrameDuration).For(plannedDuration))
            throw new InvalidDataException(
                $"The edited video is {length.TotalSeconds:0.###} s long; {plannedDuration.TotalSeconds:0.###} s was planned.");
        await EnsurePacketsNearEndsAsync(path, timeline, cancellationToken);

        var warnings = new List<string>();
        foreach (var boundary in boundaries)
        {
            await EnsureTimestampsOrderedAsync(path, timeline.StartTime + boundary.Position, cancellationToken);
            // The window is cut at the file's ends, matching the source spans the producer gives for the start and end.
            var from = Max(TimeSpan.Zero, boundary.Position - BoundaryRadius);
            var to = Min(length, boundary.Position + BoundaryRadius);
            var output = await DecodeAsync(path, timeline.Seek(timeline.StartTime + from), to - from, to - from, null, cancellationToken);
            if (output.ErrorCount == 0) continue;
            var inherited = 0;
            foreach (var window in boundary.Sources)
                inherited += (await DecodeWindowAsync(window, cancellationToken)).ErrorCount;
            if (output.ErrorCount > inherited)
                throw new InvalidDataException($"The edited video does not decode cleanly at {Timecode.Format(boundary.Position)}.");
            warnings.Add($"{output.ErrorCount} damaged packet(s) or frame(s) near {Timecode.Format(boundary.Position)}, as in the original.");
        }
        return warnings;
    }

    /// <summary>Checks the container's timestamps around a join the way Matroska defines them: within each video and audio
    /// track no two frames share a presentation time, and decode times, where the container has them, never go backwards. Frame
    /// spacing may change at the join; Matroska stores a timestamp per block, so variable frame rates are valid.</summary>
    private async Task EnsureTimestampsOrderedAsync(string path, TimeSpan position, CancellationToken cancellationToken)
    {
        var start = position - BoundaryRadius < TimeSpan.Zero ? TimeSpan.Zero : position - BoundaryRadius;
        var presented = new Dictionary<int, HashSet<long>>();
        var lastDecode = new Dictionary<int, long>();
        string? problem = null;
        var lines = new LineSplitter(line =>
        {
            var fields = line.Split(',');
            if (problem is not null || fields.Length < 3 || !int.TryParse(fields[0], CultureInfo.InvariantCulture, out var stream)) return;
            if (TryTicks(fields[1], out var pts) && !(presented.TryGetValue(stream, out var seen) ? seen : presented[stream] = []).Add(pts))
                problem = $"two frames of stream {stream} share the time {Timecode.Format(TimeSpan.FromTicks(pts))}";
            if (TryTicks(fields[2], out var dts))
            {
                if (lastDecode.TryGetValue(stream, out var previous) && dts < previous)
                    problem = $"stream {stream}'s decode times go backwards at {Timecode.Format(TimeSpan.FromTicks(dts))}";
                lastDecode[stream] = dts;
            }
        });
        // Video and audio only: subtitle events may legitimately start together.
        foreach (var type in new[] { "v", "a" })
        {
            await process.RunAsync(tools.Ffprobe, ["-v", "error", "-select_streams", type,
                "-read_intervals", $"{MediaProbe.Seconds(start)}%+{MediaProbe.Seconds(BoundaryRadius * 2)}",
                "-show_entries", "packet=stream_index,pts_time,dts_time", "-of", "csv=p=0", path], TimeSpan.FromMinutes(5), cancellationToken, lines.Append);
            lines.Append("\n");
        }
        if (problem is not null)
            throw new InvalidDataException($"The edited video's timestamps are invalid near {Timecode.Format(position)}: {problem}.");

        static bool TryTicks(string text, out long ticks)
        {
            ticks = 0;
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)) return false;
            ticks = (long)Math.Round(seconds * TimeSpan.TicksPerSecond);
            return true;
        }
    }

    /// <summary>Every audio and video stream must have packets in the first or last few seconds; reading only those
    /// windows keeps the check independent of the file's length.</summary>
    private async Task EnsurePacketsNearEndsAsync(string path, MediaTimeline timeline, CancellationToken cancellationToken)
    {
        var streams = await process.RunAsync(tools.Ffprobe, ["-v", "error", "-show_entries", "stream=index,codec_type", "-of", "json", path],
            TimeSpan.FromMinutes(5), cancellationToken);
        using var json = JsonDocument.Parse(streams.StandardOutput);
        var required = json.RootElement.GetProperty("streams").EnumerateArray()
            .Where(stream => stream.TryGetProperty("codec_type", out var type) && type.GetString() is "video" or "audio")
            .Select(stream => stream.GetProperty("index").GetInt32()).ToHashSet();
        var lines = new LineSplitter(line =>
        {
            if (int.TryParse(line.Split(',')[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)) required.Remove(index);
        });
        var intervals = $"{MediaProbe.Seconds(timeline.StartTime)}%+{MediaProbe.Seconds(PacketWindow)}," +
            $"{MediaProbe.Seconds(Max(timeline.StartTime, timeline.ContainerEnd - PacketWindow))}%";
        await process.RunAsync(tools.Ffprobe, ["-v", "error", "-read_intervals", intervals, "-show_entries", "packet=stream_index",
            "-of", "csv=p=0", path], TimeSpan.FromMinutes(5), cancellationToken, lines.Append);
        lines.Append("\n");
        if (required.Count > 0)
            throw new InvalidDataException($"The edited video's stream {required.Min()} is empty.");
    }

    /// <summary>Decodes every video and audio stream to ffmpeg's null sink, the integrity check ffmpeg's documentation
    /// describes, with frames passed on with their own timestamps (<c>-fps_mode passthrough</c>, demuxer time base) so
    /// a variable frame rate, which Matroska allows, is not re-timed by the sink. See <see cref="DecodeLog"/> for how the
    /// log is read.</summary>
    /// <param name="seek">An ffmpeg input seek, from <see cref="MediaTimeline.Seek"/>.</param>
    private async Task<DecodeReport> DecodeAsync(string path, string? seek, TimeSpan? length, TimeSpan expected,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var arguments = new List<string> { "-hide_banner", "-nostdin", "-v", "level+verbose" };
        if (seek is not null) arguments.AddRange(["-ss", seek]);
        arguments.AddRange(["-i", path]);
        if (length is { } duration) arguments.AddRange(["-t", MediaProbe.Seconds(duration)]);
        // A fine fixed time base rather than "demux", which ffmpeg only spells that way since 7.0.
        arguments.AddRange(["-map", "0:v?", "-map", "0:a?", "-fps_mode", "passthrough", "-enc_time_base", "1:90000",
            "-progress", "pipe:1", "-nostats", "-f", "null", "-"]);
        var decodedMicroseconds = 0L;
        var lines = new LineSplitter(line =>
        {
            if (!line.StartsWith("out_time_us=", StringComparison.Ordinal) ||
                !long.TryParse(line.AsSpan(12), CultureInfo.InvariantCulture, out var microseconds)) return;
            decodedMicroseconds = Math.Max(decodedMicroseconds, microseconds);
            if (expected > TimeSpan.Zero)
                progress?.Report(Math.Clamp(microseconds / (double)(expected.Ticks / 10), 0, 1));
        });
        var log = new DecodeLog();
        var result = await process.RunAsync(tools.Ffmpeg, arguments, TimeSpan.FromHours(24), cancellationToken, lines.Append, log.Append);
        log.Complete();
        if (log.Errors > 0)
            Trace.TraceWarning($"Decoding {path} found {log.Errors} damaged packet(s) or frame(s): {result.StandardError}");
        if (result.ExitCode != 0 && (decodedMicroseconds == 0 || log.FramesDecoded == 0))
            throw new InvalidDataException("The video could not be decoded. " + MediaErrors.Summarize(result.StandardError));
        return new DecodeReport(TimeSpan.FromTicks(decodedMicroseconds * 10), log.Errors);
    }

    private static string Key(ExpectedTrack track) => $"{track.Type} {track.Codec} ({track.Language})";
    private static TimeSpan Max(TimeSpan first, TimeSpan second) => first > second ? first : second;
    private static TimeSpan Min(TimeSpan first, TimeSpan second) => first < second ? first : second;
}

/// <summary>Reads an ffmpeg decode log (<c>-v level+verbose</c>) as it streams, for damage in the input:
/// <list type="bullet">
/// <item>error-level messages from the demuxer (<c>[in#N/…]</c> in ffmpeg 7 and later, the input format's own name,
/// such as <c>[matroska,webm @</c>, before): a broken container loses packets without any decoder noticing;</item>
/// <item>ffmpeg's own counters: the per-input-stream "N decode errors" summary (packets the decoder rejected, as
/// <c>-max_error_rate</c> counts them), "corrupt decoded frame" (frames carrying decode error flags, what <c>-xerror</c>
/// stops on) and "corrupt input packet" (packets the demuxer flagged).</item>
/// </list>
/// Error lines from decoders are not counted on their own: after a seek to an open-GOP keyframe the H.264 and HEVC
/// decoders log errors for the leading pictures they discard ("mmco: unref short failure"), which is not damage; real
/// decoder damage shows up in the counters. Lines from the output side (the null sink) are never counted.</summary>
internal sealed partial class DecodeLog
{
    private readonly LineSplitter _lines;
    private readonly Dictionary<string, int> _errorsByContext = new(StringComparer.Ordinal);
    private readonly HashSet<string> _inputFormats = new(StringComparer.Ordinal);
    private int _summaryErrors, _flagged;
    private long _framesDecoded;
    private bool _hasSummary;

    public DecodeLog() => _lines = new LineSplitter(Add);

    public int DemuxerErrors => _errorsByContext.Where(pair => pair.Key.StartsWith("in#", StringComparison.Ordinal) || _inputFormats.Contains(pair.Key))
        .Sum(pair => pair.Value);

    public int CountedErrors => _summaryErrors + _flagged;

    /// <summary>Damaged packets or frames found; the larger of the two measures, since each misses what the other sees.</summary>
    public int Errors => Math.Max(DemuxerErrors, CountedErrors);

    /// <summary>The summary's decoded-frame total, or null when this ffmpeg does not print one.</summary>
    public long? FramesDecoded => _hasSummary ? _framesDecoded : null;

    public void Append(string chunk) => _lines.Append(chunk);

    public DecodeLog Complete()
    {
        _lines.Append("\n");
        return this;
    }

    public static DecodeLog Parse(string log)
    {
        var parsed = new DecodeLog();
        parsed.Append(log);
        return parsed.Complete();
    }

    private void Add(string line)
    {
        if (InputBanner().Match(line) is { Success: true } banner)
            _inputFormats.Add(banner.Groups["format"].Value);
        if (line.Contains("[error] ", StringComparison.Ordinal) || line.Contains("[fatal] ", StringComparison.Ordinal))
        {
            var context = Context(line);
            _errorsByContext[context] = _errorsByContext.GetValueOrDefault(context) + 1;
        }
        if (StreamSummary().Match(line) is { Success: true } summary)
        {
            _hasSummary = true;
            _framesDecoded += Number(summary.Groups["frames"]);
            _summaryErrors += Number(summary.Groups["errors"]);
        }
        if (line.Contains("corrupt decoded frame", StringComparison.Ordinal) || line.Contains("corrupt input packet in stream", StringComparison.Ordinal))
            _flagged++;
    }

    /// <summary>The logging context of a line, "in#0/matroska,webm" or "h264", without the instance address.</summary>
    private static string Context(string line)
    {
        if (!line.StartsWith('[')) return "";
        var end = line.IndexOfAny([' ', ']'], 1);
        return end < 0 ? "" : line[1..end];
    }

    private static int Number(Group group) =>
        group.Success && int.TryParse(group.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;

    [GeneratedRegex(@"Input #\d+, (?<format>.+?), from '")]
    private static partial Regex InputBanner();

    [GeneratedRegex(@"Input stream #\d+:\d+ \([a-z]+\): .*?(?<frames>\d+) frames decoded(?:; (?<errors>\d+) decode errors)?")]
    private static partial Regex StreamSummary();
}
