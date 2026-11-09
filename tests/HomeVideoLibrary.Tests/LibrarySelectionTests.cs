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
            Settings = new LibrarySettings(Path.Combine(_root, "settings.json")),
            CreateServices = _ => new LibraryServices(_files, new UnusedMetadata()),
            CreateImporter = library => _importer?.Invoke(library) ?? throw new NotSupportedException(),
            FindUnavailableTools = _ => Task.FromResult<IReadOnlyList<string>>(_unavailableTools),
            SweepScratch = (_, _) => Task.CompletedTask,
            Post = action => action(),
        });
        await viewModel.OpenLibraryAsync(catalog.Paths.RootDirectory);
        await viewModel.RefreshIndexAsync();
        return (viewModel, [.. videos]);
    }

    private static TapeViewModel Tape(MainWindowViewModel viewModel, LibraryVideo video) => viewModel.Tapes.Single(tape => tape.Id == video.Id);

    [Fact]
    public async Task ShiftRangeSelectAllAndSelectionSurvivesSearches()
    {
        var (vm, videos) = await OpenAsync("first", "second", "third", "fourth");
        Assert.Equal(videos.Select(video => video.Id), vm.Tapes.Select(tape => tape.Id));

        vm.ToggleTapeSelection(Tape(vm, videos[0]));
        vm.SelectRange(Tape(vm, videos[2]));
        Assert.Equal(videos.Take(3).Select(video => video.Id), vm.SelectedVideos.Select(video => video.Id));
        Assert.All(vm.Tapes.Take(3), tape => Assert.True(tape.IsSelected));
        Assert.Equal("3 selected", vm.SelectionLabel);

        vm.SearchText = "fourth";
        Assert.Equal(videos[3].Id, Assert.Single(vm.Tapes).Id);
        Assert.Equal(3, vm.SelectedVideos.Count);
        Assert.Equal("3 selected · 3 hidden by this view", vm.SelectionLabel);
        Assert.True(vm.CanEditClips);
        Tape(vm, videos[3]).IsSelected = true; // The tape's checkbox.
        vm.SearchText = "";
        Assert.Equal(4, vm.SelectedVideos.Count);
        Assert.All(vm.Tapes, tape => Assert.True(tape.IsSelected));

        vm.ClearSelection();
        Assert.Empty(vm.SelectedVideos); Assert.False(vm.CanAddSelected);
        vm.SearchText = "second";
        vm.SelectAllVisible();
        vm.SearchText = "";
        Assert.Equal(videos[1].Id, Assert.Single(vm.SelectedVideos).Id);
    }

    [Fact]
    public async Task LibraryChangesPatchTapesInPlaceInsteadOfRebuildingThem()
    {
        var (vm, videos) = await OpenAsync("first", "second");
        var before = vm.Tapes.ToArray();
        var rows = vm.TapeRows.ToArray();
        var album = await vm.Model!.CreateAlbumAsync("Family", []);
        vm.SetTapeColumns(1);

        await vm.AddToAlbumAsync(album.Id, [videos[1].Id]);
        Assert.Equal(before, vm.Tapes);
        Assert.Equal(rows, vm.TapeRows);
        Assert.Contains("Added 1 video(s) to “Family”", vm.Status);

        var path = vm.Model.GetVideoPath(vm.Model.Snapshot.Find(videos[0].Id)!);
        _files.Set(path, VideoMetadata.Empty with { Description = "renamed by a save" });
        await File.AppendAllTextAsync(path, " saved");
        await vm.Model.UpsertFromFileAsync(videos[0]);
        Assert.Same(before[0], Tape(vm, videos[0]));
        Assert.Equal("renamed by a save", before[0].Label);
    }

    [Fact]
    public async Task AlbumListUpdatesInPlaceAndKeepsTheOpenAlbumAndAddMode()
    {
        var (vm, videos) = await OpenAsync("first", "second");
        vm.AlbumName = "Beach";
        await vm.CreateAlbumAsync();
        var beach = Assert.Single(vm.Albums);
        Assert.Equal(beach, vm.CurrentAlbum);
        vm.ToggleAlbumAddMode();
        Assert.True(vm.IsChoosingAlbumVideos);
        Assert.Equal(2, vm.Tapes.Count);

        vm.AlbumName = "Beach days";
        await vm.RenameAlbumAsync();
        Assert.Equal("Beach days", Assert.Single(vm.Albums).Name);
        Assert.Equal(beach.Id, vm.CurrentAlbum?.Id);
        Assert.True(vm.IsChoosingAlbumVideos);

        vm.ToggleTapeSelection(Tape(vm, videos[0]));
        await vm.AddSelectedToCurrentAsync();
        Assert.Equal(videos[1].Id, Assert.Single(vm.Tapes).Id);
        vm.AlbumName = "beach DAYS";
        await vm.CreateAlbumAsync();
        Assert.Contains("already exists", vm.Status);
        Assert.Single(vm.Albums);
    }

    [Fact]
    public async Task ImportPublishesEachVideoAndKeepsPerFileResults()
    {
        _importer = catalog => new FakeImporter(catalog, _files);
        var (vm, _) = await OpenAsync("existing");

        await vm.ImportAsync(["/somewhere/new clip.mov", "/somewhere/duplicate.mov", "/somewhere/broken.mov"]);

        Assert.Equal(2, vm.Tapes.Count);
        Assert.Contains(vm.Tapes, tape => tape.Label == "imported new clip");
        Assert.Equal(["new clip.mov: imported", "duplicate.mov: already in library", "broken.mov: bad header"], vm.ImportResults);
        Assert.StartsWith("Imported 1; 1 could not be imported. 1 already in library.", vm.Status);
        Assert.Equal(100, vm.Progress);
        Assert.False(vm.IsBusy || vm.IsProgressIndeterminate);
    }

    [Fact]
    public async Task MissingToolsDisableImportAndRefreshAndAreNamed()
    {
        _unavailableTools.Add("mkvmerge");
        var (vm, _) = await OpenAsync("first");
        Assert.False(vm.CanImport);
        Assert.Contains("mkvmerge", vm.ToolWarning);
        Assert.Equal(VideoRecordState.Pending, vm.Tapes.Single().Record.State);
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
