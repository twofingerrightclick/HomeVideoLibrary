namespace HomeVideoLibrary.Core;

public sealed record VideoChapter(ulong Uid, TimeSpan Position, string Title, string Description, IReadOnlyList<string> Tags);

public sealed record VideoThumbnail(Guid Id, TimeSpan Position, string ImagePath);

public sealed record VideoMetadata(string Title, string Description, IReadOnlyList<string> Tags,
    DateTimeOffset? RecordedAt, IReadOnlyList<VideoChapter> Chapters, IReadOnlyList<VideoThumbnail> Thumbnails)
{
    public static VideoMetadata Empty { get; } = new("", "", [], null, [], []);
}

public interface IVideoMetadataService
{
    Task<VideoMetadata> ReadAsync(LibraryVideo video, CancellationToken cancellationToken = default);
    Task SaveAsync(LibraryVideo video, VideoMetadata metadata, CancellationToken cancellationToken = default);
    Task<VideoThumbnail> CaptureThumbnailAsync(LibraryVideo video, TimeSpan position, CancellationToken cancellationToken = default);
}
