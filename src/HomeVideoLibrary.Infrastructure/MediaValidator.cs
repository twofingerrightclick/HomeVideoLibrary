using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
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

/// <param name="ErrorCount">Decoder error lines seen; a lower bound because stderr capture is bounded.</param>
public sealed record DecodeReport(TimeSpan Decoded, int ErrorCount);

/// <summary>A track an edit output must contain. Codec is mkvmerge's codec name.</summary>
public sealed record ExpectedTrack(string Type, string Codec, string Language);

/// <summary>A span of a file in its own absolute timestamps.</summary>
public sealed record DecodeWindow(string Path, TimeSpan Start, TimeSpan Length);

/// <summary>A position in an edit output where the edit joined or cut streams, with the source spans that end up
/// around it. Decoder errors the sources already had there are not the edit's fault.</summary>
public sealed record EditBoundary(TimeSpan Position, IReadOnlyList<DecodeWindow> Sources);

/// <summary>Checks decoded completeness rather than trusting potentially stale container headers.</summary>
public sealed class MediaValidator(MediaTools tools, MediaProcess process)
{
    private static readonly TimeSpan EdgeWindow = TimeSpan.FromSeconds(3);
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

    /// <summary>Decodes <paramref name="window"/> like
    /// <see cref="ValidateDecodedAsync(string, TimeSpan, DecodeTolerance, IProgress{double}?, CancellationToken)"/>.</summary>
    public async Task<DecodeReport> ValidateDecodedRangeAsync(DecodeWindow window, DecodeTolerance tolerance, CancellationToken cancellationToken = default)
    {
        var timeline = await MediaProbe.ReadTimelineAsync(process, tools, window.Path, cancellationToken);
        var report = await DecodeAsync(window.Path, timeline.Seek(window.Start), window.Length, window.Length, null, cancellationToken);
        if (window.Length - report.Decoded > tolerance.For(window.Length))
            throw new InvalidDataException($"The edited video does not decode completely after {Timecode.Format(window.Start)}.");
        return report;
    }

    /// <summary>Cheap validation for edit outputs: track layout, the video's length, packets on every audio and video
    /// track near both ends, and decodes around the edges and around every boundary. Throws
    /// <see cref="InvalidDataException"/> when the layout or length is wrong or a boundary decodes with more errors
    /// than its sources had; returns warnings about errors near the file's edges.</summary>
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
        foreach (var start in new[] { timeline.StartTime, Max(timeline.StartTime, timeline.VideoEnd - EdgeWindow) }.Distinct())
        {
            var edge = await DecodeAsync(path, timeline.Seek(start), EdgeWindow, EdgeWindow, null, cancellationToken);
            if (edge.ErrorCount > 0)
                warnings.Add($"{edge.ErrorCount} decoder error(s) near {Timecode.Format(start - timeline.StartTime)}.");
        }
        var sourceTimelines = new Dictionary<string, MediaTimeline>();
        foreach (var boundary in boundaries)
        {
            await EnsureTimestampsOrderedAsync(path, timeline.StartTime + boundary.Position, cancellationToken);
            var output = await DecodeAsync(path, timeline.Seek(timeline.StartTime + boundary.Position - BoundaryRadius), BoundaryRadius * 2,
                BoundaryRadius * 2, null, cancellationToken);
            if (output.ErrorCount == 0) continue;
            var inherited = 0;
            foreach (var window in boundary.Sources)
            {
                if (!sourceTimelines.TryGetValue(window.Path, out var source))
                    sourceTimelines[window.Path] = source = await MediaProbe.ReadTimelineAsync(process, tools, window.Path, cancellationToken);
                inherited += (await DecodeAsync(window.Path, source.Seek(window.Start), window.Length, window.Length, null, cancellationToken)).ErrorCount;
            }
            if (output.ErrorCount > inherited)
                throw new InvalidDataException($"The edited video does not decode cleanly at {Timecode.Format(boundary.Position)}.");
            warnings.Add($"{output.ErrorCount} decoder error(s) near {Timecode.Format(boundary.Position)}, as in the original.");
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
        arguments.AddRange(["-map", "0:v?", "-map", "0:a?", "-fps_mode", "passthrough", "-enc_time_base", "demux",
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
        var result = await process.RunAsync(tools.Ffmpeg, arguments, TimeSpan.FromHours(24), cancellationToken, lines.Append);
        var log = DecodeLog.Parse(result.StandardError);
        if (log.Errors > 0)
            Trace.TraceWarning($"Decoding {path} found {log.Errors} damaged packet(s) or frame(s): {result.StandardError}");
        if (result.ExitCode != 0 && (decodedMicroseconds == 0 || log.FramesDecoded == 0))
            throw new InvalidDataException("The video could not be decoded. " + MediaErrors.Summarize(result.StandardError));
        return new DecodeReport(TimeSpan.FromTicks(decodedMicroseconds * 10), log.Errors);
    }

    private static string Key(ExpectedTrack track) => $"{track.Type} {track.Codec} ({track.Language})";
    private static TimeSpan Max(TimeSpan first, TimeSpan second) => first > second ? first : second;
}
