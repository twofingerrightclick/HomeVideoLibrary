using System.Diagnostics;
using Avalonia;
using Avalonia.Media.Imaging;
using HomeVideoLibrary.Infrastructure;

namespace HomeVideoLibrary.Desktop.ViewModels;

/// <summary>One hover over a tape. The tape's hover strip is requested once (jumping the build queue when it does not exist
/// yet) and decoded once; every bucket's frame is then a crop of it, with no further media work. The session owns the
/// decoded strip and disposes it, so crops must not outlive the session.</summary>
public sealed class TapePreviewSession : IDisposable
{
    public const int Buckets = HoverStripStore.Slots;
    private readonly VideoRecord _video;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task<string?> _request;
    private Bitmap? _strip;

    /// <summary>Starts loading the strip; create it on the UI thread.</summary>
    public TapePreviewSession(VideoRecord video, HoverStripScheduler strips)
    {
        _video = video;
        _request = strips.RequestAsync(video, _lifetime.Token);
        Strip = DecodeAsync(strips, _lifetime.Token);
    }

    /// <summary>Completes with the decoded strip, or with null when the tape has none or the session ended first. Never faults.</summary>
    public Task<Bitmap?> Strip { get; }

    /// <summary>The strip does not exist yet: it is being built. Decoding an existing strip does not count.</summary>
    public bool IsBuilding => !_request.IsCompleted;

    /// <summary>Where in the video <paramref name="bucket"/>'s frame is from.</summary>
    public TimeSpan PositionOf(int bucket) => HoverStripStore.SlotTime(bucket, _video.Duration);

    /// <summary><paramref name="bucket"/>'s frame, cropped from the strip; null until the strip is decoded.</summary>
    public CroppedBitmap? FrameOf(int bucket) => _strip is { } strip ? new CroppedBitmap(strip, TileOf(bucket, strip.PixelSize)) : null;

    public static PixelRect TileOf(int bucket, PixelSize strip)
    {
        var (x, y, width, height) = HoverStripStore.Tile(bucket, strip.Width, strip.Height);
        return new PixelRect(x, y, width, height);
    }

    private async Task<Bitmap?> DecodeAsync(HoverStripScheduler strips, CancellationToken cancellationToken)
    {
        string? path = null;
        try
        {
            if ((path = await _request) is null) return null;
            var file = path;
            var strip = await Task.Run(() => new Bitmap(file), cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                strip.Dispose();
                return null;
            }
            return _strip = strip;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception exception)
        {
            // A preview is decoration: this hover shows none, and the strip is built again for the next.
            Trace.TraceWarning($"Could not load the hover strip of “{_video.Label}”: {exception.Message}");
            if (path is not null) strips.Invalidate(_video.Id, path);
            return null;
        }
    }

    public void Dispose()
    {
        if (_lifetime.IsCancellationRequested) return;
        _lifetime.Cancel();
        _lifetime.Dispose();
        _strip?.Dispose();
        _strip = null;
    }
}
