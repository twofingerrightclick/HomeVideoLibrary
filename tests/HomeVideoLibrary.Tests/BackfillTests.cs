using HomeVideoLibrary.Core;
using HomeVideoLibrary.Desktop.ViewModels;
using HomeVideoLibrary.Infrastructure;
using Xunit;
using static HomeVideoLibrary.Tests.TestLibrary;

namespace HomeVideoLibrary.Tests;

public sealed class BackfillTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hvl-backfill-" + Guid.NewGuid().ToString("N"));
    private readonly FakeMatroska _files = new();
    private readonly FakeIdentity _identity = new();

    private async Task<(LibraryModel Model, LibraryVideo[] Videos)> LibraryAsync(params string[] names)
    {
        var catalog = await CreateCatalogAsync(Path.Combine(_root, "library"));
        var videos = new List<LibraryVideo>();
        foreach (var name in names) videos.Add(await AddVideoAsync(catalog, name));
        var model = new LibraryModel(catalog, _files);
        await model.LoadAsync();
        return (model, [.. videos]);
    }

    [Fact]
    public async Task ThePassChecksEveryVideoWithAFilePublishesWhatItWroteAndIsRecordedOnce()
    {
        var (model, videos) = await LibraryAsync("old", "new", "gone");
        _identity.Writes.Add(videos[0].Id);
        File.Delete(model.Catalog.GetVideoPath(videos[2].RelativePath));
        _files.Set(model.Catalog.GetVideoPath(videos[0].RelativePath), VideoMetadata.Empty with { Title = "written" });
        var backfill = new IdentityBackfill(model, _identity);
        Assert.True(await backfill.IsDueAsync());

        var result = await backfill.RunAsync();

        Assert.Equal((3, 1), (result.Checked, result.Updated));
        Assert.Empty(result.Failures);
        Assert.Equal(new HashSet<Guid> { videos[0].Id, videos[1].Id }, _identity.Checked.ToHashSet());
        Assert.Equal("written", model.Snapshot.Find(videos[0].Id)!.Title);
        Assert.False(await backfill.IsDueAsync());
        Assert.False(await new IdentityBackfill(model, _identity).IsDueAsync());
    }

    [Fact]
    public async Task ACancelledPassIsNotRecordedAndAFailureDoesNotStopIt()
    {
        var (model, videos) = await LibraryAsync("first", "second");
        _identity.Failing.Add(videos[0].Id);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var backfill = new IdentityBackfill(model, _identity);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => backfill.RunAsync(cancellationToken: cancelled.Token));
        Assert.True(await backfill.IsDueAsync());

        var result = await backfill.RunAsync();
        Assert.Single(result.Failures);
        Assert.Equal(2, _identity.Checked.Count);
        Assert.False(await backfill.IsDueAsync());
    }

    [Fact]
    public async Task OpeningALibraryQueuesTheBackfillOnlyUntilItHasRun()
    {
        var catalog = await CreateCatalogAsync(Path.Combine(_root, "library"));
        await AddVideoAsync(catalog, "old");

        var first = await OpenAsync(catalog);
        var job = Assert.Single(first.Jobs.Jobs);
        Assert.Equal(BackfillJob.Title, job.Title);
        Assert.Equal(JobLane.Render, job.Lane);
        await job.Completion;
        Assert.Equal(JobState.Succeeded, job.State);
        Assert.True(await first.RequestCloseAsync());

        var second = await OpenAsync(catalog);
        Assert.Empty(second.Jobs.Jobs);
        Assert.Single(_identity.Checked);
        Assert.True(await second.RequestCloseAsync());
    }

    private async Task<MainWindowViewModel> OpenAsync(LibraryCatalog catalog)
    {
        var viewModel = new MainWindowViewModel(new MainWindowDependencies
        {
            Interaction = new FakeInteraction(),
            Settings = new LibrarySettings(Path.Combine(_root, "settings.json")),
            CreateServices = _ => new LibraryServices(_files, new UnusedMetadata(), _identity),
            FindUnavailableTools = _ => Task.FromResult<IReadOnlyList<string>>([]),
            SweepScratch = (_, _) => Task.CompletedTask,
            Post = action => action(),
        });
        await viewModel.OpenLibraryAsync(catalog.Paths.RootDirectory);
        await viewModel.Settings.RefreshIndexAsync();
        return viewModel;
    }

    public void Dispose()
    {
        DeleteDirectory(_root);
        DeleteDirectory(_files.OutputDirectory);
    }

    private sealed class FakeIdentity : IIdentityWriter
    {
        public List<Guid> Checked { get; } = [];
        public HashSet<Guid> Writes { get; } = [];
        public HashSet<Guid> Failing { get; } = [];

        public Task<bool> FillMissingIdentityAsync(LibraryVideo video, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (Checked) Checked.Add(video.Id);
            if (Failing.Contains(video.Id)) throw new InvalidDataException("mkvpropedit failed");
            return Task.FromResult(Writes.Contains(video.Id));
        }
    }

    private sealed class UnusedMetadata : IVideoMetadataService
    {
        public Task<VideoMetadata> ReadAsync(LibraryVideo video, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SaveAsync(LibraryVideo video, VideoMetadata metadata, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<VideoThumbnail> CaptureThumbnailAsync(LibraryVideo video, TimeSpan position, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
