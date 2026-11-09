using HomeVideoLibrary.Core;
using HomeVideoLibrary.Infrastructure;

namespace HomeVideoLibrary.Desktop.ViewModels;

/// <summary>Keeps the disposable search projection current after an explicit MKV save.</summary>
public sealed class IndexedMetadataService(IVideoMetadataService metadata, LibrarySearchIndex index) : IVideoMetadataService
{
    public event Action<string>? IndexingFailed;
    public Task<VideoMetadata> ReadAsync(LibraryVideo video, CancellationToken cancellationToken = default) => metadata.ReadAsync(video, cancellationToken);
    public Task<VideoThumbnail> CaptureThumbnailAsync(LibraryVideo video, TimeSpan position, CancellationToken cancellationToken = default) => metadata.CaptureThumbnailAsync(video, position, cancellationToken);
    public async Task SaveAsync(LibraryVideo video, VideoMetadata content, CancellationToken cancellationToken = default)
    {
        await metadata.SaveAsync(video, content, cancellationToken);
        // The MKV commit succeeded. An index failure must not misreport it as an unsaved edit.
        try { await Task.Run(() => index.UpdateVideoAsync(video, cancellationToken: cancellationToken)); }
        catch (Exception exception) { IndexingFailed?.Invoke("Changes saved. Search refresh needed: " + exception.Message); }
    }
}
