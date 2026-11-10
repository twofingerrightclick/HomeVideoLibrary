using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using HomeVideoLibrary.Core;
using HomeVideoLibrary.Desktop.ViewModels;
using HomeVideoLibrary.Desktop.Views;
using HomeVideoLibrary.Infrastructure;
using HomeVideoLibrary.UI.Controls;
using HomeVideoLibrary.UI.Logic;
using Xunit;

namespace HomeVideoLibrary.Desktop.UiTests;

public sealed class TapeHoverTests : IDisposable
{
    private static readonly TimeSpan Duration = TimeSpan.FromSeconds(100);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hvl-tape-hover-" + Guid.NewGuid().ToString("N"));
    private readonly Frames _frames = new();

    [AvaloniaFact]
    public void A_tape_without_a_strip_shows_its_time_over_a_placeholder_then_crops_of_the_strip()
    {
        using var shelf = new Shelf(_root, _frames);

        shelf.HoverAt(0.62);

        Assert.True(shelf.Popup.IsOpen);
        Assert.True(shelf.Plate.IsVisible);
        Assert.Null(shelf.Current.Source);
        Assert.Equal(ScrubTimeline.FormatTimestamp(HoverStripStore.SlotTime(12, Duration)), shelf.Time.Text);

        _frames.Release.SetResult();
        WaitFor(() => shelf.Current.Source is CroppedBitmap);
        Assert.False(shelf.Plate.IsVisible);
        var strip = new PixelSize(Frames.TileWidth * HoverStripStore.Columns, Frames.TileHeight * HoverStripStore.Rows);
        Assert.Equal(new PixelRect(2 * Frames.TileWidth, 2 * Frames.TileHeight, Frames.TileWidth, Frames.TileHeight),
            ((CroppedBitmap)shelf.Current.Source!).SourceRect);

        shelf.HoverAt(0.07);
        Assert.Equal(TapePreviewSession.TileOf(1, strip), ((CroppedBitmap)shelf.Current.Source!).SourceRect);
        Assert.Equal(ScrubTimeline.FormatTimestamp(HoverStripStore.SlotTime(1, Duration)), shelf.Time.Text);
        Assert.Equal(HoverStripStore.Slots, _frames.Captures);
    }

    [AvaloniaFact]
    public void A_strip_that_cannot_be_built_closes_the_popup_and_its_placeholder()
    {
        using var shelf = new Shelf(_root, _frames);

        shelf.HoverAt(0.5);
        Assert.True(shelf.Plate.IsVisible);
        _frames.Release.SetException(new InvalidDataException("unreadable"));

        WaitFor(() => !shelf.Popup.IsOpen);
        Assert.False(shelf.Plate.IsVisible);
        shelf.HoverAt(0.3);
        Assert.False(shelf.Popup.IsOpen);
    }

    [AvaloniaFact]
    public void Coming_back_to_a_tape_whose_strip_finished_meanwhile_shows_its_frame_without_a_placeholder()
    {
        using var shelf = new Shelf(_root, _frames);
        shelf.HoverAt(0.5);
        WaitFor(() => _frames.Captures > 0);

        shelf.Leave();
        Assert.False(shelf.Popup.IsOpen);
        _frames.Release.SetResult();
        WaitFor(() => shelf.Store.TryGet(shelf.Record) is not null);
        shelf.HoverAt(0.5);

        Assert.False(shelf.Plate.IsVisible);
        WaitFor(() => shelf.Current.Source is CroppedBitmap);
        Assert.True(shelf.Popup.IsOpen);
        Assert.False(shelf.Plate.IsVisible);
    }

    /// <summary>One tape on a window, with a scheduler building its strip from <see cref="Frames"/>.</summary>
    private sealed class Shelf : IDisposable
    {
        private readonly Window _window;
        private readonly TapeView _view;
        private readonly HoverStripScheduler _scheduler;

        public Shelf(string root, Frames frames)
        {
            var paths = new LibraryPaths(root);
            Directory.CreateDirectory(paths.VideosDirectory);
            var video = Path.Combine(paths.VideosDirectory, "tape.mkv");
            File.WriteAllText(video, "tape");
            var id = Guid.NewGuid();
            Record = new VideoRecord(id, LibraryPaths.VideoRelativePath(id), "Tape", Duration, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
                with { State = VideoRecordState.Ok, MediaIdentity = "abcd", Stamp = FileStamp.TryRead(video) };
            Store = new HoverStripStore(paths, frames);
            _scheduler = new HoverStripScheduler(Store, _ => video, () => false);
            var tape = new TapeViewModel(Record, (_, _) => { }, shelved => new TapePreviewSession(shelved.Record, _scheduler),
                (_, _) => Task.CompletedTask, _ => { });
            _view = new TapeView { DataContext = tape, Width = 750, Height = 100, HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top };
            _window = new Window { Content = _view, Width = 1000, Height = 400 };
            _window.Show();
            Dispatcher.UIThread.RunJobs();
        }

        public VideoRecord Record { get; }
        public HoverStripStore Store { get; }
        public Popup Popup => _view.FindControl<Popup>("PreviewPopup")!;
        public Border Plate => _view.FindControl<Border>("PreparingPlate")!;
        public Image Current => _view.FindControl<Image>("CurrentPreview")!;
        public TextBlock Time => _view.FindControl<TextBlock>("PreviewTime")!;

        public void HoverAt(double fraction)
        {
            var spine = _view.FindControl<VhsSpine>("Spine")!;
            _window.MouseMove(spine.TranslatePoint(new Point(spine.Bounds.Width * fraction, spine.Bounds.Height / 2), _window)!.Value);
            Dispatcher.UIThread.RunJobs();
        }

        public void Leave()
        {
            _window.MouseMove(new Point(_window.Width - 5, _window.Height - 5));
            Dispatcher.UIThread.RunJobs();
        }

        public void Dispose()
        {
            _window.Close();
            var disposed = _scheduler.DisposeAsync().AsTask();
            WaitFor(() => disposed.IsCompleted);
        }
    }

    private static void WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        Assert.True(condition());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>Captures wait for <see cref="Release"/>; tiling writes a real image of 5 × 4 tiles.</summary>
    private sealed class Frames : IMatroskaFileEditor
    {
        public const int TileWidth = 32, TileHeight = 18;
        private readonly byte[] _strip = Strip();
        private int _captures;

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Captures => _captures;

        public async Task CaptureKeyframeAsync(string mkvPath, TimeSpan position, string outputPath, int maxWidth,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _captures);
            await Release.Task.WaitAsync(cancellationToken);
            await File.WriteAllTextAsync(outputPath, "frame", cancellationToken);
        }

        public Task TileAsync(IReadOnlyList<string> images, string outputPath, int columns, CancellationToken cancellationToken = default) =>
            File.WriteAllBytesAsync(outputPath, _strip, cancellationToken);

        private static byte[] Strip()
        {
            using var bitmap = new RenderTargetBitmap(new PixelSize(TileWidth * HoverStripStore.Columns, TileHeight * HoverStripStore.Rows));
            using var stream = new MemoryStream();
            bitmap.Save(stream);
            return stream.ToArray();
        }

        public Task CaptureFrameAsync(string mkvPath, TimeSpan position, string outputPath, int maxWidth = 960,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<VideoTextMetadata> ReadTextAsync(string mkvPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task WriteStagedAsync(string mkvPath, VideoMetadata metadata, MetadataWriteOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> ExtractThumbnailAsync(string mkvPath, Guid videoId, VideoThumbnailRef thumbnail,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
