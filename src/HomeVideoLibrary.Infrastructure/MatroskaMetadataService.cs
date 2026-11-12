using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using HomeVideoLibrary.Core;

namespace HomeVideoLibrary.Infrastructure;

/// <summary>Reads and writes the metadata the MKV owns. Every operation on a file is serialized through a per-path gate
/// shared by all instances. Writes are in place with mkvpropedit, which rewrites only metadata elements.</summary>
public sealed partial class MatroskaMetadataService(LibraryCatalog catalog, MediaTools tools, MediaProcess? process = null)
    : IVideoMetadataService, IMatroskaFileEditor, IIdentityWriter
{
    private const string ManifestName = "home-video-library.thumbnails.v1.json";
    private const int MaxThumbnails = 20;
    private static readonly TimeSpan ToolTimeout = TimeSpan.FromMinutes(10);
    // mkvpropedit rewrites only metadata (well under a second on large files); a stall this long means it is stuck mid-write.
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromMinutes(1);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly MediaProcess _process = process ?? new MediaProcess();

    /// <summary>Runs after the in-place mkvpropedit and before verification; a throw exercises the failure path.</summary>
    internal Func<string, Task>? AfterWriteForTesting { get; set; }

    public async Task<VideoMetadata> ReadAsync(LibraryVideo video, CancellationToken cancellationToken = default)
    {
        var path = catalog.GetVideoPath(video.RelativePath);
        using var _ = await EnterGateAsync(path, cancellationToken);
        var scratch = CreateScratch();
        try
        {
            var state = await ReadStateAsync(path, scratch, cancellationToken);
            var thumbnails = await ExtractThumbnailsAsync(path, video.Id, state, scratch, cancellationToken);
            foreach (var warning in state.Warnings) Trace.TraceWarning($"{path}: {warning}");
            return state.Metadata with { Thumbnails = thumbnails };
        }
        finally { DeleteDirectory(scratch); }
    }

    public async Task<VideoTextMetadata> ReadTextAsync(string mkvPath, CancellationToken cancellationToken = default)
    {
        var path = Path.GetFullPath(mkvPath);
        using var _ = await EnterGateAsync(path, cancellationToken);
        var scratch = CreateScratch();
        try
        {
            var state = await ReadStateAsync(path, scratch, cancellationToken);
            var duration = state.Container.DurationNanoseconds is { } nanoseconds ? TimeSpan.FromTicks(nanoseconds / 100) : TimeSpan.Zero;
            return new VideoTextMetadata(state.Metadata, state.Manifest.Select(ToRef).ToArray(), duration, state.Warnings)
            {
                MediaIdentity = state.Container.MediaIdentity,
            };
        }
        finally { DeleteDirectory(scratch); }
    }

    public async Task SaveAsync(LibraryVideo video, VideoMetadata metadata, CancellationToken cancellationToken = default)
    {
        if (metadata.Thumbnails.Count > MaxThumbnails) throw new ArgumentException("Choose at most twenty thumbnails.", nameof(metadata));
        metadata = MetadataText.AsStored(metadata);
        var path = catalog.GetVideoPath(video.RelativePath);
        using var _ = await EnterGateAsync(path, cancellationToken);
        var scratch = CreateScratch();
        try
        {
            var original = await ReadStateAsync(path, scratch, cancellationToken);
            Validate(metadata, original.Container.DurationNanoseconds is { } nanoseconds ? TimeSpan.FromTicks(nanoseconds / 100) : video.Duration);
            var plan = await PlanWriteAsync(original, metadata, null, keepThumbnails: false, scratch, cancellationToken);
            await WriteWithRollbackAsync(path, original, metadata, plan, scratch, cancellationToken);
        }
        finally { DeleteDirectory(scratch); }
    }

    /// <summary>Writes TITLE and DATE_RECORDED into a library video that no import of this app wrote them into (one
    /// imported before imports did, or an edit of one), so the file says what it is on its own. Only an absent tag is
    /// added, with the catalog's value: TITLE from the segment title, else the name the video was added under; DATE_RECORDED
    /// from the catalog date. Nothing else in the file is rewritten or re-normalized. A file an import of this app wrote
    /// (it carries a source fingerprint) is never changed: a tag missing there was cleared by the user. Written in place
    /// like <see cref="SaveAsync"/>: verified, and rolled back on failure.</summary>
    /// <returns>False, without writing, when nothing was missing.</returns>
    public async Task<bool> FillMissingIdentityAsync(LibraryVideo video, CancellationToken cancellationToken = default)
    {
        if (video.SourceFingerprint is not null) return false;
        var path = catalog.GetVideoPath(video.RelativePath);
        using var _ = await EnterGateAsync(path, cancellationToken);
        var scratch = CreateScratch();
        try
        {
            var original = await ReadStateAsync(path, scratch, cancellationToken);
            if (MatroskaMetadataXml.HasGlobal(original.Tags, VideoImporter.SourceFingerprintTag)) return false;
            var additions = new Dictionary<string, string>();
            var expected = original.Written;
            if (!MatroskaMetadataXml.HasGlobal(original.Tags, "TITLE") &&
                MetadataText.NormalizeTitle(original.Metadata.Title.Length > 0 ? original.Metadata.Title : video.DisplayName) is { Length: > 0 } title)
            {
                additions["TITLE"] = title;
                expected = expected with { Title = title };
            }
            if (!MatroskaMetadataXml.HasGlobal(original.Tags, "DATE_RECORDED"))
            {
                var date = video.RecordedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
                additions["DATE_RECORDED"] = date;
                expected = expected with { RecordedAt = RecordedDate.TryParse(date, out var parsed) ? parsed : null, RecordedAtText = date };
            }
            if (additions.Count == 0) return false;
            var tags = new XDocument(original.Tags);
            MatroskaMetadataXml.AddGlobal(tags, additions);
            var tagsPath = Path.Combine(scratch, "tags.xml");
            await File.WriteAllTextAsync(tagsPath, "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" + tags, cancellationToken);
            var plan = new WritePlan(["--tags", "all:" + tagsPath], original.Manifest, TouchesAttachments: false, additions);
            await WriteWithRollbackAsync(path, original, expected, plan, scratch, cancellationToken);
            return true;
        }
        finally { DeleteDirectory(scratch); }
    }

    public async Task WriteStagedAsync(string mkvPath, VideoMetadata metadata, MetadataWriteOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new MetadataWriteOptions();
        if (!options.KeepExistingThumbnails && metadata.Thumbnails.Count > MaxThumbnails)
            throw new ArgumentException("Choose at most twenty thumbnails.", nameof(metadata));
        metadata = MetadataText.AsStored(metadata);
        var extraTags = options.ExtraGlobalTags.ToDictionary(pair => pair.Key, pair => MetadataText.Normalize(pair.Value));
        var path = Path.GetFullPath(mkvPath);
        using var _ = await EnterGateAsync(path, cancellationToken);
        var scratch = CreateScratch();
        try
        {
            var original = await ReadStateAsync(path, scratch, cancellationToken);
            Validate(options.KeepExistingThumbnails ? metadata with { Thumbnails = [] } : metadata,
                original.Container.DurationNanoseconds is { } nanoseconds ? TimeSpan.FromTicks(nanoseconds / 100) : TimeSpan.MaxValue);
            var plan = await PlanWriteAsync(original, metadata, extraTags, options.KeepExistingThumbnails, scratch, cancellationToken);
            await RunAsync(tools.MkvPropEdit, [path, .. plan.Arguments], cancellationToken, WriteTimeout);
            if (AfterWriteForTesting is { } hook) await hook(path);
            await VerifyAsync(path, original, metadata, plan, scratch, cancellationToken);
        }
        finally { DeleteDirectory(scratch); }
    }

    public async Task<string> ExtractThumbnailAsync(string mkvPath, Guid videoId, VideoThumbnailRef thumbnail,
        CancellationToken cancellationToken = default)
    {
        var destination = ThumbnailCachePath(videoId, thumbnail.Id);
        if (await HashMatchesAsync(destination, thumbnail.Sha256, cancellationToken)) return destination;
        var path = Path.GetFullPath(mkvPath);
        using var _ = await EnterGateAsync(path, cancellationToken);
        if (await HashMatchesAsync(destination, thumbnail.Sha256, cancellationToken)) return destination;
        var container = await IdentifyAsync(path, cancellationToken);
        var attachment = container.Attachments.FirstOrDefault(candidate => candidate.Name == thumbnail.AttachmentName)
            ?? throw new InvalidDataException("The embedded thumbnail is missing from the video file.");
        var scratch = CreateScratch();
        try
        {
            var extracted = Path.Combine(scratch, "thumbnail.jpg");
            await RunAsync(tools.MkvExtract, [path, "attachments", $"{attachment.Id}:{extracted}"], cancellationToken);
            if (!await HashMatchesAsync(extracted, thumbnail.Sha256, cancellationToken))
                throw new InvalidDataException("An embedded thumbnail failed its integrity check.");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Move(extracted, destination, overwrite: true);
            return destination;
        }
        finally { DeleteDirectory(scratch); }
    }

    public async Task CaptureFrameAsync(string mkvPath, TimeSpan position, string outputPath, int maxWidth = 960,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(position, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxWidth, 2);
        await CaptureAsync(["-ss", Seconds(position), "-i", mkvPath, "-frames:v", "1", "-vf", SquarePixels(maxWidth), "-q:v", "2"],
            outputPath, NoFrame, cancellationToken);
    }

    public async Task CaptureKeyframeAsync(string mkvPath, TimeSpan position, string outputPath, int maxWidth,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(position, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxWidth, 2);
        // Frame threading would decode frames ahead that are never shown; one thread keeps a capture to about one core.
        // Without -copyts the keyframe the seek lands on gets a negative timestamp and is dropped, so ffmpeg writes the next
        // keyframe instead, or nothing when the position is past the last one.
        await CaptureAsync(["-threads", "1", "-skip_frame", "nokey", "-noaccurate_seek", "-copyts", "-ss", Seconds(position), "-i", mkvPath,
            "-map", "0:v:0", "-frames:v", "1", "-vf", SquarePixels(maxWidth), "-c:v", "bmp"], outputPath, NoFrame, cancellationToken);
    }

    public async Task TileAsync(IReadOnlyList<string> images, string outputPath, int columns, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(images.Count, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);
        var rows = (images.Count + columns - 1) / columns;
        var filter = string.Create(CultureInfo.InvariantCulture,
            $"concat=n={images.Count}:v=1:a=0,tile={columns}x{rows},scale=out_range=full,format=yuv420p");
        await CaptureAsync([.. images.SelectMany(image => new[] { "-i", image }), "-filter_complex", filter, "-frames:v", "1", "-q:v", "4"],
            outputPath, "The images could not be tiled.", cancellationToken);
    }

    private const string NoFrame = "No video frame was available at that position.";

    /// <summary>Runs ffmpeg with <paramref name="arguments"/> writing one image to <paramref name="outputPath"/>; on failure
    /// no partial file is left.</summary>
    private async Task CaptureAsync(IEnumerable<string> arguments, string outputPath, string noOutput, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        File.Delete(outputPath);
        try
        {
            await RunAsync(tools.Ffmpeg, ["-hide_banner", "-nostdin", "-v", "error", "-y", .. arguments, "-update", "1", outputPath],
                cancellationToken);
            if (!File.Exists(outputPath)) throw new InvalidDataException(noOutput);
        }
        catch
        {
            TryDelete(outputPath);
            throw;
        }
    }

    private static string Seconds(TimeSpan position) => position.TotalSeconds.ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>Display width from the sample aspect ratio, then square pixels, so anamorphic (e.g. 720x480 8:9) sources
    /// keep their shape in viewers that ignore JFIF density. yadif touches only frames the decoder flags as interlaced.</summary>
    private static string SquarePixels(int maxWidth) => string.Create(CultureInfo.InvariantCulture,
        $"yadif=deint=interlaced,scale=w='min({maxWidth / 2 * 2},trunc(iw*sar/2)*2)':h='trunc(ow/dar/2)*2',setsar=1");

    public async Task<VideoThumbnail> CaptureThumbnailAsync(LibraryVideo video, TimeSpan position, CancellationToken cancellationToken = default)
    {
        if (position < TimeSpan.Zero || position >= video.Duration) throw new ArgumentOutOfRangeException(nameof(position));
        var id = Guid.NewGuid();
        var image = ThumbnailCachePath(video.Id, id);
        await CaptureFrameAsync(catalog.GetVideoPath(video.RelativePath), position, image, cancellationToken: cancellationToken);
        return new VideoThumbnail(id, position, image);
    }

    private string ThumbnailCachePath(Guid videoId, Guid thumbnailId) =>
        Path.Combine(catalog.Paths.ThumbnailCacheDirectory, videoId.ToString("N"), thumbnailId.ToString("N") + ".jpg");

    /// <summary>Takes the per-path gate that every read and write of <paramref name="mkvPath"/> goes through: an
    /// operation in progress finishes first, and none starts until the returned lease is disposed. For work that needs the
    /// file to itself, such as moving it to the Trash. Not reentrant: do not use this service on the same file while
    /// holding the lease.</summary>
    public static Task<IDisposable> AcquireExclusiveAsync(string mkvPath, CancellationToken cancellationToken = default) =>
        EnterGateAsync(Path.GetFullPath(mkvPath), cancellationToken);

    private static async Task<IDisposable> EnterGateAsync(string path, CancellationToken cancellationToken)
    {
        var gate = Gates.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        return new GateLease(gate);
    }

    private async Task<ContainerInfo> IdentifyAsync(string path, CancellationToken cancellationToken)
    {
        var identify = await RunAsync(tools.MkvMerge, ["-J", path], cancellationToken);
        using var json = JsonDocument.Parse(identify.StandardOutput);
        var root = json.RootElement;
        var properties = root.TryGetProperty("container", out var container) && container.TryGetProperty("properties", out var found) ? found : default;
        long? duration = properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty("duration", out var value) &&
            value.TryGetInt64(out var nanoseconds) ? nanoseconds : null;
        var title = properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty("title", out var titleValue) ? titleValue.GetString() : null;
        var tracks = root.TryGetProperty("tracks", out var trackArray)
            ? trackArray.EnumerateArray().Select(track => track.GetRawText()).ToArray() : [];
        var attachments = root.TryGetProperty("attachments", out var attachmentArray)
            ? attachmentArray.EnumerateArray().Select(attachment => new Attachment(
                attachment.GetProperty("id").GetInt32(), attachment.GetProperty("file_name").GetString() ?? "",
                attachment.TryGetProperty("content_type", out var type) ? type.GetString() ?? "" : "",
                attachment.TryGetProperty("properties", out var attachmentProperties) && attachmentProperties.TryGetProperty("uid", out var uid)
                    ? uid.GetUInt64() : 0)).ToArray()
            : [];
        return new ContainerInfo(duration, tracks, attachments, title);
    }

    /// <summary>One mkvmerge -J plus one mkvextract (tags, chapters and the manifest). Never extracts JPEGs.</summary>
    private async Task<MetadataState> ReadStateAsync(string path, string scratch, CancellationToken cancellationToken)
    {
        var container = await IdentifyAsync(path, cancellationToken);
        var warnings = new List<string>();
        if (container.DurationNanoseconds is null) warnings.Add("The video file does not state its duration.");
        var tagsPath = Path.Combine(scratch, "original-tags.xml");
        var chaptersPath = Path.Combine(scratch, "original-chapters.xml");
        var manifestPath = Path.Combine(scratch, ManifestName);
        var manifests = container.Attachments.Where(attachment => attachment.Name == ManifestName).ToArray();
        if (manifests.Length > 1) warnings.Add("The video has more than one thumbnail manifest; the first was used.");
        List<string> extract = [path, "tags", tagsPath, "chapters", chaptersPath];
        if (manifests.Length > 0) extract.AddRange(["attachments", $"{manifests[0].Id}:{manifestPath}"]);
        await RunAsync(tools.MkvExtract, extract, cancellationToken);
        var tags = LoadXml(tagsPath, "Tags");
        var chapters = LoadXml(chaptersPath, "Chapters");
        var written = MatroskaMetadataXml.Read(tags, chapters, warnings);
        var metadata = written;
        if (metadata.Title.Length == 0 && !string.IsNullOrEmpty(container.Title)) metadata = metadata with { Title = container.Title };
        var manifest = manifests.Length > 0 ? ReadManifest(manifestPath, container, warnings) : [];
        return new MetadataState(path, metadata, written, tags, chapters, File.Exists(tagsPath) ? tagsPath : null,
            File.Exists(chaptersPath) ? chaptersPath : null, container, manifest, warnings);
    }

    private static ThumbnailEntry[] ReadManifest(string manifestPath, ContainerInfo container, List<string> warnings)
    {
        ThumbnailEntry[] entries;
        try { entries = JsonSerializer.Deserialize<ThumbnailEntry[]>(File.ReadAllText(manifestPath)) ?? []; }
        catch (Exception exception) when (exception is JsonException or IOException or NotSupportedException)
        {
            warnings.Add("The embedded thumbnail manifest is unreadable; thumbnails were ignored.");
            return [];
        }
        var valid = new List<ThumbnailEntry>();
        foreach (var entry in entries)
        {
            var positionValid = entry.PositionTicks >= 0 && (container.DurationNanoseconds is not { } duration || entry.PositionTicks < duration / 100);
            if (entry.Id == Guid.Empty || entry.Name != ThumbnailName(entry.Id) || !positionValid || string.IsNullOrEmpty(entry.Sha256) ||
                valid.Any(other => other.Id == entry.Id))
                warnings.Add("An invalid thumbnail manifest entry was ignored.");
            else if (container.Attachments.All(attachment => attachment.Name != entry.Name))
                warnings.Add("An embedded thumbnail listed in the manifest is missing.");
            else if (valid.Count == MaxThumbnails)
                warnings.Add($"Only the first {MaxThumbnails} embedded thumbnails were used.");
            else
                valid.Add(entry);
        }
        return valid.ToArray();
    }

    /// <summary>Extracts every manifest thumbnail missing from the per-video cache in one mkvextract run. Thumbnails that are
    /// missing or fail their hash are left out with a trace warning rather than failing the read.</summary>
    private async Task<IReadOnlyList<VideoThumbnail>> ExtractThumbnailsAsync(string path, Guid videoId, MetadataState state, string scratch,
        CancellationToken cancellationToken)
    {
        var pending = new List<(ThumbnailEntry Entry, string Extracted, string Destination)>();
        foreach (var entry in state.Manifest)
        {
            var destination = ThumbnailCachePath(videoId, entry.Id);
            if (!await HashMatchesAsync(destination, entry.Sha256, cancellationToken))
                pending.Add((entry, Path.Combine(scratch, entry.Id.ToString("N") + ".jpg"), destination));
        }
        var failed = pending.Select(item => item.Entry.Id).ToHashSet();
        if (pending.Count > 0)
        {
            List<string> arguments = [path, "attachments"];
            foreach (var item in pending)
                arguments.Add($"{state.Container.Attachments.First(attachment => attachment.Name == item.Entry.Name).Id}:{item.Extracted}");
            try { await RunAsync(tools.MkvExtract, arguments, cancellationToken); }
            catch (InvalidDataException exception) { Trace.TraceWarning($"{path}: embedded thumbnails could not be extracted. {exception.Message}"); }
            foreach (var item in pending)
            {
                if (await HashMatchesAsync(item.Extracted, item.Entry.Sha256, cancellationToken))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(item.Destination)!);
                    File.Move(item.Extracted, item.Destination, overwrite: true);
                    failed.Remove(item.Entry.Id);
                }
                else Trace.TraceWarning($"{path}: embedded thumbnail {item.Entry.Id:N} failed its integrity check and was ignored.");
            }
        }
        return state.Manifest.Where(entry => !failed.Contains(entry.Id))
            .Select(entry => new VideoThumbnail(entry.Id, TimeSpan.FromTicks(entry.PositionTicks), ThumbnailCachePath(videoId, entry.Id)))
            .ToArray();
    }

    private async Task<WritePlan> PlanWriteAsync(MetadataState original, VideoMetadata metadata, IReadOnlyDictionary<string, string>? extraTags,
        bool keepThumbnails, string scratch, CancellationToken cancellationToken)
    {
        var tags = new XDocument(original.Tags);
        var chapters = new XDocument(original.Chapters);
        MatroskaMetadataXml.Update(tags, chapters, metadata, extraTags);
        var tagsPath = Path.Combine(scratch, "tags.xml");
        var chaptersPath = Path.Combine(scratch, "chapters.xml");
        await File.WriteAllTextAsync(tagsPath, "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" + tags, cancellationToken);
        await File.WriteAllTextAsync(chaptersPath, "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" + chapters, cancellationToken);
        List<string> arguments = ["--tags", "all:" + tagsPath, "--chapters", chapters.Descendants("ChapterAtom").Any() ? chaptersPath : ""];
        arguments.AddRange(metadata.Title.Length > 0 ? ["--edit", "info", "--set", "title=" + metadata.Title] : ["--edit", "info", "--delete", "title"]);
        extraTags ??= new Dictionary<string, string>();
        if (keepThumbnails) return new WritePlan(arguments, original.Manifest, TouchesAttachments: false, extraTags);

        var manifest = new List<ThumbnailEntry>();
        var additions = new List<string>();
        foreach (var thumbnail in metadata.Thumbnails)
        {
            var name = ThumbnailName(thumbnail.Id);
            var image = Path.Combine(scratch, name);
            File.Copy(thumbnail.ImagePath, image, overwrite: true);
            var bytes = await File.ReadAllBytesAsync(image, cancellationToken);
            if (bytes.Length < 4 || bytes[0] != 0xFF || bytes[1] != 0xD8 || bytes[^2] != 0xFF || bytes[^1] != 0xD9)
                throw new InvalidDataException("Selected thumbnails must be JPEG images.");
            manifest.Add(new ThumbnailEntry(thumbnail.Id, thumbnail.Position.Ticks, name, Convert.ToHexString(SHA256.HashData(bytes))));
            additions.AddRange(["--attachment-name", name, "--attachment-mime-type", "image/jpeg", "--add-attachment", image]);
        }
        var owned = OwnedAttachments(original);
        // Text-only edits leave the attachments alone, so they need no snapshot and no rewrite.
        if (manifest.SequenceEqual(original.Manifest) && owned.Length == (manifest.Count == 0 ? 0 : manifest.Count + 1))
            return new WritePlan(arguments, original.Manifest, TouchesAttachments: false, extraTags);
        foreach (var attachment in owned)
            arguments.AddRange(["--delete-attachment", attachment.DeleteSelector]);
        arguments.AddRange(additions);
        if (manifest.Count > 0)
        {
            var manifestPath = Path.Combine(scratch, ManifestName);
            await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest), cancellationToken);
            arguments.AddRange(["--attachment-name", ManifestName, "--attachment-mime-type", "application/json", "--add-attachment", manifestPath]);
        }
        return new WritePlan(arguments, manifest, TouchesAttachments: true, extraTags);
    }

    /// <summary>The manifest and the thumbnails the app attached. The <c>hvl-thumbnail-{guid}.jpg</c> naming is the app's own,
    /// so it also identifies thumbnails orphaned by a broken manifest; foreign attachments are never matched.</summary>
    private static Attachment[] OwnedAttachments(MetadataState state) => state.Container.Attachments.Where(IsOwned).ToArray();

    private static bool IsOwned(Attachment attachment) => attachment.Name == ManifestName || OwnedThumbnailName().IsMatch(attachment.Name);

    private async Task VerifyAsync(string path, MetadataState original, VideoMetadata expected, WritePlan plan, string scratch,
        CancellationToken cancellationToken)
    {
        var verifyScratch = Directory.CreateDirectory(Path.Combine(scratch, "verify-" + Guid.NewGuid().ToString("N"))).FullName;
        var written = await ReadStateAsync(path, verifyScratch, cancellationToken);
        var actual = written.Written;
        if (expected.Title != actual.Title || expected.Description != actual.Description ||
            RecordedDate.ToTagText(expected) != RecordedDate.ToTagText(actual) || !expected.Tags.SequenceEqual(actual.Tags) ||
            expected.Chapters.Count != actual.Chapters.Count)
            throw new InvalidDataException("Written metadata did not round-trip correctly.");
        foreach (var chapter in expected.Chapters)
        {
            var match = actual.Chapters.SingleOrDefault(candidate => candidate.Uid == chapter.Uid);
            if (match is null || chapter.Position != match.Position || chapter.End != match.End || chapter.Title != match.Title || chapter.Description != match.Description ||
                !chapter.Tags.SequenceEqual(match.Tags))
                throw new InvalidDataException($"Chapter \"{chapter.Title}\" did not round-trip correctly.");
        }
        foreach (var (name, value) in plan.ExtraTags)
            if (!MatroskaMetadataXml.GlobalValues(written.Tags, name).SequenceEqual(value.Length > 0 ? [value] : []))
                throw new InvalidDataException($"Written tag {name} did not round-trip correctly.");
        if (!written.Manifest.SequenceEqual(plan.Manifest))
            throw new InvalidDataException("Written thumbnails did not round-trip correctly.");
        if (!original.Container.Tracks.SequenceEqual(written.Container.Tracks) ||
            original.Container.DurationNanoseconds != written.Container.DurationNanoseconds)
            throw new InvalidDataException("Metadata writing unexpectedly changed media track information or duration.");
    }

    private async Task<MediaProcessResult> RunAsync(string tool, IEnumerable<string> arguments, CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        var result = await _process.RunAsync(tool, arguments, timeout ?? ToolTimeout, cancellationToken);
        // mkvtoolnix documents exit code 1 as "completed with warnings".
        var failed = Path.GetFileNameWithoutExtension(tool).StartsWith("mkv", StringComparison.OrdinalIgnoreCase) ? result.ExitCode >= 2 : result.ExitCode != 0;
        if (result.ExitCode != 0)
            Trace.TraceWarning($"{Path.GetFileName(tool)} exited with {result.ExitCode}: {result.StandardError} {result.StandardOutput}");
        if (failed)
        {
            // mkvtoolnix reports errors on stdout.
            var summary = MediaErrors.Summarize(result.StandardError.Length > 0 ? result.StandardError : result.StandardOutput);
            throw new InvalidDataException($"{Path.GetFileNameWithoutExtension(tool)} failed: {summary}");
        }
        return result;
    }

    private string CreateScratch()
    {
        var path = Path.Combine(catalog.Paths.ScratchDirectory, "metadata-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"Could not delete {path}: {exception.Message}");
        }
    }

    private static async Task<bool> HashMatchesAsync(string path, string sha256, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return false;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).Equals(sha256, StringComparison.OrdinalIgnoreCase);
    }

    private static XDocument LoadXml(string path, string root) => File.Exists(path) && new FileInfo(path).Length > 0
        ? XDocument.Load(path) : new XDocument(new XElement(root));

    private static string ThumbnailName(Guid id) => $"hvl-thumbnail-{id:N}.jpg";

    private static VideoThumbnailRef ToRef(ThumbnailEntry entry) =>
        new(entry.Id, TimeSpan.FromTicks(entry.PositionTicks), entry.Name, entry.Sha256);

    private static void Validate(VideoMetadata metadata, TimeSpan duration)
    {
        if (ChapterTiming.Conflict(metadata.Chapters, duration) is { } conflict) throw new ArgumentException(conflict, nameof(metadata));
        if (metadata.Thumbnails.Count > MaxThumbnails) throw new ArgumentException("Choose at most twenty thumbnails.", nameof(metadata));
        if (metadata.Chapters.FirstOrDefault(chapter => chapter.Uid == 0) is { } unidentified)
            throw new ArgumentException($"Chapter \"{unidentified.Title}\" has no ID.", nameof(metadata));
        if (metadata.Chapters.FirstOrDefault(chapter => chapter.Position < TimeSpan.Zero || chapter.Position >= duration) is { } outside)
            throw new ArgumentException($"Chapter \"{outside.Title}\" starts outside the video.", nameof(metadata));
        if (metadata.Chapters.GroupBy(chapter => chapter.Uid).FirstOrDefault(group => group.Count() > 1) is { } duplicate)
            throw new ArgumentException($"Chapter \"{duplicate.First().Title}\" has the same ID as another chapter.", nameof(metadata));
        if (metadata.Thumbnails.Any(thumbnail => thumbnail.Id == Guid.Empty || thumbnail.Position < TimeSpan.Zero || thumbnail.Position >= duration) ||
            metadata.Thumbnails.Select(thumbnail => thumbnail.Id).Distinct().Count() != metadata.Thumbnails.Count)
            throw new ArgumentException("Thumbnail IDs must be unique and positions must fall inside the video.", nameof(metadata));
    }

    [GeneratedRegex("^hvl-thumbnail-[0-9a-f]{32}\\.jpg$")]
    private static partial Regex OwnedThumbnailName();

    private sealed class GateLease(SemaphoreSlim gate) : IDisposable
    {
        private int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) gate.Release();
        }
    }

    private sealed record ThumbnailEntry(Guid Id, long PositionTicks, string Name, string Sha256);
    private sealed record Attachment(int Id, string Name, string ContentType, ulong Uid)
    {
        /// <summary>Selects by UID: positional IDs shift as earlier deletions in the same mkvpropedit run take effect.</summary>
        public string DeleteSelector => "=" + Uid.ToString(CultureInfo.InvariantCulture);
    }
    private sealed record ContainerInfo(long? DurationNanoseconds, string[] Tracks, Attachment[] Attachments, string? Title)
    {
        public string MediaIdentity => MediaIdentityOf(DurationNanoseconds, Tracks);
    }

    /// <summary>What a track's media is, among the properties mkvmerge -J reports. Every one is also covered by
    /// <see cref="VerifyAsync"/>, so metadata writes keep them; the statistics tags' writing date and application are left
    /// out because a remux or an MKVToolNix upgrade rewrites them without touching the media.</summary>
    private static readonly string[] MediaProperties =
    [
        "uid", "codec_id", "codec_private_data", "pixel_dimensions", "display_dimensions", "default_duration", "minimum_timestamp",
        "tag_number_of_frames", "tag_number_of_bytes", "tag_bps",
    ];

    /// <summary>A hash of the duration and each track's <see cref="MediaProperties"/>; see <see cref="VideoTextMetadata.MediaIdentity"/>.</summary>
    internal static string MediaIdentityOf(long? durationNanoseconds, IEnumerable<string> tracks)
    {
        var text = new System.Text.StringBuilder(durationNanoseconds?.ToString(CultureInfo.InvariantCulture) ?? "");
        foreach (var track in tracks)
        {
            using var json = JsonDocument.Parse(track);
            var root = json.RootElement;
            text.Append('\n').Append(Raw(root, "id")).Append(' ').Append(Raw(root, "type"));
            if (!root.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object) continue;
            foreach (var name in MediaProperties) text.Append(' ').Append(name).Append('=').Append(Raw(properties, name));
        }
        return Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text.ToString())), 0, 8);

        static string Raw(JsonElement element, string name) => element.TryGetProperty(name, out var value) ? value.GetRawText() : "";
    }
    /// <param name="Manifest">The thumbnail manifest the file must have after the write.</param>
    /// <param name="TouchesAttachments">Whether the write deletes the owned attachments and adds the planned ones.</param>
    private sealed record WritePlan(IReadOnlyList<string> Arguments, IReadOnlyList<ThumbnailEntry> Manifest, bool TouchesAttachments,
        IReadOnlyDictionary<string, string> ExtraTags);

    /// <param name="Metadata">What the app shows, with the segment title as a fallback for a missing TITLE.</param>
    /// <param name="Written">Exactly what the tags and chapters say; write verification compares against this.</param>
    private sealed record MetadataState(string Path, VideoMetadata Metadata, VideoMetadata Written, XDocument Tags, XDocument Chapters,
        string? TagsPath, string? ChaptersPath, ContainerInfo Container, ThumbnailEntry[] Manifest, IReadOnlyList<string> Warnings);
}
