using HomeVideoLibrary.Core;
using HomeVideoLibrary.Desktop.Playback;
using HomeVideoLibrary.Desktop.ViewModels;
using HomeVideoLibrary.Desktop.ViewModels.VideoPage;
using HomeVideoLibrary.Infrastructure;
using HomeVideoLibrary.UI.Logic;
using LibVLCSharp.Shared;

namespace HomeVideoLibrary.Tests;

/// <summary>Metadata held in memory: reads return <see cref="Stored"/>, saves replace it.</summary>
internal sealed class FakeMetadata : IVideoMetadataService
{
    private int _captures;
    public Action? OnSave { get; init; }
    public Func<CancellationToken, Task>? SaveOperation { get; init; }
    public Exception? ReadError { get; init; }
    public VideoMetadata Stored { get; set; } = VideoMetadata.Empty;
    public VideoMetadata? Saved { get; private set; }
    public int Reads { get; private set; }
    public int Saves { get; private set; }

    public Task<VideoMetadata> ReadAsync(LibraryVideo video, CancellationToken cancellationToken = default)
    {
        Reads++;
        return ReadError is null ? Task.FromResult(Stored) : Task.FromException<VideoMetadata>(ReadError);
    }

    public async Task SaveAsync(LibraryVideo video, VideoMetadata metadata, CancellationToken cancellationToken = default)
    {
        OnSave?.Invoke();
        if (SaveOperation is { } save) await save(cancellationToken);
        Saves++;
        Saved = metadata;
        Stored = metadata;
    }

    public Task<VideoThumbnail> CaptureThumbnailAsync(LibraryVideo video, TimeSpan position, CancellationToken cancellationToken = default) =>
        Task.FromResult(new VideoThumbnail(Guid.NewGuid(), position, Path.Combine(Path.GetTempPath(), $"hvl-capture-{Interlocked.Increment(ref _captures)}.jpg")));
}

/// <summary>Playback without a native player. Raise <see cref="Updated"/> with <see cref="Raise"/>.</summary>
internal sealed class FakePlayback : IVideoPlayback
{
    public event EventHandler<PlaybackUpdate>? Updated;
    public long Version { get; private set; } = 1;
    public List<string> Calls { get; } = [];
    public List<TimeSpan> Seeks { get; } = [];
    public List<int> Volumes { get; } = [];
    public bool Closed { get; private set; }
    public int Opens { get; private set; }
    public bool LastPlay { get; private set; }
    public TimeSpan LastStart { get; private set; }
    public Task OpenGate { get; init; } = Task.CompletedTask;
    /// <summary>When set, each seek waits for it, so a test can hold a seek in flight.</summary>
    public Task? SeekGate { get; set; }
    public Exception? StartError { get; init; }

    public void Raise(PlaybackUpdate update) => Updated?.Invoke(this, update);

    // A real MediaPlayer needs the native runtime; the page only passes it through to the view.
    public Task<MediaPlayer> CreatePlayerAsync() { Calls.Add("create"); return Task.FromResult<MediaPlayer>(null!); }

    public async Task StartAsync(string path, TimeSpan position, bool play, CancellationToken cancellationToken)
    {
        Calls.Add("start");
        Opens++; Closed = false; LastStart = position; LastPlay = play;
        await OpenGate;
        if (StartError is not null) throw StartError;
    }

    public Task CloseAsync() { Calls.Add("close"); Closed = true; Version++; return Task.CompletedTask; }
    public Task SetPlayingAsync(bool play) { LastPlay = play; return Task.CompletedTask; }

    public async Task SeekAsync(TimeSpan position)
    {
        Seeks.Add(position);
        if (SeekGate is { } gate) await gate;
    }

    public Task SetVolumeAsync(int volume) { Volumes.Add(volume); return Task.CompletedTask; }
    public ValueTask DisposeAsync() { Calls.Add("dispose"); return ValueTask.CompletedTask; }
}

internal static class VideoPages
{
    public static VideoRecord Record(VideoMetadata? content = null, TimeSpan? duration = null, string name = "Test video",
        IReadOnlyList<VideoThumbnailRef>? thumbnails = null)
    {
        var metadata = content ?? VideoMetadata.Empty;
        var id = Guid.NewGuid();
        return new VideoRecord(id, LibraryPaths.VideoRelativePath(id), name, duration ?? TimeSpan.FromSeconds(10), DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow).WithContent(new VideoTextMetadata(metadata with { Thumbnails = [] }, thumbnails ?? [], duration ?? TimeSpan.FromSeconds(10), []),
            new FileStamp(1, 1));
    }

    /// <summary>A page whose waits complete at once unless <paramref name="delay"/> says otherwise.</summary>
    public static VideoPageViewModel Create(FakeMetadata metadata, FakePlayback playback, VideoRecord? record = null,
        bool releasePlayback = false, VideoPageSession? session = null, LibrarySnapshot? snapshot = null,
        TagSuggestionProvider? suggest = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        record ??= Record(metadata.Stored);
        return new VideoPageViewModel(new VideoPageDependencies
        {
            Record = record, Path = "/unused/test.mkv", Metadata = metadata, Playback = playback,
            Snapshot = snapshot ?? LibrarySnapshot.Empty, SuggestTags = suggest ?? ((_, _, _) => []),
            Session = session ?? new VideoPageSession(), Post = action => action(),
            ReleasePlaybackDuringSave = releasePlayback, Delay = delay,
        });
    }
}
