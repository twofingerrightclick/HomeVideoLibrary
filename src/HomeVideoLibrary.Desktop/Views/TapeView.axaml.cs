using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HomeVideoLibrary.Desktop.ViewModels;
using HomeVideoLibrary.Infrastructure;

namespace HomeVideoLibrary.Desktop.Views;

public sealed partial class TapeView : UserControl
{
    private const double PreviewHalfWidth = 104;
    private readonly List<IDisposable> _visibilitySubscriptions = [];
    private TapePreviewSession? _session;
    private CancellationTokenSource? _request;
    private int _bucket = -1;
    private long _version;
    private Bitmap? _current, _previous;

    public TapeView()
    {
        InitializeComponent();
        PointerExited += (_, _) => StopPreview();
        LabelSurface.PointerExited += (_, _) => StopPreview();
        LabelSurface.PointerMoved += PreviewMoved;
        AddHandler(PointerPressedEvent, (_, _) => StopPreview(), Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        AttachedToVisualTree += (_, _) => WatchAncestorVisibility();
        DetachedFromVisualTree += (_, _) => { StopPreview(); UnwatchAncestorVisibility(); };
        DataContextChanged += (_, _) => StopPreview();
    }

    /// <summary>The popup outlives a hidden ancestor (for example, when an editor replaces the library), so close it then.</summary>
    private void WatchAncestorVisibility()
    {
        UnwatchAncestorVisibility();
        foreach (var ancestor in this.GetVisualAncestors().OfType<Visual>())
            _visibilitySubscriptions.Add(ancestor.GetObservable(IsVisibleProperty).Subscribe(new VisibilityObserver(this)));
    }

    private void UnwatchAncestorVisibility()
    {
        foreach (var subscription in _visibilitySubscriptions) subscription.Dispose();
        _visibilitySubscriptions.Clear();
    }

    private sealed class VisibilityObserver(TapeView view) : IObserver<bool>
    {
        public void OnNext(bool visible) { if (!visible) view.StopPreview(); }
        public void OnCompleted() { }
        public void OnError(Exception error) { }
    }

    private async void PreviewMoved(object? sender, PointerEventArgs args)
    {
        if (DataContext is not TapeViewModel tape || args.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { StopPreview(); return; }
        var width = LabelSurface.Bounds.Width;
        var x = args.GetPosition(LabelSurface).X;
        // Center the popup on the pointer but keep it within the label; a label narrower than the popup centers it.
        var limit = Math.Max(0, width / 2 - PreviewHalfWidth);
        PreviewPopup.HorizontalOffset = Math.Clamp(x - width / 2, -limit, limit);
        var bucket = Math.Clamp((int)(x / Math.Max(1, width) * TapePreviewSession.Buckets), 0, TapePreviewSession.Buckets - 1);
        if (bucket == _bucket) return;
        _bucket = bucket;
        _request?.Cancel();
        var cancellation = new CancellationTokenSource(); _request = cancellation;
        var version = ++_version;
        try
        {
            if (_session is null)
            {
                _session = tape.CreatePreviewSession();
                if (_session is null) return;
            }
            var frame = await _session.GetAsync(bucket, cancellation.Token);
            var bitmap = await Task.Run(() =>
            {
                using var stream = File.OpenRead(frame.ImagePath);
                return Bitmap.DecodeToWidth(stream, PreviewCache.FrameWidth);
            }, cancellation.Token);
            if (version != _version || cancellation.IsCancellationRequested) { bitmap.Dispose(); return; }
            _previous?.Dispose(); _previous = _current; _current = bitmap;
            PreviousPreview.Source = _previous; CurrentPreview.Opacity = 0; CurrentPreview.Source = _current;
            PreviewTime.Text = frame.Position.ToString(@"hh\:mm\:ss");
            PreviewPopup.IsOpen = true;
            Dispatcher.UIThread.Post(() => { if (version == _version) CurrentPreview.Opacity = 1; }, DispatcherPriority.Render);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            // Preview is decoration: a failed frame just hides the popup (PreviewCache stops retrying the video).
            System.Diagnostics.Trace.TraceWarning($"Hover preview failed: {exception.Message}");
            if (version == _version) PreviewPopup.IsOpen = false;
        }
        finally { if (_request == cancellation) _request = null; cancellation.Dispose(); }
    }

    private void StopPreview()
    {
        ++_version; _bucket = -1; _request?.Cancel(); _request = null;
        _session?.Dispose(); _session = null; PreviewPopup.IsOpen = false;
        CurrentPreview.Source = null; PreviousPreview.Source = null;
        _current?.Dispose(); _previous?.Dispose(); _current = null; _previous = null;
    }
}
