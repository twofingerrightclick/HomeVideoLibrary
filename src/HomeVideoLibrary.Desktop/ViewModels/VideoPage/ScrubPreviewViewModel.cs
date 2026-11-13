using Avalonia.Media.Imaging;
using HomeVideoLibrary.Infrastructure;

namespace HomeVideoLibrary.Desktop.ViewModels.VideoPage;

/// <summary>The frame shown in the scrub bar's hover tooltip. The bar reports <see cref="HoverTime"/>; the time falls in
/// a slot of the video (<see cref="SlotLength"/> long), and each slot's frame is captured once through
/// <see cref="PreviewCache"/> after the pointer rests on it for <see cref="Debounce"/>, then decoded off the UI thread.
/// <see cref="Image"/> retains the previous frame while the slot under the pointer loads, avoiding a flashing preview.
/// Recently shown frames stay decoded, so moving back over the bar shows them at once. Requests for slots the pointer
/// has left are cancelled.</summary>
public sealed class ScrubPreviewViewModel : ViewModelBase, IDisposable
{
    public static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan ShortestSlot = TimeSpan.FromSeconds(2);
    private const int MaxSlots = 300;
    private const int KeptFrames = 48;
    private const int DecodeWidth = 352;

    private readonly VideoRecord _video;
    private readonly string _path;
    private readonly PreviewCache? _previews;
    private readonly TimeProvider _time;
    private readonly LinkedList<(int Slot, Bitmap Frame)> _frames = new();
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _request;
    private TimeSpan? _hoverTime;
    private int? _slot;
    private Bitmap? _image;

    /// <param name="previews">The library's frame cache; null (media tools missing) shows no frames.</param>
    public ScrubPreviewViewModel(VideoRecord video, string path, PreviewCache? previews, TimeProvider? time = null)
    {
        _video = video; _path = path; _previews = previews;
        _time = time ?? TimeProvider.System;
        SlotLength = SlotLengthFor(video.Duration);
    }

    /// <summary>How much of the video one preview frame stands for: at most <see cref="MaxSlots"/> slots, at least 2 s each.</summary>
    public TimeSpan SlotLength { get; }

    /// <summary>The time under the pointer on the scrub bar; null when the pointer is elsewhere.</summary>
    public TimeSpan? HoverTime
    {
        get => _hoverTime;
        set { if (SetProperty(ref _hoverTime, value)) Show(value); }
    }

    /// <summary>The latest available frame; retained while the next loads, cleared when the pointer leaves.</summary>
    public Bitmap? Image { get => _image; private set => SetProperty(ref _image, value); }

    public static TimeSpan SlotLengthFor(TimeSpan duration) =>
        TimeSpan.FromTicks(Math.Max(ShortestSlot.Ticks, (long)Math.Ceiling(duration.Ticks / (double)MaxSlots)));

    private int SlotOf(TimeSpan time) => (int)Math.Clamp(time.Ticks / SlotLength.Ticks, 0, Math.Max(0, (_video.Duration.Ticks - 1) / SlotLength.Ticks));

    private TimeSpan MiddleOf(int slot) =>
        TimeSpan.FromTicks(Math.Min(slot * SlotLength.Ticks + SlotLength.Ticks / 2, Math.Max(0, _video.Duration.Ticks - TimeSpan.TicksPerMillisecond * 100)));

    private void Show(TimeSpan? time)
    {
        if (_previews is null || _lifetime.IsCancellationRequested) return;
        var slot = time is { } hover ? SlotOf(hover) : (int?)null;
        if (slot == _slot) return;
        _slot = slot;
        _request?.Cancel();
        if (slot is not { } wanted)
        {
            Image = null;
            return;
        }
        if (Decoded(wanted) is { } cached)
        {
            Image = cached;
            return;
        }
        var request = _request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _ = LoadAsync(_previews, wanted, request);
    }

    private Bitmap? Decoded(int slot)
    {
        for (var node = _frames.First; node is not null; node = node.Next)
        {
            if (node.Value.Slot != slot) continue;
            _frames.Remove(node);
            _frames.AddFirst(node);
            return node.Value.Frame;
        }
        return null;
    }

    private async Task LoadAsync(PreviewCache previews, int slot, CancellationTokenSource request)
    {
        var cancellationToken = request.Token;
        try
        {
            await Task.Delay(Debounce, _time, cancellationToken);
            var path = await previews.GetAsync(_video.Id, _video.Stamp, _path, MiddleOf(slot), slot, cancellationToken);
            var frame = await Task.Run(() =>
            {
                using var stream = File.OpenRead(path);
                return Bitmap.DecodeToWidth(stream, DecodeWidth);
            }, cancellationToken);
            if (cancellationToken.IsCancellationRequested || Decoded(slot) is not null)
            {
                frame.Dispose();
                return;
            }
            if (_slot == slot) Image = frame;
            Keep(slot, frame);
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

    private void Keep(int slot, Bitmap frame)
    {
        _frames.AddFirst((slot, frame));
        while (_frames.Count > KeptFrames && _frames.Last is { } oldest && !ReferenceEquals(oldest.Value.Frame, Image))
        {
            _frames.RemoveLast();
            oldest.Value.Frame.Dispose();
        }
    }

    public void Dispose()
    {
        if (_lifetime.IsCancellationRequested) return;
        _lifetime.Cancel();
        Image = null;
        foreach (var (_, frame) in _frames) frame.Dispose();
        _frames.Clear();
        _lifetime.Dispose();
    }
}
