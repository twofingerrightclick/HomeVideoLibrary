using HomeVideoLibrary.Core;
using HomeVideoLibrary.Desktop.ViewModels;
using HomeVideoLibrary.Infrastructure;
using Xunit;
using static HomeVideoLibrary.Tests.TestLibrary;

namespace HomeVideoLibrary.Tests;

public sealed class HoverStripSchedulerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hvl-strip-scheduler-" + Guid.NewGuid().ToString("N"));
    private readonly FakeMatroska _frames = new();
    /// <summary>The video path of each build, in the order builds started.</summary>
    private readonly List<string> _builds = [];
    private readonly JobQueue _jobs = new(_ => Task.CompletedTask);
    private LibraryPaths Paths => new(_root);

    public HoverStripSchedulerTests() => _frames.BeforeKeyframe = (path, _) =>
    {
        lock (_builds) { if (!_builds.Contains(path)) _builds.Add(path); }
        return Task.CompletedTask;
    };

    [Fact]
    public Task TapesBuildOneAtATimeAndARequestedTapeJumpsTheQueue() => UiThread.Run(async () =>
    {
        var (store, scheduler) = Create();
        var tapes = await TapesAsync(3);
        var first = new TaskCompletionSource();
        Hold(tapes[0], first.Task);

        scheduler.Enqueue(tapes);
        await WaitUntilAsync(() => { lock (_builds) return _builds.Count == 1; });
        var requested = scheduler.RequestAsync(tapes[2], default);
        await UiThread.Settle();
        lock (_builds) Assert.Single(_builds);
        first.SetResult();

        var strip = await requested;
        Assert.Equal(store.TryGet(tapes[2]), strip);
        await WaitUntilAsync(() => store.TryGet(tapes[1]) is not null);
        var paths = tapes.Select(PathOf).ToArray();
        lock (_builds) Assert.Equal([paths[0], paths[2], paths[1]], _builds);
        await scheduler.DisposeAsync();
    });

    [Fact]
    public Task BackfillWaitsForUserJobsButARequestedTapeDoesNot() => UiThread.Run(async () =>
    {
        var (store, scheduler) = Create();
        var tapes = await TapesAsync(2);
        var job = new TaskCompletionSource();
        _jobs.Enqueue("import", JobLane.Import, async (_, _) => { await job.Task; return JobOutcome.Done("ok"); });

        scheduler.Enqueue(tapes);
        await UiThread.Settle();
        lock (_builds) Assert.Empty(_builds);
        Assert.NotNull(await scheduler.RequestAsync(tapes[1], default));
        await UiThread.Settle();
        Assert.Null(store.TryGet(tapes[0]));

        job.SetResult();
        await WaitUntilAsync(() => store.TryGet(tapes[0]) is not null);
        await scheduler.DisposeAsync();
    });

    [Fact]
    public Task AFailedBuildIsNotRetriedUntilTheMediaChanges() => UiThread.Run(async () =>
    {
        var (store, scheduler) = Create();
        var tape = (await TapesAsync(1))[0];
        _frames.BeforeKeyframe = (_, _) => throw new InvalidDataException("unreadable");

        Assert.Null(await scheduler.RequestAsync(tape, default));
        var attempts = _frames.Keyframes;
        Assert.Null(await scheduler.RequestAsync(tape, default));
        scheduler.Enqueue([tape with { Title = "Retitled" }]);
        await UiThread.Settle();
        Assert.Equal(attempts, _frames.Keyframes);

        _frames.BeforeKeyframe = null;
        var replaced = tape with { MediaIdentity = "feed" };
        var strip = await scheduler.RequestAsync(replaced, default);
        Assert.NotNull(strip);
        Assert.Equal(store.TryGet(replaced), strip);
        await scheduler.DisposeAsync();
    });

    [Fact]
    public Task AMetadataSaveDoesNotRebuildTheStrip() => UiThread.Run(async () =>
    {
        var (_, scheduler) = Create();
        var tape = (await TapesAsync(1))[0];
        var strip = await scheduler.RequestAsync(tape, default);
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
    public Task DisposingCancelsTheRunningBuildAndWaitingRequestsSilently() => UiThread.Run(async () =>
    {
        var (store, scheduler) = Create();
        var tapes = await TapesAsync(2);
        var started = new TaskCompletionSource();
        _frames.BeforeKeyframe = async (_, token) => { started.TrySetResult(); await Task.Delay(Timeout.Infinite, token); };

        scheduler.Enqueue(tapes);
        var requested = scheduler.RequestAsync(tapes[1], default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
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

        scheduler.Enqueue(unbuildable);
        foreach (var record in unbuildable) Assert.Null(await scheduler.RequestAsync(record, default));
        await UiThread.Settle();
        Assert.Equal(0, _frames.Keyframes);
        await scheduler.DisposeAsync();
    });

    private (HoverStripStore Store, HoverStripScheduler Scheduler) Create()
    {
        var store = new HoverStripStore(Paths, _frames);
        return (store, new HoverStripScheduler(store, PathOf, _jobs));
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
