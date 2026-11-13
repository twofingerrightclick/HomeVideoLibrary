using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using HomeVideoLibrary.Core;
using HomeVideoLibrary.Desktop.ViewModels;
using HomeVideoLibrary.Desktop.Views;
using HomeVideoLibrary.Infrastructure;
using Xunit;

namespace HomeVideoLibrary.Desktop.UiTests;

/// <summary>Which delete key does what, and where focus goes after a trash. Confirmations are declined unless a test
/// agrees, and the only Trash that moves anything moves the test's own placeholder files into its temporary directory.</summary>
public sealed class TrashKeyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hvl-trash-keys-" + Guid.NewGuid().ToString("N"));
    private readonly DecliningInteraction _interaction = new();

    private async Task<(MainWindow Window, MainWindowViewModel ViewModel, ListBox List, Guid[] Videos)> ShowAsync(IFileTrash? trash = null)
    {
        var catalog = new LibraryCatalog(new LibraryPaths(Path.Combine(_root, "library")));
        await catalog.InitializeAsync();
        var videos = new List<Guid>();
        for (var index = 0; index < 3; index++)
        {
            var id = Guid.NewGuid();
            var video = new LibraryVideo(id, LibraryPaths.VideoRelativePath(id), $"tape {index}", TimeSpan.FromMinutes(1), DateTimeOffset.UtcNow,
                new DateTimeOffset(2000 - index, 1, 1, 0, 0, 0, TimeSpan.Zero));
            await File.WriteAllTextAsync(catalog.GetVideoPath(video.RelativePath), "placeholder");
            await catalog.AddAsync(video);
            videos.Add(id);
        }
        // The window reopens the saved library when it opens, as the app does; the test waits for that.
        var settings = new LibrarySettings(Path.Combine(_root, "settings.json"));
        await settings.SaveRootAsync(catalog.Paths.RootDirectory);
        var viewModel = new MainWindowViewModel(new MainWindowDependencies
        {
            Interaction = _interaction,
            Settings = settings,
            CreateServices = _ => new LibraryServices(new EmptyMkv(), new NoMetadata()),
            FindUnavailableTools = _ => Task.FromResult<IReadOnlyList<string>>([]),
            SweepScratch = (_, _) => Task.CompletedTask,
            Trash = trash ?? new RefusingTrash(),
        });
        var window = new MainWindow { DataContext = viewModel, Width = 1440, Height = 900 };
        window.Show();
        await WaitUntilAsync(() => viewModel.HasLibrary && viewModel.Import.CanImport);
        await viewModel.Settings.RefreshIndexAsync();
        Dispatcher.UIThread.RunJobs();
        return (window, viewModel, window.FindControl<ListBox>("TapeList")!, [.. videos]);
    }

    [AvaloniaFact]
    public async Task InTheLibraryEveryDeleteKeyAsksToMoveTheSelectionToTheTrash()
    {
        var (window, vm, list, _) = await ShowAsync();
        vm.Shelf.Tapes[0].IsSelected = true;
        list.ContainerFromIndex(0)!.Focus(NavigationMethod.Directional);

        Press(window, PhysicalKey.Delete);
        Press(window, PhysicalKey.Backspace);
        Press(window, PhysicalKey.Backspace, Command());
        Press(window, PhysicalKey.Delete, Command());

        Assert.Equal(4, _interaction.Asked.Count(title => title == "MOVE TO TRASH"));
        Assert.Equal(3, vm.Shelf.Tapes.Count);
    }

    [AvaloniaFact]
    public async Task InAnAlbumDeleteRemovesFromTheAlbumAndTheCommandChordStillTrashes()
    {
        var (window, vm, list, videos) = await ShowAsync();
        var album = await vm.Model!.CreateAlbumAsync("Holidays", videos);
        Dispatcher.UIThread.RunJobs();
        vm.Albums.Current = vm.Albums.Rows.Single(row => row.Id == album.Id);
        Dispatcher.UIThread.RunJobs();
        vm.Shelf.Tapes[0].IsSelected = true;
        list.ContainerFromIndex(0)!.Focus(NavigationMethod.Directional);

        Press(window, PhysicalKey.Backspace, Command());
        Assert.Equal(["MOVE TO TRASH"], _interaction.Asked);

        Press(window, PhysicalKey.Delete);
        await WaitUntilAsync(() => vm.Model.Snapshot.MembersOf(album.Id).Count == 2);
        Assert.Equal(["MOVE TO TRASH"], _interaction.Asked);
        Assert.Equal(3, vm.Model.Snapshot.Videos.Count);
    }

    [AvaloniaFact]
    public async Task AfterATrashKeyboardFocusMovesToTheNextTape()
    {
        var (window, vm, list, videos) = await ShowAsync(new TemporaryTrash(Path.Combine(_root, "Trash")));
        _interaction.Agrees = true;
        vm.Shelf.Tapes[0].IsSelected = true;
        list.ContainerFromIndex(0)!.Focus(NavigationMethod.Directional);

        var request = await vm.Trash.MoveToTrashAsync([videos[0]]);
        await request!.Completion;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal([videos[1], videos[2]], vm.Shelf.Tapes.Select(tape => tape.Id));
        Assert.Equal(videos[1], ((window.FocusManager!.GetFocusedElement() as Control)?.DataContext as TapeViewModel)?.Id);
    }

    [AvaloniaFact]
    public async Task DeleteKeysTypedIntoTheSearchFieldEditTheText()
    {
        var (window, vm, _, _) = await ShowAsync();
        vm.Shelf.Tapes[0].IsSelected = true;
        var search = window.FindControl<TextBox>("SearchBox")!;
        search.Focus();

        Press(window, PhysicalKey.Delete);
        Press(window, PhysicalKey.Backspace);
        Press(window, PhysicalKey.Backspace, Command());
        Press(window, PhysicalKey.Delete, Command());

        Assert.Empty(_interaction.Asked);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200 && !condition(); attempt++)
        {
            await Task.Delay(10);
            Dispatcher.UIThread.RunJobs();
        }
        Assert.True(condition());
    }

    private static void Press(Window window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPressQwerty(key, modifiers);
        window.KeyReleaseQwerty(key, modifiers);
        Dispatcher.UIThread.RunJobs();
    }

    private static RawInputModifiers Command() =>
        (Application.Current?.PlatformSettings?.HotkeyConfiguration.CommandModifiers ?? KeyModifiers.Control) == KeyModifiers.Meta
            ? RawInputModifiers.Meta : RawInputModifiers.Control;

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class DecliningInteraction : IInteraction
    {
        public List<string> Asked { get; } = [];
        public bool Agrees { get; set; }
        public Task<bool> ConfirmAsync(Confirmation confirmation)
        {
            Asked.Add(confirmation.Title);
            return Task.FromResult(Agrees);
        }
        public Task<LeaveChoice> AskLeaveAsync(string title, string? cannotSave) => Task.FromResult(LeaveChoice.KeepEditing);
        public Task ShowErrorAsync(string title, string message) => Task.CompletedTask;
        public Task<PickedFiles> PickVideosAsync() => Task.FromResult(PickedFiles.None);
        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);
        public Task ShowSettingsAsync(SettingsViewModel settings) => Task.CompletedTask;
    }

    private sealed class RefusingTrash : IFileTrash
    {
        public TrashResult MoveToTrash(string path) => throw new InvalidOperationException("Key tests never trash.");
    }

    private sealed class TemporaryTrash(string directory) : IFileTrash
    {
        public TrashResult MoveToTrash(string path)
        {
            Directory.CreateDirectory(directory);
            File.Move(path, Path.Combine(directory, Path.GetFileName(path)));
            return TrashResult.Trashed;
        }
    }

    private sealed class EmptyMkv : IMatroskaFileEditor
    {
        public Task<VideoTextMetadata> ReadTextAsync(string mkvPath, CancellationToken cancellationToken = default) =>
            Task.FromResult(new VideoTextMetadata(VideoMetadata.Empty, [], TimeSpan.FromMinutes(1), []));
        public Task WriteStagedAsync(string mkvPath, VideoMetadata metadata, MetadataWriteOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<string> ExtractThumbnailAsync(string mkvPath, Guid videoId, VideoThumbnailRef thumbnail, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task CaptureFrameAsync(string mkvPath, TimeSpan position, string outputPath, int maxWidth = 960, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task CaptureKeyframeAsync(string mkvPath, TimeSpan position, string outputPath, int maxWidth, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task TileAsync(IReadOnlyList<string> images, string outputPath, int columns, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class NoMetadata : IVideoMetadataService
    {
        public Task<VideoMetadata> ReadAsync(LibraryVideo video, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SaveAsync(LibraryVideo video, VideoMetadata metadata, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<VideoThumbnail> CaptureThumbnailAsync(LibraryVideo video, TimeSpan position, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
