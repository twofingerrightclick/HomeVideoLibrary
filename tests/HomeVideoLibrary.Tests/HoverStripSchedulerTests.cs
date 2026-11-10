using HomeVideoLibrary.Core;
using HomeVideoLibrary.Desktop.ViewModels;
using HomeVideoLibrary.Infrastructure;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using static HomeVideoLibrary.Tests.TestLibrary;

namespace HomeVideoLibrary.Tests;

public sealed class HoverStripSchedulerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hvl-strip-scheduler-" + Guid.NewGuid().ToString("N"));
    private readonly FakeMatroska _frames = new();
    private readonly FakeTimeProvider _time = new();
    /// <summary>The video path of each build, in the order builds started.</summary>
    private readonly List<string> _builds = [];
    private bool _busy;
    private LibraryPaths Paths => new(_root);

    public HoverStripSchedulerTests() => _frames.BeforeKeyframe = (path, _) =>
    {
        lock (_builds) { if (!_builds.Contains(path)) _builds.Add(path); }
        return Task.CompletedTask;
    };

    private string[] Builds { get { lock (_builds) return _builds.ToArray(); } }

    [Fact]
    public Task RestingOnTheLastTapeOfASweepBuildsItFirstAndSweptTapesKeepTheirBackfillPlace() => UiThread.Run(async () =>
    {
        var (store, scheduler) = Create();
        var tapes = await TapesAsync(6);
        var first = new TaskCompletionSource();
        Hold(tapes[0], first.Task);
        scheduler.Enqueue(tapes);
        await WaitUntilAsync(() => Builds.Length == 1);

        foreach (var swept in tapes[2..5])
        {
            using var leave = new CancellationTokenSource();
            var request = scheduler.RequestAsync(swept, leave.Token);
            _time.Advance(HoverStripScheduler.Dwell / 3);
            leave.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        }
        var rested = scheduler.RequestAsync(tapes[5], default);
        _time.Advance(HoverStripScheduler.Dwell);
        first.SetResult();

        var strip = await rested;
        Assert.Equal(store.TryGet(tapes[5]), strip);
        await WaitUntilAsync(() => tapes.All(tape => store.TryGet(tape) is not null));
        Assert.Equal(new[] { 0, 5, 1, 2, 3, 4 }.Select(index => PathOf(tapes[index])), Builds);
        await scheduler.DisposeAsync();
    });

    [Fact]
    public Task AHeldTapeStopsItsBuildIsNotBuiltWhileHeldAndIsQueuedAgainAfter() => UiThread.Run(async () =>
    {
        var (store, scheduler) = Create();
        var tape = (await TapesAsync(1))[0];
        var never = new TaskCompletionSource();
        Hold(tape, never.Task);
        scheduler.Enqueue([tape]);
        await WaitUntilAsync(() => Builds.Length == 1);

        var hold = await scheduler.HoldAsync(tape.Id);
        Assert.Null(await scheduler.RequestAsync(tape, default));
        Assert.Null(store.TryGet(tape));
        Assert.Empty(Directory.GetFileSystemEntries(Paths.ScratchDirectory));

        _frames.BeforeKeyframe = (_, _) => Task.CompletedTask;
        await hold.DisposeAsync();
        await WaitUntilAsync(() => store.TryGet(tape) is not null);

        scheduler.Forget(tape.Id);
        Assert.Null(store.TryGet(tape));
        Assert.Empty(Directory.GetFiles(Paths.StripDirectory));
        await scheduler.DisposeAsync();
    });

    [Fact]
    public Task AHoverShorterThanTheDwellBuildsNothing() => UiThread.Run(async () =>
    {
        var (_, scheduler) = Create();
        var tape = (await TapesAsync(1))[0];
        using var leave = new CancellationTokenSource();

        var request = scheduler.RequestAsync(tape, leave.Token);
        _time.Advance(HoverStripScheduler.Dwell / 2);
        leave.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        _time.Advance(HoverStripScheduler.Dwell);
        await UiThread.Settle();

        Assert.Empty(Builds);
        await scheduler.DisposeAsync();
    });

    [Fact]
    public Task BackfillStaysPausedWhileBusyAndResumesAfterButARequestedTapeBuildsMeanwhile() => UiThread.Run(async () =>
    {
        var (store, scheduler) = Create();
        var tapes = await TapesAsync(2);
        _busy = true;

        scheduler.Enqueue(tapes);
        await UiThread.Settle();
        scheduler.BusyChanged();
        await UiThread.Settle();
        Assert.Empty(Builds);
        var requested = scheduler.RequestAsync(tapes[1], default);
        _time.Advance(HoverStripScheduler.Dwell);
        Assert.NotNull(await requested);
        await UiThread.Settle();
        Assert.Null(store.TryGet(tapes[0]));

        _busy = false;
        scheduler.BusyChanged();
        await WaitUntilAsync(() => store.TryGet(tapes[0]) is not null);
        await scheduler.DisposeAsync();
    });

    [Fact]
    public Task OnlyAFailureOfTheUnchangedFileIsRememberedAndOnlyForAWhile() => UiThread.Run(async () =>
    {
        var (store, scheduler) = Create();
        var tape = (await TapesAsync(1))[0];

        _frames.BeforeKeyframe = (_, _) => throw new IOException("disk busy");
        Assert.Null(await RequestAsync(scheduler, tape));
        var attempts = _frames.Keyframes;
        _frames.BeforeKeyframe = async (path, _) =>
        {
            await File.AppendAllTextAsync(path, " saved meanwhile");
            throw new InvalidDataException("file changed under ffmpeg");
        };
        Assert.Null(await RequestAsync(scheduler, tape));
        Assert.True(_frames.Keyframes > attempts, "an I/O failure was remembered");
        attempts = _frames.Keyframes;

        _frames.BeforeKeyframe = (_, _) => throw new InvalidDataException("unreadable");
        var current = tape with { Stamp = FileStamp.TryRead(PathOf(tape)) };
        scheduler.Enqueue([current]);
        await WaitUntilAsync(() => _frames.Keyframes > attempts);
        await UiThread.Settle();
        attempts = _frames.Keyframes;
        Assert.Null(await RequestAsync(scheduler, current));
        scheduler.Enqueue([current with { Title = "Retitled" }]);
        await UiThread.Settle();
        Assert.Equal(attempts, _frames.Keyframes);

        _frames.BeforeKeyframe = null;
        _time.Advance(HoverStripScheduler.FailureRetry);
        Assert.NotNull(await RequestAsync(scheduler, current));
        var replaced = current with { MediaIdentity = "feed" };
        scheduler.Enqueue([replaced]);
        await WaitUntilAsync(() => store.TryGet(replaced) is not null);
        await scheduler.DisposeAsync();
    });

    [Fact]
    public Task AMetadataSaveDoesNotRebuildTheStrip() => UiThread.Run(async () =>
    {
        var (_, scheduler) = Create();
        var tape = (await TapesAsync(1))[0];
        var strip = await RequestAsync(scheduler, tape);
        var captured = _frames.Keyframes;

        await File.AppendAllTextAsync(PathOf(tape), " retitled");
        var saved = tape with { Title = "Retitled", Stamp = FileStamp.TryRead(PathOf(tape)) };
        scheduler.Enqueue([saved]);
        await UiThread.Settle();

        Assert.Equal(strip, await scheduler.RequestAsync(saved, default));
        Assert.Equal(captured, _frames.Keyframes);
        await scheduler.DisposeAsync();
    });

    [Fact]
    public Task ARequestUsesTheNewestRecordAndEndsWhenItsVideoCanNoLongerHaveAStrip() => UiThread.Run(async () =>
    {
        var (store, scheduler) = Create();
        var tapes = await TapesAsync(2);
        var newer = tapes[0] with { MediaIdentity = "beef" };
        scheduler.Enqueue([newer]);

        var strip = await RequestAsync(scheduler, tapes[0]);
        Assert.NotNull(strip);
        Assert.Equal(store.TryGet(newer), strip);
        Assert.Null(store.TryGet(tapes[0]));

        _busy = true;
        var orphan = scheduler.RequestAsync(tapes[1], default);
        scheduler.Enqueue([tapes[1] with { IsMissing = true }]);
        Assert.Null(await orphan.WaitAsync(TimeSpan.FromSeconds(5)));
        await scheduler.DisposeAsync();
    });

    [Fact]
    public Task DisposingCancelsTheRunningBuildAndWaitingRequestsSilentlyOnce() => UiThread.Run(async () =>
    {
        var (store, scheduler) = Create();
        var tapes = await TapesAsync(2);
        var started = new TaskCompletionSource();
        _frames.BeforeKeyframe = async (_, token) => { started.TrySetResult(); await Task.Delay(Timeout.Infinite, token); };

        scheduler.Enqueue(tapes);
        var requested = scheduler.RequestAsync(tapes[1], default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var disposals = new[] { scheduler.DisposeAsync().AsTask(), scheduler.DisposeAsync().AsTask() };
        await Task.WhenAll(disposals).WaitAsync(TimeSpan.FromSeconds(5));
        await scheduler.DisposeAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => requested);
        Assert.All(tapes, tape => Assert.Null(store.TryGet(tape)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Paths.ScratchDirectory));
        Assert.Null(await scheduler.RequestAsync(tapes[0], default));
    });

    [Fact]
    public Task UnreadableMissingOrUnidentifiedTapesAreNeverBuilt() => UiThread.Run(async () =>
    {
        var (_, scheduler) = Create();
        var tape = (await TapesAsync(1))[0];
        VideoRecord[] unbuildable = [tape with { State = VideoRecordState.Unreadable }, tape with { IsMissing = true }, tape with { MediaIdentity = null }];

        foreach (var record in unbuildable)
        {
            scheduler.Enqueue([record]);
            Assert.Null(await scheduler.RequestAsync(record, default));
        }
        await UiThread.Settle();
        Assert.Equal(0, _frames.Keyframes);
        await scheduler.DisposeAsync();
    });

    private (HoverStripStore Store, HoverStripScheduler Scheduler) Create()
    {
        var store = new HoverStripStore(Paths, _frames);
        return (store, new HoverStripScheduler(store, PathOf, () => _busy, _time));
    }

    /// <summary>Requests a strip and rests on the tape past the dwell.</summary>
    private Task<string?> RequestAsync(HoverStripScheduler scheduler, VideoRecord tape)
    {
        var request = scheduler.RequestAsync(tape, default);
        _time.Advance(HoverStripScheduler.Dwell);
        return request.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private string PathOf(VideoRecord record) => Path.Combine(Paths.VideosDirectory, $"{record.Id:N}.mkv");

    /// <summary>Holds <paramref name="tape"/>'s first capture until <paramref name="release"/> completes.</summary>
    private void Hold(VideoRecord tape, Task release)
    {
        var previous = _frames.BeforeKeyframe;
        _frames.BeforeKeyframe = async (path, token) =>
        {
            await previous!(path, token);
            if (path == PathOf(tape)) await release.WaitAsync(token);
        };
    }

    private async Task<VideoRecord[]> TapesAsync(int count)
    {
        Directory.CreateDirectory(Paths.VideosDirectory);
        Directory.CreateDirectory(Paths.ScratchDirectory);
        var tapes = new List<VideoRecord>();
        for (var index = 0; index < count; index++)
        {
            var record = Record($"tape {index}") with { MediaIdentity = $"{index:x4}" };
            await File.WriteAllTextAsync(PathOf(record), record.DisplayName);
            tapes.Add(record with { Stamp = FileStamp.TryRead(PathOf(record)) });
        }
        return tapes.ToArray();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline) await UiThread.Settle();
        Assert.True(condition());
    }

    public void Dispose()
    {
        DeleteDirectory(_root);
        DeleteDirectory(_frames.OutputDirectory);
    }
}
