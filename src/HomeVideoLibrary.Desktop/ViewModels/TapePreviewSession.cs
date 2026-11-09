using HomeVideoLibrary.Core;

namespace HomeVideoLibrary.Desktop.ViewModels;

/// <summary>A short-lived, bounded preview cache. Captures never save metadata to the MKV.</summary>
public sealed class TapePreviewSession(IVideoMetadataService service, LibraryVideo video) : IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<int, VideoThumbnail> _frames = [];
    private VideoMetadata? _metadata;
    private readonly SemaphoreSlim _gate = new(1);
    public async Task<VideoThumbnail> GetAsync(int bucket, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var token = linked.Token;
        await Task.Delay(160, token);
        await _gate.WaitAsync(token);
        try
        {
            if (_frames.TryGetValue(bucket, out var cached)) return cached;
            _metadata ??= await service.ReadAsync(video, token);
            var chosen = _metadata.Thumbnails.OrderBy(frame => frame.Position).ToArray();
            var frame = chosen.Length > 0 ? chosen[Math.Min(chosen.Length - 1, bucket * chosen.Length / 10)]
                : await service.CaptureThumbnailAsync(video, TimeSpan.FromSeconds(Math.Max(0, video.Duration.TotalSeconds - 0.1) * bucket / 9), token);
            _frames[bucket] = frame;
            return frame;
        }
        finally { _gate.Release(); }
    }
    public void Dispose() { _lifetime.Cancel(); _lifetime.Dispose(); }
}
