using HomeVideoLibrary.Core;
using HomeVideoLibrary.Desktop.ViewModels;
using Xunit;

namespace HomeVideoLibrary.Tests;

public sealed class TapePreviewTests
{
    private static readonly LibraryVideo Video = new(Guid.NewGuid(), "Videos/test.mkv", "Test", TimeSpan.FromSeconds(90), DateTimeOffset.Now, DateTimeOffset.Now);

    [Fact]
    public async Task ChosenFramesAreSortedAndNoCaptureOrSaveOccurs()
    {
        var service = new PreviewService { Chosen = [new(Guid.NewGuid(), TimeSpan.FromSeconds(60), "late"), new(Guid.NewGuid(), TimeSpan.FromSeconds(10), "early")] };
        using var session = new TapePreviewSession(service, Video);
        Assert.Equal("early", (await session.GetAsync(0, default)).ImagePath);
        Assert.Equal("late", (await session.GetAsync(9, default)).ImagePath);
        Assert.Equal(0, service.Captures); Assert.Equal(0, service.Saves);
    }

    [Fact]
    public async Task RepeatedBucketReusesCaptureAndCancelledDebounceDoesNotStartMediaWork()
    {
        var service = new PreviewService();
        using var session = new TapePreviewSession(service, Video);
        using var cancellation = new CancellationTokenSource();
        var cancelled = session.GetAsync(3, cancellation.Token); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.Equal(0, service.Reads);
        var first = await session.GetAsync(3, default);
        Assert.Same(first, await session.GetAsync(3, default));
        Assert.Equal(1, service.Captures); Assert.Equal(0, service.Saves);
        Assert.InRange(first.Position.TotalSeconds, 0, 90);
    }

    private sealed class PreviewService : IVideoMetadataService
    {
        public IReadOnlyList<VideoThumbnail> Chosen { get; init; } = [];
        public int Captures, Saves, Reads;
        public Task<VideoMetadata> ReadAsync(LibraryVideo video, CancellationToken cancellationToken = default)
        { Reads++; return Task.FromResult(VideoMetadata.Empty with { Thumbnails = Chosen }); }
        public Task SaveAsync(LibraryVideo video, VideoMetadata metadata, CancellationToken cancellationToken = default)
        { Saves++; return Task.CompletedTask; }
        public Task<VideoThumbnail> CaptureThumbnailAsync(LibraryVideo video, TimeSpan position, CancellationToken cancellationToken = default)
        { Captures++; return Task.FromResult(new VideoThumbnail(Guid.NewGuid(), position, "cache.jpg")); }
    }
}
