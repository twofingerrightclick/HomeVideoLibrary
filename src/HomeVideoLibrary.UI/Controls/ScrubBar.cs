using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using HomeVideoLibrary.UI.Logic;

namespace HomeVideoLibrary.UI.Controls;

public sealed class SeekRequestedEventArgs(TimeSpan time) : RoutedEventArgs(ScrubBar.SeekRequestedEvent)
{
    public TimeSpan Time { get; } = time;
}

/// <summary>
/// Chaptered playback timeline. While dragging, the thumb follows <see cref="ScrubPosition"/> and
/// <see cref="Position"/> is left alone, so a two-way bound player position is neither written per pointer
/// move nor able to drag the thumb away from the cursor. Releasing writes <see cref="Position"/> once and
/// raises <see cref="SeekRequested"/>; Escape or losing pointer capture cancels without seeking. Left/Right
/// (5 s, or 1 s with Shift), Home and End seek immediately.
/// </summary>
[TemplatePart("PART_Popup", typeof(Popup))]
[TemplatePart("PART_HoverTime", typeof(TextBlock))]
[TemplatePart("PART_HoverTimeGhost", typeof(TextBlock))]
[PseudoClasses(":scrubbing")]
public class ScrubBar : TemplatedControl
{
    private const double SegmentGap = 3;
    private const double MinSegmentWidth = 2;
    private const double GrooveInset = 2;
    private const double GrooveHeight = 10;
    private const double GrooveGrowth = 4;
    private const double HoveredSegmentGrowth = 3;
    private const double ThumbWidth = 12;
    private const double ThumbHeight = 18;
    private const double ThumbGrowth = 4;
    private const double MarkerReach = 13;
    private const double MarkerCapWidth = 11;
    private const double MarkerCapHeight = 6;

    private static readonly TimeSpan LargeStep = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SmallStep = TimeSpan.FromSeconds(1);

    public static readonly StyledProperty<IEnumerable<IChapterMark>?> ChapterSegmentsProperty =
        AvaloniaProperty.Register<ScrubBar, IEnumerable<IChapterMark>?>(nameof(ChapterSegments));

