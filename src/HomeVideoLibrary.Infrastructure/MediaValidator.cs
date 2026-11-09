using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
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

/// <summary>Checks decoded completeness rather than trusting potentially stale container headers.</summary>
public sealed class MediaValidator(MediaTools tools, MediaProcess process)
{
    private static readonly TimeSpan EdgeWindow = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan BoundaryRadius = TimeSpan.FromSeconds(2);

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

    /// <summary>Decodes [<paramref name="start"/>, <paramref name="start"/> + <paramref name="length"/>) like
    /// <see cref="ValidateDecodedAsync(string, TimeSpan, DecodeTolerance, IProgress{double}?, CancellationToken)"/>.</summary>
    public async Task<DecodeReport> ValidateDecodedRangeAsync(string path, TimeSpan start, TimeSpan length, DecodeTolerance tolerance,
        CancellationToken cancellationToken = default)
    {
        var report = await DecodeAsync(path, start, length, length, null, cancellationToken);
        if (length - report.Decoded > tolerance.For(length))
            throw new InvalidDataException($"The edited video does not decode completely after {Core.Timecode.Format(start)}.");
        return report;
    }

    /// <summary>Cheap validation for stream-copied edit outputs: track layout, the video's length, packets on every
    /// audio and video track (a cut can leave a subtitle track empty), and clean decodes around the edges and around
    /// every boundary the edit created. Throws
    /// <see cref="InvalidDataException"/> on any mismatch; returns warnings about errors near the file's edges.</summary>
    /// <param name="boundaries">Output positions where the edit joined or cut streams. Any decoder error near one fails.</param>
    public async Task<IReadOnlyList<string>> ValidateStructureAsync(string path, IReadOnlyList<ExpectedTrack> expectedTracks,
        TimeSpan plannedDuration, IReadOnlyList<TimeSpan> boundaries, CancellationToken cancellationToken = default)
    {
        var identify = await process.RunAsync(tools.MkvMerge, ["-J", path], TimeSpan.FromMinutes(5), cancellationToken);
        if (identify.ExitCode > 1)
            throw new InvalidDataException("The edited video is not a readable MKV. " + MediaErrors.Summarize(identify.StandardOutput));
        var actual = ReadTracks(identify.StandardOutput);
        var missing = expectedTracks.Select(Key).ToList();
        foreach (var track in actual.Select(Key))
            if (!missing.Remove(track))
                throw new InvalidDataException($"The edited video has an unexpected {track} track.");
        if (missing.Count > 0)
            throw new InvalidDataException($"The edited video lost its {missing[0]} track.");

        var packets = await process.RunAsync(tools.Ffprobe,
            ["-v", "error", "-count_packets", "-show_entries", "stream=index,codec_type,nb_read_packets", "-of", "json", path],
            TimeSpan.FromMinutes(10), cancellationToken);
        if (packets.ExitCode != 0)
            throw new InvalidDataException("The edited video could not be scanned. " + MediaErrors.Summarize(packets.StandardError));
        using (var json = JsonDocument.Parse(packets.StandardOutput))
            foreach (var stream in json.RootElement.GetProperty("streams").EnumerateArray()
                         .Where(stream => stream.TryGetProperty("codec_type", out var type) && type.GetString() is "video" or "audio"))
                if (!stream.TryGetProperty("nb_read_packets", out var count) || count.GetString() is "0" or null)
                    throw new InvalidDataException($"The edited video's stream {stream.GetProperty("index").GetInt32()} is empty.");

        var video = await MediaProbe.ReadVideoEndAsync(process, tools, path, cancellationToken);
        var tolerance = DecodeTolerance.ForEdit(video.FrameDuration).For(plannedDuration);
        if ((video.End - plannedDuration).Duration() > tolerance)
            throw new InvalidDataException(
                $"The edited video is {video.End.TotalSeconds:0.###} s long; {plannedDuration.TotalSeconds:0.###} s was planned.");

        var warnings = new List<string>();
        var edges = new[] { (Start: TimeSpan.Zero, Length: EdgeWindow), (Start: video.End - EdgeWindow, Length: EdgeWindow) };
        foreach (var (start, length) in edges)
        {
            var edge = await DecodeAsync(path, Clamp(start, video.End), length, length, null, cancellationToken);
            if (edge.ErrorCount > 0)
                warnings.Add($"{edge.ErrorCount} decoder error(s) near {Core.Timecode.Format(Clamp(start, video.End))}.");
        }
        foreach (var boundary in boundaries)
        {
            var start = Clamp(boundary - BoundaryRadius, video.End);
            var report = await DecodeAsync(path, start, BoundaryRadius * 2, BoundaryRadius * 2, null, cancellationToken);
            if (report.ErrorCount > 0)
                throw new InvalidDataException($"The edited video does not decode cleanly at {Core.Timecode.Format(boundary)}.");
        }
        return warnings;
    }

    private async Task<DecodeReport> DecodeAsync(string path, TimeSpan? start, TimeSpan? length, TimeSpan expected,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var arguments = new List<string> { "-hide_banner", "-nostdin", "-v", "error" };
        if (start is { } from) arguments.AddRange(["-ss", Seconds(from)]);
        arguments.AddRange(["-i", path]);
        if (length is { } duration) arguments.AddRange(["-t", Seconds(duration)]);
        arguments.AddRange(["-map", "0:v?", "-map", "0:a?", "-progress", "pipe:1", "-nostats", "-f", "null", "-"]);
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
        var errors = result.StandardError.Split('\n').Count(line => line.Trim().Length > 0);
        if (errors > 0)
            Trace.TraceWarning($"Decoding {path} reported {errors} error line(s): {result.StandardError}");
        if (result.ExitCode != 0 && decodedMicroseconds == 0)
            throw new InvalidDataException("The video could not be decoded. " + MediaErrors.Summarize(result.StandardError));
        return new DecodeReport(TimeSpan.FromTicks(decodedMicroseconds * 10), errors);
    }

    private static IEnumerable<ExpectedTrack> ReadTracks(string mkvmergeJson)
    {
        using var json = JsonDocument.Parse(mkvmergeJson);
        return json.RootElement.GetProperty("tracks").EnumerateArray().Select(track => new ExpectedTrack(
            track.GetProperty("type").GetString() ?? "",
            track.GetProperty("codec").GetString() ?? "",
            track.TryGetProperty("properties", out var properties) && properties.TryGetProperty("language", out var language)
                ? language.GetString() ?? "und" : "und")).ToArray();
    }

    private static string Key(ExpectedTrack track) => $"{track.Type} {track.Codec} ({track.Language})";
    private static TimeSpan Clamp(TimeSpan value, TimeSpan end) => value < TimeSpan.Zero ? TimeSpan.Zero : value > end ? end : value;
    private static string Seconds(TimeSpan value) => value.TotalSeconds.ToString("0.######", CultureInfo.InvariantCulture);
}
