using System.Text.Json;
using HomeVideoLibrary.Core;

namespace HomeVideoLibrary.Infrastructure;

public enum VideoRecordState
{
    /// <summary>The MKV metadata has not been read for the current file. Content, if any, is from an older read.</summary>
    Pending,
    /// <summary>The content was read from the file at <see cref="VideoRecord.Stamp"/>.</summary>
    Ok,
    /// <summary>Reading the file at <see cref="VideoRecord.Stamp"/> failed; see <see cref="VideoRecord.Error"/>. Content,
    /// if any, is from an older read. It is read again once the file changes.</summary>
    Unreadable,
}

/// <param name="End">Explicit range end, equal to Start for a moment.</param>
public sealed record VideoChapterRecord(ulong Uid, TimeSpan Start, TimeSpan End, string Title, string Description, IReadOnlyList<string> Tags);

/// <param name="ModifiedTicks">Last write time in UTC ticks.</param>
public readonly record struct FileStamp(long Length, long ModifiedTicks)
{
    public static FileStamp? TryRead(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? new FileStamp(info.Length, info.LastWriteTimeUtc.Ticks) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return null; }
    }
}

/// <summary>An immutable in-memory view of one library video: catalog membership fields plus the MKV content
/// projection (title, description, tags, chapters, thumbnail refs), which is only ever a cache of the MKV.</summary>
public sealed record VideoRecord(Guid Id, string RelativePath, string DisplayName, TimeSpan Duration, DateTimeOffset ImportedAt,
    DateTimeOffset CatalogRecordedAt)
{
    public string? SourceFingerprint { get; init; }
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public IReadOnlyList<string> Tags { get; init; } = [];
    /// <summary>DATE_RECORDED from the MKV, when it parsed to an instant.</summary>
    public DateTimeOffset? EmbeddedRecordedAt { get; init; }
    public string RecordedAtText { get; init; } = "";
    /// <summary>Sorted by start.</summary>
    public IReadOnlyList<VideoChapterRecord> Chapters { get; init; } = [];
    public IReadOnlyList<VideoThumbnailRef> Thumbnails { get; init; } = [];
    /// <summary>See <see cref="VideoTextMetadata.MediaIdentity"/>; unlike <see cref="Stamp"/>, a metadata save keeps it.</summary>
    public string? MediaIdentity { get; init; }
    /// <summary>The file stamp of the last read attempt (see <see cref="State"/>); null when none counts.</summary>
    public FileStamp? Stamp { get; init; }
    /// <summary>The outcome of the last read attempt.</summary>
    public VideoRecordState State { get; init; }
    public string? Error { get; init; }
    /// <summary>The file was not found at the last check. Independent of <see cref="State"/>: content stays labelled and
    /// searchable, and a file that comes back unchanged needs no new read.</summary>
    public bool IsMissing { get; init; }

    /// <summary>The instant used for ordering, in UTC: the embedded date when present, else the import-time date.</summary>
    public DateTimeOffset RecordedAt => (EmbeddedRecordedAt ?? CatalogRecordedAt).ToUniversalTime();

    public string Label => !string.IsNullOrWhiteSpace(Title) ? Title : DisplayName;

    /// <summary>When the record's content last changed, for tag recency: the file's write time, else its import time.</summary>
    public DateTimeOffset LastModifiedUtc => Stamp is { } stamp ? new DateTimeOffset(stamp.ModifiedTicks, TimeSpan.Zero) : ImportedAt.ToUniversalTime();

    public static VideoRecord FromCatalog(LibraryVideo video) =>
        new(video.Id, video.RelativePath, video.DisplayName, video.Duration, video.ImportedAt, video.RecordedAt)
        {
            SourceFingerprint = video.SourceFingerprint,
            Title = video.Title,
            Description = video.Description,
        };

    public LibraryVideo ToLibraryVideo() =>
        new(Id, RelativePath, DisplayName, Duration, ImportedAt, RecordedAt)
        {
            Title = Title,
            Description = Description,
            SourceFingerprint = SourceFingerprint,
        };

    /// <summary>The text metadata read from the current file, without thumbnail images. Null unless <see cref="State"/> is Ok.</summary>
    public VideoMetadata? ToMetadata() => State != VideoRecordState.Ok ? null :
        new VideoMetadata(Title, Description, Tags, EmbeddedRecordedAt,
            Chapters.Select(chapter => new VideoChapter(chapter.Uid, chapter.Start, chapter.Title, chapter.Description, chapter.Tags) { End = chapter.End }).ToArray(), [])
        {
            RecordedAtText = RecordedAtText,
        };

    public VideoRecord WithContent(VideoTextMetadata text, FileStamp stamp)
    {
        var metadata = text.Metadata;
        var duration = text.Duration > TimeSpan.Zero ? text.Duration : Duration;
        return this with
        {
            Duration = duration,
            Title = metadata.Title,
            Description = metadata.Description,
            Tags = metadata.Tags.ToArray(),
            EmbeddedRecordedAt = metadata.RecordedAt,
            RecordedAtText = metadata.RecordedAtText,
            Chapters = BuildChapters(metadata.Chapters.Select(chapter =>
                (chapter.Uid, chapter.Position, chapter.End, chapter.Title, chapter.Description, chapter.Tags)), duration),
            Thumbnails = text.Thumbnails.OrderBy(thumbnail => thumbnail.Position).ToArray(),
            MediaIdentity = text.MediaIdentity,
            Stamp = stamp,
            State = VideoRecordState.Ok,
            Error = null,
            IsMissing = false,
        };
    }

    private static VideoChapterRecord[] BuildChapters(
        IEnumerable<(ulong Uid, TimeSpan Start, TimeSpan End, string Title, string Description, IReadOnlyList<string> Tags)> chapters, TimeSpan duration)
    {
        var sorted = chapters.OrderBy(chapter => chapter.Start).ThenBy(chapter => chapter.Uid).ToArray();
        return sorted.Select((chapter, index) => new VideoChapterRecord(chapter.Uid, chapter.Start,
            chapter.End,
            chapter.Title, chapter.Description, chapter.Tags.ToArray())).ToArray();
    }

    internal string ToCacheJson() => JsonSerializer.Serialize(new CachedContent(CachedContent.CurrentVersion, Duration.Ticks, Title, Description,
        Tags, EmbeddedRecordedAt, RecordedAtText,
        Chapters.Select(chapter => new CachedChapter(chapter.Uid, chapter.Start.Ticks, chapter.End.Ticks, chapter.Title, chapter.Description, chapter.Tags)).ToArray(),
        Thumbnails.Select(thumbnail => new CachedThumbnail(thumbnail.Id, thumbnail.Position.Ticks, thumbnail.AttachmentName, thumbnail.Sha256)).ToArray(),
        MediaIdentity));

    /// <summary>Applies cached content; returns null when the cache entry is unreadable or from another format version. The
    /// record is <see cref="VideoRecordState.Pending"/> when the entry lacks a <see cref="MediaIdentity"/>.</summary>
    internal VideoRecord? WithCachedContent(CatalogCacheEntry entry)
    {
        CachedContent? content;
        try { content = JsonSerializer.Deserialize<CachedContent>(entry.RecordJson); }
        catch (JsonException) { return null; }
        if (content is null || content.Version != CachedContent.CurrentVersion) return null;
        var duration = content.DurationTicks > 0 ? TimeSpan.FromTicks(content.DurationTicks) : Duration;
        return this with
        {
            Duration = duration,
            Title = content.Title ?? "",
            Description = content.Description ?? "",
            Tags = content.Tags ?? [],
            EmbeddedRecordedAt = content.RecordedAt,
            RecordedAtText = content.RecordedAtText ?? "",
            Chapters = BuildChapters((content.Chapters ?? []).Select(chapter => (chapter.Uid, TimeSpan.FromTicks(chapter.StartTicks), TimeSpan.FromTicks(chapter.EndTicks),
                chapter.Title ?? "", chapter.Description ?? "", (IReadOnlyList<string>)(chapter.Tags ?? []))), duration),
            Thumbnails = (content.Thumbnails ?? []).Select(thumbnail => new VideoThumbnailRef(thumbnail.Id, TimeSpan.FromTicks(thumbnail.PositionTicks),
                thumbnail.AttachmentName ?? "", thumbnail.Sha256 ?? "")).OrderBy(thumbnail => thumbnail.Position).ToArray(),
            MediaIdentity = content.MediaIdentity,
            Stamp = new FileStamp(entry.FileLength, entry.ModifiedTicks),
            // An entry written before media identities existed shows its content until the file is read again.
            State = content.MediaIdentity is null ? VideoRecordState.Pending : VideoRecordState.Ok,
            Error = null,
        };
    }

    private sealed record CachedContent(int Version, long DurationTicks, string? Title, string? Description, IReadOnlyList<string>? Tags,
        DateTimeOffset? RecordedAt, string? RecordedAtText, IReadOnlyList<CachedChapter>? Chapters, IReadOnlyList<CachedThumbnail>? Thumbnails,
        string? MediaIdentity)
    {
        // Version 2 stores explicit ends. Re-read version 1 from MKV rather than retaining inferred ranges.
        public const int CurrentVersion = 2;
    }

    private sealed record CachedChapter(ulong Uid, long StartTicks, long EndTicks, string? Title, string? Description, IReadOnlyList<string>? Tags);
    private sealed record CachedThumbnail(Guid Id, long PositionTicks, string? AttachmentName, string? Sha256);
}
