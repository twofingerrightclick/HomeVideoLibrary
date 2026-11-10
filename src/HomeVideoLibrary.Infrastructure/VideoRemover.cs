using System.Diagnostics;

namespace HomeVideoLibrary.Infrastructure;

/// <summary>Whatever outside the library model reads a video's file or keeps files made from it (hover strips, scrub
/// previews). Both members are called from a background job, never on the UI thread.</summary>
public interface IVideoHolders
{
    /// <summary>Stops every reader of the video's file, completing once they have let go of it, and keeps new ones off
    /// until the returned hold is disposed.</summary>
    Task<IAsyncDisposable> HoldAsync(Guid videoId, CancellationToken cancellationToken);

    /// <summary>Forgets whatever is cached for a video that has left the library, deleting its files.</summary>
    void Forget(Guid videoId);
}

public enum RemovalResult
{
    Trashed,
    Deleted,
    /// <summary>Only the catalog row was removed; the file was already gone.</summary>
    RemovedFromLibrary,
    /// <summary>The file's drive has no Trash; the video is untouched. It can be deleted permanently instead.</summary>
    Unavailable,
    /// <summary>Another program kept the file open; the video is untouched.</summary>
    Locked,
    /// <summary>The user declined the system's warning that the file would be deleted outright; the video is untouched.</summary>
    Declined,
    /// <summary>The file is gone, so there was nothing to move; the video stays in the library until removed from it.</summary>
    Missing,
    /// <summary>See <see cref="VideoRemoval.Error"/>.</summary>
    Failed,
}

/// <param name="Label">The video's label as the library showed it, for messages.</param>
public sealed record VideoRemoval(Guid VideoId, string Label, RemovalResult Result, string? Error = null)
{
    /// <summary>Whether the video has left the library.</summary>
    public bool IsRemoved => Result is RemovalResult.Trashed or RemovalResult.Deleted or RemovalResult.RemovedFromLibrary;
}

