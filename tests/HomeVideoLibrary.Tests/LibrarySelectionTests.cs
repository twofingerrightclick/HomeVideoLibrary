using HomeVideoLibrary.Core;
using HomeVideoLibrary.Desktop.ViewModels;
using HomeVideoLibrary.Infrastructure;
using Xunit;
using static HomeVideoLibrary.Tests.TestLibrary;

namespace HomeVideoLibrary.Tests;

public sealed class LibrarySelectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hvl-selection-" + Guid.NewGuid().ToString("N"));
    private readonly FakeMatroska _files = new();
    private readonly List<string> _unavailableTools = [];
    private readonly FakeInteraction _interaction = new();
    private Func<LibraryCatalog, IVideoImporter>? _importer;

    private async Task<(MainWindowViewModel ViewModel, LibraryVideo[] Videos)> OpenAsync(params string[] names)
    {
        var catalog = await CreateCatalogAsync(Path.Combine(_root, "library"));
        var videos = new List<LibraryVideo>();
        for (var index = 0; index < names.Length; index++)
        {
            var video = await AddVideoAsync(catalog, names[index], new DateTimeOffset(2000 - index, 1, 1, 0, 0, 0, TimeSpan.Zero));
            _files.Set(catalog.GetVideoPath(video.RelativePath), VideoMetadata.Empty with { Description = names[index] });
            videos.Add(video);
        }
        var viewModel = new MainWindowViewModel(new MainWindowDependencies
        {
            Interaction = _interaction,
            Settings = new LibrarySettings(Path.Combine(_root, "settings.json")),
            CreateServices = _ => new LibraryServices(_files, new UnusedMetadata()),
            CreateImporter = library => _importer?.Invoke(library) ?? throw new NotSupportedException(),
            FindUnavailableTools = _ => Task.FromResult<IReadOnlyList<string>>(_unavailableTools),
            SweepScratch = (_, _) => Task.CompletedTask,
            Post = action => action(),
        });
        await viewModel.OpenLibraryAsync(catalog.Paths.RootDirectory);
        await viewModel.Settings.RefreshIndexAsync();
        return (viewModel, [.. videos]);
    }

    private static TapeViewModel Tape(MainWindowViewModel viewModel, LibraryVideo video) => viewModel.Shelf.Tapes.Single(tape => tape.Id == video.Id);

    [Fact]
    public async Task ShiftRangeSelectAllAndSelectionSurvivesSearches()
    {
        var (vm, videos) = await OpenAsync("first", "second", "third", "fourth");
        var shelf = vm.Shelf;
        Assert.Equal(videos.Select(video => video.Id), shelf.Tapes.Select(tape => tape.Id));

        Tape(vm, videos[0]).ToggleSelectionCommand.Execute(null);
        Tape(vm, videos[2]).SelectRangeCommand.Execute(null);
        Assert.Equal(videos.Take(3).Select(video => video.Id), shelf.SelectedRecords.Select(video => video.Id));
        Assert.All(shelf.Tapes.Take(3), tape => Assert.True(tape.IsSelected));
        Assert.Equal("3 selected", shelf.SelectionLabel);

        shelf.SearchText = "fourth";
        Assert.Equal(videos[3].Id, Assert.Single(shelf.Tapes).Id);
        Assert.Equal(3, shelf.SelectedRecords.Count);
        Assert.Equal("3 selected · 3 hidden by this view", shelf.SelectionLabel);
        Assert.True(vm.MergeSelectedCommand.CanExecute(null));
        Tape(vm, videos[3]).IsSelected = true; // The tape's checkbox.
        Assert.Equal("4 selected · 3 hidden by this view", shelf.SelectionLabel);
        shelf.SearchText = "";
        Assert.Equal(4, shelf.SelectedRecords.Count);
        Assert.Equal("4 selected", shelf.SelectionLabel);
        Assert.All(shelf.Tapes, tape => Assert.True(tape.IsSelected));

        shelf.ClearSelection();
        Assert.Empty(shelf.SelectedRecords); Assert.False(vm.PlaySelectedCommand.CanExecute(null));
        shelf.SearchText = "second";
        shelf.SelectAllCommand.Execute(null);
        shelf.SearchText = "";
        Assert.Equal(videos[1].Id, Assert.Single(shelf.SelectedRecords).Id);
    }

    [Fact]
    public async Task EscapeClearsTheSelectionBeforeTheSearch()
    {
        var (vm, videos) = await OpenAsync("first", "second");
        vm.Shelf.SearchText = "first";
        Tape(vm, videos[0]).IsSelected = true;

        vm.Shelf.ClearCommand.Execute(null);
        Assert.False(vm.Shelf.HasSelection);
        Assert.Equal("first", vm.Shelf.SearchText);
        vm.Shelf.ClearCommand.Execute(null);
        Assert.Equal("", vm.Shelf.SearchText);
    }

    [Fact]
    public async Task LibraryChangesPatchTapesInPlaceInsteadOfRebuildingThem()
    {
        var (vm, videos) = await OpenAsync("first", "second");
        var before = vm.Shelf.Tapes.ToArray();
        var album = await vm.Model!.CreateAlbumAsync("Family", []);

        await vm.Albums.AddToAlbumAsync(album.Id, [videos[1].Id]);
        Assert.Equal(before, vm.Shelf.Tapes);
        Assert.Contains("Added 1 video to “Family”", vm.Status);

        var path = vm.Model.GetVideoPath(vm.Model.Snapshot.Find(videos[0].Id)!);
        _files.Set(path, VideoMetadata.Empty with { Description = "renamed by a save" });
        await File.AppendAllTextAsync(path, " saved");
        await vm.Model.UpsertFromFileAsync(videos[0]);
        Assert.Same(before[0], Tape(vm, videos[0]));
        Assert.Equal("renamed by a save", before[0].Label);
    }

    [Fact]
    public async Task AnInsertAtTheTopOfALongShelfIsOneInsertAndMonthHeadersFollow()
    {
        var names = Enumerable.Range(0, 200).Select(index => $"tape {index}").ToArray();
        var (vm, _) = await OpenAsync(names);
        var changes = new List<System.Collections.Specialized.NotifyCollectionChangedAction>();
        vm.Shelf.Tapes.CollectionChanged += (_, args) => changes.Add(args.Action);

        var catalog = vm.Model!.Catalog;
        var newest = await AddVideoAsync(catalog, "newest", new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await vm.Model.UpsertFromFileAsync(newest);

        Assert.Equal([System.Collections.Specialized.NotifyCollectionChangedAction.Add], changes);
        Assert.Equal(newest.Id, vm.Shelf.Tapes[0].Id);
        Assert.Equal(vm.Shelf.Tapes[0].MonthLabel, vm.Shelf.Tapes[0].MonthHeader);
        Assert.NotNull(vm.Shelf.Tapes[1].MonthHeader); // 2001 then 2000: a new month.

        vm.Shelf.SearchText = "tape 1";
        Assert.All(vm.Shelf.Tapes.Zip(vm.Shelf.Tapes.Skip(1)), pair =>
            Assert.Equal(pair.First.MonthLabel == pair.Second.MonthLabel ? null : pair.Second.MonthLabel, pair.Second.MonthHeader));
        Assert.NotNull(vm.Shelf.Tapes[0].MonthHeader);

        vm.Shelf.SetTopVisible(vm.Shelf.Tapes[3]);
        Assert.Equal(vm.Shelf.Tapes[3].MonthLabel, vm.Shelf.StickyMonth);
    }

    [Fact]
    public async Task NewAlbumsGetAFreeNameAndOpenForRenameWhichKeepsTheShownAlbum()
    {
        var (vm, videos) = await OpenAsync("first", "second");
        await vm.Albums.CreateCommand.ExecuteAsync();
        await vm.Albums.CreateCommand.ExecuteAsync();
        Assert.Equal(["New album", "New album 2"], vm.Albums.Rows.Select(row => row.Name));
        var second = vm.Albums.Current!;
        Assert.Equal("New album 2", second.Name);
        Assert.NotNull(second.Rename);

        second.Rename!.DraftName = "  ";
        await second.Rename.CommitCommand.ExecuteAsync();
        Assert.Equal("Give the album a name.", vm.Status);
        Assert.NotNull(second.Rename);

        second.Rename.DraftName = "Beach";
        await second.Rename.CommitCommand.ExecuteAsync();
        Assert.Null(second.Rename);
        Assert.Same(second, vm.Albums.Current);
        Assert.Equal("Beach", second.Name);
        Assert.Equal("Beach", vm.Albums.ViewTitle);

        vm.Albums.StartAddingCommand.Execute(null);
        Assert.True(vm.Albums.IsAddingVideos);
        Assert.Equal(2, vm.Shelf.Tapes.Count);
        Tape(vm, videos[0]).IsSelected = true;
        await vm.Albums.AddSelectedHereCommand.ExecuteAsync();
        Assert.Contains("Added 1 video to “Beach”", vm.Status);
        Assert.Equal(videos[1].Id, Assert.Single(vm.Shelf.Tapes).Id);
        Assert.Equal(1, second.Count);
        Assert.True(vm.Albums.IsAddingVideos);

        second.BeginRename();
        second.Rename!.DraftName = "new ALBUM";
        await second.Rename.CommitCommand.ExecuteAsync();
        Assert.Contains("already exists", vm.Status);
    }

    [Fact]
    public async Task ImportQueuesOneJobPerFileAndPublishesEachVideo()
    {
        _importer = catalog => new FakeImporter(catalog, _files);
        var (vm, _) = await OpenAsync("existing");
        _interaction.Picked = new PickedFiles(["/somewhere/new clip.mov", "/somewhere/duplicate.mov", "/somewhere/broken.mov"], 0);

        await vm.Import.ImportCommand.ExecuteAsync();
        Assert.Equal(["Queued (#2)", "Queued (#1)"], vm.Jobs.Jobs.Take(2).Select(job => job.StatusText));
        await vm.Jobs.WhenIdleAsync();

        Assert.Equal(2, vm.Shelf.Tapes.Count);
        Assert.Contains(vm.Shelf.Tapes, tape => tape.Label == "imported new clip");
        var jobs = vm.Jobs.Jobs.Reverse().ToArray();
        Assert.Equal([JobState.Succeeded, JobState.Succeeded, JobState.Failed], jobs.Select(job => job.State));
        Assert.Equal("Already in the library.", jobs[1].StatusText);
        Assert.Equal("bad header", jobs[2].StatusText);
        Assert.True(jobs[0].CanOpen);
        Assert.False(vm.Import.IsImporting);
    }

    [Fact]
    public async Task MissingToolsDisableImportAndRefreshAndAreNamed()
    {
        _unavailableTools.Add("mkvmerge");
        var (vm, _) = await OpenAsync("first");
        Assert.False(vm.Import.ImportCommand.CanExecute(null));
        Assert.Contains("mkvmerge", vm.ToolWarning);
        Assert.Equal(VideoRecordState.Pending, vm.Shelf.Tapes.Single().Record.State);
        Assert.Null(vm.Shelf.Tapes.Single().CreatePreviewSession());
    }

    [Fact]
    public async Task ClosingWithRunningJobsAsksAndKeepOpenLeavesThemRunning()
    {
        var started = new TaskCompletionSource();
        _importer = _ => new BlockingImporter(started);
        var (vm, _) = await OpenAsync("existing");
        vm.Import.Enqueue(["/somewhere/slow.mov"]);
        await started.Task;

        _interaction.CloseChoice = CloseChoice.KeepOpen;
        Assert.False(await vm.RequestCloseAsync());
        Assert.True(vm.Import.IsImporting);

        _interaction.CloseChoice = CloseChoice.CancelJobs;
        var close = vm.RequestCloseAsync();
        Assert.Same(close, vm.RequestCloseAsync());
        Assert.True(await close);
        Assert.Equal(2, _interaction.CloseAsked);
        Assert.Equal(JobState.Cancelled, Assert.Single(vm.Jobs.Jobs).State);
        Assert.False(vm.Import.IsImporting);
    }

    [Fact]
    public async Task EscapeInAnAlbumRenameKeepsTheOldName()
    {
        var (vm, _) = await OpenAsync("first");
        await vm.Albums.CreateCommand.ExecuteAsync();
        var row = vm.Albums.Current!;
        var rename = row.Rename!;
        rename.DraftName = "Typed then abandoned";

        rename.CancelCommand.Execute(null);
        await rename.CommitCommand.ExecuteAsync(); // The removed field's final lost focus.

        Assert.Null(row.Rename);
        Assert.Equal("New album", row.Name);
        Assert.Equal("New album", vm.Model!.Snapshot.Albums.Single().Name);
    }

    [Fact]
    public async Task DeletingTheShownAlbumWithAnotherReturnsToAllVideosInOnePass()
    {
        var (vm, _) = await OpenAsync("first");
        foreach (var name in new[] { "A", "B", "C" }) await vm.Model!.CreateAlbumAsync(name, []);
        vm.Albums.Current = vm.Albums.Rows.Single(row => row.Name == "C");
        var b = vm.Albums.Rows.Single(row => row.Name == "B").Id;
        var c = vm.Albums.Current.Id;
        // The list writes its lost selection back to Current while the row is removed, as the real ListBox does.
        vm.Albums.Rows.CollectionChanged += (_, args) =>
        {
            if (args.OldItems?.OfType<AlbumRowViewModel>().Any(row => row.Id == c) == true) vm.Albums.Current = null;
        };

        await vm.Model!.DeleteAlbumAsync(b);
        await vm.Model.DeleteAlbumAsync(c);

        Assert.True(vm.Albums.IsAllVideos);
        Assert.Equal(["A"], vm.Albums.Rows.Select(row => row.Name));
        Assert.Single(vm.Shelf.Tapes);
    }

    [Fact]
    public async Task AddModeAddsOnlyTheSelectedVideosItShows()
    {
        var (vm, videos) = await OpenAsync("first", "second", "third");
        var album = await vm.Model!.CreateAlbumAsync("Beach", [videos[0].Id]);
        Tape(vm, videos[0]).IsSelected = true;
        vm.Albums.Current = vm.Albums.Rows.Single(row => row.Id == album.Id);
        vm.Albums.StartAddingCommand.Execute(null);

        Assert.Equal("ADD 0", vm.Albums.AddHereLabel);
        Assert.False(vm.Albums.AddSelectedHereCommand.CanExecute(null));
        Tape(vm, videos[1]).IsSelected = true;
        Assert.Equal("ADD 1", vm.Albums.AddHereLabel);
        await vm.Albums.AddSelectedHereCommand.ExecuteAsync();
        Assert.Contains("Added 1 video to “Beach”.", vm.Status);
    }

    private sealed class BlockingImporter(TaskCompletionSource started) : IVideoImporter
    {
        public async Task<ImportResult> ImportAsync(string sourcePath, IProgress<ImportProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }

    private sealed class FakeImporter(LibraryCatalog catalog, FakeMatroska files) : IVideoImporter
    {
        public async Task<ImportResult> ImportAsync(string sourcePath, IProgress<ImportProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            var name = Path.GetFileNameWithoutExtension(sourcePath);
            if (name == "broken") throw new InvalidDataException("bad header");
            progress?.Report(new ImportProgress("Copying", 0.5));
            progress?.Report(new ImportProgress("Verifying"));
            var existing = (await catalog.ListAsync(cancellationToken)).FirstOrDefault();
            if (name == "duplicate" && existing is not null) return new ImportResult(ImportOutcome.AlreadyInLibrary, existing, []);
            var video = await AddVideoAsync(catalog, name);
            files.Set(catalog.GetVideoPath(video.RelativePath), VideoMetadata.Empty with { Title = "imported " + name });
            return new ImportResult(ImportOutcome.Imported, video, []);
        }
    }

    private sealed class UnusedMetadata : IVideoMetadataService
    {
        public Task<VideoMetadata> ReadAsync(LibraryVideo video, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SaveAsync(LibraryVideo video, VideoMetadata metadata, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<VideoThumbnail> CaptureThumbnailAsync(LibraryVideo video, TimeSpan position, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    public void Dispose()
    {
        DeleteDirectory(_root);
        DeleteDirectory(_files.OutputDirectory);
    }
}
