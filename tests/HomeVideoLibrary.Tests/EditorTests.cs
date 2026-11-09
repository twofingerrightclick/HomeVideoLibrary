using HomeVideoLibrary.Core;
using HomeVideoLibrary.Desktop.Playback;
using HomeVideoLibrary.Desktop.ViewModels;
using LibVLCSharp.Shared;
using Xunit;

namespace HomeVideoLibrary.Tests;

public sealed class EditorTests
{
    [Fact]
    public async Task LoadingIsCleanAndChapterEditsBecomeDirty()
    {
        var playback = new FakePlayback();
        await using var editor = CreateEditor(new FakeMetadata(), playback);
        await editor.LoadAsync();
        Assert.False(editor.IsDirty);
        await editor.SeekAsync(4);
        editor.AddChapter();
        var chapter = Assert.Single(editor.Chapters);
        Assert.NotEqual(0UL, chapter.Uid);
        Assert.Equal(TimeSpan.FromSeconds(4), chapter.ToChapter().Position);
        Assert.True(editor.IsDirty);
        chapter.Description = "A memory";
        await editor.SaveAsync();
        Assert.False(editor.IsDirty);
        chapter.Tags = "family\nfamily\n birthday ";
        Assert.True(editor.IsDirty);
        Assert.Equal(["family", "birthday"], chapter.ToChapter().Tags);
    }

    [Fact]
    public async Task SaveReleasesPlaybackBeforeReplacingFileThenRestoresPositionAndState()
    {
        var playback = new FakePlayback();
        var metadata = new FakeMetadata { OnSave = () => Assert.True(playback.Closed) };
        await using var editor = CreateEditor(metadata, playback);
        await editor.LoadAsync();
        await editor.SeekAsync(3.5);
        editor.Description = "Saved memory";

        await editor.SaveAsync();

        Assert.Equal("Saved memory", metadata.Saved!.Description);
        Assert.Equal(TimeSpan.FromSeconds(3.5), playback.LastPosition);
        Assert.True(playback.LastPlay);
        Assert.False(editor.IsDirty);
    }

    [Fact]
    public async Task FailedSaveRetainsDirtyEditsAndReopensPlayback()
    {
        var playback = new FakePlayback();
        var metadata = new FakeMetadata { OnSave = () => throw new IOException("disk is full") };
        await using var editor = CreateEditor(metadata, playback);
        await editor.LoadAsync();
        editor.Description = "Keep these edits";
        await editor.SaveAsync();
        Assert.True(editor.IsDirty);
        Assert.Equal("Keep these edits", editor.Description);
        Assert.Contains("disk is full", editor.Status);
        Assert.False(playback.Closed);
    }

    [Fact]
    public async Task CancellingSaveRetainsDirtyEditsAndRestoresPlayback()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var playback = new FakePlayback();
        var metadata = new FakeMetadata
        {
            SaveOperation = async token => { started.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); }
        };
        await using var editor = CreateEditor(metadata, playback);
        await editor.LoadAsync();
        editor.Description = "Keep cancelled edits";

        var saving = editor.SaveAsync();
        await started.Task;
        editor.CancelOperation();
        await saving;

        Assert.True(editor.IsDirty);
        Assert.False(editor.IsBusy);
        Assert.False(playback.Closed);
        Assert.Contains("cancelled", editor.Status);
    }

    [Fact]
    public async Task InvalidChapterTimeDoesNotClosePlaybackOrWriteMetadata()
    {
        var playback = new FakePlayback();
        var metadata = new FakeMetadata();
        await using var editor = CreateEditor(metadata, playback);
        await editor.LoadAsync();
        editor.AddChapter();
        editor.Chapters[0].Position = "invalid";
        await editor.SaveAsync();
        Assert.Null(metadata.Saved);
        Assert.False(playback.Closed);
        Assert.True(editor.IsDirty);
        Assert.Contains("Chapter position", editor.Status);
    }

    [Theory]
    [InlineData(true, false, false, false, true)]
    [InlineData(true, false, true, false, false)]
    [InlineData(true, true, false, false, false)]
    [InlineData(true, false, false, true, false)]
    [InlineData(false, false, false, false, false)]
    public void MarkerShortcutHonorsTextFocusModifiersAndBusyState(bool m, bool modifier, bool typing, bool busy, bool expected)
        => Assert.Equal(expected, VideoEditorViewModel.ShouldAddMarker(m, modifier, typing, busy));

    [Theory]
    [InlineData(-1, 10, 0)]
    [InlineData(15, 10, 9.999)]
    [InlineData(2.5, 10, 2.5)]
    [InlineData(double.NaN, 10, 0)]
    [InlineData(3, 0, 0)]
    public void SeekStaysInsideVideo(double position, double duration, double expected)
        => Assert.Equal(expected, VideoEditorViewModel.ClampSeek(position, duration), precision: 5);

    private static VideoEditorViewModel CreateEditor(FakeMetadata metadata, FakePlayback playback) => new(
        new LibraryVideo(Guid.NewGuid(), "Videos/test.mkv", "Test video", TimeSpan.FromSeconds(10), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
        "/unused/test.mkv", metadata, playback);

    private sealed class FakeMetadata : IVideoMetadataService
    {
        public Action? OnSave { get; init; }
        public Func<CancellationToken, Task>? SaveOperation { get; init; }
        public VideoMetadata? Saved { get; private set; }
        public Task<VideoMetadata> ReadAsync(LibraryVideo video, CancellationToken cancellationToken = default) => Task.FromResult(VideoMetadata.Empty);
        public async Task SaveAsync(LibraryVideo video, VideoMetadata metadata, CancellationToken cancellationToken = default)
        {
            OnSave?.Invoke();
            if (SaveOperation is { } save) await save(cancellationToken);
            Saved = metadata;
        }
        public Task<VideoThumbnail> CaptureThumbnailAsync(LibraryVideo video, TimeSpan position, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakePlayback : IVideoPlayback
    {
        public event EventHandler<PlaybackUpdate>? Updated { add { } remove { } }
        public long Version => 1;
        public MediaPlayer? NativePlayer => null;
        public bool Closed { get; private set; }
        public bool LastPlay { get; private set; }
        public TimeSpan LastPosition { get; private set; }
        public Task OpenAsync(string path, TimeSpan position, bool play, Func<Task> attachPlayer, CancellationToken cancellationToken)
        {
            Closed = false; LastPosition = position; LastPlay = play; return Task.CompletedTask;
        }
        public Task CloseAsync(Func<Task> detachPlayer) { Closed = true; return Task.CompletedTask; }
        public Task SetPlayingAsync(bool play) { LastPlay = play; return Task.CompletedTask; }
        public Task SeekAsync(TimeSpan position) { LastPosition = position; return Task.CompletedTask; }
        public Task SetVolumeAsync(int volume) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
