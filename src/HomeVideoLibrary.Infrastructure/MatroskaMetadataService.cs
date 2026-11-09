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
    : IVideoMetadataService, IMatroskaFileEditor
{
    private const string ManifestName = "home-video-library.thumbnails.v1.json";
    private const int MaxThumbnails = 20;
    private static readonly TimeSpan ToolTimeout = TimeSpan.FromMinutes(10);
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
        finally { DeleteScratch(scratch); }
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
            return new VideoTextMetadata(state.Metadata, state.Manifest.Select(ToRef).ToArray(), duration, state.Warnings);
        }
        finally { DeleteScratch(scratch); }
    }

    public async Task SaveAsync(LibraryVideo video, VideoMetadata metadata, CancellationToken cancellationToken = default)
    {
        if (metadata.Thumbnails.Count > MaxThumbnails) throw new ArgumentException("Choose at most twenty thumbnails.", nameof(metadata));
        metadata = MetadataText.Normalize(metadata);
        var path = catalog.GetVideoPath(video.RelativePath);
        using var _ = await EnterGateAsync(path, cancellationToken);
        var scratch = CreateScratch();
        try
        {
            var original = await ReadStateAsync(path, scratch, cancellationToken);
            Validate(metadata, original.Container.DurationNanoseconds is { } nanoseconds ? TimeSpan.FromTicks(nanoseconds / 100) : video.Duration);
            var plan = await PlanWriteAsync(original, metadata, null, keepThumbnails: false, scratch, cancellationToken);
            var snapshot = await SnapshotAsync(path, original, plan, scratch, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            // Past this point the file is being modified: finish (sub-second) or roll back, but never stop half way.
            var result = await RunAsync(tools.MkvPropEdit, [path, .. plan.Arguments], CancellationToken.None, throwOnError: false);
            if (result.ExitCode >= 2)
            {
                Trace.TraceWarning($"In-place metadata write refused for {path}; retrying on a copy. {result.StandardError} {result.StandardOutput}");
                // A refusal normally happens before anything is written; restore anyway in case it did not.
                try { await RollBackAsync(path, snapshot, scratch); }
                catch (InvalidDataException) { }
                await WriteThroughCopyAsync(path, original, metadata, plan, scratch);
                return;
            }
            try
            {
                if (AfterWriteForTesting is { } hook) await hook(path);
                await VerifyAsync(path, original, metadata, plan, scratch, CancellationToken.None);
            }
            catch (Exception exception)
            {
                Trace.TraceError($"Metadata write to {path} failed verification; rolling back. {exception}");
                await RollBackAsync(path, snapshot, scratch);
                throw;
            }
        }
        finally { DeleteScratch(scratch); }
    }

    public async Task WriteStagedAsync(string mkvPath, VideoMetadata metadata, MetadataWriteOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new MetadataWriteOptions();
        if (!options.KeepExistingThumbnails && metadata.Thumbnails.Count > MaxThumbnails)
            throw new ArgumentException("Choose at most twenty thumbnails.", nameof(metadata));
        metadata = MetadataText.Normalize(metadata);
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
            await RunAsync(tools.MkvPropEdit, [path, .. plan.Arguments], cancellationToken);
            if (AfterWriteForTesting is { } hook) await hook(path);
            await VerifyAsync(path, original, metadata, plan, scratch, cancellationToken);
        }
        finally { DeleteScratch(scratch); }
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
        finally { DeleteScratch(scratch); }
    }

    public async Task CaptureFrameAsync(string mkvPath, TimeSpan position, string outputPath, int maxWidth = 960,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(position, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxWidth, 2);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        File.Delete(outputPath);
        // Display width from the sample aspect ratio, then square pixels, so anamorphic (e.g. 720x480 8:9) sources keep
        // their shape in viewers that ignore JFIF density. yadif touches only frames the decoder flags as interlaced.
        var filter = string.Create(CultureInfo.InvariantCulture,
            $"yadif=deint=interlaced,scale=w='min({maxWidth / 2 * 2},trunc(iw*sar/2)*2)':h='trunc(ow/dar/2)*2',setsar=1");
        try
        {
            await RunAsync(tools.Ffmpeg, ["-hide_banner", "-nostdin", "-v", "error", "-y",
                "-ss", position.TotalSeconds.ToString("0.######", CultureInfo.InvariantCulture), "-i", mkvPath,
                "-frames:v", "1", "-vf", filter, "-q:v", "2", outputPath], cancellationToken);
            if (!File.Exists(outputPath)) throw new InvalidDataException("No video frame was available at that position.");
        }
        catch
        {
            TryDelete(outputPath);
            throw;
        }
    }

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
        DateTimeOffset? dateUtc = properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty("date_utc", out var dateValue) &&
            DateTimeOffset.TryParse(dateValue.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date : null;
        var tracks = root.TryGetProperty("tracks", out var trackArray)
            ? trackArray.EnumerateArray().Select(track => track.GetRawText()).ToArray() : [];
        var attachments = root.TryGetProperty("attachments", out var attachmentArray)
            ? attachmentArray.EnumerateArray().Select(attachment => new Attachment(
                attachment.GetProperty("id").GetInt32(), attachment.GetProperty("file_name").GetString() ?? "",
                attachment.TryGetProperty("content_type", out var type) ? type.GetString() ?? "" : "",
                attachment.TryGetProperty("properties", out var attachmentProperties) && attachmentProperties.TryGetProperty("uid", out var uid)
                    ? uid.GetUInt64() : 0)).ToArray()
            : [];
        return new ContainerInfo(duration, tracks, attachments, title, dateUtc);
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
        if (metadata.RecordedAt is null && container.DateUtc is { } dateUtc) metadata = metadata with { RecordedAt = dateUtc };
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
        if (keepThumbnails) return new WritePlan(arguments, original.Manifest, [], extraTags ?? new Dictionary<string, string>());

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
            return new WritePlan(arguments, original.Manifest, [], extraTags ?? new Dictionary<string, string>());
        foreach (var attachment in owned)
            arguments.AddRange(["--delete-attachment", attachment.DeleteSelector]);
        arguments.AddRange(additions);
        if (manifest.Count > 0)
        {
            var manifestPath = Path.Combine(scratch, ManifestName);
            await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest), cancellationToken);
            arguments.AddRange(["--attachment-name", ManifestName, "--attachment-mime-type", "application/json", "--add-attachment", manifestPath]);
        }
        return new WritePlan(arguments, manifest, owned, extraTags ?? new Dictionary<string, string>());
    }

    /// <summary>The manifest and the thumbnails the app attached. The <c>hvl-thumbnail-{guid}.jpg</c> naming is the app's own,
    /// so it also identifies thumbnails orphaned by a broken manifest; foreign attachments are never matched.</summary>
    private static Attachment[] OwnedAttachments(MetadataState state) => state.Container.Attachments
        .Where(attachment => attachment.Name == ManifestName || OwnedThumbnailName().IsMatch(attachment.Name)).ToArray();

    private async Task<Snapshot> SnapshotAsync(string path, MetadataState original, WritePlan plan, string scratch, CancellationToken cancellationToken)
    {
        var directory = Directory.CreateDirectory(Path.Combine(scratch, "snapshot")).FullName;
        var attachments = plan.DeletedAttachments
            .Select(attachment => (attachment, File: Path.Combine(directory, attachment.Id.ToString(CultureInfo.InvariantCulture))))
            .ToArray();
        if (attachments.Length > 0)
            await RunAsync(tools.MkvExtract, [path, "attachments", .. attachments.Select(item => $"{item.attachment.Id}:{item.File}")], cancellationToken);
        return new Snapshot(original.TagsPath, original.ChaptersPath, original.Container.Title,
            attachments.Select(item => new SavedAttachment(item.attachment.Name, item.attachment.ContentType, item.File)).ToArray());
    }

    /// <summary>Restores the snapshot in place. Runs to completion regardless of cancellation; a failure is reported with both errors.</summary>
    private async Task RollBackAsync(string path, Snapshot snapshot, string scratch)
    {
        try
        {
            var emptyTags = Path.Combine(scratch, "empty-tags.xml");
            await File.WriteAllTextAsync(emptyTags, "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<Tags />");
            List<string> arguments = [path, "--tags", "all:" + (snapshot.TagsPath ?? emptyTags), "--chapters", snapshot.ChaptersPath ?? ""];
            arguments.AddRange(string.IsNullOrEmpty(snapshot.Title)
                ? ["--edit", "info", "--delete", "title"] : ["--edit", "info", "--set", "title=" + snapshot.Title]);
            if (snapshot.Attachments.Count > 0)
            {
                var current = await IdentifyAsync(path, CancellationToken.None);
                foreach (var attachment in current.Attachments.Where(attachment => attachment.Name == ManifestName || OwnedThumbnailName().IsMatch(attachment.Name)))
                    arguments.AddRange(["--delete-attachment", attachment.DeleteSelector]);
                foreach (var attachment in snapshot.Attachments)
                    arguments.AddRange(["--attachment-name", attachment.Name, "--attachment-mime-type",
                        attachment.ContentType.Length > 0 ? attachment.ContentType : "application/octet-stream", "--add-attachment", attachment.File]);
            }
            await RunAsync(tools.MkvPropEdit, arguments, CancellationToken.None);
        }
        catch (Exception exception)
        {
            Trace.TraceError($"Rolling back metadata of {path} failed: {exception}");
            throw new InvalidDataException("Saving metadata failed and the previous metadata could not be fully restored.", exception);
        }
    }

    /// <summary>Fallback when mkvpropedit refuses the file in place: write a clone (cheap on APFS/btrfs), verify, then swap.</summary>
    private async Task WriteThroughCopyAsync(string path, MetadataState original, VideoMetadata metadata, WritePlan plan, string scratch)
    {
        var staged = Path.Combine(catalog.Paths.ScratchDirectory, $"metadata-{Guid.NewGuid():N}.mkv");
        try
        {
            File.Copy(path, staged);
            await RunAsync(tools.MkvPropEdit, [staged, .. plan.Arguments], CancellationToken.None);
            await VerifyAsync(staged, original, metadata, plan, scratch, CancellationToken.None);
            File.Move(staged, path, overwrite: true);
        }
        finally { TryDelete(staged); }
    }

    private async Task VerifyAsync(string path, MetadataState original, VideoMetadata expected, WritePlan plan, string scratch,
        CancellationToken cancellationToken)
    {
        var verifyScratch = Directory.CreateDirectory(Path.Combine(scratch, "verify-" + Guid.NewGuid().ToString("N"))).FullName;
        var written = await ReadStateAsync(path, verifyScratch, cancellationToken);
        var actual = written.Written;
        if (expected.Title != actual.Title || expected.Description != actual.Description ||
            RecordedDate.ToTagText(expected) != actual.RecordedAtText || !expected.Tags.SequenceEqual(actual.Tags) ||
            expected.Chapters.Count != actual.Chapters.Count)
            throw new InvalidDataException("Written metadata did not round-trip correctly.");
        foreach (var chapter in expected.Chapters)
        {
            var match = actual.Chapters.SingleOrDefault(candidate => candidate.Uid == chapter.Uid);
            if (match is null || chapter.Position != match.Position || chapter.Title != match.Title || chapter.Description != match.Description ||
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
        bool throwOnError = true)
    {
        var result = await _process.RunAsync(tool, arguments, ToolTimeout, cancellationToken);
        // mkvtoolnix documents exit code 1 as "completed with warnings".
        var failed = Path.GetFileNameWithoutExtension(tool).StartsWith("mkv", StringComparison.OrdinalIgnoreCase) ? result.ExitCode >= 2 : result.ExitCode != 0;
        if (result.ExitCode != 0)
            Trace.TraceWarning($"{Path.GetFileName(tool)} exited with {result.ExitCode}: {result.StandardError} {result.StandardOutput}");
        if (failed && throwOnError)
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

    private static void DeleteScratch(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"Could not delete metadata scratch {path}: {exception.Message}");
        }
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
    private sealed record ContainerInfo(long? DurationNanoseconds, string[] Tracks, Attachment[] Attachments, string? Title, DateTimeOffset? DateUtc);
    private sealed record SavedAttachment(string Name, string ContentType, string File);
    private sealed record Snapshot(string? TagsPath, string? ChaptersPath, string? Title, IReadOnlyList<SavedAttachment> Attachments);
    private sealed record WritePlan(IReadOnlyList<string> Arguments, IReadOnlyList<ThumbnailEntry> Manifest,
        IReadOnlyList<Attachment> DeletedAttachments, IReadOnlyDictionary<string, string> ExtraTags);

    /// <param name="Metadata">What the app shows, with container fallbacks for title and recorded date.</param>
    /// <param name="Written">Exactly what the tags and chapters say; write verification compares against this.</param>
    private sealed record MetadataState(string Path, VideoMetadata Metadata, VideoMetadata Written, XDocument Tags, XDocument Chapters,
        string? TagsPath, string? ChaptersPath, ContainerInfo Container, ThumbnailEntry[] Manifest, IReadOnlyList<string> Warnings);
}
