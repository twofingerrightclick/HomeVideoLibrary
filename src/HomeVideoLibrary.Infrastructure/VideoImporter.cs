using System.Globalization;
using System.Text.Json;
using HomeVideoLibrary.Core;

namespace HomeVideoLibrary.Infrastructure;

public sealed class VideoImporter(LibraryCatalog catalog, MediaTools tools, MediaProcess? process = null) : IVideoImporter
{
    private readonly MediaProcess _process = process ?? new MediaProcess();

    public async Task<LibraryVideo> ImportAsync(string sourcePath, IProgress<ImportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        sourcePath = Path.GetFullPath(sourcePath);
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("The selected source video no longer exists.", sourcePath);
        progress?.Report(new ImportProgress("Inspecting source"));
        var source = await ProbeAsync(sourcePath, cancellationToken);
        var recordedAt = source.RecordedAt ?? new DateTimeOffset(File.GetLastWriteTimeUtc(sourcePath));
        var id = Guid.NewGuid();
        var relativePath = Path.Combine("Videos", $"{id:N}.mkv");
        var destination = catalog.GetVideoPath(relativePath);
        // Stage in the destination directory so the publish rename stays on the same filesystem.
        var staging = Path.Combine(catalog.Paths.VideosDirectory, $".{id:N}.import.mkv");
        var published = false;
        try
        {
            progress?.Report(new ImportProgress("Copying streams to MKV", 0));
            if (source.IsMatroska)
            {
                // Preserve Matroska-specific chapter tags, track UIDs, and attachments byte-for-byte.
                await using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                await using var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                await input.CopyToAsync(output, cancellationToken);
            }
            else
            {
                var copy = await ConvertAsync(sourcePath, staging, source.Duration, false, progress, cancellationToken);
                if (copy.ExitCode != 0)
                {
                    File.Delete(staging);
                    progress?.Report(new ImportProgress("Converting incompatible streams (H.264 / AAC / SRT)", 0));
                    var transcode = await ConvertAsync(sourcePath, staging, source.Duration, true, progress, cancellationToken);
                    if (transcode.ExitCode != 0)
                        throw new InvalidOperationException($"The source cannot be converted to MKV. {transcode.StandardError}");
                }
            }
            progress?.Report(new ImportProgress("Verifying MKV"));
            var result = await ProbeAsync(staging, cancellationToken);
            var tolerance = Math.Max(1.0, source.Duration.TotalSeconds * 0.01);
            if (Math.Abs(result.Duration.TotalSeconds - source.Duration.TotalSeconds) > tolerance || result.VideoStreams != source.VideoStreams)
                throw new InvalidDataException("The converted video failed duration or video stream validation. The original was preserved.");
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(staging, destination, overwrite: false);
            published = true;
            var video = new LibraryVideo(id, relativePath, Path.GetFileNameWithoutExtension(sourcePath), result.Duration,
                DateTimeOffset.UtcNow, recordedAt);
            await catalog.AddAsync(video, cancellationToken);
            published = false; // Database commit is the import's completion point.
            progress?.Report(new ImportProgress("Imported", 1));
            return video;
        }
        finally
        {
            File.Delete(staging);
            if (published)
                File.Delete(destination);
        }
    }

    private Task<MediaProcessResult> ConvertAsync(string source, string output, TimeSpan duration, bool transcode,
        IProgress<ImportProgress>? progress, CancellationToken cancellationToken)
    {
        var arguments = new List<string> { "-hide_banner", "-nostdin", "-v", "error", "-n", "-i", source,
            "-map", "0:v?", "-map", "0:a?", "-map", "0:s?", "-map", "0:t?", "-map_metadata", "0", "-map_chapters", "0", "-c", "copy" };
        if (transcode)
            arguments.AddRange(["-c:v", "libx264", "-preset", "medium", "-crf", "18", "-c:a", "aac", "-b:a", "192k", "-c:s", "srt"]);
        arguments.AddRange(["-progress", "pipe:1", "-nostats", "-f", "matroska", output]);
        var pending = "";
        return _process.RunAsync(tools.Ffmpeg, arguments, TimeSpan.FromHours(24), cancellationToken, chunk =>
        {
            pending += chunk;
            int end;
            while ((end = pending.IndexOf('\n')) >= 0)
            {
                var line = pending[..end].Trim();
                pending = pending[(end + 1)..];
                if (line.StartsWith("out_time_us=", StringComparison.Ordinal) &&
                    long.TryParse(line.AsSpan(12), CultureInfo.InvariantCulture, out var microseconds))
                    progress?.Report(new ImportProgress(transcode ? "Converting streams" : "Copying streams", Math.Clamp(microseconds / 1_000_000d / duration.TotalSeconds, 0, 1)));
            }
            if (pending.Length > 4096)
                pending = "";
        });
    }

    private async Task<ProbeInfo> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        var result = await _process.RunAsync(tools.Ffprobe,
            ["-v", "error", "-show_entries", "format=duration,format_name:format_tags=creation_time:stream=codec_type:stream_disposition=attached_pic", "-of", "json", path],
            TimeSpan.FromSeconds(60), cancellationToken);
        if (result.ExitCode != 0)
            throw new InvalidDataException($"The file is not readable video media. {result.StandardError}");
        using var json = JsonDocument.Parse(result.StandardOutput);
        var root = json.RootElement;
        var streams = root.GetProperty("streams").EnumerateArray().ToArray();
        var videos = streams.Count(stream => stream.GetProperty("codec_type").GetString() == "video");
        var hasPlayableVideo = streams.Any(stream => stream.GetProperty("codec_type").GetString() == "video" &&
            (!stream.TryGetProperty("disposition", out var disposition) || !disposition.TryGetProperty("attached_pic", out var attached) || attached.GetInt32() == 0));
        if (!hasPlayableVideo || !root.TryGetProperty("format", out var format) || !format.TryGetProperty("duration", out var durationValue) ||
            !double.TryParse(durationValue.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ||
            !double.IsFinite(seconds) || seconds <= 0 || seconds > TimeSpan.MaxValue.TotalSeconds)
            throw new InvalidDataException("Import requires a video stream and a finite positive duration.");
        DateTimeOffset? recordedAt = null;
        if (format.TryGetProperty("tags", out var tags) && tags.TryGetProperty("creation_time", out var creationTime) &&
            DateTimeOffset.TryParse(creationTime.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            recordedAt = parsed;
        var isMatroska = format.TryGetProperty("format_name", out var formatName) &&
            (formatName.GetString()?.Split(',').Contains("matroska") ?? false);
        return new ProbeInfo(TimeSpan.FromSeconds(seconds), videos, recordedAt, isMatroska);
    }

    private sealed record ProbeInfo(TimeSpan Duration, int VideoStreams, DateTimeOffset? RecordedAt, bool IsMatroska);
}
