using Avalonia.Controls;
using Avalonia.Threading;
using HomeVideoLibrary.Core;
using HomeVideoLibrary.Desktop.Playback;
using HomeVideoLibrary.Desktop.ViewModels.VideoPage;
using HomeVideoLibrary.Desktop.Views;
using HomeVideoLibrary.Infrastructure;

namespace HomeVideoLibrary.Desktop.UiTests;

/// <summary>Video pages over in-memory metadata and a playback without a native player, shown in a headless window
/// through the app's page template.</summary>
internal static class VideoPageFixture
{
    public static VideoPageViewModel Page(VideoMetadata? stored = null, string name = "Test video", TimeSpan? duration = null,
        PreviewCache? previews = null, string path = "/unused/test.mkv", TimeProvider? time = null, Func<Task>? back = null)
    {
        var metadata = stored ?? VideoMetadata.Empty;
        var length = duration ?? TimeSpan.FromMinutes(1);
        var id = Guid.NewGuid();
        var record = new VideoRecord(id, LibraryPaths.VideoRelativePath(id), name, length, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
            .WithContent(new VideoTextMetadata(metadata with { Thumbnails = [] }, [], length, []), new FileStamp(1, 1));
        return new VideoPageViewModel(new VideoPageDependencies
        {
            Record = record, Path = path, Metadata = new MemoryMetadata(metadata), Playback = new SilentPlayback(),
            Snapshot = LibrarySnapshot.Empty, SuggestTags = (_, _, _) => [], Session = new VideoPageSession(),
            Post = action => Dispatcher.UIThread.Post(action), Previews = previews, Time = time ?? TimeProvider.System,
            Back = back ?? (() => Task.CompletedTask), Edits = NoClipEditing.Edits(),
        });
    }

    /// <summary>Shows <paramref name="page"/> in <paramref name="host"/> and loads it, as the shell does.</summary>
    public static void Show(ContentControl host, VideoPageViewModel page)
    {
        host.Content = page;
        Dispatcher.UIThread.RunJobs();
        var load = page.LoadAsync();
        while (!load.IsCompleted) Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();
    }

    public static (Window Window, ContentControl Host) Window(double width = 1440, double height = 900)
    {
        var host = new ContentControl { ContentTemplate = PageViews.Template };
        var window = new Window { Content = host, Width = width, Height = height };
        window.Show();
        return (window, host);
    }

    private sealed class MemoryMetadata(VideoMetadata stored) : IVideoMetadataService
    {
        private VideoMetadata _stored = stored;
        public Task<VideoMetadata> ReadAsync(LibraryVideo video, CancellationToken cancellationToken = default) => Task.FromResult(_stored);
        public Task SaveAsync(LibraryVideo video, VideoMetadata metadata, CancellationToken cancellationToken = default)
        {
            _stored = metadata;
            return Task.CompletedTask;
        }
        public Task<VideoThumbnail> CaptureThumbnailAsync(LibraryVideo video, TimeSpan position, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class SilentPlayback : IVideoPlayback
    {
        public event EventHandler<PlaybackUpdate>? Updated { add { } remove { } }
        public long Version => 1;
        // A real MediaPlayer needs the native runtime; the surface stands for it.
        public Task<VideoSurface> CreatePlayerAsync() => Task.FromResult(new VideoSurface(null));
        public Task StartAsync(string path, TimeSpan position, bool play, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task CloseAsync() => Task.CompletedTask;
        public Task SetPlayingAsync(bool play) => Task.CompletedTask;
        public Task SeekAsync(TimeSpan position) => Task.CompletedTask;
        public Task SetVolumeAsync(int volume) => Task.CompletedTask;
        public Task SetSpeedAsync(double speed) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
