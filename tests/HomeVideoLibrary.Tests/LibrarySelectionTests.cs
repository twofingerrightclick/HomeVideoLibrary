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
    public async Task ImportPublishesEachVideoAndKeepsPerFileResults()
    {
        _importer = catalog => new FakeImporter(catalog, _files);
        var (vm, _) = await OpenAsync("existing");
        _interaction.Picked = new PickedFiles(["/somewhere/new clip.mov", "/somewhere/duplicate.mov", "/somewhere/broken.mov"], 0);

        await vm.Import.ImportCommand.ExecuteAsync();

        Assert.Equal(2, vm.Shelf.Tapes.Count);
        Assert.Contains(vm.Shelf.Tapes, tape => tape.Label == "imported new clip");
        Assert.Equal(["new clip.mov: imported", "duplicate.mov: already in library", "broken.mov: bad header"], vm.Import.Results);
        Assert.StartsWith("Imported 1; 1 could not be imported. 1 already in library.", vm.Status);
        Assert.Equal(100, vm.Import.Progress);
        Assert.False(vm.Import.IsImporting || vm.Import.IsProgressIndeterminate);
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
    public async Task ClosingDuringAnImportWaitsForItsCancellationAndIsShared()
    {
        var started = new TaskCompletionSource();
        _importer = _ => new BlockingImporter(started);
        var (vm, _) = await OpenAsync("existing");
        _interaction.Picked = new PickedFiles(["/somewhere/slow.mov"], 0);
        var import = vm.Import.ImportCommand.ExecuteAsync();
        await started.Task;

        var close = vm.RequestCloseAsync();
        Assert.Same(close, vm.RequestCloseAsync());
        Assert.True(await close);
        Assert.True(import.IsCompleted);
        Assert.False(vm.Import.IsImporting);
        Assert.StartsWith("Import cancelled.", vm.Status);
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
