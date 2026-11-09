using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using HomeVideoLibrary.Core;

namespace HomeVideoLibrary.Infrastructure;

public sealed class MatroskaMetadataService(LibraryCatalog catalog, MediaTools tools, MediaProcess? process = null)
    : IVideoMetadataService, IMatroskaFileEditor
{
    private const string ManifestName = "home-video-library.thumbnails.v1.json";
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly MediaProcess _process = process ?? new MediaProcess();

    public async Task<VideoMetadata> ReadAsync(LibraryVideo video, CancellationToken cancellationToken = default)
    {
        var path = catalog.GetVideoPath(video.RelativePath);
        var gate = Gates.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try { return (await ReadStateAsync(path, cancellationToken)).Metadata; }
        finally { gate.Release(); }
    }

    // TODO: read without extracting thumbnail JPEGs and report manifest problems as warnings (chunk 2 of the overhaul).
    public async Task<VideoTextMetadata> ReadTextAsync(string mkvPath, CancellationToken cancellationToken = default)
    {
        var path = Path.GetFullPath(mkvPath);
        var gate = Gates.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var state = await ReadStateAsync(path, cancellationToken);
            var thumbnails = state.Manifest
                .Select(item => new VideoThumbnailRef(item.Id, TimeSpan.FromTicks(item.PositionTicks), item.Name, item.Sha256))
                .ToArray();
            return new VideoTextMetadata(state.Metadata with { Thumbnails = [] }, thumbnails,
                TimeSpan.FromTicks(state.DurationNanoseconds / 100), []);
        }
        finally { gate.Release(); }
    }

    public Task WriteStagedAsync(string mkvPath, VideoMetadata metadata, MetadataWriteOptions? options = null,
        CancellationToken cancellationToken = default) => throw new NotImplementedException("chunk 2");

    public Task<string> ExtractThumbnailAsync(string mkvPath, Guid videoId, VideoThumbnailRef thumbnail,
        CancellationToken cancellationToken = default) => throw new NotImplementedException("chunk 2");

    public Task CaptureFrameAsync(string mkvPath, TimeSpan position, string outputPath, int maxWidth = 960,
        CancellationToken cancellationToken = default) => throw new NotImplementedException("chunk 2");

    public async Task SaveAsync(LibraryVideo video, VideoMetadata metadata, CancellationToken cancellationToken = default)
    {
        Validate(metadata, video.Duration);
        var path = catalog.GetVideoPath(video.RelativePath);
        var gate = Gates.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        string? scratch = null;
        var staging = Path.Combine(catalog.Paths.VideosDirectory, $".{Guid.NewGuid():N}.metadata.mkv");
        try
        {
            scratch = ScratchDirectory();
            var original = await ReadStateAsync(path, cancellationToken);
            MatroskaMetadataXml.Update(original.Tags, original.Chapters, metadata);
            var tagsPath = Path.Combine(scratch, "tags.xml");
            var chaptersPath = Path.Combine(scratch, "chapters.xml");
            await File.WriteAllTextAsync(tagsPath, "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" + original.Tags, cancellationToken);
            await File.WriteAllTextAsync(chaptersPath, "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" + original.Chapters, cancellationToken);
            await CopyAsync(path, staging, cancellationToken);
            var arguments = new List<string> { staging, "--tags", "all:" + tagsPath, "--chapters", original.Chapters.Descendants("ChapterAtom").Any() ? chaptersPath : "",
                "--edit", "info", "--set", "title=" + metadata.Title };
            // Delete only IDs explicitly listed in our versioned manifest, never an external image.
            foreach (var attachment in original.Attachments.Where(attachment => attachment.Name == ManifestName || original.Manifest.Any(item => item.Name == attachment.Name)))
                arguments.AddRange(["--delete-attachment", "=" + attachment.Uid.ToString(CultureInfo.InvariantCulture)]);
            var manifest = new List<ThumbnailEntry>();
            foreach (var thumbnail in metadata.Thumbnails)
            {
                var name = $"hvl-thumbnail-{thumbnail.Id:N}.jpg";
                var image = Path.Combine(scratch, name);
                await CopyAsync(thumbnail.ImagePath, image, cancellationToken);
                var bytes = await File.ReadAllBytesAsync(image, cancellationToken);
                if (bytes.Length < 4 || bytes[0] != 0xFF || bytes[1] != 0xD8 || bytes[^2] != 0xFF || bytes[^1] != 0xD9)
                    throw new InvalidDataException("Selected thumbnails must be JPEG images.");
                manifest.Add(new ThumbnailEntry(thumbnail.Id, thumbnail.Position.Ticks, name, Convert.ToHexString(SHA256.HashData(bytes))));
                arguments.AddRange(["--attachment-name", name, "--attachment-mime-type", "image/jpeg", "--add-attachment", image]);
            }
            if (manifest.Count > 0)
            {
                var manifestPath = Path.Combine(scratch, ManifestName);
                await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest), cancellationToken);
                arguments.AddRange(["--attachment-name", ManifestName, "--attachment-mime-type", "application/json", "--add-attachment", manifestPath]);
            }
            await RunAsync(tools.MkvPropEdit, arguments, cancellationToken);
            var written = await ReadStateAsync(staging, cancellationToken);
            EnsureMatches(metadata, written.Metadata);
            if (!original.Tracks.SequenceEqual(written.Tracks) || original.DurationNanoseconds != written.DurationNanoseconds)
                throw new InvalidDataException("Metadata writing unexpectedly changed media track information or duration.");
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(staging, path, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(staging);
                if (scratch is not null) Directory.Delete(scratch, recursive: true);
            }
            finally { gate.Release(); }
        }
    }

    public async Task<VideoThumbnail> CaptureThumbnailAsync(LibraryVideo video, TimeSpan position, CancellationToken cancellationToken = default)
    {
        if (position < TimeSpan.Zero || position >= video.Duration) throw new ArgumentOutOfRangeException(nameof(position));
        var id = Guid.NewGuid();
        var image = Path.Combine(catalog.Paths.CacheDirectory, "thumbnails", $"{id:N}.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(image)!);
        try
        {
            await RunAsync(tools.Ffmpeg, ["-hide_banner", "-nostdin", "-v", "error", "-n", "-ss", position.TotalSeconds.ToString("R", CultureInfo.InvariantCulture),
                "-i", catalog.GetVideoPath(video.RelativePath), "-frames:v", "1", "-vf", "scale=960:-2", "-q:v", "2", image], cancellationToken);
            if (!File.Exists(image)) throw new InvalidDataException("No video frame was available at that position.");
            return new VideoThumbnail(id, position, image);
        }
        catch { File.Delete(image); throw; }
    }

    private async Task<MetadataState> ReadStateAsync(string path, CancellationToken cancellationToken)
    {
        var scratch = ScratchDirectory();
        try
        {
            var identify = await RunAsync(tools.MkvMerge, ["-J", path], cancellationToken);
            using var json = JsonDocument.Parse(identify.StandardOutput);
            var root = json.RootElement;
            var duration = root.GetProperty("container").GetProperty("properties").GetProperty("duration").GetInt64();
            var tracks = root.GetProperty("tracks").EnumerateArray().Select(track => track.GetRawText()).ToArray();
            var attachments = root.GetProperty("attachments").EnumerateArray().Select(attachment => new Attachment(
                attachment.GetProperty("id").GetInt32(), attachment.GetProperty("file_name").GetString()!,
                attachment.GetProperty("properties").GetProperty("uid").GetUInt64())).ToArray();
            var tagsPath = Path.Combine(scratch, "tags.xml");
            var chaptersPath = Path.Combine(scratch, "chapters.xml");
            await RunAsync(tools.MkvExtract, [path, "tags", tagsPath, "chapters", chaptersPath], cancellationToken);
            var tags = LoadXml(tagsPath, "Tags");
            var chapters = LoadXml(chaptersPath, "Chapters");
            var metadata = MatroskaMetadataXml.Read(tags, chapters);
            if (metadata.Title.Length == 0 && root.GetProperty("container").GetProperty("properties").TryGetProperty("title", out var title))
                metadata = metadata with { Title = title.GetString() ?? "" };
            var manifest = Array.Empty<ThumbnailEntry>();
            var manifestAttachment = attachments.SingleOrDefault(attachment => attachment.Name == ManifestName);
            var thumbnails = new List<VideoThumbnail>();
            if (manifestAttachment is not null)
            {
                var manifestPath = Path.Combine(scratch, ManifestName);
                await RunAsync(tools.MkvExtract, [path, "attachments", $"{manifestAttachment.Id}:{manifestPath}"], cancellationToken);
                manifest = JsonSerializer.Deserialize<ThumbnailEntry[]>(await File.ReadAllTextAsync(manifestPath, cancellationToken)) ?? [];
                if (manifest.Length > 20 || manifest.Select(item => item.Id).Distinct().Count() != manifest.Length)
                    throw new InvalidDataException("The embedded thumbnail manifest is invalid.");
                foreach (var item in manifest)
                {
                    if (item.Id == Guid.Empty || item.Name != $"hvl-thumbnail-{item.Id:N}.jpg" || item.PositionTicks < 0 || item.PositionTicks >= duration / 100)
                        throw new InvalidDataException("The embedded thumbnail manifest has an invalid position or attachment name.");
                    var attachment = attachments.SingleOrDefault(attachment => attachment.Name == item.Name)
                        ?? throw new InvalidDataException("An embedded thumbnail attachment is missing.");
                    var imagePath = Path.Combine(catalog.Paths.CacheDirectory, "thumbnails", item.Id.ToString("N") + ".jpg");
                    Directory.CreateDirectory(Path.GetDirectoryName(imagePath)!);
                    var extracted = Path.Combine(scratch, item.Id.ToString("N") + ".jpg");
                    await RunAsync(tools.MkvExtract, [path, "attachments", $"{attachment.Id}:{extracted}"], cancellationToken);
                    var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(extracted, cancellationToken)));
                    if (hash != item.Sha256) throw new InvalidDataException("An embedded thumbnail failed its integrity check.");
                    File.Move(extracted, imagePath, overwrite: true);
                    thumbnails.Add(new VideoThumbnail(item.Id, TimeSpan.FromTicks(item.PositionTicks), imagePath));
                }
            }
            return new MetadataState(metadata with { Thumbnails = thumbnails }, tags, chapters, attachments, manifest, tracks, duration);
        }
        finally { Directory.Delete(scratch, recursive: true); }
    }

    private async Task<MediaProcessResult> RunAsync(string tool, IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        var result = await _process.RunAsync(tool, arguments, TimeSpan.FromMinutes(10), cancellationToken);
        if (result.ExitCode != 0) throw new InvalidDataException($"{Path.GetFileName(tool)} failed: {result.StandardError} {result.StandardOutput}");
        return result;
    }

    private string ScratchDirectory()
    {
        var path = Path.Combine(catalog.Paths.CacheDirectory, "metadata-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static XDocument LoadXml(string path, string root) => File.Exists(path) && new FileInfo(path).Length > 0
        ? XDocument.Load(path) : new XDocument(new XElement(root));

    private static async Task CopyAsync(string source, string destination, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        await input.CopyToAsync(output, cancellationToken);
    }

    private static void Validate(VideoMetadata metadata, TimeSpan duration)
    {
        if (metadata.Thumbnails.Count > 20) throw new ArgumentException("Choose at most twenty thumbnails.", nameof(metadata));
        if (metadata.Chapters.Any(chapter => chapter.Uid == 0 || chapter.Position < TimeSpan.Zero || chapter.Position >= duration) ||
            metadata.Chapters.Select(chapter => chapter.Uid).Distinct().Count() != metadata.Chapters.Count)
            throw new ArgumentException("Chapter UIDs must be unique and nonzero, and positions must fall inside the video.", nameof(metadata));
        if (metadata.Thumbnails.Any(thumbnail => thumbnail.Id == Guid.Empty || thumbnail.Position < TimeSpan.Zero || thumbnail.Position >= duration) ||
            metadata.Thumbnails.Select(thumbnail => thumbnail.Id).Distinct().Count() != metadata.Thumbnails.Count)
            throw new ArgumentException("Thumbnail IDs must be unique and positions must fall inside the video.", nameof(metadata));
    }

    private static void EnsureMatches(VideoMetadata expected, VideoMetadata actual)
    {
        if (expected.Title != actual.Title || expected.Description != actual.Description || expected.RecordedAt != actual.RecordedAt ||
            !expected.Tags.SequenceEqual(actual.Tags) || expected.Chapters.Count != actual.Chapters.Count || expected.Thumbnails.Count != actual.Thumbnails.Count)
            throw new InvalidDataException("Written metadata did not round-trip correctly.");
        foreach (var chapter in expected.Chapters)
        {
            var written = actual.Chapters.SingleOrDefault(candidate => candidate.Uid == chapter.Uid);
            if (written is null || chapter.Position != written.Position || chapter.Title != written.Title || chapter.Description != written.Description || !chapter.Tags.SequenceEqual(written.Tags))
                throw new InvalidDataException("Written chapter metadata did not round-trip correctly.");
        }
        foreach (var thumbnail in expected.Thumbnails)
            if (!actual.Thumbnails.Any(candidate => candidate.Id == thumbnail.Id && candidate.Position == thumbnail.Position))
                throw new InvalidDataException("Written thumbnails did not round-trip correctly.");
    }

    private sealed record ThumbnailEntry(Guid Id, long PositionTicks, string Name, string Sha256);
    private sealed record Attachment(int Id, string Name, ulong Uid);
    private sealed record MetadataState(VideoMetadata Metadata, XDocument Tags, XDocument Chapters, Attachment[] Attachments,
        ThumbnailEntry[] Manifest, string[] Tracks, long DurationNanoseconds);
}