    public static readonly StyledProperty<TimeSpan> PositionProperty =
        AvaloniaProperty.Register<ScrubBar, TimeSpan>(nameof(Position), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<TimeSpan> DurationProperty =
        AvaloniaProperty.Register<ScrubBar, TimeSpan>(nameof(Duration));

    public static readonly StyledProperty<TimeSpan> BufferedPositionProperty =
        AvaloniaProperty.Register<ScrubBar, TimeSpan>(nameof(BufferedPosition));

    /// <summary>Where a new chapter would be inserted, drawn as a yellow slice marker; null hides it.</summary>
    public static readonly StyledProperty<TimeSpan?> PreviewMarkerPositionProperty =
        AvaloniaProperty.Register<ScrubBar, TimeSpan?>(nameof(PreviewMarkerPosition));

    /// <summary>Time under the dragged thumb while <see cref="IsScrubbing"/>; null otherwise. Use it for live previews.</summary>
    public static readonly DirectProperty<ScrubBar, TimeSpan?> ScrubPositionProperty =
        AvaloniaProperty.RegisterDirect<ScrubBar, TimeSpan?>(nameof(ScrubPosition), o => o.ScrubPosition);

    public static readonly DirectProperty<ScrubBar, bool> IsScrubbingProperty =
        AvaloniaProperty.RegisterDirect<ScrubBar, bool>(nameof(IsScrubbing), o => o.IsScrubbing);

    /// <summary>Time under the pointer while it is over the bar or dragging; null otherwise.</summary>
    public static readonly DirectProperty<ScrubBar, TimeSpan?> HoverTimeProperty =
        AvaloniaProperty.RegisterDirect<ScrubBar, TimeSpan?>(nameof(HoverTime), o => o.HoverTime);

    /// <summary>Title of the chapter under the pointer; null when not hovering or over untitled footage.</summary>
    public static readonly DirectProperty<ScrubBar, string?> HoverChapterTitleProperty =
        AvaloniaProperty.RegisterDirect<ScrubBar, string?>(nameof(HoverChapterTitle), o => o.HoverChapterTitle);

    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<ScrubBar, IBrush?>(nameof(TrackBrush));

    public static readonly StyledProperty<IBrush?> BufferedBrushProperty =
        AvaloniaProperty.Register<ScrubBar, IBrush?>(nameof(BufferedBrush));

    public static readonly StyledProperty<IBrush?> PlayedBrushProperty =
        AvaloniaProperty.Register<ScrubBar, IBrush?>(nameof(PlayedBrush));

    public static readonly StyledProperty<IBrush?> SegmentSheenBrushProperty =
        AvaloniaProperty.Register<ScrubBar, IBrush?>(nameof(SegmentSheenBrush));

    public static readonly StyledProperty<IBrush?> MarkerBrushProperty =
        AvaloniaProperty.Register<ScrubBar, IBrush?>(nameof(MarkerBrush));

    public static readonly StyledProperty<IBrush?> ThumbBrushProperty =
        AvaloniaProperty.Register<ScrubBar, IBrush?>(nameof(ThumbBrush));

    public static readonly StyledProperty<IBrush?> ThumbIndexBrushProperty =
        AvaloniaProperty.Register<ScrubBar, IBrush?>(nameof(ThumbIndexBrush));

    /// <summary>Ring drawn around the groove; themes set it only for keyboard focus.</summary>
    public static readonly StyledProperty<IBrush?> FocusRingBrushProperty =
        AvaloniaProperty.Register<ScrubBar, IBrush?>(nameof(FocusRingBrush));

    public static readonly StyledProperty<BoxShadows> GrooveShadowProperty =
        AvaloniaProperty.Register<ScrubBar, BoxShadows>(nameof(GrooveShadow));

    public static readonly StyledProperty<BoxShadows> PlayedGlowProperty =
        AvaloniaProperty.Register<ScrubBar, BoxShadows>(nameof(PlayedGlow));

    public static readonly StyledProperty<BoxShadows> ThumbShadowProperty =
        AvaloniaProperty.Register<ScrubBar, BoxShadows>(nameof(ThumbShadow));

    /// <summary>0 at rest, 1 fully grown for hover and drag; themes animate it with a transition.</summary>
    public static readonly StyledProperty<double> HoverExpansionProperty =
        AvaloniaProperty.Register<ScrubBar, double>(nameof(HoverExpansion));

    public static readonly RoutedEvent<RoutedEventArgs> ScrubStartedEvent =
        RoutedEvent.Register<ScrubBar, RoutedEventArgs>(nameof(ScrubStarted), RoutingStrategies.Bubble);

    public static readonly RoutedEvent<RoutedEventArgs> ScrubCompletedEvent =
        RoutedEvent.Register<ScrubBar, RoutedEventArgs>(nameof(ScrubCompleted), RoutingStrategies.Bubble);

    /// <summary>Raised when a drag or click is released and on every keyboard seek.</summary>
    public static readonly RoutedEvent<SeekRequestedEventArgs> SeekRequestedEvent =
        RoutedEvent.Register<ScrubBar, SeekRequestedEventArgs>(nameof(SeekRequested), RoutingStrategies.Bubble);

    private IReadOnlyList<ChapterSpan> _spans = [];
    private INotifyCollectionChanged? _observedCollection;
    private readonly List<INotifyPropertyChanged> _observedItems = [];
    private bool _isAttached;
    private bool _isScrubbing;
    private TimeSpan? _scrubPosition;
    private TimeSpan? _hoverTime;
    private string? _hoverChapterTitle;
    private Popup? _popup;
    private TextBlock? _hoverTimeText;
    private TextBlock? _hoverTimeGhost;

    static ScrubBar()
    {
        FocusableProperty.OverrideDefaultValue<ScrubBar>(true);
        AffectsRender<ScrubBar>(
            PositionProperty, BufferedPositionProperty, PreviewMarkerPositionProperty, TrackBrushProperty,
            BufferedBrushProperty, PlayedBrushProperty, SegmentSheenBrushProperty, MarkerBrushProperty,
            ThumbBrushProperty, ThumbIndexBrushProperty, FocusRingBrushProperty, GrooveShadowProperty, PlayedGlowProperty,
            ThumbShadowProperty, HoverExpansionProperty, BackgroundProperty, BorderBrushProperty);
    }

    public IEnumerable<IChapterMark>? ChapterSegments
    {
        get => GetValue(ChapterSegmentsProperty);
        set => SetValue(ChapterSegmentsProperty, value);
    }

    public TimeSpan Position
    {
        get => GetValue(PositionProperty);
        set => SetValue(PositionProperty, value);
    }

    public TimeSpan Duration
    {
        get => GetValue(DurationProperty);
        set => SetValue(DurationProperty, value);
    }

    public TimeSpan BufferedPosition
    {
        get => GetValue(BufferedPositionProperty);
        set => SetValue(BufferedPositionProperty, value);
    }

    public TimeSpan? PreviewMarkerPosition
    {
        get => GetValue(PreviewMarkerPositionProperty);
        set => SetValue(PreviewMarkerPositionProperty, value);
    }

    public bool IsScrubbing
    {
        get => _isScrubbing;
        private set
        {
            if (SetAndRaise(IsScrubbingProperty, ref _isScrubbing, value))
            {
                PseudoClasses.Set(":scrubbing", value);
            }
        }
    }

    public TimeSpan? ScrubPosition
    {
        get => _scrubPosition;
        private set
        {
            if (SetAndRaise(ScrubPositionProperty, ref _scrubPosition, value))
            {
                InvalidateVisual();
            }
        }
    }

    public TimeSpan? HoverTime
    {
        get => _hoverTime;
        private set => SetAndRaise(HoverTimeProperty, ref _hoverTime, value);
    }

    public string? HoverChapterTitle
    {
        get => _hoverChapterTitle;
        private set => SetAndRaise(HoverChapterTitleProperty, ref _hoverChapterTitle, value);
    }

    public IBrush? TrackBrush
    {
        get => GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public IBrush? BufferedBrush
    {
        get => GetValue(BufferedBrushProperty);
        set => SetValue(BufferedBrushProperty, value);
    }

    public IBrush? PlayedBrush
    {
        get => GetValue(PlayedBrushProperty);
        set => SetValue(PlayedBrushProperty, value);
    }

    /// <summary>Overlay painted on every segment after its fill, for a molded highlight.</summary>
    public IBrush? SegmentSheenBrush
    {
        get => GetValue(SegmentSheenBrushProperty);
        set => SetValue(SegmentSheenBrushProperty, value);
    }

    public IBrush? MarkerBrush
    {
        get => GetValue(MarkerBrushProperty);
        set => SetValue(MarkerBrushProperty, value);
    }

    public IBrush? ThumbBrush
    {
        get => GetValue(ThumbBrushProperty);
        set => SetValue(ThumbBrushProperty, value);
    }

    public IBrush? ThumbIndexBrush
    {
        get => GetValue(ThumbIndexBrushProperty);
        set => SetValue(ThumbIndexBrushProperty, value);
    }

    public IBrush? FocusRingBrush
    {
        get => GetValue(FocusRingBrushProperty);
        set => SetValue(FocusRingBrushProperty, value);
    }

    public BoxShadows GrooveShadow
    {
        get => GetValue(GrooveShadowProperty);
        set => SetValue(GrooveShadowProperty, value);
    }

    public BoxShadows PlayedGlow
    {
        get => GetValue(PlayedGlowProperty);
        set => SetValue(PlayedGlowProperty, value);
    }

    public BoxShadows ThumbShadow
    {
        get => GetValue(ThumbShadowProperty);
        set => SetValue(ThumbShadowProperty, value);
    }

    public double HoverExpansion
    {
        get => GetValue(HoverExpansionProperty);
        set => SetValue(HoverExpansionProperty, value);
    }

    public event EventHandler<RoutedEventArgs>? ScrubStarted
    {
        add => AddHandler(ScrubStartedEvent, value);
        remove => RemoveHandler(ScrubStartedEvent, value);
    }

    public event EventHandler<RoutedEventArgs>? ScrubCompleted
    {
        add => AddHandler(ScrubCompletedEvent, value);
        remove => RemoveHandler(ScrubCompletedEvent, value);
    }

    public event EventHandler<SeekRequestedEventArgs>? SeekRequested
    {
        add => AddHandler(SeekRequestedEvent, value);
        remove => RemoveHandler(SeekRequestedEvent, value);
    }

    private double TrackLeft => GrooveInset;

    private double TrackWidth => Math.Max(0, Bounds.Width - 2 * GrooveInset);

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        if (_popup is not null)
        {
            _popup.IsOpen = false;
        }

        _popup = e.NameScope.Find<Popup>("PART_Popup");
        _hoverTimeText = e.NameScope.Find<TextBlock>("PART_HoverTime");
        _hoverTimeGhost = e.NameScope.Find<TextBlock>("PART_HoverTimeGhost");
        if (_popup is not null)
        {
            _popup.PlacementTarget = this;
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttached = true;
        Observe(ChapterSegments);
        RebuildSpans();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _isAttached = false;
        Observe(null);
        ClearHover();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ChapterSegmentsProperty)
        {
            Observe(ChapterSegments);
            RebuildSpans();
        }
        else if (change.Property == DurationProperty)
        {
            RebuildSpans();
        }
        else if ((change.Property == IsEffectivelyEnabledProperty && !IsEffectivelyEnabled)
            || (change.Property == IsVisibleProperty && !IsVisible))
        {
            CancelScrub();
            ClearHover();
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var x = e.GetPosition(this).X;
        if (IsScrubbing)
        {
            ScrubPosition = TimeAt(x);
        }

        UpdateHover(x);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (!IsScrubbing)
        {
            ClearHover();
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.Handled
            || IsScrubbing
            || e.GetCurrentPoint(this).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed
            || Duration <= TimeSpan.Zero)
        {
            return;
        }

        e.Handled = true;
        Focus(NavigationMethod.Pointer);
        e.Pointer.Capture(this);
        var x = e.GetPosition(this).X;
        ScrubPosition = TimeAt(x);
        IsScrubbing = true;
        RaiseEvent(new RoutedEventArgs(ScrubStartedEvent));
        UpdateHover(x);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!IsScrubbing || e.InitialPressMouseButton != MouseButton.Left)
        {
            return;
        }

        e.Handled = true;
        ScrubPosition = TimeAt(e.GetPosition(this).X);
        EndScrub(commit: true);
        e.Pointer.Capture(null);
        if (!new Rect(Bounds.Size).Contains(e.GetPosition(this)))
        {
            ClearHover();
        }
    }

    // Capture is lost without a release when the window deactivates or a popup grabs the pointer; the
    // user never let go on a target, so that ends the drag without seeking.
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        CancelScrub();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled)
        {
            return;
        }

