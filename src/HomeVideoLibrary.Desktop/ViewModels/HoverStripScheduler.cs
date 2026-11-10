using System.Diagnostics;
using HomeVideoLibrary.Core;
using HomeVideoLibrary.Infrastructure;

namespace HomeVideoLibrary.Desktop.ViewModels;

/// <summary>Builds the open library's hover strips in the background, one tape at a time. Strips are a cache, so this is
/// not a <see cref="JobQueue"/> lane: it shows no activity, raises no toasts and never holds up a library switch or close.
/// <para>Order: a tape under the pointer first, once it has been requested for <see cref="Dwell"/> (so sweeping across the
/// shelf builds nothing), most recent request first; then the backfill queue, but only while the app is not busy. A request
/// is withdrawn when its last waiter gives up, so tapes the pointer has left never outrank backfill.</para>
/// <para>A tape whose build failed for a reason that will not go away by itself is not tried again until its media changes
/// or <see cref="FailureRetry"/> passes.</para>
/// <para>All state is on the UI thread, like <see cref="JobQueue"/>: the scheduler is created there and its loop resumes
/// there; builds run on the thread pool.</para></summary>
public sealed class HoverStripScheduler : IAsyncDisposable
{
    public static readonly TimeSpan Dwell = TimeSpan.FromMilliseconds(150);
    public static readonly TimeSpan FailureRetry = TimeSpan.FromMinutes(10);
    private readonly HoverStripStore _store;
    private readonly Func<VideoRecord, string> _pathOf;
    private readonly Func<bool> _isBusy;
    private readonly TimeProvider _time;
    /// <summary>The newest record of each video, from <see cref="Enqueue"/>; builds always use it.</summary>
    private readonly Dictionary<Guid, VideoRecord> _latest = [];
    private readonly OrderedDictionary<Guid, VideoRecord> _backfill = [];
    private readonly Dictionary<Guid, Request> _requests = [];
    private readonly Dictionary<Guid, (string Identity, DateTimeOffset At)> _failed = [];
    /// <summary>Videos whose file must not be read (see <see cref="HoldAsync"/>), with how many holds each has.</summary>
    private readonly Dictionary<Guid, int> _held = [];
    /// <summary>The build in progress: whose it is, how to stop it alone, and its task.</summary>
    private (Guid Id, CancellationTokenSource Stop, Task Task)? _building;
    private readonly CancellationTokenSource _lifetime = new();
    private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _loop = Task.CompletedTask;
    private Task? _disposal;

    /// <param name="pathOf">The video file of a record.</param>
    /// <param name="isBusy">Whether user work is running that backfill must not compete with; call <see cref="BusyChanged"/>
    /// when it may have changed.</param>
    public HoverStripScheduler(HoverStripStore store, Func<VideoRecord, string> pathOf, Func<bool> isBusy, TimeProvider? time = null)
    {
        _store = store; _pathOf = pathOf; _isBusy = isBusy; _time = time ?? TimeProvider.System;
    }

    /// <summary>Takes the newest records of videos, and queues a strip for each that can have one, after those already
    /// queued; a record replaces a queued record of the same video in place. A waiting request whose video can no longer
    /// have a strip completes with null. Records whose strip exists are skipped when their turn comes.</summary>
    public void Enqueue(IEnumerable<VideoRecord> records)
    {
        if (_lifetime.IsCancellationRequested) return;
        foreach (var record in records)
        {
            _latest[record.Id] = record;
            if (IsWanted(record))
            {
                _backfill[record.Id] = record;
                continue;
            }
            _backfill.Remove(record.Id);
            if (_requests.Remove(record.Id, out var request)) request.Done.TrySetResult(null);
        }
        Run();
    }

    /// <summary>The strip of the video's media: at once when it exists, otherwise when it is built. Completes with null when
    /// there can be none: the file is unreadable or missing, its build failed, or it changed during the build. Cancelling
    /// withdraws this waiter; the request is dropped with its last waiter.</summary>
    public async Task<string?> RequestAsync(VideoRecord record, CancellationToken cancellationToken)
    {
        if (!_latest.TryAdd(record.Id, record)) record = _latest[record.Id];
        if (_store.TryGet(record) is { } built) return built;
        if (_lifetime.IsCancellationRequested || !IsWanted(record)) return null;
        if (!_requests.TryGetValue(record.Id, out var request)) _requests[record.Id] = request = new Request();
        request.Waiters++;
        request.At = _time.GetUtcNow();
        Wake();
        try { return await request.Done.Task.WaitAsync(cancellationToken); }
        finally
        {
            if (--request.Waiters == 0 && _requests.GetValueOrDefault(record.Id) == request)
            {
                _requests.Remove(record.Id);
                Wake();
            }
        }
    }

