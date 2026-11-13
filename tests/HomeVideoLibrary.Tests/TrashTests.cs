using HomeVideoLibrary.Core;
using HomeVideoLibrary.Desktop.Playback;
using HomeVideoLibrary.Desktop.ViewModels;
using HomeVideoLibrary.Desktop.ViewModels.VideoPage;
using HomeVideoLibrary.Infrastructure;
using Xunit;
using static HomeVideoLibrary.Tests.TestLibrary;

namespace HomeVideoLibrary.Tests;

public sealed class TrashTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hvl-trash-vm-" + Guid.NewGuid().ToString("N"));
    private readonly FakeMatroska _files = new();
    private readonly FakeInteraction _interaction = new();
    private readonly FakeTrash _trash;
    /// <summary>When set, a page's player closes only once it completes.</summary>
    private TaskCompletionSource? _playerClosing;

    public TrashTests() => _trash = new FakeTrash(Path.Combine(_root, "Trash"));

    private async Task<(MainWindowViewModel ViewModel, LibraryVideo[] Videos)> OpenAsync(params string[] names)
    {
        var catalog = await CreateCatalogAsync(Path.Combine(_root, "library"));
        var videos = new List<LibraryVideo>();
        for (var index = 0; index < names.Length; index++)
        {
            var video = await AddVideoAsync(catalog, names[index], new DateTimeOffset(2000 - index, 1, 1, 0, 0, 0, TimeSpan.Zero));
            _files.Set(catalog.GetVideoPath(video.RelativePath), VideoMetadata.Empty with { Title = names[index] });
            videos.Add(video);
        }
        var viewModel = new MainWindowViewModel(new MainWindowDependencies
        {
            Interaction = _interaction,
            Settings = new LibrarySettings(Path.Combine(_root, "settings.json")),
            CreateServices = _ => new LibraryServices(_files, new Metadata()),
            FindUnavailableTools = _ => Task.FromResult<IReadOnlyList<string>>([]),
            SweepScratch = (_, _) => Task.CompletedTask,
            CreatePlayback = () => new Playback(() => _playerClosing?.Task ?? Task.CompletedTask),
            Trash = _trash,
            Post = action => action(),
        });
        await viewModel.OpenLibraryAsync(catalog.Paths.RootDirectory);
        await viewModel.Settings.RefreshIndexAsync();
        return (viewModel, [.. videos]);
    }

    private static string PathOf(MainWindowViewModel viewModel, LibraryVideo video) => viewModel.Model!.Catalog.GetVideoPath(video.RelativePath);

    private static void Select(MainWindowViewModel viewModel, params LibraryVideo[] videos)
    {
        foreach (var video in videos) viewModel.Shelf.Tapes.Single(tape => tape.Id == video.Id).IsSelected = true;
    }

    [Fact]
    public async Task TheTrashKeyAppearsWithTheLibraryAndDecliningTouchesNothing()
    {
        var (vm, videos) = await OpenAsync("first", "second");
        Assert.True(vm.Shelf.HasMoveToTrash);
        Assert.False(vm.Shelf.MoveToTrashCommand!.CanExecute(null));
        Select(vm, videos[0]);
        Assert.True(vm.DeleteSelectedCommand.CanExecute(null));

        Assert.Null(await vm.Trash.MoveToTrashAsync(vm.Shelf.SelectedIds.ToArray()));

        var asked = Assert.Single(_interaction.Confirmations);
        Assert.Equal("Move 1 video to Trash? under 0.01 GB moves to the Trash. To undo, restore from the Trash and import again; albums are not kept.",
            asked.Message);
        Assert.Empty(vm.Jobs.Jobs);
        Assert.Empty(_trash.Moved);
        Assert.Equal(2, vm.Shelf.Tapes.Count);
        Assert.True(File.Exists(PathOf(vm, videos[0])));
    }

    [Fact]
    public async Task ConfirmingTrashesTheSelectionInOneJobAndTheShelfMovesOn()
    {
        var (vm, videos) = await OpenAsync("first", "second", "third");
        var album = await vm.Model!.CreateAlbumAsync("Holidays", [videos[0].Id]);
        Select(vm, videos[0], videos[1]);
        _interaction.ConfirmAnswers.Enqueue(true);
        TapeViewModel? focused = null;
        vm.Shelf.FocusRequested += tape => focused = tape;

        var request = await vm.Trash.MoveToTrashAsync(vm.Shelf.SelectedIds.ToArray());
        Assert.NotNull(request);
        Assert.True(vm.Trash.Holds(videos[0].Id));
        await request.Completion;

        var asked = Assert.Single(_interaction.Confirmations);
        Assert.StartsWith("Move 2 videos to Trash?", asked.Message);
        Assert.Equal(["first · under 0.01 GB · in Holidays", "second · under 0.01 GB"], asked.Details);
        Assert.Equal("Move 2 videos to Trash", Assert.Single(vm.Jobs.Jobs).Title);
        Assert.Equal(JobState.Succeeded, request.Job.State);
        Assert.Equal([PathOf(vm, videos[0]), PathOf(vm, videos[1])], _trash.Moved);
        Assert.Equal([videos[2].Id], vm.Shelf.Tapes.Select(tape => tape.Id));
        Assert.Empty(vm.Shelf.SelectedIds);
        Assert.Empty(vm.Model.Snapshot.MembersOf(album.Id));
        Assert.Equal(videos[2].Id, focused?.Id);
        Assert.Equal("Moved 2 videos to the Trash. To undo, restore from the Trash and import again; albums are not kept.", vm.Status);
        Assert.False(vm.Trash.Holds(videos[0].Id));
    }

    [Fact]
    public async Task OnlyTheSelectedVideosThisViewShowsAreTrashed()
    {
        var (vm, videos) = await OpenAsync("beach", "garden", "attic");
        Select(vm, videos[0], videos[2]);
        vm.Shelf.SearchText = "garden";
        Assert.False(vm.Shelf.MoveToTrashCommand!.CanExecute(null));
        Select(vm, videos[1]);
        Assert.Equal(3, vm.Shelf.SelectedCount);
        _interaction.ConfirmAnswers.Enqueue(true);

        await vm.Trash.MoveSelectedToTrashCommand.ExecuteAsync();
        await vm.Jobs.WhenIdleAsync();

        Assert.StartsWith("Move 1 video to Trash?", Assert.Single(_interaction.Confirmations).Message);
        Assert.Equal([PathOf(vm, videos[1])], _trash.Moved);
        vm.Shelf.SearchText = "";
        Assert.Equal([videos[0].Id, videos[2].Id], vm.Shelf.Tapes.Select(tape => tape.Id));
    }

    [Fact]
    public async Task AHiddenMissingVideoOffersNoRemoval()
    {
        var (vm, videos) = await OpenAsync("gone", "here");
        File.Delete(PathOf(vm, videos[0]));
        await vm.Model!.StatPassAsync();
        Select(vm, videos[0]);
        vm.Shelf.SearchText = "here";

        Assert.False(vm.Trash.HasMissingSelected);
        Assert.False(vm.Trash.RemoveMissingCommand.CanExecute(null));
    }

    [Fact]
    public async Task TrashingAVideoDeletesItsHoverStripAndPreviewFrames()
    {
        var (vm, videos) = await OpenAsync("first");
        var paths = vm.Model!.Catalog.Paths;
        Directory.CreateDirectory(paths.StripDirectory);
        var strip = Path.Combine(paths.StripDirectory, $"{videos[0].Id:N}.00ff.v1.jpg");
        await File.WriteAllTextAsync(strip, "strip");
        var frames = Directory.CreateDirectory(Path.Combine(paths.PreviewDirectory, videos[0].Id.ToString("N"))).FullName;
        Select(vm, videos[0]);
        _interaction.ConfirmAnswers.Enqueue(true);

        await (await vm.Trash.MoveToTrashAsync(vm.Shelf.VisibleSelectedIds()))!.Completion;

        Assert.False(File.Exists(strip));
        Assert.False(Directory.Exists(frames));
    }

    [Fact]
    public async Task OneLockedFileFailsAloneAndKeepsItsVideo()
    {
        var (vm, videos) = await OpenAsync("first", "second");
        _trash.ByName[Path.GetFileName(PathOf(vm, videos[1]))] = TrashResult.Locked;
        Select(vm, videos[0], videos[1]);
        _interaction.ConfirmAnswers.Enqueue(true);

        var request = await vm.Trash.MoveToTrashAsync(vm.Shelf.SelectedIds.ToArray());
        await request!.Completion;

        Assert.Equal(JobState.Succeeded, request.Job.State);
        Assert.Equal("Moved 1 video to the Trash. “second” is open in another program; try again later.", request.Job.StatusText);
        Assert.Equal([videos[1].Id], vm.Shelf.Tapes.Select(tape => tape.Id));
        Assert.True(File.Exists(PathOf(vm, videos[1])));
        Assert.Single(_interaction.Confirmations);
    }

    [Fact]
    public async Task OnlyTheVideosWithoutATrashAreOfferedForPermanentDeletion()
    {
        var (vm, videos) = await OpenAsync("first", "second", "third");
        _trash.ByName[Path.GetFileName(PathOf(vm, videos[1]))] = TrashResult.Unavailable;
        _trash.ByName[Path.GetFileName(PathOf(vm, videos[2]))] = TrashResult.Unavailable;
        var deleted = PathOf(vm, videos[1]);
        Select(vm, videos[0], videos[1], videos[2]);
        _interaction.ConfirmAnswers.Enqueue(true);
        _interaction.ConfirmAnswers.Enqueue(true);

        await (await vm.Trash.MoveToTrashAsync(vm.Shelf.SelectedIds.ToArray()))!.Completion;

        Assert.Equal(2, _interaction.Confirmations.Count);
        var permanent = _interaction.Confirmations[1];
        Assert.Equal("DELETE PERMANENTLY", permanent.Title);
        Assert.StartsWith("2 videos cannot go to the Trash", permanent.Message);
        Assert.Equal(["second · under 0.01 GB", "third · under 0.01 GB"], permanent.Details);
        Assert.Equal([PathOf(vm, videos[0])], _trash.Moved.Select(path => path));
        Assert.False(File.Exists(deleted));
        Assert.Empty(vm.Shelf.Tapes);
        Assert.Equal(2, vm.Jobs.Jobs.Count);
        Assert.Contains("Deleted 2 videos permanently.", vm.Status);
    }

    [Fact]
    public async Task DecliningPermanentDeletionKeepsThoseVideos()
    {
        var (vm, videos) = await OpenAsync("first");
        _trash.Answers.Enqueue(TrashResult.Unavailable);
        Select(vm, videos[0]);
        _interaction.ConfirmAnswers.Enqueue(true);

        var request = await vm.Trash.MoveToTrashAsync(vm.Shelf.SelectedIds.ToArray());
        await request!.Completion;

        Assert.Equal(JobState.Succeeded, request.Job.State);
        Assert.Equal(2, _interaction.Confirmations.Count);
        Assert.True(File.Exists(PathOf(vm, videos[0])));
        Assert.Single(vm.Shelf.Tapes);
        Assert.Equal("“first” cannot go to the Trash on its drive.", vm.Status);
    }

    [Fact]
    public async Task ARefusedCloseLeavesTheNormalTrashFallbackAvailable()
    {
        var (vm, videos) = await OpenAsync("first");
        var render = new TaskCompletionSource();
        vm.Jobs.Enqueue("Render", JobLane.Render, async (_, _) =>
        {
            await render.Task;
            return JobOutcome.Done("ok");
        });
        _trash.Answers.Enqueue(TrashResult.Unavailable);
        _interaction.ConfirmAnswers.Enqueue(true);
        _interaction.ConfirmAnswers.Enqueue(false);
        var request = await vm.Trash.MoveToTrashAsync([videos[0].Id]);

        Assert.False(await vm.RequestCloseAsync());
        render.SetResult();
        await request!.Completion;

        Assert.Equal(2, _interaction.Confirmations.Count);
        Assert.Equal("MOVE TO TRASH", _interaction.Confirmations[0].Title);
        Assert.Single(vm.Jobs.Jobs, job => job.Title.StartsWith("Move", StringComparison.Ordinal));
        Assert.True(File.Exists(PathOf(vm, videos[0])));
        Assert.NotNull(vm.Model);
    }

    [Fact]
    public async Task DecliningTheSystemsWarningKeepsTheVideoWithoutAskingAgain()
    {
        var (vm, videos) = await OpenAsync("first");
        _trash.Answers.Enqueue(TrashResult.Declined);
        Select(vm, videos[0]);
        _interaction.ConfirmAnswers.Enqueue(true);

        var request = await vm.Trash.MoveToTrashAsync(vm.Shelf.SelectedIds.ToArray());
        await request!.Completion;

        Assert.Equal(JobState.Succeeded, request.Job.State);
        Assert.Single(_interaction.Confirmations);
        Assert.True(File.Exists(PathOf(vm, videos[0])));
        Assert.Single(vm.Shelf.Tapes);
        Assert.Equal("Kept “first”.", vm.Status);
    }

    [Fact]
    public async Task TrashingFromItsPageLeavesThePageAndAVideoInATrashJobCannotOpen()
    {
        var (vm, videos) = await OpenAsync("first", "second");
        await vm.OpenVideoAsync(vm.Model!.Snapshot.Find(videos[0].Id)!);
        var page = Assert.IsType<VideoPageViewModel>(vm.Page);
        Assert.NotNull(page.MoveToTrashCommand);
        _interaction.ConfirmAnswers.Enqueue(true);

        var request = await vm.Trash.MoveToTrashAsync([videos[0].Id]);

        Assert.Null(vm.Page);
        Assert.True(vm.Trash.Holds(videos[0].Id));
        await vm.OpenVideoAsync(vm.Model.Snapshot.Find(videos[0].Id) ?? throw new InvalidOperationException("removed too early"));
        Assert.Null(vm.Page);
        Assert.Contains("being moved to the Trash", vm.Status);
        await request!.Completion;
        Assert.Null(vm.Model.Snapshot.Find(videos[0].Id));
    }

    [Fact]
    public async Task AVideoCannotOpenAgainWhileItsPageClosesForTheTrash()
    {
        var (vm, videos) = await OpenAsync("first", "second");
        await vm.OpenVideoAsync(vm.Model!.Snapshot.Find(videos[0].Id)!);
        _playerClosing = new TaskCompletionSource();
        _interaction.ConfirmAnswers.Enqueue(true);

        var trashing = vm.Trash.MoveToTrashAsync([videos[0].Id]);
        var reopening = vm.OpenVideoAsync(vm.Model.Snapshot.Find(videos[0].Id)!);

        Assert.True(reopening.IsCompleted);
        Assert.Contains("being moved to the Trash", vm.Status);
        _playerClosing.SetResult();
        await (await trashing)!.Completion;
        Assert.Null(vm.Page);
        Assert.Null(vm.Model.Snapshot.Find(videos[0].Id));
    }

    [Fact]
    public async Task AnOpenWaitingForAnotherNavigationIsRefusedWhenItsVideoLeftMeanwhile()
    {
        var (vm, videos) = await OpenAsync("first", "second");
        await vm.OpenVideoAsync(vm.Model!.Snapshot.Find(videos[1].Id)!);
        _playerClosing = new TaskCompletionSource();

        var leaving = vm.NavigateAsync(() => null);
        var opening = vm.OpenVideoAsync(vm.Model.Snapshot.Find(videos[0].Id)!);
        await vm.Model.RemoveVideosAsync([videos[0].Id]);
        _playerClosing.SetResult();
        await leaving;
        await opening;

        Assert.Null(vm.Page);
        Assert.Equal("“first” is no longer in the library.", vm.Status);
    }

    [Fact]
    public async Task UnsavedChangesTheUserKeepsStopTheTrash()
    {
        var (vm, videos) = await OpenAsync("first");
        await vm.OpenVideoAsync(vm.Model!.Snapshot.Find(videos[0].Id)!);
        Assert.IsType<VideoPageViewModel>(vm.Page).Details.Title = "unsaved";
        _interaction.ConfirmAnswers.Enqueue(true);
        _interaction.Answers.Enqueue(LeaveChoice.KeepEditing);

        Assert.Null(await vm.Trash.MoveToTrashAsync([videos[0].Id]));

        Assert.IsType<VideoPageViewModel>(vm.Page);
        Assert.Empty(vm.Jobs.Jobs);
        Assert.Empty(_trash.Moved);
        Assert.False(vm.Trash.Holds(videos[0].Id));
    }

    [Fact]
    public async Task AMissingVideoIsRemovedFromTheLibraryNotTrashed()
    {
        var (vm, videos) = await OpenAsync("gone", "here");
        File.Delete(PathOf(vm, videos[0]));
        await vm.Model!.StatPassAsync();
        Select(vm, videos[0]);
        Assert.True(vm.Trash.HasMissingSelected);
        Assert.False(vm.Shelf.MoveToTrashCommand!.CanExecute(null));
        Assert.True(vm.Trash.RemoveMissingCommand.CanExecute(null));
        _interaction.ConfirmAnswers.Enqueue(true);

        await vm.Trash.RemoveMissingAsync(vm.Shelf.SelectedIds.ToArray());

        Assert.Equal("REMOVE FROM LIBRARY", Assert.Single(_interaction.Confirmations).Title);
        Assert.Equal([videos[1].Id], vm.Shelf.Tapes.Select(tape => tape.Id));
        Assert.Equal("Removed 1 video from the library.", vm.Status);
        Assert.Empty(_trash.Moved);
    }

    [Fact]
    public async Task TheShellsHoldsNeverWaitOnAUiThreadThatHasStopped()
    {
        var stopped = new List<Action>();
        var holders = new ShellVideoHolders(() => null, () => null, stopped.Add);
        using var cancellation = new CancellationTokenSource();

        var taking = holders.HoldAsync(Guid.NewGuid(), cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => taking.WaitAsync(TimeSpan.FromSeconds(5)));
        var running = new ShellVideoHolders(() => null, () => null, action => action());
        await (await running.HoldAsync(Guid.NewGuid(), CancellationToken.None)).DisposeAsync();
    }

    public void Dispose()
    {
        DeleteDirectory(_root);
        DeleteDirectory(_files.OutputDirectory);
    }

    private sealed class Metadata : IVideoMetadataService
    {
        public Task<VideoMetadata> ReadAsync(LibraryVideo video, CancellationToken cancellationToken = default) => Task.FromResult(VideoMetadata.Empty);
        public Task SaveAsync(LibraryVideo video, VideoMetadata metadata, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<VideoThumbnail> CaptureThumbnailAsync(LibraryVideo video, TimeSpan position, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class Playback(Func<Task> closing) : IVideoPlayback
    {
        public event EventHandler<PlaybackUpdate>? Updated { add { } remove { } }
        public long Version => 0;
        public Task<VideoSurface> CreatePlayerAsync() => Task.FromResult(new VideoSurface(null));
        public Task StartAsync(string path, TimeSpan position, bool play, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task CloseAsync() => Task.CompletedTask;
        public Task SetPlayingAsync(bool play) => Task.CompletedTask;
        public Task SeekAsync(TimeSpan position) => Task.CompletedTask;
        public Task SetVolumeAsync(int volume) => Task.CompletedTask;
        public Task SetSpeedAsync(double speed) => Task.CompletedTask;
        public ValueTask DisposeAsync() => new(closing());
    }
}
