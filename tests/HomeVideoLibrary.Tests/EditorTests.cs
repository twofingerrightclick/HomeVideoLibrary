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
        chapter = Assert.Single(editor.Chapters);
        Assert.Equal("A memory", chapter.Description);
        chapter.Tags = "family\nFamily\n birthday ";
        Assert.True(editor.IsDirty);
        Assert.Equal(["family", "birthday"], chapter.ToChapter().Tags);
    }

    [Fact]
    public async Task SaveCanReleasePlaybackThenRestoresPositionAndState()
    {
        var playback = new FakePlayback();
        var metadata = new FakeMetadata { OnSave = () => Assert.True(playback.Closed) };
        await using var editor = CreateEditor(metadata, playback, releasePlayback: true);
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
    public async Task InPlaceSaveKeepsPlaybackOpenAndShowsTheSavedDetails()
    {
        var playback = new FakePlayback();
        var metadata = new FakeMetadata { OnSave = () => Assert.False(playback.Closed) };
        await using var editor = CreateEditor(metadata, playback, releasePlayback: false);
        await editor.LoadAsync();
        var opens = playback.Opens;
        editor.Tags = "beach, Beach\nsunset";

        await editor.SaveAsync();

        Assert.Equal(["beach", "sunset"], metadata.Saved!.Tags);
        Assert.Equal(opens, playback.Opens);
        Assert.Equal("beach" + Environment.NewLine + "sunset", editor.Tags);
        Assert.False(editor.IsDirty);
    }

    [Fact]
    public async Task PlaybackStartsEvenWhenTheMetadataReadFails()
    {
        var playback = new FakePlayback();
        await using var editor = CreateEditor(new FakeMetadata { ReadError = new InvalidDataException("bad manifest") }, playback);

        await editor.LoadAsync();

        Assert.Equal(1, playback.Opens);
        Assert.False(editor.CanEdit);
        Assert.Contains("bad manifest", editor.Status);
    }

    [Fact]
    public async Task EditingIsAvailableWhilePlaybackIsStillOpening()
    {
        var opening = new TaskCompletionSource();
        var playback = new FakePlayback { OpenGate = opening.Task };
        await using var editor = CreateEditor(new FakeMetadata(), playback);

        var loading = editor.LoadAsync();

        Assert.True(editor.CanEdit);
        Assert.False(loading.IsCompleted);
        opening.SetResult();
        await loading;
    }

    [Fact]
    public async Task FailedPlaybackStartReleasesThePlayer()
    {
        var playback = new FakePlayback { StartError = new InvalidOperationException("The video could not be played.") };
        await using var editor = CreateEditor(new FakeMetadata(), playback);

        await editor.LoadAsync();

        Assert.Equal(["create", "start", "close"], playback.Calls);
        Assert.Null(editor.Player);
        Assert.False(editor.IsPlaying);
        Assert.True(editor.CanEdit);
        Assert.Contains("could not be played", editor.Status);
    }

    [Fact]
    public async Task DirtyIsTheDifferenceFromTheLoadedState()
    {
        var metadata = new FakeMetadata
        {
            Stored = VideoMetadata.Empty with { Title = "Beach", Tags = ["sun"], Chapters = [new VideoChapter(9, TimeSpan.FromTicks(12_345_678), "Swim", "", [])] },
        };
        await using var editor = CreateEditor(metadata, new FakePlayback());
        await editor.LoadAsync();
        Assert.False(editor.IsDirty);

        editor.Title = "Lake";
        Assert.True(editor.IsDirty);
        editor.Title = "Beach";
        Assert.False(editor.IsDirty);

        editor.Tags = "sun, Sun";
        Assert.False(editor.IsDirty);
        editor.Chapters[0].Title = "Dive";
        Assert.True(editor.IsDirty);
        editor.Chapters[0].Title = "Swim";
        Assert.False(editor.IsDirty);
        Assert.Equal(TimeSpan.FromTicks(12_345_678), editor.Chapters[0].ToChapter().Position);
    }

    [Fact]
    public async Task SaveKeepsUnchangedRowsAndAdoptsTheStoredForm()
    {
        var thumbnail = new VideoThumbnail(Guid.NewGuid(), TimeSpan.FromSeconds(1), "/unused/thumb.jpg");
        var metadata = new FakeMetadata
        {
            Stored = VideoMetadata.Empty with { Chapters = [new VideoChapter(9, TimeSpan.FromSeconds(2), "Swim", "", [])], Thumbnails = [thumbnail] },
        };
        await using var editor = CreateEditor(metadata, new FakePlayback());
        await editor.LoadAsync();
        var chapterRow = editor.Chapters[0];
        var thumbnailRow = editor.Thumbnails[0];
        var reads = metadata.Reads;
        editor.Title = "  Beach  ";

        await editor.SaveAsync();

        Assert.Same(chapterRow, Assert.Single(editor.Chapters));
        Assert.Same(thumbnailRow, Assert.Single(editor.Thumbnails));
        Assert.Equal(reads, metadata.Reads);
        Assert.Equal("Beach", editor.Title);
        Assert.False(editor.IsDirty);
    }

    [Fact]
    public async Task MissingTitleStaysBlankWithTheFileNameAsPlaceholder()
    {
        await using var editor = CreateEditor(new FakeMetadata(), new FakePlayback());
        await editor.LoadAsync();
        Assert.Equal("", editor.Title);
        Assert.Equal("Test video", editor.TitlePlaceholder);
    }

    [Fact]
    public async Task PartialRecordedDateIsShownAndKeptUnlessEdited()
    {
        var metadata = new FakeMetadata
        {
            Stored = VideoMetadata.Empty with { RecordedAt = new DateTimeOffset(1997, 1, 1, 0, 0, 0, TimeSpan.Zero), RecordedAtText = "1997" },
        };
        await using var editor = CreateEditor(metadata, new FakePlayback());
        await editor.LoadAsync();
        Assert.Equal("1997", editor.RecordedAt);

        editor.Description = "Unrelated";
        await editor.SaveAsync();
        Assert.Equal("1997", metadata.Saved!.RecordedAtText);

        editor.RecordedAt = "1997-07";
        await editor.SaveAsync();
        Assert.Equal(new DateTimeOffset(1997, 7, 1, 0, 0, 0, TimeSpan.Zero), metadata.Saved!.RecordedAt);
        Assert.Equal("1997-07", RecordedDate.ToTagText(metadata.Saved));
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
        await using var editor = CreateEditor(metadata, playback, releasePlayback: true);
        await editor.LoadAsync();
        editor.AddChapter();
        editor.Chapters[0].StartText = "1:60";
        await editor.SaveAsync();
        Assert.Null(metadata.Saved);
        Assert.False(playback.Closed);
        Assert.True(editor.IsDirty);
        Assert.Contains("\"New chapter\" has an invalid start time", editor.Status);

        editor.Chapters[0].StartText = "11";
        await editor.SaveAsync();
        Assert.Null(metadata.Saved);
        Assert.False(playback.Closed);
        Assert.Contains("after the end", editor.Status);
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

    private static VideoEditorViewModel CreateEditor(FakeMetadata metadata, FakePlayback playback, bool releasePlayback = false) => new(
        new LibraryVideo(Guid.NewGuid(), "Videos/test.mkv", "Test video", TimeSpan.FromSeconds(10), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
        "/unused/test.mkv", metadata, playback) { ReleasePlaybackDuringSave = releasePlayback };

    private sealed class FakeMetadata : IVideoMetadataService
    {
        public Action? OnSave { get; init; }
        public Func<CancellationToken, Task>? SaveOperation { get; init; }
        public Exception? ReadError { get; init; }
        public VideoMetadata Stored { get; set; } = VideoMetadata.Empty;
        public VideoMetadata? Saved { get; private set; }
        public int Reads { get; private set; }
        public Task<VideoMetadata> ReadAsync(LibraryVideo video, CancellationToken cancellationToken = default)
        {
            Reads++;
            return ReadError is null ? Task.FromResult(Stored) : Task.FromException<VideoMetadata>(ReadError);
        }
        public async Task SaveAsync(LibraryVideo video, VideoMetadata metadata, CancellationToken cancellationToken = default)
        {
            OnSave?.Invoke();
            if (SaveOperation is { } save) await save(cancellationToken);
            Saved = metadata;
            Stored = metadata;
        }
        public Task<VideoThumbnail> CaptureThumbnailAsync(LibraryVideo video, TimeSpan position, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakePlayback : IVideoPlayback
    {
        public event EventHandler<PlaybackUpdate>? Updated { add { } remove { } }
        public long Version => 1;
        public List<string> Calls { get; } = [];
        public bool Closed { get; private set; }
        public int Opens { get; private set; }
        public bool LastPlay { get; private set; }
        public TimeSpan LastPosition { get; private set; }
        public Task OpenGate { get; init; } = Task.CompletedTask;
        public Exception? StartError { get; init; }
        // A real MediaPlayer needs the native runtime; the editor only passes it through to the view.
        public Task<MediaPlayer> CreatePlayerAsync() { Calls.Add("create"); return Task.FromResult<MediaPlayer>(null!); }
        public async Task StartAsync(string path, TimeSpan position, bool play, CancellationToken cancellationToken)
        {
            Calls.Add("start");
            Opens++; Closed = false; LastPosition = position; LastPlay = play;
            await OpenGate;
            if (StartError is not null) throw StartError;
        }
        public Task CloseAsync() { Calls.Add("close"); Closed = true; return Task.CompletedTask; }
        public Task SetPlayingAsync(bool play) { LastPlay = play; return Task.CompletedTask; }
        public Task SeekAsync(TimeSpan position) { LastPosition = position; return Task.CompletedTask; }
        public Task SetVolumeAsync(int volume) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
