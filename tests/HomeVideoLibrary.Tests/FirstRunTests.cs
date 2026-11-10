using HomeVideoLibrary.Core;
using HomeVideoLibrary.Desktop.ViewModels;
using HomeVideoLibrary.Infrastructure;
using Xunit;
using static HomeVideoLibrary.Tests.TestLibrary;

namespace HomeVideoLibrary.Tests;

public sealed class FirstRunTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hvl-firstrun-" + Guid.NewGuid().ToString("N"));
    private readonly FakeMatroska _files = new();
    private readonly FakeInteraction _interaction = new();
    private readonly List<MainWindowViewModel> _opened = [];
    private LibrarySettings Settings => new(Path.Combine(_root, "settings.json"));

    private string Folder(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private MainWindowViewModel NewViewModel()
    {
        var viewModel = new MainWindowViewModel(new MainWindowDependencies
        {
            Interaction = _interaction,
            Settings = Settings,
            CreateServices = _ => new LibraryServices(_files, new UnusedMetadata()),
            FindUnavailableTools = _ => Task.FromResult<IReadOnlyList<string>>([]),
            SweepScratch = (_, _) => Task.CompletedTask,
            Post = action => action(),
        });
        _opened.Add(viewModel);
        return viewModel;
    }

    private async Task<MainWindowViewModel> LaunchAsync(string? savedRoot = null)
    {
        if (savedRoot is not null) await Settings.SaveRootAsync(savedRoot);
        var viewModel = NewViewModel();
        await viewModel.RestoreLibraryAsync();
        await viewModel.Settings.RefreshIndexAsync(); // Waits for the refresh an open started.
        return viewModel;
    }

    /// <summary>Picks <paramref name="folder"/> in CREATE LIBRARY and returns the opened library's folder.</summary>
    private async Task<string> CreateAsync(MainWindowViewModel viewModel, string folder)
    {
        _interaction.Folder = folder;
        await viewModel.Settings.CreateLibraryCommand.ExecuteAsync();
        await viewModel.Settings.RefreshIndexAsync();
        Assert.True(viewModel.HasLibrary, viewModel.Status);
        Assert.Equal("Choose where to create your library", _interaction.FolderTitles[^1]);
        return viewModel.Settings.LibraryRoot;
    }

    private static void AssertNoLibraryOpen(MainWindowViewModel viewModel)
    {
        Assert.False(viewModel.HasLibrary);
        Assert.True(viewModel.Shelf.IsEmpty);
        Assert.True(viewModel.Settings.CreateLibraryCommand.CanExecute(null));
        Assert.True(viewModel.Settings.ChooseFolderCommand.CanExecute(null));
        Assert.False(viewModel.Import.ImportCommand.CanExecute(null));
        Assert.False(viewModel.Albums.CreateCommand.CanExecute(null));
        Assert.False(viewModel.Settings.RefreshCommand.CanExecute(null));
    }

    [Fact]
    public async Task CreateMakesAHomeVideoLibraryFolderInTheChosenOneAndOpensIt()
    {
        var parent = Folder("Movies");
        var viewModel = await LaunchAsync();

        var opened = await CreateAsync(viewModel, parent);

        var expected = Path.Combine(parent, "HomeVideoLibrary");
        Assert.Equal(expected, opened);
        Assert.True(LibraryFolder.IsLibrary(expected));
        Assert.Equal(expected, await Settings.LoadRootAsync());
        Assert.True(viewModel.Import.ImportCommand.CanExecute(null));
        Assert.Equal("Your collection starts here", viewModel.EmptyTitle);
    }

    [Fact]
    public async Task CreateOpensAnExistingHomeVideoLibraryInTheChosenFolder()
    {
        var parent = Folder("Movies");
        var catalog = await CreateCatalogAsync(Path.Combine(parent, "HomeVideoLibrary"));
        await AddVideoAsync(catalog, "birthday");
        var viewModel = await LaunchAsync();

        Assert.Equal(catalog.Paths.RootDirectory, await CreateAsync(viewModel, parent));
        Assert.Equal(1, viewModel.TotalCount);
    }

    [Fact]
    public async Task CreateNeverMixesIntoAnUnrelatedHomeVideoLibraryFolder()
    {
        var parent = Folder("Movies");
        var unrelated = Folder(Path.Combine("Movies", "HomeVideoLibrary"));
        await File.WriteAllTextAsync(Path.Combine(unrelated, "notes.txt"), "not a library");
        var viewModel = await LaunchAsync();

        Assert.Equal(Path.Combine(parent, "HomeVideoLibrary 2"), await CreateAsync(viewModel, parent));
        Assert.False(LibraryFolder.IsLibrary(unrelated));
        Assert.Equal(["notes.txt"], Directory.EnumerateFileSystemEntries(unrelated).Select(Path.GetFileName));
    }

    [Theory]
    [InlineData("Existing library", true)]
    [InlineData("HomeVideoLibrary", false)]
    public async Task CreateOpensAChosenLibraryOrHomeVideoLibraryFolderInsteadOfNestingOne(string name, bool isLibrary)
    {
        var chosen = Folder(name);
        if (isLibrary) await CreateCatalogAsync(chosen);
        var viewModel = await LaunchAsync();

        Assert.Equal(chosen, await CreateAsync(viewModel, chosen));
        Assert.False(Directory.Exists(Path.Combine(chosen, "HomeVideoLibrary")));
    }

    [Fact]
    public async Task OpenIsTheSettingsFolderChoiceAndOpensTheChosenFolderItself()
    {
        var chosen = Folder("Family videos");
        var viewModel = await LaunchAsync();
        _interaction.Folder = chosen;

        await viewModel.Settings.ChooseFolderCommand.ExecuteAsync();
        await viewModel.Settings.RefreshIndexAsync();

        Assert.Equal("Choose your video library folder", Assert.Single(_interaction.FolderTitles));
        Assert.True(viewModel.HasLibrary);
        Assert.Equal(chosen, viewModel.Settings.LibraryRoot);
    }

    [Fact]
    public async Task CancellingThePickerKeepsTheEmptyState()
    {
        var viewModel = await LaunchAsync();
        _interaction.Folder = null;

        await viewModel.Settings.CreateLibraryCommand.ExecuteAsync();

        AssertNoLibraryOpen(viewModel);
    }

    [Fact]
    public void AFolderOnlyTheOsWroteIntoCountsAsEmpty()
    {
        var parent = Folder("Movies");
        File.WriteAllText(Path.Combine(Folder(Path.Combine("Movies", "HomeVideoLibrary")), ".DS_Store"), "");

        Assert.Equal(Path.Combine(parent, "HomeVideoLibrary"), LibraryFolder.ForNewLibrary(parent));
    }

    private sealed class UnusedMetadata : IVideoMetadataService
    {
        public Task<VideoMetadata> ReadAsync(LibraryVideo video, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SaveAsync(LibraryVideo video, VideoMetadata metadata, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<VideoThumbnail> CaptureThumbnailAsync(LibraryVideo video, TimeSpan position, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    public void Dispose()
    {
        foreach (var viewModel in _opened) viewModel.RequestCloseAsync().GetAwaiter().GetResult();
        DeleteDirectory(_root);
        DeleteDirectory(_files.OutputDirectory);
    }
}
