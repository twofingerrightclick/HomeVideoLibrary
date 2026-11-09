using System.Globalization;

namespace HomeVideoLibrary.Infrastructure;

/// <summary>Checks decoded completeness rather than trusting potentially stale container headers.</summary>
public sealed class MediaValidator(MediaTools tools, MediaProcess process)
{
    public async Task ValidateDecodedAsync(string path, TimeSpan expectedDuration, CancellationToken cancellationToken = default)
    {
        var decoded = await process.RunAsync(tools.Ffmpeg,
            ["-hide_banner", "-nostdin", "-v", "error", "-xerror", "-err_detect", "explode", "-i", path,
                "-map", "0:v?", "-map", "0:a?", "-progress", "pipe:1", "-nostats", "-f", "null", "-"],
            TimeSpan.FromHours(24), cancellationToken);
        if (decoded.ExitCode != 0)
            throw new InvalidDataException($"The video could not be decoded completely. The original was preserved. {decoded.StandardError}");
        var decodedSeconds = decoded.StandardOutput.Split('\n')
            .Where(line => line.StartsWith("out_time_us=", StringComparison.Ordinal))
            .Select(line => long.TryParse(line.AsSpan(12).Trim(), CultureInfo.InvariantCulture, out var value) ? value / 1_000_000d : 0)
            .DefaultIfEmpty(0).Max();
        var tolerance = Math.Max(0.25, expectedDuration.TotalSeconds * 0.01);
        if (decodedSeconds <= 0 || Math.Abs(decodedSeconds - expectedDuration.TotalSeconds) > tolerance)
            throw new InvalidDataException("The decoded playback duration does not match the complete video. The file may be truncated; the original was preserved.");
    }
}
