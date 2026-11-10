using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Animation;
using Avalonia.Styling;
using Avalonia.VisualTree;
using HomeVideoLibrary.Desktop.ViewModels;

namespace HomeVideoLibrary.Desktop.Views;

public sealed partial class TapeView : UserControl
{
    private const double PreviewHalfWidth = 104;
    private static readonly Animation FadeIn = new()
    {
        Duration = TimeSpan.FromMilliseconds(160),
        FillMode = FillMode.Forward,
        Children =
        {
            new KeyFrame { Cue = new Cue(0), Setters = { new Setter(OpacityProperty, 0d) } },
            new KeyFrame { Cue = new Cue(1), Setters = { new Setter(OpacityProperty, 1d) } },
        },
    };
    private readonly List<IDisposable> _visibilitySubscriptions = [];
    private TapePreviewSession? _session;
    private CancellationTokenSource? _fade;
    private int _bucket = -1;

    public TapeView()
    {
        InitializeComponent();
        PointerExited += (_, _) => StopPreview();
        Spine.PointerMoved += PreviewMoved;
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

    private void PreviewMoved(object? sender, PointerEventArgs args)
    {
        if (DataContext is not TapeViewModel tape || args.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { StopPreview(); return; }
        var width = Spine.Bounds.Width;
        var x = args.GetPosition(Spine).X;
        // Center the popup on the pointer but keep it within the spine; a spine narrower than the popup centers it.
        var limit = Math.Max(0, width / 2 - PreviewHalfWidth);
        PreviewPopup.HorizontalOffset = Math.Clamp(x - width / 2, -limit, limit);
        var bucket = Math.Clamp((int)(x / Math.Max(1, width) * TapePreviewSession.Buckets), 0, TapePreviewSession.Buckets - 1);
        if (bucket == _bucket) return;
        _bucket = bucket;
        if (_session is null)
        {
            if (tape.CreatePreviewSession() is not { } session) return;
            _session = session;
            _ = ShowWhenReadyAsync(session);
        }
        ShowBucket(_session);
    }

    /// <summary>Shows the hovered bucket's frame, or its time over a placeholder while the strip is built. Decoding a
    /// built strip is quick, so meanwhile the popup is left as it is rather than flashing the placeholder.</summary>
    private void ShowBucket(TapePreviewSession session)
    {
        if (session.Strip is { IsCompleted: true, Result: null })
        {
            // Preview is decoration: a tape without a strip shows no popup.
            PreviewPopup.IsOpen = false;
            return;
        }
        var frame = session.FrameOf(_bucket);
        if (frame is null && !session.IsBuilding) return;
        PreviewTime.Text = HomeVideoLibrary.UI.Logic.ScrubTimeline.FormatTimestamp(session.PositionOf(_bucket));
        PreviewPopup.IsOpen = true;
        PreparingPlate.IsVisible = frame is null;
        if (frame is null) return;
        PreviousPreview.Source = CurrentPreview.Source;
        CurrentPreview.Source = frame;
        _fade?.Cancel();
        _fade = new CancellationTokenSource();
        _ = FadeIn.RunAsync(CurrentPreview, _fade.Token);
    }

    private async Task ShowWhenReadyAsync(TapePreviewSession session)
    {
        await session.Strip;
        if (session == _session) ShowBucket(session);
    }

    private void StopPreview()
    {
        _bucket = -1;
        _fade?.Cancel(); _fade = null;
        PreviewPopup.IsOpen = false; PreparingPlate.IsVisible = false;
        // The crops show the session's strip, so they go before it is disposed.
        CurrentPreview.Source = null; PreviousPreview.Source = null;
        _session?.Dispose(); _session = null;
    }
}
