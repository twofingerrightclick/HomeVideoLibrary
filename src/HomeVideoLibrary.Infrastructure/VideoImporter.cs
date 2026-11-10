using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HomeVideoLibrary.Core;
using HomeVideoLibrary.Infrastructure.Editing;
using Microsoft.Data.Sqlite;

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
    private const int FingerprintEdge = 1 << 20, FingerprintSample = 64 * 1024, FingerprintSamples = 8;
    private const int CopyBuffer = 1 << 20;

    /// <summary>Prefix naming how a fingerprint was computed; fingerprints only match ones made the same way.</summary>
    private const string FingerprintVersion = "v2:";
    private static readonly TimeSpan RebaseThreshold = TimeSpan.FromSeconds(0.1);
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
        var sourceStamp = Stamp(sourcePath);
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
        var staging = Path.Combine(catalog.Paths.ScratchDirectory, $"import-{id:N}.mkv");
        var warnings = new List<string>();
        try
        {
            var copy = source.IsMatroska ? await InspectMatroskaAsync(sourcePath, cancellationToken) : null;
            var byteCopy = copy is { Reasons.Count: 0 };
            report.Begin("Copying streams to MKV", CopyWeight);
            StreamCounts expected;
            if (byteCopy)
            {
                await CopyAsync(sourcePath, staging, report, cancellationToken);
                expected = new StreamCounts(source.VideoCount, source.AudioCount, source.SubtitleCount);
            }
            else if (copy is not null)
            {
                await RemuxAsync(sourcePath, staging, copy, report, cancellationToken);
                warnings.Add($"The MKV was rewritten without re-encoding ({string.Join(", ", copy.Reasons)}).");
                expected = new StreamCounts(source.VideoCount, source.AudioCount, source.SubtitleCount);
            }
            else expected = await ConvertAsync(sourcePath, staging, source, report, warnings, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (Stamp(sourcePath) != sourceStamp) throw new IOException(SourceChanged);

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

            // Every import is decoded in full: a byte copy can be exact and still hold a damaged or truncated recording.
            report.Begin("Checking complete video and audio playback", DecodeWeight);
            var decoded = await new MediaValidator(tools, _process).ValidateDecodedAsync(staging, source.Duration, tolerance,
                new FractionProgress(report), cancellationToken);
            if (decoded.ErrorCount > 0)
                warnings.Add($"{decoded.ErrorCount} damaged frame(s) or audio packet(s) were found; the video was imported as it is.");
            cancellationToken.ThrowIfCancellationRequested();

            var video = new LibraryVideo(id, relativePath, Path.GetFileNameWithoutExtension(sourcePath), result.Duration,
                DateTimeOffset.UtcNow, (metadata.RecordedAt ?? recordedAt).ToUniversalTime())
            {
                Title = metadata.Title,
                Description = metadata.Description,
                SourceFingerprint = fingerprint,
            };
            try
            {
                if ((await LibraryPublisher.PublishAsync(catalog, [(video, staging)], cancellationToken)).Unfinished.Count > 0)
                    warnings.Add(PublishResult.UnfinishedNotice);
            }
            catch (SqliteException exception) when (LibraryCatalog.IsDuplicateSourceFingerprint(exception))
            {
                // Another import of the same source committed first; the unique index made that the deciding insert.
                if (await catalog.FindBySourceFingerprintAsync(fingerprint, CancellationToken.None) is { } concurrent)
                    return new ImportResult(ImportOutcome.AlreadyInLibrary, concurrent, []);
                throw;
            }
            report.Complete("Imported");
            return new ImportResult(ImportOutcome.Imported, video, warnings);
        }
        finally { ScratchFiles.TryDelete(staging); }
    }

    /// <summary>True when ffmpeg's error output means the container or a codec rejected a stream, so a more
    /// converting tier may succeed; false for errors such as I/O failures that no conversion fixes.</summary>
    internal static bool IsCodecIncompatibility(string stderr) =>
        IncompatibilitySignatures.Any(signature => stderr.Contains(signature, StringComparison.OrdinalIgnoreCase));

    /// <summary>SHA-256 over the length, the first and last MiB and eight 64 KiB samples spread between them: cheap for
    /// large files, independent of the file name (a renamed copy is the same video), and different for captures that
    /// share a size and identical lead-in and tail.</summary>
    internal static async Task<string> ComputeFingerprintAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, useAsync: true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var length = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(length, stream.Length);
        hash.AppendData(length);
        var buffer = new byte[FingerprintEdge];
        async Task AppendAsync(long offset, int count)
        {
            stream.Seek(Math.Clamp(offset, 0, Math.Max(0, stream.Length - count)), SeekOrigin.Begin);
            hash.AppendData(buffer, 0, await stream.ReadAtLeastAsync(buffer.AsMemory(0, count), count, throwOnEndOfStream: false, cancellationToken));
        }
        await AppendAsync(0, FingerprintEdge);
        for (var sample = 1; sample <= FingerprintSamples; sample++)
            await AppendAsync(stream.Length * sample / (FingerprintSamples + 1), FingerprintSample);
        await AppendAsync(stream.Length - FingerprintEdge, FingerprintEdge);
        return FingerprintVersion + Convert.ToHexStringLower(hash.GetHashAndReset());
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

    /// <summary>What a Matroska source needs before it can join the library, as reasons to rewrite it with mkvmerge
    /// (lossless); none means a byte copy.</summary>
    private async Task<MatroskaInspection> InspectMatroskaAsync(string sourcePath, CancellationToken cancellationToken)
    {
        var identify = await _process.RunAsync(tools.MkvMerge, ["-J", sourcePath], ProbeTimeout, cancellationToken);
        if (identify.ExitCode > 1)
            throw new InvalidDataException("The MKV cannot be read. " + MediaErrors.Summarize(identify.StandardOutput));
        var mkv = MkvInfo.Parse(identify.StandardOutput);
        var timeline = await MediaProbe.ReadTimelineAsync(_process, tools, sourcePath, cancellationToken);
        var reasons = new List<string>();
        if (await ReadDocTypeAsync(sourcePath, cancellationToken) == "webm") reasons.Add("WebM files cannot hold attachments");
        if (mkv.Duration <= TimeSpan.Zero) reasons.Add("it had no duration");
        if (mkv.Tracks.FirstOrDefault(track => track.Type == "video") is { IndexEntries: 0 }) reasons.Add("it had no seek index");
        if (timeline.StartTime > RebaseThreshold) reasons.Add($"its timeline started at {Timecode.Format(timeline.StartTime)}");
        return new MatroskaInspection(mkv, timeline, reasons);
    }

    /// <summary>Copies the bytes; the full decode afterwards checks the content.</summary>
    private static async Task CopyAsync(string sourcePath, string staging, WeightedProgress report, CancellationToken cancellationToken)
    {
        var length = new FileInfo(sourcePath).Length;
        await using (var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBuffer, useAsync: true))
        await using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None, CopyBuffer, useAsync: true))
        {
            var buffer = new byte[CopyBuffer];
            long copied = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                copied += read;
                report.Report(length > 0 ? copied / (double)length : 1);
            }
            if (copied != length) throw new IOException(SourceChanged);
        }
    }

    private const string SourceChanged = "The video changed while it was being imported. Import it again once it is complete.";

    /// <summary>Size and write time, compared before and after the source is read: a recorder still writing the file
    /// would otherwise leave a copy, remux or conversion of a partial recording.</summary>
    private static (long Length, DateTime Modified) Stamp(string path)
    {
        var file = new FileInfo(path);
        return (file.Length, file.LastWriteTimeUtc);
    }

    /// <summary>Rewrites a Matroska source with mkvmerge, keeping every track, chapter, tag and attachment, and moving
    /// a timeline that does not start at zero (as recorders and earlier cuts leave it) so it does.</summary>
    private async Task RemuxAsync(string sourcePath, string staging, MatroskaInspection inspection, WeightedProgress report,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string> { "--no-date", "-o", staging };
        if (inspection.Timeline.StartTime > RebaseThreshold)
        {
            var shift = "-" + ((long)inspection.Timeline.StartTime.TotalMilliseconds).ToString(CultureInfo.InvariantCulture);
            foreach (var track in inspection.Mkv.Tracks) arguments.AddRange(["--sync", $"{track.Id}:{shift}"]);
            arguments.AddRange(["--chapter-sync", shift]);
        }
        arguments.Add(sourcePath);
        await new MediaCommands(tools, _process).MkvMergeAsync(arguments, local => report.Report(local), cancellationToken);
    }

    /// <summary>The EBML DocType ("matroska" or "webm") from the first bytes of the file.</summary>
    private static async Task<string> ReadDocTypeAsync(string path, CancellationToken cancellationToken)
    {
        var header = new byte[64];
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, useAsync: true);
        var length = await stream.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken);
        for (var i = 0; i + 2 < length; i++)
        {
            if (header[i] != 0x42 || header[i + 1] != 0x82 || header[i + 2] < 0x80) continue;
            var size = header[i + 2] & 0x7F;
            return i + 3 + size <= length ? Encoding.ASCII.GetString(header, i + 3, size) : "";
        }
        return "";
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
        // Matroska header durations are missing in piped or crashed recordings and differ between muxers for files that
        // start late; the timeline reconciles them with the packets.
        if (formatNames.Contains("matroska"))
            seconds = (await MediaProbe.ReadTimelineAsync(_process, tools, path, cancellationToken)).ClaimedLength.TotalSeconds;
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

    /// <param name="Reasons">Why the source is rewritten rather than byte-copied; empty for a byte copy.</param>
    private sealed record MatroskaInspection(MkvInfo Mkv, MediaTimeline Timeline, IReadOnlyList<string> Reasons);

    private sealed record ConversionPlan(IReadOnlyList<string> Arguments, StreamCounts Counts, IReadOnlyList<string> Warnings);

    private sealed class FractionProgress(WeightedProgress report) : IProgress<double>
    {
        public void Report(double value) => report.Report(value);
    }
}