    /// <summary>Stops reading the video's file: its build, if running, is cancelled and awaited (its processes killed),
    /// and it is neither built nor requested until the returned hold is disposed, when it is queued again if it is still
    /// known. For work that needs the file to itself, such as moving it to the Trash.</summary>
    public async Task<IAsyncDisposable> HoldAsync(Guid videoId)
    {
        _held[videoId] = _held.GetValueOrDefault(videoId) + 1;
        _backfill.Remove(videoId);
        if (_requests.Remove(videoId, out var request)) request.Done.TrySetResult(null);
        if (_building is { } building && building.Id == videoId)
        {
            building.Stop.Cancel();
            await building.Task.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
        return new Hold(this, videoId);
    }

    /// <summary>Forgets a video that has left the library, deleting its strips.</summary>
    public void Forget(Guid videoId)
    {
        _latest.Remove(videoId);
        _backfill.Remove(videoId);
        _failed.Remove(videoId);
        _store.Forget(videoId);
    }

    private void Release(Guid videoId)
    {
        if (--_held[videoId] > 0) return;
        _held.Remove(videoId);
        if (_latest.TryGetValue(videoId, out var record)) Enqueue([record]);
    }

    private sealed class Hold(HoverStripScheduler owner, Guid videoId) : IAsyncDisposable
    {
        private int _released;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) owner.Release(videoId);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Re-checks whether backfill may run; call when the busy condition may have changed.</summary>
    public void BusyChanged() => Wake();

    /// <summary>Drops a strip that turned out unreadable and queues its video again.</summary>
    public void Invalidate(Guid videoId, string strip)
    {
        _store.Invalidate(strip);
        if (_latest.TryGetValue(videoId, out var record)) Enqueue([record]);
    }

    private bool IsWanted(VideoRecord record) => record is { State: VideoRecordState.Ok, IsMissing: false } && !_held.ContainsKey(record.Id) &&
        HoverStripStore.CanBuild(record) &&
        !(_failed.TryGetValue(record.Id, out var failure) && failure.Identity == record.MediaIdentity && _time.GetUtcNow() - failure.At < FailureRetry);

    private void Wake()
    {
        _wake.TrySetResult();
        Run();
    }

    private void Run()
    {
        if (_loop.IsCompleted && !_lifetime.IsCancellationRequested && (_backfill.Count > 0 || _requests.Count > 0)) _loop = RunAsync();
    }

    private async Task RunAsync()
    {
        var token = _lifetime.Token;
        while (!token.IsCancellationRequested && (_backfill.Count > 0 || _requests.Count > 0))
        {
            _wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var (next, wait) = Next();
            if (next is null)
            {
                await (wait is { } delay ? Task.WhenAny(_wake.Task, Task.Delay(delay, _time, token)) : _wake.Task);
                continue;
            }
            _backfill.Remove(next.Id);
            string? path = null, strip = null;
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            try
            {
                var file = path = _pathOf(next);
                var build = Task.Run(() => _store.GetOrBuildAsync(next, file, stop.Token), stop.Token);
                _building = (next.Id, stop, build);
                strip = await build;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception exception)
            {
                Trace.TraceWarning($"Could not build the hover strip of “{next.Label}”: {exception.Message}");
                // Only a failure on the very file the record describes says something about its media; I/O errors and
                // a file saved meanwhile do not, and the save's re-read queues the video again.
                if (exception is InvalidDataException && path is not null && FileStamp.TryRead(path) == next.Stamp)
                    _failed[next.Id] = (next.MediaIdentity!, _time.GetUtcNow());
            }
            finally { _building = null; }
            if (_requests.Remove(next.Id, out var request)) request.Done.TrySetResult(strip);
        }
    }

    /// <summary>The next tape to build, or how long until a requested tape's dwell ends (null: until woken).</summary>
    private (VideoRecord? Next, TimeSpan? Wait) Next()
    {
        if (_requests.Count > 0)
        {
            var now = _time.GetUtcNow();
            var (id, newest) = _requests.MaxBy(pair => pair.Value.At);
            if (now - newest.At >= Dwell) return (_latest[id], null);
            // A tape under the pointer is about to be chosen; backfill would only delay it.
            return (null, Dwell - (now - newest.At));
        }
        return _backfill.Count > 0 && !_isBusy() ? (_backfill.GetAt(0).Value, null) : (null, null);
    }

    /// <summary>Stops the running build (killing its processes) and cancels waiting requests; nothing is announced. Every
    /// call, including concurrent ones, waits for the same shutdown.</summary>
    public ValueTask DisposeAsync() => new(_disposal ??= DisposeCoreAsync());

    private async Task DisposeCoreAsync()
    {
        _lifetime.Cancel();
        _wake.TrySetResult();
        foreach (var request in _requests.Values) request.Done.TrySetCanceled(_lifetime.Token);
        _requests.Clear();
        _backfill.Clear();
        await _loop;
        _lifetime.Dispose();
    }

    private sealed class Request
    {
        public TaskCompletionSource<string?> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Waiters { get; set; }
        /// <summary>When the latest waiter came.</summary>
        public DateTimeOffset At { get; set; }
    }
}
