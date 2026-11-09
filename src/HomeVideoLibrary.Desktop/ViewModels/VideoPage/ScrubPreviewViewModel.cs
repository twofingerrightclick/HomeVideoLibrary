using Avalonia.Media.Imaging;
using HomeVideoLibrary.Infrastructure;

namespace HomeVideoLibrary.Desktop.ViewModels.VideoPage;

/// <summary>The frame shown in the scrub bar's hover tooltip. The bar reports <see cref="HoverTime"/>; the time is
/// rounded to one of <see cref="Buckets"/> slots, and each slot's frame is captured once through
/// <see cref="PreviewCache"/> after a short pause on it, decoded off the UI thread and kept for the page's lifetime, so
/// moving back over the bar shows frames at once. Requests for slots the pointer has left are cancelled.</summary>
public sealed class ScrubPreviewViewModel : ViewModelBase, IDisposable
{
    public const int Buckets = 48;
    private const int DecodeWidth = 352;
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(120);

    private readonly VideoRecord _video;
    private readonly string _path;
    private readonly PreviewCache? _previews;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Dictionary<int, Bitmap> _frames = [];
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _request;
    private TimeSpan? _hoverTime;
    private int? _bucket;
    private Bitmap? _image;

    /// <param name="previews">The library's frame cache; null (media tools missing) shows no frames.</param>
    /// <param name="delay">Waits before capturing a slot the pointer rests on; tests replace it.</param>
    public ScrubPreviewViewModel(VideoRecord video, string path, PreviewCache? previews, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _video = video; _path = path; _previews = previews;
        _delay = delay ?? Task.Delay;
    }

    /// <summary>The time under the pointer on the scrub bar; null when the pointer is elsewhere.</summary>
    public TimeSpan? HoverTime
    {
        get => _hoverTime;
        set { if (SetProperty(ref _hoverTime, value)) Show(value); }
    }

    /// <summary>The frame for <see cref="HoverTime"/>; while a new slot loads, the previous slot's frame stays.</summary>
    public Bitmap? Image { get => _image; private set => SetProperty(ref _image, value); }

    public static int BucketOf(TimeSpan time, TimeSpan duration) => duration <= TimeSpan.Zero ? 0
        : Math.Clamp((int)(time.Ticks * (double)Buckets / duration.Ticks), 0, Buckets - 1);

    public static TimeSpan PositionOf(int bucket, TimeSpan duration) => TimeSpan.FromTicks((long)(duration.Ticks * (bucket + 0.5) / Buckets));

    private void Show(TimeSpan? time)
    {
        if (_previews is null || _lifetime.IsCancellationRequested) return;
        if (time is not { } hover)
        {
            _bucket = null;
            _request?.Cancel();
            Image = null;
            return;
        }
        var bucket = BucketOf(hover, _video.Duration);
        if (bucket == _bucket) return;
        _bucket = bucket;
        _request?.Cancel();
        if (_frames.TryGetValue(bucket, out var frame))
        {
            Image = frame;
            return;
        }
        var request = _request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _ = LoadAsync(_previews, bucket, request);
    }

    private async Task LoadAsync(PreviewCache previews, int bucket, CancellationTokenSource request)
    {
        var cancellationToken = request.Token;
        try
        {
            await _delay(Debounce, cancellationToken);
            // The shelf's hover previews use slots 0..TapePreviewSession.Buckets-1 of the same cache; these follow them.
            var path = await previews.GetAsync(_video.Id, _video.Stamp, _path, PositionOf(bucket, _video.Duration),
                TapePreviewSession.Buckets + bucket, cancellationToken);
            var bitmap = await Task.Run(() =>
            {
                using var stream = File.OpenRead(path);
                return Bitmap.DecodeToWidth(stream, DecodeWidth);
            }, cancellationToken);
            if (_lifetime.IsCancellationRequested || !_frames.TryAdd(bucket, bitmap))
            {
                bitmap.Dispose();
                return;
            }
            if (_bucket == bucket) Image = bitmap;
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            // A preview is decoration; PreviewCache remembers a failing video and stops capturing it.
            System.Diagnostics.Trace.TraceWarning($"Scrub preview failed: {exception.Message}");
        }
        finally
        {
            if (_request == request) _request = null;
            request.Dispose();
        }
    }

    public void Dispose()
    {
        if (_lifetime.IsCancellationRequested) return;
        _lifetime.Cancel();
        Image = null;
        foreach (var frame in _frames.Values) frame.Dispose();
        _frames.Clear();
    }
}
