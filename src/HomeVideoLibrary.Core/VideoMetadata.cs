namespace HomeVideoLibrary.Core;

public sealed record VideoChapter(ulong Uid, TimeSpan Position, string Title, string Description, IReadOnlyList<string> Tags);

public sealed record VideoThumbnail(Guid Id, TimeSpan Position, string ImagePath);

public sealed record VideoMetadata(string Title, string Description, IReadOnlyList<string> Tags,
    DateTimeOffset? RecordedAt, IReadOnlyList<VideoChapter> Chapters, IReadOnlyList<VideoThumbnail> Thumbnails)
{
    public static VideoMetadata Empty { get; } = new("", "", [], null, [], []);

    /// <summary>The raw DATE_RECORDED value as read, which may be partial (e.g. "1997").
    /// Writers set <see cref="RecordedAt"/>; when the two disagree, <see cref="RecordedAt"/> wins.</summary>
    public string RecordedAtText { get; init; } = "";
}

/// <summary>An embedded thumbnail known from the manifest, without its JPEG having been extracted.</summary>
public sealed record VideoThumbnailRef(Guid Id, TimeSpan Position, string AttachmentName, string Sha256);

/// <param name="Metadata">Text metadata; its <see cref="VideoMetadata.Thumbnails"/> is always empty.</param>
public sealed record VideoTextMetadata(VideoMetadata Metadata, IReadOnlyList<VideoThumbnailRef> Thumbnails,
    TimeSpan Duration, IReadOnlyList<string> Warnings)
{
    /// <summary>Identifies the file's media (its tracks and duration), not its metadata: saving metadata leaves it
    /// unchanged, and replacing the media changes it. Null when the reader cannot tell.</summary>
    public string? MediaIdentity { get; init; }
}

public sealed record MetadataWriteOptions
{
    /// <summary>Global (level 50) tags written beside the owned ones, replacing same-named existing values.</summary>
    public IReadOnlyDictionary<string, string> ExtraGlobalTags { get; init; } = new Dictionary<string, string>();

    /// <summary>When true, the metadata's thumbnails are ignored and existing HVL attachments are left untouched.</summary>
    public bool KeepExistingThumbnails { get; init; }
}

public interface IVideoTextMetadataReader
{
    /// <summary>Reads text metadata and thumbnail refs without extracting any JPEG. Takes the per-path gate.
    /// Thumbnail or manifest problems are reported in <see cref="VideoTextMetadata.Warnings"/>, never thrown.</summary>
    Task<VideoTextMetadata> ReadTextAsync(string mkvPath, CancellationToken cancellationToken = default);
}

public interface IMatroskaFileEditor : IVideoTextMetadataReader
{
    /// <summary>Writes metadata in place to a file the caller owns exclusively, such as a staged import or edit output,
    /// and verifies the round trip. There is no rollback: on failure the caller deletes the file.</summary>
    Task WriteStagedAsync(string mkvPath, VideoMetadata metadata, MetadataWriteOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>Extracts the thumbnail once to <c>.cache/thumbnails/{videoId:N}/{thumbnail.Id:N}.jpg</c>, verifies its
    /// SHA-256 and returns the path. A cache hit starts no process.</summary>
    Task<string> ExtractThumbnailAsync(string mkvPath, Guid videoId, VideoThumbnailRef thumbnail,
        CancellationToken cancellationToken = default);

    /// <summary>Writes one square-pixel JPEG of the frame at <paramref name="position"/>, deinterlaced when needed,
    /// overwriting <paramref name="outputPath"/>.</summary>
    Task CaptureFrameAsync(string mkvPath, TimeSpan position, string outputPath, int maxWidth = 960,
        CancellationToken cancellationToken = default);
}

public interface IVideoMetadataService
{
    Task<VideoMetadata> ReadAsync(LibraryVideo video, CancellationToken cancellationToken = default);
    Task SaveAsync(LibraryVideo video, VideoMetadata metadata, CancellationToken cancellationToken = default);
    Task<VideoThumbnail> CaptureThumbnailAsync(LibraryVideo video, TimeSpan position, CancellationToken cancellationToken = default);
}