        if (IsScrubbing)
        {
            if (e.Key == Key.Escape)
            {
                CancelScrub();
                e.Handled = true;
            }

            return;
        }

        var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? SmallStep : LargeStep;
        TimeSpan? target = e.Key switch
        {
            Key.Left => Position - step,
            Key.Right => Position + step,
            Key.Home => TimeSpan.Zero,
            Key.End => Duration,
            _ => null,
        };
        if (target is not { } time || Duration <= TimeSpan.Zero)
        {
            return;
        }

        e.Handled = true;
        Position = time < TimeSpan.Zero ? TimeSpan.Zero : time > Duration ? Duration : time;
        RaiseEvent(new SeekRequestedEventArgs(Position));
    }

    public override void Render(DrawingContext context)
    {
        var size = Bounds.Size;
        // Without a fill the bar is only hit-testable where segments are drawn.
        context.FillRectangle(Brushes.Transparent, new Rect(size));

        var expansion = Math.Clamp(HoverExpansion, 0, 1);
        var centerY = Snap(size.Height / 2);
        var grooveHeight = Snap(GrooveHeight + GrooveGrowth * expansion);
        var groove = new Rect(0, centerY - Snap(grooveHeight / 2), size.Width, grooveHeight);
        context.DrawRectangle(Background, null, new RoundedRect(groove, 3), GrooveShadow);
        if (FocusRingBrush is { } ring)
        {
            context.DrawRectangle(null, new Pen(ring, 2), new RoundedRect(groove.Inflate(4), 6));
        }

        var inner = groove.Deflate(GrooveInset);
        var trackWidth = TrackWidth;
        var playX = Snap(TrackLeft + ScrubTimeline.TimeToX(ScrubPosition ?? Position, Duration, trackWidth));
        var bufferedX = Snap(TrackLeft + ScrubTimeline.TimeToX(BufferedPosition, Duration, trackWidth));
        var hoverIndex = HoverTime is { } hover && _spans.Count > 1 ? ScrubTimeline.SpanIndexAt(_spans, hover) : -1;

        IReadOnlyList<SegmentExtent> extents = _spans.Count == 0
            ? [new SegmentExtent(0, trackWidth)]
            : ScrubTimeline.LayoutSegments(_spans, Duration, trackWidth, SegmentGap, MinSegmentWidth);
        for (var i = 0; i < extents.Count; i++)
        {
            var left = Snap(TrackLeft + extents[i].Left);
            var right = Math.Max(left + 1, Snap(TrackLeft + extents[i].Right));
            var grow = i == hoverIndex ? Snap(HoveredSegmentGrowth * expansion) : 0;
            var segment = new Rect(left, inner.Top - grow, right - left, inner.Height + 2 * grow);
            DrawSegment(context, segment, playX, bufferedX);
        }

        if (Duration > TimeSpan.Zero)
        {
            DrawThumb(context, playX, centerY, expansion);
            if (PreviewMarkerPosition is { } marker)
            {
                var markerX = Snap(TrackLeft + ScrubTimeline.TimeToX(marker, Duration, trackWidth));
                DrawMarker(context, markerX, centerY);
            }
        }
    }

    private void DrawSegment(DrawingContext context, Rect segment, double playX, double bufferedX)
    {
        var shape = new RoundedRect(segment, 1.5);
        context.DrawRectangle(TrackBrush, null, shape);
        var tall = new Rect(segment.Left, segment.Top - 8, segment.Width, segment.Height + 16);

        if (bufferedX > playX && bufferedX > segment.Left && playX < segment.Right)
        {
            using (context.PushClip(new Rect(Math.Max(playX, segment.Left), tall.Top, bufferedX - Math.Max(playX, segment.Left), tall.Height)))
            {
                context.DrawRectangle(BufferedBrush, null, shape);
            }
        }

        if (playX > segment.Left)
        {
            using (context.PushClip(new Rect(tall.Left - 8, tall.Top, Math.Min(playX, segment.Right) - tall.Left + 8, tall.Height)))
            {
                context.DrawRectangle(PlayedBrush, null, shape, PlayedGlow);
            }
        }

        context.DrawRectangle(SegmentSheenBrush, null, shape);
    }

    private void DrawThumb(DrawingContext context, double x, double centerY, double expansion)
    {
        var width = Snap(ThumbWidth + ThumbGrowth / 2 * expansion);
        var height = Snap(ThumbHeight + ThumbGrowth * expansion);
        var body = new Rect(x - Snap(width / 2), centerY - Snap(height / 2), width, height);
        context.DrawRectangle(ThumbBrush, null, new RoundedRect(body, 3), ThumbShadow);
        if (BorderBrush is { } rim)
        {
            context.DrawRectangle(null, new Pen(rim, 1), new RoundedRect(body.Deflate(0.5), 2.5));
        }

        var index = new Rect(x - 1, body.Top + 4, 2, body.Height - 8);
        context.FillRectangle(ThumbIndexBrush ?? Brushes.Transparent, index);
    }

    // A slice line through the track with a downward cap, outlined so it reads over red and gray alike.
    private void DrawMarker(DrawingContext context, double x, double centerY)
    {
        var top = centerY - MarkerReach;
        var bottom = centerY + MarkerReach - 2;
        var halfCap = MarkerCapWidth / 2;
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(new Point(x - halfCap, top), true);
            g.LineTo(new Point(x + halfCap, top));
            g.LineTo(new Point(x + 1, top + MarkerCapHeight));
            g.LineTo(new Point(x + 1, bottom));
            g.LineTo(new Point(x - 1, bottom));
            g.LineTo(new Point(x - 1, top + MarkerCapHeight));
            g.EndFigure(true);
        }

        if (BorderBrush is { } outline)
        {
            context.DrawGeometry(null, new Pen(outline, 2, lineJoin: PenLineJoin.Miter), geometry);
        }

        context.DrawGeometry(MarkerBrush, null, geometry);
    }

    private TimeSpan TimeAt(double x) => ScrubTimeline.XToTime(x - TrackLeft, Duration, TrackWidth);

    private void UpdateHover(double x)
    {
        if (Duration <= TimeSpan.Zero || !IsEffectivelyEnabled)
        {
            ClearHover();
            return;
        }

        var time = TimeAt(x);
        var index = ScrubTimeline.SpanIndexAt(_spans, time);
        HoverTime = time;
        HoverChapterTitle = index >= 0 && !string.IsNullOrEmpty(_spans[index].Title) ? _spans[index].Title : null;
        var text = ScrubTimeline.FormatTimestamp(time);
        if (_hoverTimeText is not null)
        {
            _hoverTimeText.Text = text;
        }

        if (_hoverTimeGhost is not null)
        {
            _hoverTimeGhost.Text = new string(text.Select(c => char.IsDigit(c) ? '8' : c).ToArray());
        }

        PlaceTooltip(Math.Clamp(x, TrackLeft, TrackLeft + TrackWidth));
        InvalidateVisual();
    }

    private void PlaceTooltip(double x)
    {
        if (_popup?.Child is not { } tip)
        {
            return;
        }

        tip.Measure(Size.Infinity);
        var width = Bounds.Width;
        var half = tip.DesiredSize.Width / 2;
        var center = half * 2 >= width ? width / 2 : Math.Clamp(x, half, width - half);
        _popup.HorizontalOffset = Math.Round(center - width / 2);
        if (!_popup.IsOpen)
        {
            _popup.IsOpen = true;
        }
    }

    private void ClearHover()
    {
        HoverTime = null;
        HoverChapterTitle = null;
        if (_popup is not null)
        {
            _popup.IsOpen = false;
        }

        InvalidateVisual();
    }

    private void EndScrub(bool commit)
    {
        if (!IsScrubbing)
        {
            return;
        }

        var target = ScrubPosition;
        IsScrubbing = false;
        ScrubPosition = null;
        if (commit && target is { } time)
        {
            Position = time;
            RaiseEvent(new SeekRequestedEventArgs(time));
        }

        RaiseEvent(new RoutedEventArgs(ScrubCompletedEvent));
    }

    private void CancelScrub()
    {
        EndScrub(commit: false);
    }

    private void Observe(IEnumerable<IChapterMark>? marks)
    {
        if (_observedCollection is not null)
        {
            _observedCollection.CollectionChanged -= OnMarksCollectionChanged;
            _observedCollection = null;
        }

        ObserveItems(null);
        // Subscribing only while attached keeps a long-lived view model collection from rooting this control.
        if (marks is null || !_isAttached)
        {
            return;
        }

        if (marks is INotifyCollectionChanged collection)
        {
            _observedCollection = collection;
            collection.CollectionChanged += OnMarksCollectionChanged;
        }

        ObserveItems(marks);
    }

    private void ObserveItems(IEnumerable<IChapterMark>? marks)
    {
        foreach (var item in _observedItems)
        {
            item.PropertyChanged -= OnMarkPropertyChanged;
        }

        _observedItems.Clear();
        foreach (var item in marks?.OfType<INotifyPropertyChanged>() ?? [])
        {
            item.PropertyChanged += OnMarkPropertyChanged;
            _observedItems.Add(item);
        }
    }

    // Chapter collections may change from a background metadata scan; rebuild on the UI thread.
    private void OnMarksCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnMarksCollectionChanged(sender, e));
            return;
        }

        ObserveItems(ChapterSegments);
        RebuildSpans();
    }

    private void OnMarkPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(RebuildSpans);
            return;
        }

        RebuildSpans();
    }

    private void RebuildSpans()
    {
        _spans = ScrubTimeline.BuildSpans(ChapterSegments ?? [], Duration);
        InvalidateVisual();
    }

    private double Snap(double value)
    {
        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        return Math.Floor(value * scale + 0.5) / scale;
    }
}
