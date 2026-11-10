using System.Diagnostics;
using HomeVideoLibrary.Core;
using HomeVideoLibrary.Infrastructure;

namespace HomeVideoLibrary.Desktop.ViewModels;

/// <summary>Publishes every successful MKV save to the library model, so search, tags and tapes reflect it at once, and
/// tells the library's identity backfill, when it has one, so it never refills what the save cleared.</summary>
public sealed class IndexedMetadataService(IVideoMetadataService metadata, LibraryModel model, IdentityBackfill? backfill = null)
    : IVideoMetadataService
{
    public event Action<string>? IndexingFailed;
    public Task<VideoMetadata> ReadAsync(LibraryVideo video, CancellationToken cancellationToken = default) => metadata.ReadAsync(video, cancellationToken);
    public Task<VideoThumbnail> CaptureThumbnailAsync(LibraryVideo video, TimeSpan position, CancellationToken cancellationToken = default) => metadata.CaptureThumbnailAsync(video, position, cancellationToken);
    public async Task SaveAsync(LibraryVideo video, VideoMetadata content, CancellationToken cancellationToken = default)
    {
        var before = model.Snapshot.Find(video.Id);
        await metadata.SaveAsync(video, content, cancellationToken);
        if (backfill is not null && before is not null)
        {
            try { await Task.Run(() => backfill.SavedAsync(before, content)); }
            catch (Exception exception) { Trace.TraceWarning($"Could not record a save of {video.RelativePath} for the title and date backfill: {exception}"); }
        }
        // The MKV commit succeeded and must not be reported as unsaved, nor its publish cancelled by the save's token.
        try
        {
            var record = await Task.Run(() => model.UpsertFromFileAsync(video, CancellationToken.None));
            if (record.State != VideoRecordState.Ok) IndexingFailed?.Invoke("Changes saved. Library refresh needed: " + record.Error);
        }
        catch (Exception exception) { IndexingFailed?.Invoke("Changes saved. Library refresh needed: " + exception.Message); }
    }
}
