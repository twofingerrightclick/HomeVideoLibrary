using HomeVideoLibrary.Core;
using HomeVideoLibrary.Desktop.Playback;
using HomeVideoLibrary.Desktop.ViewModels;
using HomeVideoLibrary.Infrastructure;
using LibVLCSharp.Shared;
using Xunit;
using static HomeVideoLibrary.Tests.TestLibrary;

namespace HomeVideoLibrary.Tests;

public sealed class EditorNavigationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hvl-navigation-" + Guid.NewGuid().ToString("N"));
    private readonly FakeMatroska _files = new();
    private readonly Metadata _metadata = new();

    private async Task<MainWindowViewModel> OpenEditorAsync()
    {
        var catalog = await CreateCatalogAsync(Path.Combine(_root, "library"));
        var video = await AddVideoAsync(catalog, "video");
        var viewModel = new MainWindowViewModel(new MainWindowDependencies
        {
            Settings = new LibrarySettings(Path.Combine(_root, "settings.json")),
            CreateServices = _ => new LibraryServices(_files, _metadata),
            FindUnavailableTools = _ => Task.FromResult<IReadOnlyList<string>>([]),
            SweepScratch = (_, _) => Task.CompletedTask,
            CreatePlayback = () => new Playback(),
            Post = action => action(),
        });
        await viewModel.OpenLibraryAsync(catalog.Paths.RootDirectory);
        await viewModel.OpenVideoAsync(video);
        Assert.NotNull(viewModel.Editor);
        return viewModel;
    }

    [Fact]
    public async Task CleanEditorClosesWithoutAsking()
    {
        var viewModel = await OpenEditorAsync();
        Assert.True(await viewModel.CloseEditorAsync());
        Assert.Null(viewModel.Editor);
        Assert.False(viewModel.IsAskingToLeave);
    }

    [Fact]
    public async Task KeepEditingLeavesTheEditorOpenAndDiscardCloses()
    {
        var viewModel = await OpenEditorAsync();
        viewModel.Editor!.Title = "unsaved";

        var closing = viewModel.CloseEditorAsync();
        Assert.False(closing.IsCompleted);
        viewModel.LeavePrompt!.Answer(LeaveChoice.KeepEditing);
        Assert.False(await closing);
        Assert.NotNull(viewModel.Editor);
        Assert.False(viewModel.IsAskingToLeave);

        closing = viewModel.CloseEditorAsync();
        viewModel.LeavePrompt!.Answer(LeaveChoice.Discard);
        Assert.True(await closing);
        Assert.Null(viewModel.Editor);
        Assert.Equal(0, _metadata.Saves);
    }

    [Fact]
    public async Task ANewerCloseRequestSupersedesAnUnansweredOne()
    {
        var viewModel = await OpenEditorAsync();
        viewModel.Editor!.Title = "unsaved";

        var windowClose = viewModel.CloseEditorAsync();
        var firstPrompt = viewModel.LeavePrompt;
        var back = viewModel.CloseEditorAsync(); // Back to library after asking to close the window.
        Assert.False(await windowClose); // The window stays open; only the latest intent acts on the answer.
        Assert.NotSame(firstPrompt, viewModel.LeavePrompt);
        viewModel.LeavePrompt!.Answer(LeaveChoice.Discard);
        Assert.True(await back);
        Assert.Null(viewModel.Editor);
    }

    [Fact]
    public async Task SaveAndLeaveSavesThenCloses()
    {
        var viewModel = await OpenEditorAsync();
        viewModel.Editor!.Title = "saved title";

        var closing = viewModel.CloseEditorAsync();
        viewModel.LeavePrompt!.Answer(LeaveChoice.Save);
        Assert.True(await closing);
        Assert.Equal(1, _metadata.Saves);
        Assert.Null(viewModel.Editor);
    }

    [Fact]
    public void VisibleOpenActionRequiresASelectionAndClearsWithIt()
    {
        var viewModel = new MainWindowViewModel();
        Assert.False(viewModel.CanOpenSelected);
        viewModel.SelectedVideo = new LibraryVideo(Guid.NewGuid(), "Videos/test.mkv", "Test", TimeSpan.FromSeconds(10), DateTimeOffset.Now, DateTimeOffset.Now);
        Assert.True(viewModel.CanOpenSelected);
        Assert.False(viewModel.HasEditor);
        viewModel.SelectedVideo = null;
        Assert.False(viewModel.CanOpenSelected);
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
        // A real MediaPlayer needs the native runtime; the editor only passes it through to the view.
        public Task<MediaPlayer> CreatePlayerAsync() => Task.FromResult<MediaPlayer>(null!);
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
