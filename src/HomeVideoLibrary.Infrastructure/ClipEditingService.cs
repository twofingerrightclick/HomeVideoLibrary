using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using HomeVideoLibrary.Core;

namespace HomeVideoLibrary.Infrastructure;

/// <summary>Creates new primary-A/V derivatives; source files are always read-only.</summary>
public sealed class ClipEditingService(LibraryCatalog catalog, MediaTools tools, IVideoMetadataService? metadataService = null) : IClipEditingService
{
    private readonly MediaProcess _process = new();
    private readonly IVideoMetadataService _metadata = metadataService ?? new MatroskaMetadataService(catalog, tools);
    public async Task<ClipOutputFormat> GetSourceFormatAsync(LibraryVideo source, CancellationToken cancellationToken = default) =>
        (await ProbeAsync(catalog.GetVideoPath(source.RelativePath), cancellationToken)).Format;

    public Task<IReadOnlyList<LibraryVideo>> TrimAsync(LibraryVideo source, TimeSpan start, TimeSpan end, string title,
        IProgress<ClipEditProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ValidateRange(source, start, end);
        return ExecuteAsync([new EditJob(TitleOrDefault(title, source.DisplayName + " · trim"), [new Segment(source, start, end)])], progress, cancellationToken);
    }

    public Task<IReadOnlyList<LibraryVideo>> SplitAsync(LibraryVideo source, TimeSpan position, string title,
        IProgress<ClipEditProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ValidateRange(source, TimeSpan.Zero, position); ValidateRange(source, position, source.Duration);
        var name = TitleOrDefault(title, source.DisplayName);
        return ExecuteAsync([new EditJob(name + " · part 1", [new Segment(source, TimeSpan.Zero, position)]),
            new EditJob(name + " · part 2", [new Segment(source, position, source.Duration)])], progress, cancellationToken);
    }

    public Task<IReadOnlyList<LibraryVideo>> MergeAsync(IReadOnlyList<LibraryVideo> sources, string title,
        IProgress<ClipEditProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (sources.Count < 2) throw new ArgumentException("Select at least two videos to merge.", nameof(sources));
        foreach (var source in sources) ValidateRange(source, TimeSpan.Zero, source.Duration);
        return ExecuteAsync([new EditJob(TitleOrDefault(title, "Merged home video"), sources.Select(source => new Segment(source, TimeSpan.Zero, source.Duration)).ToArray())], progress, cancellationToken);
    }

