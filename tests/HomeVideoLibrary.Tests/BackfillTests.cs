using System.Collections.Concurrent;
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

    private async Task<(LibraryModel Model, LibraryVideo[] Videos)> LibraryAsync(int count)
    {
        var catalog = await CreateCatalogAsync(Path.Combine(_root, "library"));
        var videos = new List<LibraryVideo>();
        for (var index = 0; index < count; index++)
            videos.Add(await AddVideoAsync(catalog, $"video {index}", new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(-index)));
        var model = new LibraryModel(catalog, _files);
        await model.LoadAsync();
        return (model, [.. videos]);
    }

    [Fact]
    public async Task ASliceWritesWhatIsMissingDefersBusyVideosAndPassesOverGoneFiles()
    {
        var (model, videos) = await LibraryAsync(4);
        _identity.Writes.Add(videos[0].Id);
        _files.Set(model.Catalog.GetVideoPath(videos[0].RelativePath), VideoMetadata.Empty with { Title = "written" });
        File.Delete(model.Catalog.GetVideoPath(videos[2].RelativePath));
        await model.RemoveVideosAsync([videos[3].Id]);
        var backfill = new IdentityBackfill(model, _identity);

        var slice = await backfill.RunAsync(videos.Select(video => video.Id).ToArray(), id => id == videos[1].Id);

        Assert.Equal(1, slice.Updated);
        Assert.Equal([videos[1].Id], slice.Deferred);
        Assert.Empty(slice.Skipped);
        Assert.Equal([videos[0].Id], _identity.Checked);
        Assert.Equal("written", model.Snapshot.Find(videos[0].Id)!.Title);
        Assert.Equal([videos[1].Id], await backfill.PlanAsync());
    }

    [Fact]
    public Task ThePassRunsInSlicesThatLetOtherJobsInAndIsRecordedOnce() => UiThread.Run(async () =>
    {
        var (model, _) = await LibraryAsync(IdentityBackfill.SliceSize + 5);
        var jobs = new JobQueue(_ => Task.CompletedTask);
        var started = new ConcurrentQueue<string>();
        var firstSlice = new TaskCompletionSource();
        _identity.Before = async () =>
        {
            if (_identity.Checked.Count == 1) await firstSlice.Task;
        };
        jobs.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(JobQueue.ActiveCount)) return;
            foreach (var job in jobs.Jobs.Where(job => job.IsRunning && !started.Contains(job.Title))) started.Enqueue(job.Title);
        };

        var pass = BackfillJob.RunAsync(jobs, new IdentityBackfill(model, _identity), _ => false, () => true);
        var user = jobs.Enqueue("Trim", JobLane.Render, (_, _) => Task.FromResult(JobOutcome.Done("ok")));
        firstSlice.SetResult();
        await pass;

        Assert.Equal([$"{BackfillJob.Title} (1–25 of 30)", "Trim", $"{BackfillJob.Title} (26–30 of 30)"], started);
        Assert.Equal(30, _identity.Checked.Count);
        Assert.True(File.Exists(IdentityBackfill.StampPath(model.Catalog.Paths)));
        Assert.False(new IdentityBackfill(model, _identity).IsDue);
    });

    [Fact]
    public Task AnOpenVideoIsRetriedOnceAfterTheRestAndThenWaitsForTheNextOpen() => UiThread.Run(async () =>
    {
        var (model, videos) = await LibraryAsync(3);
        var jobs = new JobQueue(_ => Task.CompletedTask);
        var queued = new List<string>();
        jobs.Jobs.CollectionChanged += (_, args) => queued.AddRange(args.NewItems?.Cast<JobViewModel>().Select(job => job.Title) ?? []);

        await BackfillJob.RunAsync(jobs, new IdentityBackfill(model, _identity), id => id == videos[0].Id, () => true);

        Assert.Equal([videos[1].Id, videos[2].Id], _identity.Checked);
        Assert.Equal([$"{BackfillJob.Title} (1–3 of 3)", $"{BackfillJob.Title} (videos that were open)"], queued);
        Assert.False(File.Exists(IdentityBackfill.StampPath(model.Catalog.Paths)));

        await BackfillJob.RunAsync(jobs, new IdentityBackfill(model, _identity), _ => false, () => true);

        Assert.Equal([videos[1].Id, videos[2].Id, videos[0].Id], _identity.Checked);
        Assert.True(File.Exists(IdentityBackfill.StampPath(model.Catalog.Paths)));
        Assert.False(File.Exists(IdentityBackfill.PendingPath(model.Catalog.Paths)));
    });

    [Fact]
    public Task AVideoWhoseFileIsGoneDoesNotKeepThePassDue() => UiThread.Run(async () =>
    {
        var (model, videos) = await LibraryAsync(2);
        File.Delete(model.Catalog.GetVideoPath(videos[1].RelativePath));

        await BackfillJob.RunAsync(new JobQueue(_ => Task.CompletedTask), new IdentityBackfill(model, _identity), _ => false, () => true);

        Assert.Equal([videos[0].Id], _identity.Checked);
        Assert.False(new IdentityBackfill(model, _identity).IsDue);
    });

    [Fact]
    public Task AFailingVideoIsTriedAgainOnLaterOpensAloneUntilThePassGivesUpOnIt() => UiThread.Run(async () =>
    {
        var (model, videos) = await LibraryAsync(2);
        _identity.Failing.Add(videos[1].Id);

        for (var open = 1; open <= IdentityBackfill.MaxAttempts; open++)
        {
            Assert.True(new IdentityBackfill(model, _identity).IsDue);
            await BackfillJob.RunAsync(new JobQueue(_ => Task.CompletedTask), new IdentityBackfill(model, _identity), _ => false, () => true);
        }

        Assert.Equal([videos[0].Id, .. Enumerable.Repeat(videos[1].Id, IdentityBackfill.MaxAttempts)], _identity.Checked);
        Assert.False(new IdentityBackfill(model, _identity).IsDue);
    });

    [Fact]
    public Task AStoppedPassContinuesWhereItStoppedAndNeverChecksAVideoTwice() => UiThread.Run(async () =>
    {
        var (model, videos) = await LibraryAsync(IdentityBackfill.SliceSize + 5);
        var jobs = new JobQueue(_ => Task.CompletedTask);
        var continuing = false;

        await BackfillJob.RunAsync(jobs, new IdentityBackfill(model, _identity), _ => false, () => continuing);
        Assert.Equal(IdentityBackfill.SliceSize, _identity.Checked.Count);
        var added = await AddVideoAsync(model.Catalog, "imported since");
        await model.UpsertFromFileAsync(added);
        continuing = true;
        await BackfillJob.RunAsync(jobs, new IdentityBackfill(model, _identity), _ => false, () => continuing);

        Assert.Equal(videos.Select(video => video.Id).Order(), _identity.Checked.Order());
        Assert.DoesNotContain(added.Id, _identity.Checked);
        Assert.False(new IdentityBackfill(model, _identity).IsDue);
    });

    [Fact]
    public async Task AUserSaveThatClearsATitleHandlesItsVideoAndOtherSavesLeaveItPending()
    {
        var (model, videos) = await LibraryAsync(2);
        _files.Set(model.Catalog.GetVideoPath(videos[0].RelativePath), VideoMetadata.Empty with { Title = "Birthday" });
        await model.UpsertFromFileAsync(videos[0]);
        var backfill = new IdentityBackfill(model, _identity);
        var cleared = model.Snapshot.Find(videos[0].Id)!;
        var described = model.Snapshot.Find(videos[1].Id)!;

        await backfill.SavedAsync(cleared, VideoMetadata.Empty);
        await backfill.SavedAsync(described, VideoMetadata.Empty with { Description = "At the lake" });
        await backfill.RunAsync([videos[0].Id, videos[1].Id], _ => false);

        Assert.Equal([videos[1].Id], _identity.Checked);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0f8fad5b-d9cb")]
    public async Task ADamagedPendingListIsRebuiltRatherThanReadAsNothingPending(string content)
    {
        var (model, videos) = await LibraryAsync(2);
        await new IdentityBackfill(model, _identity).RunAsync([videos[0].Id], _ => true);
        Assert.True(File.Exists(IdentityBackfill.PendingPath(model.Catalog.Paths)));
        await File.WriteAllTextAsync(IdentityBackfill.PendingPath(model.Catalog.Paths), content);

        var plan = await new IdentityBackfill(model, _identity).PlanAsync();

        Assert.Equal(videos.Select(video => video.Id).Order(), plan.Order());
        Assert.False(File.Exists(IdentityBackfill.StampPath(model.Catalog.Paths)));
    }

    [Fact]
    public async Task AnEmptyLibraryIsNotDue()
    {
        var (model, _) = await LibraryAsync(0);
        Assert.False(new IdentityBackfill(model, _identity).IsDue);
    }

    [Fact]
    public async Task OpeningALibraryRunsTheBackfillOnlyUntilItHasCompleted()
    {
        var catalog = await CreateCatalogAsync(Path.Combine(_root, "library"));
        await AddVideoAsync(catalog, "old");

        var first = await OpenAsync(catalog);
        await WaitUntilAsync(() => File.Exists(IdentityBackfill.StampPath(catalog.Paths)));
        await WaitUntilAsync(() => first.Jobs.Jobs.Count == 0);
        Assert.False(first.HasToast);
        Assert.True(await first.RequestCloseAsync());

        var second = await OpenAsync(catalog);
        Assert.Empty(second.Jobs.Jobs);
        Assert.Single(_identity.Checked);
        Assert.True(await second.RequestCloseAsync());
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200 && !condition(); attempt++) await Task.Delay(10);
        Assert.True(condition());
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
        public Func<Task>? Before { get; set; }

        public async Task<bool> FillMissingIdentityAsync(LibraryVideo video, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (Checked) Checked.Add(video.Id);
            if (Before is { } before) await before();
            if (Failing.Contains(video.Id)) throw new InvalidDataException("mkvpropedit failed");
            return Writes.Contains(video.Id);
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
