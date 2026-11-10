using HomeVideoLibrary.Core;
using HomeVideoLibrary.Desktop.ViewModels;
using HomeVideoLibrary.Infrastructure;
using Xunit;
using static HomeVideoLibrary.Tests.TestLibrary;

namespace HomeVideoLibrary.Tests;

public sealed class TapePreviewTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hvl-preview-" + Guid.NewGuid().ToString("N"));
    private readonly FakeMatroska _files = new();
    private LibraryPaths Paths => new(_root);

    [Theory]
    [InlineData(20, 0, 0)]
    [InlineData(20, 9, 19)]
    [InlineData(20, 5, 11)]
    [InlineData(3, 9, 2)]
    [InlineData(1, 9, 0)]
    public void ScrubbingAcrossTheTapeReachesTheFirstAndLastChosenThumbnail(int count, int bucket, int expected) =>
        Assert.Equal(expected, TapePreviewSession.ThumbnailIndex(bucket, count));

    [Fact]
    public async Task ChosenThumbnailsComeFromTheRecordRefsWithoutReadingTheMkv()
    {
        var late = new VideoThumbnailRef(Guid.NewGuid(), TimeSpan.FromSeconds(60), "late.jpg", "AA");
        var early = new VideoThumbnailRef(Guid.NewGuid(), TimeSpan.FromSeconds(10), "early.jpg", "BB");
        var video = Record("video") with { Thumbnails = [early, late] };
        using var session = new TapePreviewSession(video, "video.mkv", _files, new PreviewCache(Paths, _files));

        Assert.Equal(early.Position, (await session.GetAsync(0, default)).Position);
        var frame = await session.GetAsync(9, default);
        Assert.Equal(late.Position, frame.Position);
        Assert.True(File.Exists(frame.ImagePath));
        Assert.Equal((0, 0, 2), (_files.Reads, _files.Captures, _files.Extracts));
    }

    [Fact]
    public async Task UnannotatedVideosUseCachedCapturesAndACancelledDebounceStartsNoWork()
    {
        var previews = new PreviewCache(Paths, _files);
        var video = Record("video");
        using var session = new TapePreviewSession(video, "video.mkv", _files, previews);
        using (var cancellation = new CancellationTokenSource())
        {
            var cancelled = session.GetAsync(3, cancellation.Token); cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        }
        Assert.Equal(0, _files.Captures);

        var first = await session.GetAsync(3, default);
        using var again = new TapePreviewSession(video, "video.mkv", _files, previews);
        Assert.Equal(first, await again.GetAsync(3, default));
        Assert.Equal(1, _files.Captures);
        Assert.Equal(Path.Combine(Paths.PreviewDirectory, video.Id.ToString("N"), "3.jpg"), first.ImagePath);
        Assert.InRange(first.Position.TotalSeconds, 0, video.Duration.TotalSeconds);
    }

    [Fact]
    public async Task CaptureCacheIsBoundedAndEvictsTheLeastRecentlyUsedFrame()
    {
        var previews = new PreviewCache(Paths, _files, capacity: 2);
        var video = Guid.NewGuid();
        var zero = await previews.GetAsync(video, null, "video.mkv", TimeSpan.Zero, 0, default);
        var one = await previews.GetAsync(video, null, "video.mkv", TimeSpan.FromSeconds(1), 1, default);
        await previews.GetAsync(video, null, "video.mkv", TimeSpan.Zero, 0, default);
        var two = await previews.GetAsync(video, null, "video.mkv", TimeSpan.FromSeconds(2), 2, default);

        Assert.Equal(2, previews.Count);
        Assert.True(File.Exists(zero)); Assert.True(File.Exists(two));
        Assert.False(File.Exists(one));
        Assert.Equal(3, _files.Captures);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(zero)!, "*.tmp.jpg"));
    }

    [Fact]
    public async Task FailedCaptureIsRetriedOnlyAfterTheFileChangesAndCancelledCaptureLeavesNoFile()
    {
        var previews = new PreviewCache(Paths, _files);
        var broken = Guid.NewGuid();
        _files.BeforeCapture = _ => throw new InvalidDataException("ffmpeg failed");
        await Assert.ThrowsAsync<InvalidDataException>(() => previews.GetAsync(broken, null, "broken.mkv", TimeSpan.Zero, 0, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => previews.GetAsync(broken, null, "broken.mkv", TimeSpan.Zero, 1, default));
        Assert.Equal(1, _files.Captures);
        _files.BeforeCapture = null;
        var changed = new FileStamp(2, 2); // The file was replaced: try again.
        Assert.True(File.Exists(await previews.GetAsync(broken, changed, "broken.mkv", TimeSpan.Zero, 1, default)));

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _files.BeforeCapture = async token => { started.SetResult(); await Task.Delay(Timeout.Infinite, token); };
        using var cancellation = new CancellationTokenSource();
        var video = Guid.NewGuid();
        var pending = previews.GetAsync(video, null, "video.mkv", TimeSpan.Zero, 0, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(1, previews.Count); // Only the retried capture above.
        _files.BeforeCapture = null;
        Assert.True(File.Exists(await previews.GetAsync(video, null, "video.mkv", TimeSpan.Zero, 0, default)));
    }

    [Fact]
    public async Task RealCaptureWritesASquarePixelJpeg()
    {
        var catalog = await CreateCatalogAsync(_root);
        var tools = MediaTools.Discover();
        var source = Path.Combine(catalog.Paths.VideosDirectory, "real.mkv");
        var generated = await new MediaProcess().RunAsync(tools.Ffmpeg, ["-v", "error", "-n", "-f", "lavfi", "-i", "testsrc=size=160x120:rate=10", "-t", "2",
            "-c:v", "libx264", "-pix_fmt", "yuv420p", source], TimeSpan.FromSeconds(30));
        Assert.Equal(0, generated.ExitCode);
        var previews = new PreviewCache(catalog.Paths, new MatroskaMetadataService(catalog, tools));

        var path = await previews.GetAsync(Guid.NewGuid(), null, source, TimeSpan.FromSeconds(1), 5, default);
        var bytes = await File.ReadAllBytesAsync(path);
        Assert.True(bytes.Length > 100 && bytes[0] == 0xFF && bytes[1] == 0xD8);
    }

    public void Dispose()
    {
        DeleteDirectory(_root);
        DeleteDirectory(_files.OutputDirectory);
    }
}
