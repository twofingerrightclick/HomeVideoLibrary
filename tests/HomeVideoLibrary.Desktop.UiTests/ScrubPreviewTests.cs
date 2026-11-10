using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using HomeVideoLibrary.Core;
using HomeVideoLibrary.Desktop.ViewModels.VideoPage;
using HomeVideoLibrary.Infrastructure;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace HomeVideoLibrary.Desktop.UiTests;

public sealed class ScrubPreviewTests : IDisposable
{
    private static readonly TimeSpan Duration = TimeSpan.FromHours(2);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hvl-scrub-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new();
    private readonly Frames _frames = new();

    [AvaloniaFact]
    public void Passing_over_the_bar_captures_only_where_the_pointer_rests()
    {
        using var preview = Preview();

        preview.HoverTime = TimeSpan.FromMinutes(10);
        preview.HoverTime = TimeSpan.FromMinutes(20);
        preview.HoverTime = TimeSpan.FromMinutes(30);
        _time.Advance(ScrubPreviewViewModel.Debounce);
        WaitFor(() => preview.Image is not null);

        Assert.Single(_frames.Captured);
        Assert.InRange(_frames.Captured.Single(), TimeSpan.FromMinutes(30) - preview.SlotLength, TimeSpan.FromMinutes(30) + preview.SlotLength);
    }

    [AvaloniaFact]
    public void A_frame_shows_only_for_its_own_slot()
    {
        using var preview = Preview();
        preview.HoverTime = TimeSpan.FromMinutes(10);
        _time.Advance(ScrubPreviewViewModel.Debounce);
        WaitFor(() => preview.Image is not null);
        var first = preview.Image;

        preview.HoverTime = TimeSpan.FromMinutes(50);
        Assert.Null(preview.Image);
        _time.Advance(ScrubPreviewViewModel.Debounce);
        WaitFor(() => preview.Image is not null);
        Assert.NotSame(first, preview.Image);

        preview.HoverTime = TimeSpan.FromMinutes(10);
        Assert.Same(first, preview.Image);
        Assert.Equal(2, _frames.Captured.Count);
    }

    [AvaloniaFact]
    public void Slots_stay_short_on_long_videos()
    {
        using var preview = Preview();
        Assert.True(preview.SlotLength <= TimeSpan.FromSeconds(30), $"{preview.SlotLength} per frame on a 2 h video");
    }

    private ScrubPreviewViewModel Preview()
    {
        var id = Guid.NewGuid();
        var record = new VideoRecord(id, LibraryPaths.VideoRelativePath(id), "Long video", Duration, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
            with { Stamp = new FileStamp(1, 1), State = VideoRecordState.Ok };
        var cache = new PreviewCache(new LibraryPaths(_root), _frames);
        return new ScrubPreviewViewModel(record, Path.Combine(_root, "video.mkv"), cache, _time);
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

    /// <summary>Writes a small real image for every capture and records the captured positions.</summary>
    private sealed class Frames : IMatroskaFileEditor
    {
        private readonly byte[] _image = Image();

        public ConcurrentQueue<TimeSpan> Queue { get; } = new();
        public IReadOnlyCollection<TimeSpan> Captured => Queue;

        public async Task CaptureFrameAsync(string mkvPath, TimeSpan position, string outputPath, int maxWidth = 960,
            CancellationToken cancellationToken = default)
        {
            Queue.Enqueue(position);
            await File.WriteAllBytesAsync(outputPath, _image, cancellationToken);
        }

        private static byte[] Image()
        {
            using var bitmap = new RenderTargetBitmap(new PixelSize(32, 18));
            using var stream = new MemoryStream();
            bitmap.Save(stream);
            return stream.ToArray();
        }

        public Task<VideoTextMetadata> ReadTextAsync(string mkvPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task WriteStagedAsync(string mkvPath, VideoMetadata metadata, MetadataWriteOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> ExtractThumbnailAsync(string mkvPath, Guid videoId, VideoThumbnailRef thumbnail,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CaptureKeyframeAsync(string mkvPath, TimeSpan position, string outputPath, int maxWidth,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task TileAsync(IReadOnlyList<string> images, string outputPath, int columns, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
