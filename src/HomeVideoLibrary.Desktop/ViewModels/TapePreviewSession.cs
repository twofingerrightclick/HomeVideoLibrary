using HomeVideoLibrary.Core;

namespace HomeVideoLibrary.Desktop.ViewModels;

/// <summary>A short-lived preview cache that owns only its newly captured, disposable files.</summary>
public sealed class TapePreviewSession(IVideoMetadataService service, LibraryVideo video) : IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<int, VideoThumbnail> _frames = [];
    private readonly HashSet<string> _ownedCaptures = [];
    private readonly object _ownershipLock = new();
    private bool _disposed;
    private VideoMetadata? _metadata;
    private readonly SemaphoreSlim _gate = new(1);

    public async Task<VideoThumbnail> GetAsync(int bucket, CancellationToken cancellationToken)
    {
        CancellationTokenSource linked;
        lock (_ownershipLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        }
        using var request = linked;
        var token = request.Token;
        await Task.Delay(160, token);
        await _gate.WaitAsync(token);
        try
        {
            if (_frames.TryGetValue(bucket, out var cached)) return cached;
            _metadata ??= await service.ReadAsync(video, token);
            var chosen = _metadata.Thumbnails.OrderBy(frame => frame.Position).ToArray();
            VideoThumbnail frame;
            if (chosen.Length > 0)
                frame = chosen[Math.Min(chosen.Length - 1, bucket * chosen.Length / 10)];
            else
            {
                frame = await service.CaptureThumbnailAsync(video,
                    TimeSpan.FromSeconds(Math.Max(0, video.Duration.TotalSeconds - 0.1) * bucket / 9), token);
                // A service may complete after cancellation. Never publish or leak that late capture.
                lock (_ownershipLock)
                {
                    if (_disposed || token.IsCancellationRequested)
                    {
                        DeleteCapture(frame.ImagePath);
                        throw new OperationCanceledException(token);
                    }
                    _ownedCaptures.Add(frame.ImagePath);
                }
            }
            token.ThrowIfCancellationRequested();
            _frames[bucket] = frame;
            return frame;
        }
        finally { _gate.Release(); }
    }

    public void Dispose()
    {
        string[] captures;
        lock (_ownershipLock)
        {
            if (_disposed) return;
            _disposed = true;
            captures = _ownedCaptures.ToArray();
            _ownedCaptures.Clear();
        }
        _lifetime.Cancel();
        _lifetime.Dispose();
        foreach (var path in captures) DeleteCapture(path);
    }

    private static void DeleteCapture(string path)
    {
        try { File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning($"Could not remove disposable preview {path}: {exception.Message}");
        }
    }
}
