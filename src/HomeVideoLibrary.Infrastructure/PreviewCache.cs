using System.Diagnostics;
using HomeVideoLibrary.Core;

namespace HomeVideoLibrary.Infrastructure;

/// <summary>The video page's scrub-bar preview frames, captured on demand to
/// <c>.cache/preview/{videoId:N}/{bucket}.jpg</c>. The cache owns these files: it keeps at most
/// <c>capacity</c> of them (least recently used are deleted) and runs at most <c>maxConcurrentCaptures</c>
/// captures at once. A capture that failed is not retried until the video file changes.</summary>
public sealed class PreviewCache
{
    public const int FrameWidth = 416;
    private readonly LibraryPaths _paths;
    private readonly IMatroskaFileEditor _frames;
    private readonly int _capacity;
    private readonly SemaphoreSlim _captures;
    private readonly object _lock = new();
    private readonly LinkedList<(Guid VideoId, int Bucket, string Path)> _recent = new();
    private readonly Dictionary<(Guid, int), LinkedListNode<(Guid VideoId, int Bucket, string Path)>> _entries = [];
    private readonly Dictionary<Guid, (FileStamp? Stamp, string Reason)> _failed = [];

    public PreviewCache(LibraryPaths paths, IMatroskaFileEditor frames, int capacity = 400, int maxConcurrentCaptures = 2)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrentCaptures, 1);
        _paths = paths; _frames = frames; _capacity = capacity; _captures = new SemaphoreSlim(maxConcurrentCaptures);
    }

    public int Count { get { lock (_lock) return _entries.Count; } }

    /// <summary>Returns the frame's JPEG path, capturing it first on a cache miss.</summary>
    /// <param name="fileStamp">The video file's current stamp; a failure is remembered only for this version of the file.</param>
    /// <exception cref="InvalidOperationException">An earlier capture of this version of the file failed.</exception>
    public async Task<string> GetAsync(Guid videoId, FileStamp? fileStamp, string mkvPath, TimeSpan position, int bucket,
        CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue((videoId, bucket), out var node) && File.Exists(node.Value.Path))
            {
                _recent.Remove(node); _recent.AddFirst(node);
                return node.Value.Path;
            }
            if (_failed.TryGetValue(videoId, out var failure) && failure.Stamp == fileStamp)
                throw new InvalidOperationException("No preview is available for this video: " + failure.Reason);
        }
        await _captures.WaitAsync(cancellationToken);
        var directory = Path.Combine(_paths.PreviewDirectory, videoId.ToString("N"));
        var target = Path.Combine(directory, $"{bucket}.jpg");
        var temporary = Path.Combine(directory, $"{bucket}.{Guid.NewGuid():N}.tmp.jpg");
        try
        {
            Directory.CreateDirectory(directory);
            await _frames.CaptureFrameAsync(mkvPath, position, temporary, FrameWidth, cancellationToken);
            File.Move(temporary, target, overwrite: true);
        }
        catch (Exception exception)
        {
            Delete(temporary);
            if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested) throw;
            lock (_lock) _failed[videoId] = (fileStamp, exception.Message);
            throw;
        }
        finally { _captures.Release(); }
        List<string> evicted = [];
        lock (_lock)
        {
            if (_entries.Remove((videoId, bucket), out var stale)) _recent.Remove(stale);
            _entries[(videoId, bucket)] = _recent.AddFirst((videoId, bucket, target));
            while (_entries.Count > _capacity && _recent.Last is { } oldest)
            {
                _recent.RemoveLast();
                _entries.Remove((oldest.Value.VideoId, oldest.Value.Bucket));
                evicted.Add(oldest.Value.Path);
            }
        }
        foreach (var path in evicted) Delete(path);
        return target;
    }

    /// <summary>Forgets a video that has left the library, deleting its frames.</summary>
    public void Forget(Guid videoId)
    {
        lock (_lock)
        {
            foreach (var key in _entries.Keys.Where(key => key.Item1 == videoId).ToArray())
                if (_entries.Remove(key, out var node)) _recent.Remove(node);
            _failed.Remove(videoId);
        }
        var directory = Path.Combine(_paths.PreviewDirectory, videoId.ToString("N"));
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"Could not remove the previews in {directory}: {exception.Message}");
        }
    }

    private static void Delete(string path)
    {
        try { File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"Could not remove preview {path}: {exception.Message}");
        }
    }
}