/// <summary>Takes videos out of the library one at a time, in the only safe order: gain exclusive access to the file,
/// move it to the Trash (or delete it), then delete the catalog row. Exclusive access means releasing the
/// <see cref="IVideoHolders"/> and holding the metadata gate (<see cref="MatroskaMetadataService.AcquireExclusiveAsync"/>),
/// so no read, save or capture of the file is in progress; the model's writer is never held meanwhile. A crash after the
/// file moved leaves a row whose file is missing, which <see cref="RemoveMissingAsync"/> removes. The caller must keep the
/// video off any open page.</summary>
public sealed class VideoRemover
{
    // Windows reports a file another program has open as a sharing violation; scanners and indexers let go quickly.
    private static readonly TimeSpan[] LockedRetries = [TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1)];
    private const string UnreadableFile = "Its file could not be checked (no permission, or its drive is not there); nothing was changed.";
    private readonly LibraryModel _model;
    private readonly IFileTrash _trash;
    private readonly IVideoHolders _holders;
    private readonly TimeProvider _time;

    /// <param name="holders">Previews and other readers to release first; none when null.</param>
    public VideoRemover(LibraryModel model, IFileTrash trash, IVideoHolders? holders = null, TimeProvider? time = null)
    {
        _model = model; _trash = trash; _holders = holders ?? NoHolders.Instance; _time = time ?? TimeProvider.System;
    }

    /// <summary>Moves the video's file to the Trash and removes the video from the library. Never throws for a failure
    /// of this one video; cancellation is observed only before the file moves.</summary>
    public Task<VideoRemoval> MoveToTrashAsync(Guid videoId, CancellationToken cancellationToken = default) =>
        RemoveAsync(videoId, path => _trash.MoveToTrash(path) switch
        {
            TrashResult.Trashed => RemovalResult.Trashed,
            TrashResult.Deleted => RemovalResult.Deleted,
            TrashResult.Locked => RemovalResult.Locked,
            TrashResult.Declined => RemovalResult.Declined,
            _ => RemovalResult.Unavailable,
        }, cancellationToken);

    /// <summary>Deletes the video's file for good and removes the video from the library; for drives without a Trash,
    /// after the user agreed. Otherwise like <see cref="MoveToTrashAsync"/>.</summary>
    public Task<VideoRemoval> DeletePermanentlyAsync(Guid videoId, CancellationToken cancellationToken = default) =>
        RemoveAsync(videoId, path =>
        {
            try { File.Delete(path); }
            catch (IOException exception) when (IsSharingViolation(exception)) { return RemovalResult.Locked; }
            return RemovalResult.Deleted;
        }, cancellationToken);

    /// <summary>Removes videos whose files are gone from the library (their rows, albums and caches). A video whose file
    /// is there after all, or cannot be checked, is left alone and reported as <see cref="RemovalResult.Failed"/>, so this
    /// never orphans a file.</summary>
    public async Task<IReadOnlyList<VideoRemoval>> RemoveMissingAsync(IReadOnlyCollection<Guid> videoIds, CancellationToken cancellationToken = default)
    {
        var results = new List<VideoRemoval>();
        foreach (var id in videoIds)
        {
            if (_model.Snapshot.Find(id) is not { } record) continue;
            results.Add(LibraryFiles.Probe(_model.GetVideoPath(record)) switch
            {
                FilePresence.Absent => new VideoRemoval(id, record.Label, RemovalResult.RemovedFromLibrary),
                FilePresence.Present => new VideoRemoval(id, record.Label, RemovalResult.Failed, "Its file is there after all; move it to the Trash instead."),
                _ => new VideoRemoval(id, record.Label, RemovalResult.Failed, UnreadableFile),
            });
        }
        var removed = results.Where(result => result.IsRemoved).Select(result => result.VideoId).ToArray();
        await _model.RemoveVideosAsync(removed, cancellationToken);
        foreach (var id in removed) Forget(id);
        return results;
    }

    private async Task<VideoRemoval> RemoveAsync(Guid videoId, Func<string, RemovalResult> remove, CancellationToken cancellationToken)
    {
        if (_model.Snapshot.Find(videoId) is not { } record)
            return new VideoRemoval(videoId, "", RemovalResult.Failed, "It is no longer in the library.");
        IAsyncDisposable hold;
        try { hold = await _holders.HoldAsync(videoId, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            Trace.TraceWarning($"Could not stop the readers of video {videoId}: {exception}");
            return new VideoRemoval(videoId, record.Label, RemovalResult.Failed, exception.Message);
        }
        // Held until the row is gone, so nothing starts reading the file again in between.
        await using (hold) return await RemoveHeldAsync(record, remove, cancellationToken);
    }

    private async Task<VideoRemoval> RemoveHeldAsync(VideoRecord record, Func<string, RemovalResult> remove, CancellationToken cancellationToken)
    {
        var videoId = record.Id;
        RemovalResult result;
        try
        {
            var path = _model.GetVideoPath(record);
            using (await MatroskaMetadataService.AcquireExclusiveAsync(path, cancellationToken))
            {
                switch (LibraryFiles.Probe(path))
                {
                    case FilePresence.Absent: return new VideoRemoval(videoId, record.Label, RemovalResult.Missing);
                    case FilePresence.Unknown: return new VideoRemoval(videoId, record.Label, RemovalResult.Failed, UnreadableFile);
                }
                result = await RetryWhileLockedAsync(() => remove(path), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            Trace.TraceWarning($"Could not remove video {videoId}: {exception}");
            return new VideoRemoval(videoId, record.Label, RemovalResult.Failed, exception.Message);
        }
        if (result is not (RemovalResult.Trashed or RemovalResult.Deleted)) return new VideoRemoval(videoId, record.Label, result);
        // The file has moved; the row must follow even if the job is being cancelled.
        try { await _model.RemoveVideosAsync([videoId], CancellationToken.None); }
        catch (Exception exception)
        {
            Trace.TraceError($"Video {videoId} left its file behind in the catalog: {exception}");
            return new VideoRemoval(videoId, record.Label, RemovalResult.Failed,
                $"Its file is gone, but the library could not forget it ({exception.Message}). Use Remove from library.");
        }
        Forget(videoId);
        return new VideoRemoval(videoId, record.Label, result);
    }

    private async Task<RemovalResult> RetryWhileLockedAsync(Func<RemovalResult> attempt, CancellationToken cancellationToken)
    {
        var result = attempt();
        foreach (var delay in LockedRetries)
        {
            if (result != RemovalResult.Locked) break;
            await Task.Delay(delay, _time, cancellationToken);
            result = attempt();
        }
        return result;
    }

    /// <summary>Deletes the thumbnails extracted from the video's MKV and tells the holders to forget it (they own their
    /// own caches). These are caches, so a failure is only traced.</summary>
    private void Forget(Guid videoId)
    {
        var cache = Path.Combine(_model.Catalog.Paths.ThumbnailCacheDirectory, videoId.ToString("N"));
        try { if (Directory.Exists(cache)) Directory.Delete(cache, recursive: true); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"Could not delete {cache}: {exception.Message}");
        }
        _holders.Forget(videoId);
    }

    private static bool IsSharingViolation(IOException exception) => (exception.HResult & 0xFFFF) is 32 or 33;

    private sealed class NoHolders : IVideoHolders, IAsyncDisposable
    {
        public static NoHolders Instance { get; } = new();
        public Task<IAsyncDisposable> HoldAsync(Guid videoId, CancellationToken cancellationToken) => Task.FromResult<IAsyncDisposable>(this);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void Forget(Guid videoId) { }
    }
}
