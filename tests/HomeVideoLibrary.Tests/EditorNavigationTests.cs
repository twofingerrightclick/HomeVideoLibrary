using HomeVideoLibrary.Core;
using HomeVideoLibrary.Desktop.Playback;
using HomeVideoLibrary.Desktop.ViewModels;
using HomeVideoLibrary.Desktop.ViewModels.VideoPage;
using HomeVideoLibrary.Infrastructure;
using Xunit;
using static HomeVideoLibrary.Tests.TestLibrary;

namespace HomeVideoLibrary.Tests;

public sealed class EditorNavigationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hvl-navigation-" + Guid.NewGuid().ToString("N"));
    private readonly FakeMatroska _files = new();
    private readonly Metadata _metadata = new();
    private readonly FakeInteraction _interaction = new();

    private LibraryPaths Library => new(Path.Combine(_root, "library"));

    private MainWindowViewModel NewViewModel() => new(new MainWindowDependencies
    {
        Interaction = _interaction,
        Settings = new LibrarySettings(Path.Combine(_root, "settings.json")),
        CreateServices = _ => new LibraryServices(_files, _metadata),
        CreateClipEditing = _ => new BlockingClipEditing(),
        FindUnavailableTools = _ => Task.FromResult<IReadOnlyList<string>>([]),
        SweepScratch = (_, _) => Task.CompletedTask,
        CreatePlayback = () => new Playback(),
        Post = action => action(),
    });

    private async Task<(MainWindowViewModel ViewModel, LibraryVideo Video)> OpenLibraryAsync()
    {
        var catalog = await CreateCatalogAsync(Library.RootDirectory);
        var video = await AddVideoAsync(catalog, "video");
        var viewModel = NewViewModel();
        await viewModel.OpenLibraryAsync(catalog.Paths.RootDirectory);
        Assert.True(viewModel.HasLibrary);
        await viewModel.Settings.RefreshIndexAsync(); // Waits for the refresh the open started.
        return (viewModel, video);
    }

    private async Task<MainWindowViewModel> OpenEditorAsync()
    {
        var (viewModel, video) = await OpenLibraryAsync();
        await viewModel.OpenVideoAsync(viewModel.Model!.Snapshot.Find(video.Id)!);
        Assert.IsType<VideoPageViewModel>(viewModel.Page);
        return viewModel;
    }

    [Fact]
    public async Task ALibraryOpenInAnotherInstanceIsNotOpenedUntilThatOneCloses()
    {
        var (first, _) = await OpenLibraryAsync();
        var second = NewViewModel();

        await second.OpenLibraryAsync(Library.RootDirectory);
        Assert.False(second.HasLibrary);
        Assert.Contains("open in another", second.Status);

        Assert.True(await first.RequestCloseAsync());
        await second.OpenLibraryAsync(Library.RootDirectory);
        Assert.True(second.HasLibrary);
        await second.Settings.RefreshIndexAsync();
    }

    [Fact]
    public async Task RecoverySnapshotsAreReportedWhenTheLibraryOpens()
    {
        Directory.CreateDirectory(Path.Combine(MatroskaMetadataService.RecoveryDirectory(Library), "video-20260101T000000Z"));
        var (viewModel, _) = await OpenLibraryAsync();
        Assert.True(viewModel.HasRecoveryNotice);
        Assert.Contains("1 metadata recovery snapshot", viewModel.RecoveryNotice);
    }

    [Fact]
    public async Task ClosingTheWindowCancelsARunningClipEditThenReleasesTheLibrary()
    {
        var (viewModel, _) = await OpenLibraryAsync();
        viewModel.Shelf.Tapes.Single().IsSelected = true;
        await viewModel.OpenClipEditorAsync();
        var clips = Assert.IsType<ClipEditorPage>(viewModel.Page).Editor;
        var trim = clips.TrimAsync();
        Assert.True(clips.IsBusy);
        Assert.False(await viewModel.NavigateAsync(() => null)); // Back keeps a running edit.

        Assert.True(await viewModel.RequestCloseAsync());
        await trim;
        Assert.Null(viewModel.Page);
        using var released = LibraryLock.TryAcquire(Library);
        Assert.NotNull(released);
    }

    [Fact]
    public async Task CleanEditorClosesWithoutAsking()
    {
        var viewModel = await OpenEditorAsync();
        Assert.True(await viewModel.NavigateAsync(() => null));
        Assert.Null(viewModel.Page);
        Assert.Equal(0, _interaction.Asked);
    }

    [Fact]
    public async Task KeepEditingLeavesTheEditorOpenAndDiscardCloses()
    {
        var viewModel = await OpenEditorAsync();
        var page = viewModel.Page;
        Page(viewModel).Details.Title = "unsaved";

        _interaction.Answers.Enqueue(LeaveChoice.KeepEditing);
        Assert.False(await viewModel.NavigateAsync(() => null));
        Assert.Same(page, viewModel.Page);

        _interaction.Answers.Enqueue(LeaveChoice.Discard);
        Assert.True(await viewModel.NavigateAsync(() => null));
        Assert.Null(viewModel.Page);
        Assert.Equal(0, _metadata.Saves);
    }

    [Fact]
    public async Task ASecondNavigationWaitsForTheOpenQuestionInsteadOfStackingAnother()
    {
        var viewModel = await OpenEditorAsync();
        Page(viewModel).Details.Title = "unsaved";
        var answer = _interaction.Pending = new TaskCompletionSource<LeaveChoice>();

        var back = viewModel.NavigateAsync(() => null);
        var close = viewModel.RequestCloseAsync();
        Assert.False(back.IsCompleted);
        Assert.Equal(1, _interaction.Asked);

        answer.SetResult(LeaveChoice.Discard);
        Assert.True(await back);
        Assert.True(await close);
        Assert.Equal(1, _interaction.Asked);
    }

    [Fact]
    public async Task SaveAndLeaveSavesThenCloses()
    {
        var viewModel = await OpenEditorAsync();
        Page(viewModel).Details.Title = "saved title";

        _interaction.Answers.Enqueue(LeaveChoice.Save);
        Assert.True(await viewModel.NavigateAsync(() => null));
        Assert.Equal(1, _metadata.Saves);
        Assert.Null(viewModel.Page);
    }

    [Fact]
    public async Task CloseAsksJobsBeforeLeavingPage()
    {
        var viewModel = await OpenEditorAsync();
        var page = viewModel.Page;
        Page(viewModel).Details.Title = "unsaved";
        var job = viewModel.Jobs.Enqueue("render", JobLane.Render, async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return JobOutcome.Done("never");
        });

        _interaction.CloseChoice = CloseChoice.KeepOpen;
        Assert.False(await viewModel.RequestCloseAsync());
        Assert.Equal(1, _interaction.CloseAsked);
        Assert.Equal(0, _interaction.Asked);
        Assert.Same(page, viewModel.Page);
        Assert.Equal("unsaved", Page(viewModel).Details.Title);
        Assert.Equal(0, _metadata.Saves);
        Assert.True(job.IsRunning);

        _interaction.CloseChoice = CloseChoice.CancelJobs;
        _interaction.Answers.Enqueue(LeaveChoice.KeepEditing);
        Assert.False(await viewModel.RequestCloseAsync());
        Assert.Same(page, viewModel.Page);
        Assert.True(job.IsRunning);

        _interaction.Answers.Enqueue(LeaveChoice.Discard);
        Assert.True(await viewModel.RequestCloseAsync());
        Assert.Equal(JobState.Cancelled, job.State);
        Assert.Equal(0, _metadata.Saves);
    }

    [Fact]
    public async Task QuitWhenDoneCanBeCancelledWhileItWaits()
    {
        var (viewModel, _) = await OpenLibraryAsync();
        var gate = new TaskCompletionSource<JobOutcome>();
        var job = viewModel.Jobs.Enqueue("render", JobLane.Render, (_, _) => gate.Task);
        _interaction.CloseChoice = CloseChoice.WaitForJobs;

        var close = viewModel.RequestCloseAsync();
        Assert.True(viewModel.IsWaitingToQuit);
        viewModel.CancelQuitCommand.Execute(null);
        Assert.False(await close);
        Assert.False(viewModel.IsWaitingToQuit);
        Assert.True(job.IsRunning);

        var quit = viewModel.RequestCloseAsync();
        gate.SetResult(JobOutcome.Done("ok"));
        Assert.True(await quit);
        Assert.Equal(JobState.Succeeded, job.State);
    }

    [Fact]
    public async Task CloseAfterFailedSwitchStillCloses()
    {
        var viewModel = await OpenEditorAsync();
        Page(viewModel).Details.Title = "unsaved";
        var other = await CreateCatalogAsync(Path.Combine(_root, "other"));
        var broken = _interaction.Pending = new TaskCompletionSource<LeaveChoice>();
        broken.SetException(new InvalidOperationException("prompt failed"));

        await viewModel.OpenLibraryAsync(other.Paths.RootDirectory);
        Assert.Contains("prompt failed", viewModel.Status);
        Assert.Equal(Library.RootDirectory, viewModel.Settings.LibraryRoot);
        using (var free = LibraryLock.TryAcquire(other.Paths)) Assert.NotNull(free);

        _interaction.Answers.Enqueue(LeaveChoice.Discard);
        Assert.True(await viewModel.RequestCloseAsync());
        using var released = LibraryLock.TryAcquire(Library);
        Assert.NotNull(released);
    }

    [Fact]
    public async Task KeepEditingDuringALibrarySwitchReleasesTheNewLibrary()
    {
        var viewModel = await OpenEditorAsync();
        Page(viewModel).Details.Title = "unsaved";
        var other = await CreateCatalogAsync(Path.Combine(_root, "other"));

        _interaction.Answers.Enqueue(LeaveChoice.KeepEditing);
        await viewModel.OpenLibraryAsync(other.Paths.RootDirectory);

        Assert.Contains("not switched", viewModel.Status);
        Assert.Equal(Library.RootDirectory, viewModel.Settings.LibraryRoot);
        Assert.IsType<VideoPageViewModel>(viewModel.Page);
        using var free = LibraryLock.TryAcquire(other.Paths);
        Assert.NotNull(free);
    }

    [Fact]
    public async Task NavigateFailureDoesNotPoisonLaterNavigation()
    {
        var viewModel = await OpenEditorAsync();
        var page = viewModel.Page;
        Page(viewModel).Details.Title = "unsaved";
        var broken = _interaction.Pending = new TaskCompletionSource<LeaveChoice>();
        broken.SetException(new InvalidOperationException("prompt failed"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => viewModel.NavigateAsync(() => null));
        Assert.Same(page, viewModel.Page);

        _interaction.Answers.Enqueue(LeaveChoice.Discard);
        Assert.True(await viewModel.NavigateAsync(() => null));
        Assert.Null(viewModel.Page);
    }

    private static VideoPageViewModel Page(MainWindowViewModel viewModel) => Assert.IsType<VideoPageViewModel>(viewModel.Page);

    private sealed class BlockingClipEditing : IClipEditingService
    {
        private static readonly ClipCutPlan Plan = new(TimeSpan.Zero, TimeSpan.FromSeconds(10), true, "");
        public Task<ClipOutputFormat> GetSourceFormatAsync(LibraryVideo source, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ClipOutputFormat(160, 120, "10", "1:1"));
        public Task<ClipCutPlan> PlanTrimAsync(LibraryVideo source, TimeSpan start, TimeSpan end, ClipCutMode mode = ClipCutMode.Precise,
            CancellationToken cancellationToken = default) => Task.FromResult(Plan);
        public Task<ClipCutPlan> PlanSplitAsync(LibraryVideo source, TimeSpan position, CancellationToken cancellationToken = default) =>
            Task.FromResult(Plan);
        public Task<ClipMergePlan> PlanMergeAsync(IReadOnlyList<LibraryVideo> sources, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ClipMergePlan(true, 0, ""));
        public async Task<IReadOnlyList<LibraryVideo>> TrimAsync(LibraryVideo source, TimeSpan start, TimeSpan end, string title,
            IProgress<ClipEditProgress>? progress = null, CancellationToken cancellationToken = default, ClipCutMode mode = ClipCutMode.Precise)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return [];
        }
        public Task<IReadOnlyList<LibraryVideo>> SplitAsync(LibraryVideo source, TimeSpan position, string title,
            IProgress<ClipEditProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<LibraryVideo>> MergeAsync(IReadOnlyList<LibraryVideo> sources, string title,
            IProgress<ClipEditProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class Metadata : IVideoMetadataService
    {
        private VideoMetadata _content = VideoMetadata.Empty;
        public int Saves { get; private set; }
        public Task<VideoMetadata> ReadAsync(LibraryVideo video, CancellationToken cancellationToken = default) => Task.FromResult(_content);
        public Task SaveAsync(LibraryVideo video, VideoMetadata metadata, CancellationToken cancellationToken = default)
        {
            Saves++; _content = metadata;
            return Task.CompletedTask;
        }
        public Task<VideoThumbnail> CaptureThumbnailAsync(LibraryVideo video, TimeSpan position, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class Playback : IVideoPlayback
    {
        public event EventHandler<PlaybackUpdate>? Updated { add { } remove { } }
        public long Version => 0;
        public Task<VideoSurface> CreatePlayerAsync() => Task.FromResult(new VideoSurface(null));
        public Task StartAsync(string path, TimeSpan position, bool play, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task CloseAsync() => Task.CompletedTask;
        public Task SetPlayingAsync(bool play) => Task.CompletedTask;
        public Task SeekAsync(TimeSpan position) => Task.CompletedTask;
        public Task SetVolumeAsync(int volume) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        DeleteDirectory(_root);
        DeleteDirectory(_files.OutputDirectory);
    }
}
