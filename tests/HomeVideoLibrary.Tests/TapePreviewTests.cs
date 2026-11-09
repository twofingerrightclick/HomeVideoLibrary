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
        using var service = new PreviewService { Chosen = [new(Guid.NewGuid(), TimeSpan.FromSeconds(60), "late"), new(Guid.NewGuid(), TimeSpan.FromSeconds(10), "early")] };
        using var session = new TapePreviewSession(service, Video);
        Assert.Equal("early", (await session.GetAsync(0, default)).ImagePath);
        Assert.Equal("late", (await session.GetAsync(9, default)).ImagePath);
        Assert.Equal(0, service.Captures); Assert.Equal(0, service.Saves);
        // Chosen frames are shared extracted metadata, never owned by the hover session.
        var shared = service.CreateFile();
        using (var chosenSession = new TapePreviewSession(new ChosenService(shared), Video))
            await chosenSession.GetAsync(0, default);
        Assert.True(File.Exists(shared));
    }

    [Fact]
    public async Task RepeatedBucketReusesCaptureAndCancelledDebounceDoesNotStartMediaWork()
    {
        using var service = new PreviewService();
        using var session = new TapePreviewSession(service, Video);
        using var cancellation = new CancellationTokenSource();
        var cancelled = session.GetAsync(3, cancellation.Token); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.Equal(0, service.Reads);
        var first = await session.GetAsync(3, default);
        Assert.Same(first, await session.GetAsync(3, default));
        Assert.Equal(1, service.Captures); Assert.Equal(0, service.Saves);
        Assert.InRange(first.Position.TotalSeconds, 0, 90);
        Assert.True(File.Exists(first.ImagePath));
        session.Dispose();
        Assert.False(File.Exists(first.ImagePath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateCaptureIsDeletedAfterRequestCancellationOrSessionDisposal(bool disposeSession)
    {
        using var service = new PreviewService { DelayCapture = true };
        using var session = new TapePreviewSession(service, Video);
        using var cancellation = new CancellationTokenSource();
        var pending = session.GetAsync(2, cancellation.Token);
        await service.CaptureStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (disposeSession) session.Dispose(); else cancellation.Cancel();
        service.ReleaseCapture.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.False(File.Exists(service.LastCapturePath));
    }

    private sealed class ChosenService(string path) : IVideoMetadataService
    {
        public Task<VideoMetadata> ReadAsync(LibraryVideo video, CancellationToken cancellationToken = default) =>
            Task.FromResult(VideoMetadata.Empty with { Thumbnails = [new(Guid.NewGuid(), TimeSpan.Zero, path)] });
        public Task SaveAsync(LibraryVideo video, VideoMetadata metadata, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<VideoThumbnail> CaptureThumbnailAsync(LibraryVideo video, TimeSpan position, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class PreviewService : IVideoMetadataService, IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "hvl-preview-" + Guid.NewGuid());
        public IReadOnlyList<VideoThumbnail> Chosen { get; init; } = [];
        public bool DelayCapture { get; init; }
        public TaskCompletionSource CaptureStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseCapture { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? LastCapturePath { get; private set; }
        public int Captures, Saves, Reads;
        public string CreateFile()
        {
            Directory.CreateDirectory(_root);
            var path = Path.Combine(_root, Guid.NewGuid() + ".jpg");
            File.WriteAllText(path, "test capture"); return path;
        }
        public Task<VideoMetadata> ReadAsync(LibraryVideo video, CancellationToken cancellationToken = default)
        { Reads++; return Task.FromResult(VideoMetadata.Empty with { Thumbnails = Chosen }); }
        public Task SaveAsync(LibraryVideo video, VideoMetadata metadata, CancellationToken cancellationToken = default)
        { Saves++; return Task.CompletedTask; }
        public async Task<VideoThumbnail> CaptureThumbnailAsync(LibraryVideo video, TimeSpan position, CancellationToken cancellationToken = default)
        {
            Captures++; CaptureStarted.TrySetResult();
            if (DelayCapture) await ReleaseCapture.Task; // Deliberately completes after cancellation.
            LastCapturePath = CreateFile();
            return new VideoThumbnail(Guid.NewGuid(), position, LastCapturePath);
        }
        public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
    }
}
