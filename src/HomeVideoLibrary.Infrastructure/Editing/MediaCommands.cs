using System.Diagnostics;
using System.Globalization;

namespace HomeVideoLibrary.Infrastructure.Editing;

/// <summary>Runs the muxing and encoding steps of an edit, reporting each step's own 0..1 progress.</summary>
internal sealed class MediaCommands(MediaTools tools, MediaProcess process)
{
    private static readonly TimeSpan LongRunning = TimeSpan.FromHours(24);
    private const string AppendWarning = "can probably not be appended";

    /// <summary>Runs mkvmerge in GUI mode. Exit code 1 means warnings, which are returned; anything above fails.</summary>
    public async Task<IReadOnlyList<string>> MkvMergeAsync(IEnumerable<string> arguments, Action<double>? progress,
        CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        var lines = new LineSplitter(line =>
        {
            if (line.StartsWith("#GUI#progress ", StringComparison.Ordinal) &&
                int.TryParse(line.AsSpan(14).TrimEnd('%'), NumberStyles.Integer, CultureInfo.InvariantCulture, out var percent))
                progress?.Invoke(percent / 100d);
            else if (line.StartsWith("#GUI#warning ", StringComparison.Ordinal))
                warnings.Add(line[13..]);
        });
        // English messages, because the append refusal is recognized by its text.
        var result = await process.RunAsync(tools.MkvMerge, ["--ui-language", "en_US", "--gui-mode", .. arguments], LongRunning, cancellationToken, lines.Append);
        lines.Append("\n");
        if (result.ExitCode > 1)
        {
            Trace.TraceError($"mkvmerge failed ({result.ExitCode}): {result.StandardOutput} {result.StandardError}");
            throw new InvalidDataException("Video editing failed; originals unchanged. " +
                MediaErrors.Summarize(string.Join('\n', result.StandardOutput.Split('\n').Where(line => line.Contains("#GUI#error", StringComparison.Ordinal)))
                    is { Length: > 0 } error ? error.Replace("#GUI#error ", "", StringComparison.Ordinal) : result.StandardOutput));
        }
        foreach (var warning in warnings) Trace.TraceWarning($"mkvmerge: {warning}");
        return warnings;
    }

    /// <summary>Sets the first video track's nominal frame duration in the header of a file this process owns.
    /// mkvmerge derives it from a timestamp file's millisecond values (83 ms for 12 fps), which players then show as
    /// the frame rate; the frames' own timestamps are not touched.</summary>
    public async Task SetVideoFrameDurationAsync(string path, TimeSpan frameDuration, CancellationToken cancellationToken)
    {
        if (frameDuration <= TimeSpan.Zero) return;
        var result = await process.RunAsync(tools.MkvPropEdit, [path, "--edit", "track:v1", "--set",
            "default-duration=" + (frameDuration.Ticks * 100).ToString(CultureInfo.InvariantCulture)], TimeSpan.FromMinutes(1), cancellationToken);
        if (result.ExitCode > 1)
            throw new InvalidDataException("Video editing failed; originals unchanged. " + MediaErrors.Summarize(result.StandardOutput));
    }

    public static bool RefusesToAppend(IEnumerable<string> warnings) =>
        warnings.Any(warning => warning.Contains(AppendWarning, StringComparison.OrdinalIgnoreCase));

    /// <summary>Runs ffmpeg and returns the number of video frames it wrote.</summary>
    /// <param name="duration">The expected output duration, used to turn ffmpeg's position into a fraction.</param>
    public async Task<long> FfmpegAsync(IEnumerable<string> arguments, TimeSpan duration, Action<double>? progress,
        CancellationToken cancellationToken)
    {
        long frames = 0;
        var lines = new LineSplitter(line =>
        {
            if (line.StartsWith("frame=", StringComparison.Ordinal) &&
                long.TryParse(line.AsSpan(6), NumberStyles.Integer, CultureInfo.InvariantCulture, out var frame))
                frames = frame;
            else if (line.StartsWith("out_time_us=", StringComparison.Ordinal) && duration > TimeSpan.Zero &&
                long.TryParse(line.AsSpan(12), NumberStyles.Integer, CultureInfo.InvariantCulture, out var microseconds))
                progress?.Invoke(Math.Clamp(microseconds * 10d / duration.Ticks, 0, 1));
        });
        var result = await process.RunAsync(tools.Ffmpeg,
            ["-hide_banner", "-nostdin", "-v", "error", "-y", "-progress", "pipe:1", "-nostats", .. arguments],
            LongRunning, cancellationToken, lines.Append);
        lines.Append("\n");
        if (result.ExitCode != 0)
        {
            Trace.TraceError($"ffmpeg failed ({result.ExitCode}): {result.StandardError}");
            throw new InvalidDataException("Video editing failed; originals unchanged. " + MediaErrors.Summarize(result.StandardError));
        }
        return frames;
    }
}
