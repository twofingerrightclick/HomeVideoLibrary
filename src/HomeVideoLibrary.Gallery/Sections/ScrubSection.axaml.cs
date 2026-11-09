using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using HomeVideoLibrary.UI.Controls;
using HomeVideoLibrary.UI.Logic;

namespace HomeVideoLibrary.Gallery.Sections;

public partial class ScrubSection : UserControl, IScreenshotScript
{
    private static readonly TimeSpan TapeLength = TimeSpan.FromMinutes(42);
    private static readonly TimeSpan TimerStep = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan MarkTolerance = TimeSpan.FromSeconds(1);

    private static readonly IReadOnlyList<ChapterMark> Chapters =
    [
        new(At(0, 0), "Arriving at Grandma's"),
        new(At(4, 35), "Opening presents"),
        new(At(13, 20), "Dad's new camcorder"),
        new(At(13, 25), "Lens cap still on"),
        new(At(13, 30), "Sledding on Miller Hill"),
        new(At(22, 40), "Snowball fight"),
        new(At(29, 5), "Christmas dinner"),
        new(At(37, 50), "Carols by the piano"),
    ];

    private readonly ObservableCollection<IChapterMark> _liveChapters = new(Chapters);
    private readonly DispatcherTimer _playTimer;

    public ScrubSection()
    {
        InitializeComponent();
        foreach (var bar in new[] { PlayingBar, MarkerBar, HoverBar, PlainBar, DisabledBar, LiveBar })
        {
            bar.Duration = TapeLength;
            bar.ChapterSegments = Chapters;
        }

        PlainBar.ChapterSegments = null;
        LiveBar.ChapterSegments = _liveChapters;

        PlayingBar.Position = TapeLength * 0.35;
        PlayingBar.BufferedPosition = TapeLength * 0.47;
        MarkerBar.Position = At(16, 10);
        MarkerBar.BufferedPosition = At(19, 0);
        MarkerBar.PreviewMarkerPosition = At(26, 30);
        HoverBar.Position = At(9, 0);
        HoverBar.BufferedPosition = At(12, 30);
        PlainBar.Position = At(30, 0);
        PlainBar.BufferedPosition = At(33, 0);
        DisabledBar.Position = At(20, 0);
        DisabledBar.BufferedPosition = At(24, 0);
        LiveBar.Position = At(2, 0);
        LiveBar.BufferedPosition = At(3, 30);

        _playTimer = new DispatcherTimer { Interval = TimerStep };
        _playTimer.Tick += (_, _) => Advance();
        PlayLatch.IsCheckedChanged += (_, _) => _playTimer.IsEnabled = PlayLatch.IsChecked == true;
        DetachedFromVisualTree += (_, _) => _playTimer.Stop();

        LiveBar.SeekRequested += (_, e) => LiveBar.BufferedPosition = e.Time + TimeSpan.FromSeconds(90);
        LiveBar.PropertyChanged += (_, e) =>
        {
            if (e.Property == ScrubBar.PositionProperty)
            {
                UpdateLiveReadout();
            }
        };
        MarkButton.PointerEntered += (_, _) => LiveBar.PreviewMarkerPosition = LiveBar.Position;
        MarkButton.PointerExited += (_, _) => LiveBar.PreviewMarkerPosition = null;
        MarkButton.Click += (_, _) => Mark();
        UpdateLiveReadout();
    }

    public void Prepare(Window window)
    {
        AssertClickSeeks(window);
        AssertDragSeeks(window);
        AssertKeyboardSeeks(window);
        window.MouseMove(PointOn(HoverBar, 17.75 / 42, window));
    }

    private void AssertClickSeeks(Window window)
    {
        var started = 0;
        var completed = 0;
        TimeSpan? sought = null;
        LiveBar.ScrubStarted += (_, _) => started++;
        LiveBar.ScrubCompleted += (_, _) => completed++;
        LiveBar.SeekRequested += (_, e) => sought = e.Time;

        var center = PointOn(LiveBar, 0.5, window);
        window.MouseDown(center, MouseButton.Left);
        Check(LiveBar.IsScrubbing, "pressing the bar starts a scrub");
        window.MouseUp(center, MouseButton.Left);

        Check(!LiveBar.IsScrubbing && started == 1 && completed == 1, "a click raises ScrubStarted and ScrubCompleted once");
        Check(sought is { } t && Near(t, TapeLength / 2), $"clicking the center seeks to 21:00 (got {sought})");
        Check(Near(LiveBar.Position, TapeLength / 2), "a click moves Position");
    }

