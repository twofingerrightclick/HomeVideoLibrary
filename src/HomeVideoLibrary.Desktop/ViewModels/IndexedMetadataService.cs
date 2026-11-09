using HomeVideoLibrary.Core;
using HomeVideoLibrary.Infrastructure;

namespace HomeVideoLibrary.Desktop.ViewModels;

/// <summary>Publishes every successful MKV save to the library model, so search, tags and tapes reflect it at once.</summary>
public sealed class IndexedMetadataService(IVideoMetadataService metadata, LibraryModel model) : IVideoMetadataService
{
    public event Action<string>? IndexingFailed;
    public Task<VideoMetadata> ReadAsync(LibraryVideo video, CancellationToken cancellationToken = default) => metadata.ReadAsync(video, cancellationToken);
    public Task<VideoThumbnail> CaptureThumbnailAsync(LibraryVideo video, TimeSpan position, CancellationToken cancellationToken = default) => metadata.CaptureThumbnailAsync(video, position, cancellationToken);
    public async Task SaveAsync(LibraryVideo video, VideoMetadata content, CancellationToken cancellationToken = default)
    {
        await metadata.SaveAsync(video, content, cancellationToken);
        // The MKV commit succeeded and must not be reported as unsaved, nor its publish cancelled by the save's token.
        try
        {
            var record = await Task.Run(() => model.UpsertFromFileAsync(video, CancellationToken.None));
            if (record.State != VideoRecordState.Ok) IndexingFailed?.Invoke("Changes saved. Library refresh needed: " + record.Error);
        }
        catch (Exception exception) { IndexingFailed?.Invoke("Changes saved. Library refresh needed: " + exception.Message); }
    }
}
