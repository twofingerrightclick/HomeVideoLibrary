using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
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
        var paths = new LibraryPaths(_root);
        Directory.CreateDirectory(paths.VideosDirectory);
        var video = Path.Combine(paths.VideosDirectory, "tape.mkv");
        File.WriteAllText(video, "tape");
        var id = Guid.NewGuid();
        var record = new VideoRecord(id, LibraryPaths.VideoRelativePath(id), "Tape", Duration, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
            with { State = VideoRecordState.Ok, MediaIdentity = "abcd", Stamp = FileStamp.TryRead(video) };
        var scheduler = new HoverStripScheduler(new HoverStripStore(paths, _frames), _ => video, new JobQueue(_ => Task.CompletedTask));
        var tape = new TapeViewModel(record, (_, _) => { }, shelved => new TapePreviewSession(shelved.Record, scheduler),
            (_, _) => Task.CompletedTask, _ => { });
        var view = new TapeView { DataContext = tape };
        var window = new Window { Content = view, Width = 1000, Height = 300 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var spine = view.FindControl<VhsSpine>("Spine")!;
        var popup = view.FindControl<Popup>("PreviewPopup")!;
        var plate = view.FindControl<Border>("PreparingPlate")!;
        var current = view.FindControl<Image>("CurrentPreview")!;
        var time = view.FindControl<TextBlock>("PreviewTime")!;
        Point At(double fraction) => spine.TranslatePoint(new Point(spine.Bounds.Width * fraction, spine.Bounds.Height / 2), window)!.Value;

        window.MouseMove(At(0.62));
        Dispatcher.UIThread.RunJobs();

        Assert.True(popup.IsOpen);
        Assert.True(plate.IsVisible);
        Assert.Null(current.Source);
        Assert.Equal(ScrubTimeline.FormatTimestamp(HoverStripStore.SlotTime(12, Duration)), time.Text);

        _frames.Release.SetResult();
        WaitFor(() => current.Source is CroppedBitmap);
        Assert.False(plate.IsVisible);
        var strip = new PixelSize(Frames.TileWidth * HoverStripStore.Columns, Frames.TileHeight * HoverStripStore.Rows);
        Assert.Equal(new PixelRect(2 * Frames.TileWidth, 2 * Frames.TileHeight, Frames.TileWidth, Frames.TileHeight),
            ((CroppedBitmap)current.Source!).SourceRect);

        window.MouseMove(At(0.07));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(TapePreviewSession.TileOf(1, strip), ((CroppedBitmap)current.Source!).SourceRect);
        Assert.Equal(ScrubTimeline.FormatTimestamp(HoverStripStore.SlotTime(1, Duration)), time.Text);
        Assert.Equal(HoverStripStore.Slots, _frames.Captures);

        window.Close();
        var disposed = scheduler.DisposeAsync().AsTask();
        WaitFor(() => disposed.IsCompleted);
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