    private void AssertDragSeeks(Window window)
    {
        var seeks = new List<TimeSpan>();
        EventHandler<SeekRequestedEventArgs> record = (_, e) => seeks.Add(e.Time);
        LiveBar.SeekRequested += record;

        var before = LiveBar.Position;
        window.MouseDown(PointOn(LiveBar, 0.25, window), MouseButton.Left);
        window.MouseMove(PointOn(LiveBar, 0.75, window));
        Check(seeks.Count == 0 && LiveBar.ScrubPosition is { } dragged && NearPixel(dragged, TapeLength * 0.75), "the thumb follows the drag before any seek");
        Check(LiveBar.Position == before, "Position is not written while dragging");
        window.MouseMove(PointOn(LiveBar, 0.75, window) + new Point(0, 200));
        Check(LiveBar.ScrubPosition is { } below && NearPixel(below, TapeLength * 0.75), "dragging below the bar keeps the horizontal position");
        window.MouseUp(PointOn(LiveBar, 0.75, window), MouseButton.Left);
        Check(seeks.Count == 1 && NearPixel(seeks[0], TapeLength * 0.75), "releasing a drag seeks once to the release point");

        LiveBar.SeekRequested -= record;
    }

    private void AssertKeyboardSeeks(Window window)
    {
        var before = LiveBar.Position;
        Press(window, PhysicalKey.ArrowRight, RawInputModifiers.None);
        Check(Near(LiveBar.Position, before + TimeSpan.FromSeconds(5)), "Right seeks forward 5 s");
        Press(window, PhysicalKey.ArrowLeft, RawInputModifiers.Shift);
        Check(Near(LiveBar.Position, before + TimeSpan.FromSeconds(4)), "Shift+Left seeks back 1 s");
        Press(window, PhysicalKey.End, RawInputModifiers.None);
        Check(LiveBar.Position == TapeLength, "End seeks to the end");
        Press(window, PhysicalKey.Home, RawInputModifiers.None);
        Check(LiveBar.Position == TimeSpan.Zero, "Home seeks to the start");
        LiveBar.Position = At(8, 0);
    }

    private static void Press(Window window, PhysicalKey key, RawInputModifiers modifiers)
    {
        window.KeyPressQwerty(key, modifiers);
        window.KeyReleaseQwerty(key, modifiers);
    }

    private void Advance()
    {
        if (LiveBar.IsScrubbing)
        {
            return;
        }

        LiveBar.Position = LiveBar.Position + TimerStep >= TapeLength ? TimeSpan.Zero : LiveBar.Position + TimerStep;
        if (LiveBar.BufferedPosition < LiveBar.Position + TimeSpan.FromSeconds(30))
        {
            LiveBar.BufferedPosition = LiveBar.Position + TimeSpan.FromSeconds(90);
        }
    }

    private void Mark()
    {
        var starts = _liveChapters.Select(c => c.Start).ToList();
        if (ChapterPlacement.HasChapterAt(starts, LiveBar.Position, MarkTolerance))
        {
            return;
        }

        var index = ChapterPlacement.PlaceholderIndex(starts, LiveBar.Position);
        _liveChapters.Insert(index, new ChapterMark(LiveBar.Position, $"Chapter {_liveChapters.Count + 1}"));
        LiveBar.PreviewMarkerPosition = null;
        UpdateLiveReadout();
    }

    private void UpdateLiveReadout()
    {
        var starts = _liveChapters.Select(c => c.Start).ToList();
        var position = LiveBar.Position;
        MarkButton.IsEnabled = !ChapterPlacement.HasChapterAt(starts, position, MarkTolerance);
        LiveReadout.Text = $"{ScrubTimeline.FormatTimestamp(position)} / {ScrubTimeline.FormatTimestamp(TapeLength)}"
            + $"  ·  a chapter marked here becomes row {ChapterPlacement.PlaceholderIndex(starts, position) + 1} of {starts.Count + 1}";
    }

    private static Point PointOn(Control bar, double fraction, Window window) =>
        bar.TranslatePoint(new Point(bar.Bounds.Width * fraction, bar.Bounds.Height / 2), window)
        ?? throw new InvalidOperationException($"{bar.Name} is not in the window.");

    private static bool Near(TimeSpan actual, TimeSpan expected) => (actual - expected).Duration() <= TimeSpan.FromSeconds(1);

    // Off-center points land up to a pixel (2.5 s here) away from width * fraction because of the groove inset.
    private static bool NearPixel(TimeSpan actual, TimeSpan expected) => (actual - expected).Duration() <= TimeSpan.FromSeconds(3);

    private static void Check(bool condition, string behavior)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"ScrubBar check failed: {behavior}.");
        }
    }

    private static TimeSpan At(int minutes, int seconds) => new(0, minutes, seconds);
}
