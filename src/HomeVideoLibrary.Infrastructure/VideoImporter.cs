using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HomeVideoLibrary.Core;
using HomeVideoLibrary.Infrastructure.Editing;

namespace HomeVideoLibrary.Infrastructure;

/// <summary>Copies a source video into the library as MKV without touching the original. Streams are copied whenever
/// Matroska can hold them; only the streams it cannot hold are converted, and video is re-encoded only as a last resort.
/// The staged file gets its title, recorded date and source fingerprint written into the MKV and is fully decoded
/// before it is published.</summary>
public sealed class VideoImporter(LibraryCatalog catalog, MediaTools tools, MediaProcess? process = null, IMatroskaFileEditor? editor = null)
    : IVideoImporter
{
    public const string SourceFingerprintTag = "HVL_SOURCE_FINGERPRINT";
    public const string OriginalFileNameTag = "HVL_ORIGINAL_FILENAME";
    private const double CopyWeight = 0.6, MetadataWeight = 0.02, DecodeWeight = 0.38;
    private const int FingerprintChunk = 1 << 20;
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMinutes(5);
    private static readonly DateTimeOffset EarliestRecordedDate = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly string[] IncompatibilitySignatures =
    [
        "not supported", "could not write header", "subtitle encoding", "unknown timestamp", "timestamps are unset",
        "could not find tag for codec", "incorrect codec parameters", "only audio, video, and subtitles are supported",
        "non-monotonic", "invalid dts", "invalid pts",
    ];

    private static readonly HashSet<string> MatroskaAudio =
    [
        "aac", "ac3", "eac3", "mp3", "mp2", "mp1", "flac", "opus", "vorbis", "dts", "truehd", "mlp", "alac", "tta", "wavpack",
        "pcm_s16le", "pcm_s16be", "pcm_s24le", "pcm_s24be", "pcm_s32le", "pcm_s32be", "pcm_u8", "pcm_f32le", "pcm_f64le",
    ];

    private static readonly HashSet<string> MatroskaSubtitles =
        ["subrip", "srt", "ass", "ssa", "webvtt", "dvd_subtitle", "hdmv_pgs_subtitle", "dvb_subtitle", "arib_caption"];

    private static readonly HashSet<string> TextSubtitles = ["mov_text", "text", "tx3g", "microdvd", "sami", "realtext", "subviewer", "jacosub", "mpl2", "vplayer", "pjs", "stl"];

    private static readonly HashSet<string> RawStreamFormats = ["h264", "hevc", "mpegvideo", "m4v", "rawvideo", "vc1", "av1", "obu", "ivf"];

    private readonly MediaProcess _process = process ?? new MediaProcess();
    private readonly IMatroskaFileEditor _editor = editor ?? new MatroskaMetadataService(catalog, tools);

    public async Task<ImportResult> ImportAsync(string sourcePath, IProgress<ImportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        sourcePath = Path.GetFullPath(sourcePath);
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("The selected source video no longer exists.", sourcePath);
        var report = new WeightedProgress(1, (stage, fraction) => progress?.Report(new ImportProgress(stage, fraction)));
        report.Begin("Inspecting source", 0, determinate: false);
        var fingerprint = await ComputeFingerprintAsync(sourcePath, cancellationToken);
        if (await catalog.FindBySourceFingerprintAsync(fingerprint, cancellationToken) is { } existing)
            return new ImportResult(ImportOutcome.AlreadyInLibrary, existing, []);

        var source = await ProbeAsync(sourcePath, cancellationToken);
        var recordedAt = (UsableRecordedDate(source.RecordedAt) ?? new DateTimeOffset(File.GetLastWriteTimeUtc(sourcePath))).ToUniversalTime();
        var sourceLength = new FileInfo(sourcePath).Length;
        Directory.CreateDirectory(catalog.Paths.ScratchDirectory);
        ScratchFiles.EnsureFreeSpace(catalog.Paths.ScratchDirectory, (long)(sourceLength * 1.1));

        var id = Guid.NewGuid();
        var relativePath = LibraryPaths.VideoRelativePath(id);
        var destination = catalog.GetVideoPath(relativePath);
        var staging = Path.Combine(catalog.Paths.ScratchDirectory, $"import-{id:N}.mkv");
        var warnings = new List<string>();
        var published = false;
        var committed = false;
        try
        {
            report.Begin("Copying streams to MKV", CopyWeight);
            var expected = source.IsMatroska
                ? await CopyBytesAsync(sourcePath, staging, sourceLength, report, source, cancellationToken)
                : await ConvertAsync(sourcePath, staging, source, report, warnings, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            report.Begin("Writing title and recorded date into the MKV", MetadataWeight, determinate: false);
            var metadata = await WriteMetadataAsync(staging, sourcePath, fingerprint, recordedAt, cancellationToken);

            report.Begin("Verifying MKV", 0, determinate: false);
            var result = await ProbeAsync(staging, cancellationToken);
            var tolerance = DecodeTolerance.ForImport(source.IsMpegStream);
            if ((result.Duration - source.Duration).Duration() > tolerance.For(source.Duration))
                throw new InvalidDataException(
                    $"The converted video is {result.Duration.TotalSeconds:0.#} s long but the original is {source.Duration.TotalSeconds:0.#} s. The original was preserved.");
            if (result.VideoCount != expected.Video || result.AudioCount != expected.Audio || result.SubtitleCount != expected.Subtitles)
                throw new InvalidDataException(
                    $"The converted video has {result.VideoCount} video, {result.AudioCount} audio and {result.SubtitleCount} subtitle tracks; " +
                    $"{expected.Video}, {expected.Audio} and {expected.Subtitles} were expected. The original was preserved.");

            report.Begin("Checking complete video and audio playback", DecodeWeight);
            var decoded = await new MediaValidator(tools, _process).ValidateDecodedAsync(staging, source.Duration, tolerance,
                new FractionProgress(report), cancellationToken);
            if (decoded.ErrorCount > 0)
                warnings.Add($"{decoded.ErrorCount} damaged frame(s) or audio packet(s) were found; the video was imported as it is.");
            cancellationToken.ThrowIfCancellationRequested();

            await using (var stream = new FileStream(staging, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                stream.Flush(flushToDisk: true);
            File.Move(staging, destination, overwrite: false);
            published = true;
            var video = new LibraryVideo(id, relativePath, Path.GetFileNameWithoutExtension(sourcePath), result.Duration,
                DateTimeOffset.UtcNow, (metadata.RecordedAt ?? recordedAt).ToUniversalTime())
            {
                Title = metadata.Title,
                Description = metadata.Description,
                SourceFingerprint = fingerprint,
            };
            await catalog.AddAsync(video, cancellationToken);
            committed = true;
            report.Complete("Imported");
            return new ImportResult(ImportOutcome.Imported, video, warnings);
        }
        finally
        {
            ScratchFiles.TryDelete(staging);
            if (published && !committed) ScratchFiles.TryDelete(destination);
        }
    }

    /// <summary>True when ffmpeg's error output means the container or a codec rejected a stream, so a more
    /// converting tier may succeed; false for errors such as I/O failures that no conversion fixes.</summary>
    internal static bool IsCodecIncompatibility(string stderr) =>
        IncompatibilitySignatures.Any(signature => stderr.Contains(signature, StringComparison.OrdinalIgnoreCase));

    /// <summary>SHA-256 over the length, the first and last MiB, and the lower-case file name: cheap for large files,
    /// stable across copies, and distinct for different clips that share a name.</summary>
    internal static async Task<string> ComputeFingerprintAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, useAsync: true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var length = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(length, stream.Length);
        hash.AppendData(length);
        var buffer = new byte[FingerprintChunk];
        hash.AppendData(buffer, 0, await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken));
        stream.Seek(Math.Max(0, stream.Length - FingerprintChunk), SeekOrigin.Begin);
        hash.AppendData(buffer, 0, await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken));
        hash.AppendData(Encoding.UTF8.GetBytes(Path.GetFileName(path).ToLowerInvariant()));
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>Recorded dates before 1980 or more than a day in the future are camera defaults or corruption.</summary>
    internal static DateTimeOffset? UsableRecordedDate(DateTimeOffset? value) =>
        value is { } date && date >= EarliestRecordedDate && date <= DateTimeOffset.UtcNow.AddDays(1) ? date : null;

    /// <summary>Fills a missing TITLE (the file name) and DATE_RECORDED, keeping everything the file already says, and
    /// records where the video came from.</summary>
    private async Task<VideoMetadata> WriteMetadataAsync(string staging, string sourcePath, string fingerprint, DateTimeOffset recordedAt,
        CancellationToken cancellationToken)
    {
        var existing = await _editor.ReadTextAsync(staging, cancellationToken);
        foreach (var warning in existing.Warnings) Trace.TraceWarning($"{sourcePath}: {warning}");
        var metadata = existing.Metadata;
        if (metadata.Title.Trim().Length == 0) metadata = metadata with { Title = Path.GetFileNameWithoutExtension(sourcePath) };
        // RecordedAt without RecordedAtText is only the muxing date the reader falls back to, not a recorded date.
        if (metadata.RecordedAtText.Trim().Length == 0) metadata = metadata with { RecordedAt = recordedAt, RecordedAtText = "" };
        await _editor.WriteStagedAsync(staging, metadata, new MetadataWriteOptions
        {
            ExtraGlobalTags = new Dictionary<string, string>
            {
                [SourceFingerprintTag] = fingerprint,
                [OriginalFileNameTag] = Path.GetFileName(sourcePath),
            },
            KeepExistingThumbnails = true,
        }, cancellationToken);
        return metadata;
    }

    /// <summary>Byte-copies a Matroska source, which keeps its chapters, tags, track UIDs and attachments exactly.</summary>
    private static async Task<StreamCounts> CopyBytesAsync(string sourcePath, string staging, long length, WeightedProgress report,
        ProbeInfo source, CancellationToken cancellationToken)
    {
        await using (var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true))
        await using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
        {
            var buffer = new byte[1 << 20];
            long copied = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                copied += read;
                report.Report(length > 0 ? copied / (double)length : 1);
            }
        }
        return new StreamCounts(source.VideoCount, source.AudioCount, source.SubtitleCount);
    }

    /// <summary>Tries each tier in turn, moving on only when ffmpeg reports a codec or container incompatibility.</summary>
    private async Task<StreamCounts> ConvertAsync(string sourcePath, string staging, ProbeInfo source, WeightedProgress report,
        List<string> warnings, CancellationToken cancellationToken)
    {
        if (source.Streams.Any(stream => stream.Type == "video" && stream.AttachedPicture))
            warnings.Add("Cover art was not imported.");
        if (source.Streams.Any(stream => stream.Type is "data"))
            warnings.Add("Timecode and sensor data tracks were not imported.");
        var tier = source.Streams.Any(stream => stream.Type == "subtitle" && TextSubtitles.Contains(stream.Codec))
            ? ImportTier.ConvertStreams : ImportTier.Copy;
        while (true)
        {
            var plan = Plan(source, tier);
            var result = await _process.RunAsync(tools.Ffmpeg, [.. plan.Arguments, staging], TimeSpan.FromHours(24), cancellationToken,
                new LineSplitter(line =>
                {
                    if (line.StartsWith("out_time_us=", StringComparison.Ordinal) && source.Duration > TimeSpan.Zero &&
                        long.TryParse(line.AsSpan(12), NumberStyles.Integer, CultureInfo.InvariantCulture, out var microseconds))
                        report.Report(microseconds * 10d / source.Duration.Ticks);
                }).Append);
            if (result.ExitCode == 0)
            {
                warnings.AddRange(plan.Warnings);
                return plan.Counts;
            }
            Trace.TraceWarning($"Import of {sourcePath} failed at tier {tier}: {result.StandardError}");
            ScratchFiles.TryDelete(staging);
            if (!IsCodecIncompatibility(result.StandardError) || tier == ImportTier.ReencodeVideo)
                throw new InvalidDataException("The source cannot be converted to MKV. " + MediaErrors.Summarize(result.StandardError));
            tier = tier == ImportTier.Copy && !result.StandardError.Contains("timestamp", StringComparison.OrdinalIgnoreCase)
                ? ImportTier.ConvertStreams : tier + 1;
        }
    }

    private static ConversionPlan Plan(ProbeInfo source, ImportTier tier)
    {
        var arguments = new List<string> { "-hide_banner", "-nostdin", "-v", "error", "-n" };
        if (tier >= ImportTier.CopyWithGeneratedTimestamps) arguments.AddRange(["-fflags", "+genpts"]);
        arguments.AddRange(["-i", source.Path]);
        var codecs = new List<string>();
        var warnings = new List<string>();
        int video = 0, audio = 0, subtitles = 0, output = 0;
        foreach (var stream in source.Streams)
        {
            var codec = stream.Type switch
            {
                "video" when stream.AttachedPicture => null,
                "video" => tier == ImportTier.ReencodeVideo ? "reencode" : "copy",
                "audio" => tier < ImportTier.ConvertStreams || MatroskaAudio.Contains(stream.Codec) ? "copy"
                    : stream.Codec.StartsWith("pcm_", StringComparison.Ordinal) || stream.Codec is "alac" or "adpcm_ima_qt" ? "flac" : "aac",
                "subtitle" => tier < ImportTier.ConvertStreams || MatroskaSubtitles.Contains(stream.Codec) ? "copy"
                    : TextSubtitles.Contains(stream.Codec) ? "srt" : null,
                _ => null,
            };
            if (codec is null)
            {
                if (stream.Type == "subtitle") warnings.Add($"A {stream.Codec} subtitle track could not be imported.");
                continue;
            }
            arguments.AddRange(["-map", $"0:{stream.Index}"]);
            var specifier = $"-c:{output}";
            switch (codec)
            {
                case "copy": codecs.AddRange([specifier, "copy"]); break;
                case "reencode": codecs.AddRange([specifier, "libx264", $"-preset:{output}", "medium", $"-crf:{output}", "18", .. PixelArguments(stream, output)]); break;
                case "aac": codecs.AddRange([specifier, "aac", $"-b:{output}", "192k"]); break;
                default: codecs.AddRange([specifier, codec]); break;
            }
            if (codec != "copy")
                warnings.Add(stream.Type == "video" ? "The video was re-encoded because MKV cannot hold its format."
                    : $"A {stream.Codec} {stream.Type} track was converted to {(codec == "reencode" ? "H.264" : codec)}.");
            switch (stream.Type)
            {
                case "video": video++; break;
                case "audio": audio++; break;
                default: subtitles++; break;
            }
            output++;
        }
        arguments.AddRange(["-map", "0:t?", "-map_metadata", "0", "-map_chapters", "0", .. codecs]);
        if (tier >= ImportTier.CopyWithGeneratedTimestamps) arguments.AddRange(["-avoid_negative_ts", "make_zero"]);
        arguments.AddRange(["-progress", "pipe:1", "-nostats", "-f", "matroska"]);
        return new ConversionPlan(arguments, new StreamCounts(video, audio, subtitles), warnings.Distinct().ToArray());
    }

    /// <summary>Keeps the source's pixel format and colour description where libx264 supports them.</summary>
    private static IEnumerable<string> PixelArguments(SourceStream stream, int output)
    {
        yield return $"-pix_fmt:{output}";
        yield return VideoEncoding.EncodablePixelFormat("h264", stream.PixelFormat);
        if (stream.FieldOrder is "tt" or "tb" or "bb" or "bt")
        {
            yield return $"-flags:{output}";
            yield return "+ilme+ildct";
        }
        foreach (var (option, value) in new[] { ("-color_range", stream.ColorRange), ("-colorspace", stream.ColorSpace),
                     ("-color_primaries", stream.ColorPrimaries), ("-color_trc", stream.ColorTransfer) })
        {
            if (value.Length == 0 || value is "unknown" or "reserved") continue;
            yield return $"{option}:{output}";
            yield return value;
        }
    }

    private async Task<ProbeInfo> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        MediaProcessResult result;
        try
        {
            result = await _process.RunAsync(tools.Ffprobe, ["-v", "error", "-show_entries",
                "format=duration,format_name:format_tags=creation_time:stream=index,codec_type,codec_name,duration,field_order,pix_fmt,color_range,color_space,color_primaries,color_transfer:stream_disposition=attached_pic",
                "-of", "json", path], ProbeTimeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            throw new InvalidDataException("Reading the file took more than five minutes. Copy it to a local drive and import it from there.");
        }
        if (result.ExitCode != 0)
            throw new InvalidDataException("The file is not readable video media. " + MediaErrors.Summarize(result.StandardError));
        using var json = JsonDocument.Parse(result.StandardOutput);
        var root = json.RootElement;
        var format = root.TryGetProperty("format", out var formatElement) ? formatElement : default;
        var formatNames = Text(format, "format_name").Split(',');
        var streams = (root.TryGetProperty("streams", out var streamArray) ? streamArray.EnumerateArray().ToArray() : [])
            .Select(stream => new SourceStream(stream.TryGetProperty("index", out var index) ? index.GetInt32() : 0,
                Text(stream, "codec_type"), Text(stream, "codec_name"),
                stream.TryGetProperty("disposition", out var disposition) && disposition.TryGetProperty("attached_pic", out var attached) && attached.GetInt32() == 1,
                Seconds(Text(stream, "duration")), Text(stream, "field_order"), Text(stream, "pix_fmt"), Text(stream, "color_range"),
                Text(stream, "color_space"), Text(stream, "color_primaries"), Text(stream, "color_transfer")))
            .ToArray();
        if (formatNames.Any(RawStreamFormats.Contains))
            throw new InvalidDataException("This is a raw video stream without timing information. Wrap it in an MP4 or MKV file first.");
        if (!streams.Any(stream => stream.Type == "video" && !stream.AttachedPicture))
            throw new InvalidDataException("Import requires a video stream.");
        var seconds = Seconds(Text(format, "duration")) ?? streams.Max(stream => stream.Duration);
        if (seconds is not { } duration || duration <= 0 || duration > TimeSpan.MaxValue.TotalSeconds / 2)
            throw new InvalidDataException("Import requires a finite positive duration.");
        DateTimeOffset? recordedAt = format.ValueKind == JsonValueKind.Object && format.TryGetProperty("tags", out var tags) &&
            tags.TryGetProperty("creation_time", out var creationTime) &&
            DateTimeOffset.TryParse(creationTime.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed : null;
        return new ProbeInfo(path, TimeSpan.FromSeconds(duration), streams, recordedAt, formatNames.Contains("matroska"),
            formatNames.Any(name => name is "mpegts" or "mpeg" or "mpegps" or "vob"));
    }

    private static string Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "" : "";

    private static double? Seconds(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) && value > 0 ? value : null;

    private enum ImportTier { Copy, CopyWithGeneratedTimestamps, ConvertStreams, ReencodeVideo }

    private sealed record SourceStream(int Index, string Type, string Codec, bool AttachedPicture, double? Duration, string FieldOrder,
        string PixelFormat, string ColorRange, string ColorSpace, string ColorPrimaries, string ColorTransfer);

    private sealed record ProbeInfo(string Path, TimeSpan Duration, IReadOnlyList<SourceStream> Streams, DateTimeOffset? RecordedAt,
        bool IsMatroska, bool IsMpegStream)
    {
        public int VideoCount => Streams.Count(stream => stream.Type == "video" && !stream.AttachedPicture);
        public int AudioCount => Streams.Count(stream => stream.Type == "audio");
        public int SubtitleCount => Streams.Count(stream => stream.Type == "subtitle");
    }

    private sealed record StreamCounts(int Video, int Audio, int Subtitles);

    private sealed record ConversionPlan(IReadOnlyList<string> Arguments, StreamCounts Counts, IReadOnlyList<string> Warnings);

    private sealed class FractionProgress(WeightedProgress report) : IProgress<double>
    {
        public void Report(double value) => report.Report(value);
    }
}
