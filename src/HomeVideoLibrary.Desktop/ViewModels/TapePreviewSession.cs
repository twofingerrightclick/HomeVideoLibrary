using HomeVideoLibrary.Core;
using HomeVideoLibrary.Infrastructure;

namespace HomeVideoLibrary.Desktop.ViewModels;

public sealed record PreviewFrame(TimeSpan Position, string ImagePath);

/// <summary>Resolves hover frames for one tape. Chosen thumbnails come from the record's refs (extracted once into the
/// thumbnail cache, never by re-reading the MKV); videos without any fall back to <see cref="PreviewCache"/> frames.
/// The session owns no files: both caches outlive it.</summary>
public sealed class TapePreviewSession(VideoRecord video, string mkvPath, IMatroskaFileEditor thumbnails, PreviewCache previews) : IDisposable
{
    public const int Buckets = 10;
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(160);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _gate = new(1);
    private bool _disposed;

    /// <summary>Waits a short debounce first, so passing over a tape starts no media work.</summary>
    public async Task<PreviewFrame> GetAsync(int bucket, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bucket);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(bucket, Buckets);
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var token = request.Token;
        await Task.Delay(Debounce, token);
        await _gate.WaitAsync(token);
        try
        {
            var chosen = video.Thumbnails;
            if (chosen.Count > 0)
            {
                var thumbnail = chosen[ThumbnailIndex(bucket, chosen.Count)];
                return new PreviewFrame(thumbnail.Position, await thumbnails.ExtractThumbnailAsync(mkvPath, video.Id, thumbnail, token));
            }
            var position = TimeSpan.FromSeconds(Math.Max(0, video.Duration.TotalSeconds - 0.1) * bucket / (Buckets - 1));
            return new PreviewFrame(position, await previews.GetAsync(video.Id, video.Stamp, mkvPath, position, bucket, token));
        }
        finally { _gate.Release(); }
    }

    /// <summary>The chosen thumbnail shown for <paramref name="bucket"/>: the buckets span every thumbnail from the first
    /// to the last, so the right end of the tape reaches the last one.</summary>
    public static int ThumbnailIndex(int bucket, int count) =>
        count <= 1 ? 0 : (int)Math.Round(bucket * (count - 1) / (double)(Buckets - 1), MidpointRounding.AwayFromZero);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