    private async Task<IReadOnlyList<LibraryVideo>> ExecuteAsync(IReadOnlyList<EditJob> jobs, IProgress<ClipEditProgress>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var scratch = Path.Combine(catalog.Paths.VideosDirectory, ".edit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var prepared = new List<(LibraryVideo Video, string Staged)>();
        var published = new List<string>();
        var committed = false;
        try
        {
            var sourceMetadata = new Dictionary<string, VideoMetadata>();
            foreach (var source in jobs.SelectMany(job => job.Segments).Select(segment => segment.Source).DistinctBy(source => source.RelativePath))
                sourceMetadata.Add(source.RelativePath, await _metadata.ReadAsync(source, cancellationToken));
            var segmentNumber = 0;
            foreach (var job in jobs)
            {
                var targetFormat = (await ProbeAsync(catalog.GetVideoPath(job.Segments[0].Source.RelativePath), cancellationToken)).Format;
                var normalized = new List<(Segment Segment, string Path, TimeSpan Duration)>();
                foreach (var segment in job.Segments)
                {
                    var segmentPath = Path.Combine(scratch, $"segment-{segmentNumber++}.mkv");
                    progress?.Report(new ClipEditProgress($"Preparing {segment.Source.DisplayName}"));
                    var sourceProbe = await ProbeAsync(catalog.GetVideoPath(segment.Source.RelativePath), cancellationToken);
                    if (segment.End.TotalSeconds > sourceProbe.Duration.TotalSeconds + 0.1)
                        throw new InvalidDataException("The source duration changed. Refresh the library before editing it.");
                    await EncodeAsync(segment, segmentPath, sourceProbe.HasAudio, job.Segments.Count > 1 ? targetFormat : null, progress, cancellationToken);
                    var encoded = await ValidateOutputAsync(segmentPath, segment.End - segment.Start, cancellationToken);
                    normalized.Add((segment, segmentPath, encoded.Duration));
                }
                var id = Guid.NewGuid();
                var staged = Path.Combine(scratch, $"{id:N}.mkv");
                if (normalized.Count == 1) File.Move(normalized[0].Path, staged);
                else
                {
                    var list = Path.Combine(scratch, $"concat-{id:N}.txt");
                    await File.WriteAllLinesAsync(list, normalized.Select(segment => "file '" + Path.GetFileName(segment.Path) + "'"), cancellationToken);
                    await RunFfmpegAsync(["-f", "concat", "-safe", "1", "-i", list, "-map", "0:v:0", "-map", "0:a:0", "-c", "copy", staged],
                        "Joining normalized clips", TimeSpan.FromTicks(normalized.Sum(segment => segment.Duration.Ticks)), progress, cancellationToken);
                }
                var expected = TimeSpan.FromTicks(normalized.Sum(segment => segment.Duration.Ticks));
                var output = await ValidateOutputAsync(staged, expected, cancellationToken);
                var content = MapMetadata(job.Title, normalized.Select(segment => (segment.Segment, segment.Duration)).ToArray(), sourceMetadata, output.Duration);
                var temporaryVideo = new LibraryVideo(id, Path.GetRelativePath(catalog.Paths.RootDirectory, staged), job.Title, output.Duration,
                    DateTimeOffset.UtcNow, content.RecordedAt ?? job.Segments[0].Source.RecordedAt);
                progress?.Report(new ClipEditProgress("Embedding remapped descriptions, chapters and thumbnails"));
                await _metadata.SaveAsync(temporaryVideo, content, cancellationToken);
                prepared.Add((temporaryVideo with { RelativePath = Path.Combine("Videos", $"{id:N}.mkv") }, staged));
            }
            progress?.Report(new ClipEditProgress("Publishing completed video outputs"));
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var item in prepared)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destination = catalog.GetVideoPath(item.Video.RelativePath);
                File.Move(item.Staged, destination, overwrite: false); published.Add(destination);
            }
            await catalog.AddBatchAsync(prepared.Select(item => item.Video).ToArray(), cancellationToken);
            committed = true;
            progress?.Report(new ClipEditProgress("Saved new videos; originals unchanged", 1));
            return prepared.Select(item => item.Video).ToArray();
        }
        finally
        {
            if (!committed) foreach (var path in published) File.Delete(path);
            Directory.Delete(scratch, recursive: true);
        }
    }

    private async Task EncodeAsync(Segment segment, string output, bool hasAudio, ClipOutputFormat? mergeFormat, IProgress<ClipEditProgress>? progress, CancellationToken cancellationToken)
    {
        var arguments = new List<string> { "-xerror", "-err_detect", "explode", "-ss", Seconds(segment.Start), "-i", catalog.GetVideoPath(segment.Source.RelativePath) };
        if (!hasAudio) arguments.AddRange(["-f", "lavfi", "-i", "anullsrc=channel_layout=stereo:sample_rate=48000"]);
        var filter = "scale=max(2\\,trunc(iw/2)*2):max(2\\,trunc(ih/2)*2)";
        if (mergeFormat is { } target)
        {
            var sar = target.SampleAspectRatio.Replace(':', '/');
            filter = $"scale=w='max(2,trunc(iw*sar/({sar})/2)*2)':h='max(2,trunc(ih/2)*2)',setsar={sar}," +
                $"scale=w='min(iw,{target.Width})':h='min(ih,{target.Height})':force_original_aspect_ratio=decrease:force_divisible_by=2," +
                $"pad={target.Width}:{target.Height}:(ow-iw)/2:(oh-ih)/2,setsar={sar},fps={target.FrameRate}";
        }
        arguments.AddRange(["-t", Seconds(segment.End - segment.Start), "-map", "0:V:0", "-map", hasAudio ? "0:a:0" : "1:a:0",
            "-map_metadata", "-1", "-map_chapters", "-1", "-vf", filter, "-fps_mode", mergeFormat is null ? "passthrough" : "cfr",
            "-af", "aresample=48000:async=1:first_pts=0,apad", "-ac", "2", "-ar", "48000", "-c:v", "libx264", "-preset", "fast", "-crf", "18",
            "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "192k", "-shortest", output]);
        await RunFfmpegAsync(arguments, "Encoding " + segment.Source.DisplayName, segment.End - segment.Start, progress, cancellationToken);
    }

    private async Task RunFfmpegAsync(IEnumerable<string> arguments, string stage, TimeSpan duration, IProgress<ClipEditProgress>? progress, CancellationToken cancellationToken)
    {
        var pending = "";
        var result = await _process.RunAsync(tools.Ffmpeg, new[] { "-hide_banner", "-nostdin", "-v", "error", "-n", "-progress", "pipe:1", "-nostats" }.Concat(arguments),
            TimeSpan.FromHours(24), cancellationToken, chunk =>
            {
                pending += chunk;
                int newline;
                while ((newline = pending.IndexOf('\n')) >= 0)
                {
                    var line = pending[..newline].Trim(); pending = pending[(newline + 1)..];
                    if (line.StartsWith("out_time_us=", StringComparison.Ordinal) && long.TryParse(line.AsSpan(12), CultureInfo.InvariantCulture, out var time))
                        progress?.Report(new ClipEditProgress(stage, Math.Clamp(time / 1_000_000d / duration.TotalSeconds, 0, 1)));
                }
                if (pending.Length > 4096) pending = "";
            });
        if (result.ExitCode != 0) throw new InvalidDataException("Video editing failed; originals unchanged. " + result.StandardError);
    }

    private async Task<Probe> ValidateOutputAsync(string path, TimeSpan expected, CancellationToken cancellationToken)
    {
        var probe = await ProbeAsync(path, cancellationToken);
        var frameTolerance = 1 / ParseRatio(probe.Format.FrameRate, 30) + 0.03;
        if (!probe.HasAudio || Math.Abs(probe.Duration.TotalSeconds - expected.TotalSeconds) > Math.Max(Math.Max(0.15, frameTolerance), expected.TotalSeconds * 0.01))
            throw new InvalidDataException("The edited video failed output duration/audio validation.");
        await new MediaValidator(tools, _process).ValidateDecodedAsync(path, probe.Duration, cancellationToken);
        return probe;
    }

    private async Task<Probe> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        var result = await _process.RunAsync(tools.Ffprobe, ["-v", "error", "-show_entries", "format=duration:stream=codec_type,width,height,avg_frame_rate,r_frame_rate,sample_aspect_ratio:stream_disposition=attached_pic:stream_side_data=rotation", "-of", "json", path], TimeSpan.FromSeconds(60), cancellationToken);
        if (result.ExitCode != 0) throw new InvalidDataException("The video is unreadable. " + result.StandardError);
        using var document = JsonDocument.Parse(result.StandardOutput);
        var streams = document.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        var seconds = double.Parse(document.RootElement.GetProperty("format").GetProperty("duration").GetString()!, CultureInfo.InvariantCulture);
        if (!double.IsFinite(seconds) || seconds <= 0 || !streams.Any(stream => stream.GetProperty("codec_type").GetString() == "video"))
            throw new InvalidDataException("Editing requires video media with a finite duration.");
        var primary = streams.First(stream => stream.GetProperty("codec_type").GetString() == "video" &&
            (!stream.TryGetProperty("disposition", out var disposition) || !disposition.TryGetProperty("attached_pic", out var attached) || attached.GetInt32() == 0));
        var width = primary.GetProperty("width").GetInt32(); var height = primary.GetProperty("height").GetInt32();
        var sar = primary.TryGetProperty("sample_aspect_ratio", out var sampleAspect) && ParseRatio(sampleAspect.GetString() ?? "", 0) > 0 ? sampleAspect.GetString()! : "1:1";
        var fps = primary.TryGetProperty("avg_frame_rate", out var average) && ParseRatio(average.GetString() ?? "", 0) > 0 ? average.GetString()! : "30/1";
        if (primary.TryGetProperty("side_data_list", out var sideData) && sideData.EnumerateArray().Any(side => side.TryGetProperty("rotation", out var rotation) && Math.Abs(rotation.GetDouble() % 180) == 90))
        {
            (width, height) = (height, width); var parts = sar.Split(':'); sar = parts[1] + ":" + parts[0];
        }
        return new Probe(TimeSpan.FromSeconds(seconds), streams.Any(stream => stream.GetProperty("codec_type").GetString() == "audio"),
            new ClipOutputFormat(Math.Max(2, width / 2 * 2), Math.Max(2, height / 2 * 2), fps, sar));
    }

    private static VideoMetadata MapMetadata(string title, IReadOnlyList<(Segment Segment, TimeSpan Duration)> segments,
        IReadOnlyDictionary<string, VideoMetadata> metadata, TimeSpan outputDuration)
    {
        var chapters = new List<VideoChapter>(); var thumbnails = new List<VideoThumbnail>(); var uids = new HashSet<ulong>();
        var offset = TimeSpan.Zero;
        foreach (var (segment, duration) in segments)
        {
            var source = metadata[segment.Source.RelativePath];
            var ordered = source.Chapters.OrderBy(chapter => chapter.Position).ToArray();
            var active = ordered.LastOrDefault(chapter => chapter.Position <= segment.Start);
            var selected = ordered.Where(chapter => chapter.Position > segment.Start && chapter.Position < segment.End).ToList();
            if (active is not null) selected.Insert(0, active);
            foreach (var chapter in selected)
            {
                var position = offset + (chapter.Position <= segment.Start ? TimeSpan.Zero : chapter.Position - segment.Start);
                if (position >= offset + duration || position >= outputDuration) continue;
                var uid = chapter.Uid;
                while (uid == 0 || !uids.Add(uid)) uid = BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8));
                chapters.Add(chapter with { Uid = uid, Position = position });
            }
            foreach (var thumbnail in source.Thumbnails.Where(thumbnail => thumbnail.Position >= segment.Start && thumbnail.Position < segment.End))
            {
                var position = offset + thumbnail.Position - segment.Start;
                if (position < offset + duration && position < outputDuration) thumbnails.Add(thumbnail with { Id = Guid.NewGuid(), Position = position });
            }
            offset += duration;
        }
        var contents = segments.Select(segment => metadata[segment.Segment.Source.RelativePath]).ToArray();
        return new VideoMetadata(title, string.Join(Environment.NewLine, contents.Select(content => content.Description).Where(description => description.Length > 0)),
            contents.SelectMany(content => content.Tags).Distinct(StringComparer.Ordinal).ToArray(),
            contents[0].RecordedAt ?? segments[0].Segment.Source.RecordedAt, chapters.OrderBy(chapter => chapter.Position).ToArray(), thumbnails.OrderBy(thumbnail => thumbnail.Position).Take(20).ToArray());
    }

    private static void ValidateRange(LibraryVideo source, TimeSpan start, TimeSpan end)
    {
        if (start < TimeSpan.Zero || end > source.Duration || end <= start || end - start < TimeSpan.FromMilliseconds(100))
            throw new ArgumentOutOfRangeException(nameof(start), "Choose an ordered range inside the video, at least 0.1 seconds long.");
    }
    private static string TitleOrDefault(string title, string fallback) => string.IsNullOrWhiteSpace(title) ? fallback : title.Trim();
    private static string Seconds(TimeSpan time) => time.TotalSeconds.ToString("R", CultureInfo.InvariantCulture);
    private sealed record Segment(LibraryVideo Source, TimeSpan Start, TimeSpan End);
    private sealed record EditJob(string Title, IReadOnlyList<Segment> Segments);
    private static double ParseRatio(string value, double fallback)
    {
        var parts = value.Split(['/', ':']);
        return parts.Length == 2 && double.TryParse(parts[0], CultureInfo.InvariantCulture, out var numerator) &&
            double.TryParse(parts[1], CultureInfo.InvariantCulture, out var denominator) && denominator > 0 && numerator > 0 ? numerator / denominator : fallback;
    }
    private sealed record Probe(TimeSpan Duration, bool HasAudio, ClipOutputFormat Format);
}
