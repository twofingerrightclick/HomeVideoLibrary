using System.Diagnostics;
using HomeVideoLibrary.Infrastructure;

namespace HomeVideoLibrary.Desktop.ViewModels;

/// <summary>Builds the open library's hover strips in the background, one tape at a time. Strips are a cache, so this is
/// not a <see cref="JobQueue"/> lane: it shows no activity, raises no toasts and never holds up a library switch or close.
/// Backfill waits while user jobs run; a requested tape (one under the pointer) goes first and builds even then. A tape
/// whose build failed is not tried again until its media changes. All state is on the UI thread, like
/// <see cref="JobQueue"/>: the scheduler is created there and its loop resumes there; builds run on the thread pool.</summary>
public sealed class HoverStripScheduler : IAsyncDisposable
{
    private readonly HoverStripStore _store;
    private readonly Func<VideoRecord, string> _pathOf;
    private readonly JobQueue _jobs;
    private readonly OrderedDictionary<Guid, VideoRecord> _queue = [];
    private readonly Dictionary<(Guid, string), TaskCompletionSource<string?>> _requests = [];
    /// <summary>The media identity whose build failed, per video.</summary>
    private readonly Dictionary<Guid, string> _failed = [];
    private readonly CancellationTokenSource _lifetime = new();
    private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _loop = Task.CompletedTask;

    /// <param name="pathOf">The video file of a record.</param>
    /// <param name="jobs">The user's jobs; backfill waits while any is active.</param>
    public HoverStripScheduler(HoverStripStore store, Func<VideoRecord, string> pathOf, JobQueue jobs)
    {
        _store = store; _pathOf = pathOf; _jobs = jobs;
    }

    /// <summary>Queues a strip for each record that can have one, after those already queued; a record replaces a queued
    /// record of the same video in place. Records whose strip exists are skipped when their turn comes.</summary>
    public void Enqueue(IEnumerable<VideoRecord> records)
    {
        if (_lifetime.IsCancellationRequested) return;
        foreach (var record in records)
        {
            if (IsWanted(record)) _queue[record.Id] = record;
            else _queue.Remove(record.Id);
        }
        Run();
    }

    /// <summary>The strip of <paramref name="record"/>'s media: at once when it exists, otherwise when it is built, ahead of
    /// every other tape. Completes with null when there can be none: the file is unreadable or missing, its build failed,
    /// or it changed during the build.</summary>
    public Task<string?> RequestAsync(VideoRecord record, CancellationToken cancellationToken)
    {
        if (_store.TryGet(record) is { } built) return Task.FromResult<string?>(built);
        if (_lifetime.IsCancellationRequested || !IsWanted(record)) return Task.FromResult<string?>(null);
        if (!_requests.TryGetValue(Key(record), out var request))
            _requests[Key(record)] = request = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue[record.Id] = record;
        _wake.TrySetResult();
        Run();
        return request.Task.WaitAsync(cancellationToken);
    }

    private bool IsWanted(VideoRecord record) => record is { State: VideoRecordState.Ok, IsMissing: false } &&
        HoverStripStore.CanBuild(record) && _failed.GetValueOrDefault(record.Id) != record.MediaIdentity;

    private static (Guid, string) Key(VideoRecord record) => (record.Id, record.MediaIdentity!);

    private void Run()
    {
        if (_loop.IsCompleted && _queue.Count > 0) _loop = RunAsync();
    }

    /// <summary>Requested tapes first, then the rest in order once no user job is active.</summary>
    private VideoRecord? Next() => _queue.Values.FirstOrDefault(record => _requests.ContainsKey(Key(record)))
        ?? (_jobs.HasActive ? null : _queue.GetAt(0).Value);

    private async Task RunAsync()
    {
        var token = _lifetime.Token;
        while (_queue.Count > 0 && !token.IsCancellationRequested)
        {
            if (Next() is not { } record)
            {
                _wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                await Task.WhenAny(_jobs.WhenIdleAsync(), _wake.Task);
                continue;
            }
            _queue.Remove(record.Id);
            string? strip = null;
            try { strip = await Task.Run(() => _store.GetOrBuildAsync(record, _pathOf(record), token), token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                Trace.TraceWarning($"Could not build the hover strip of “{record.Label}”: {exception.Message}");
                _failed[record.Id] = record.MediaIdentity!;
            }
            if (_requests.Remove(Key(record), out var request)) request.TrySetResult(strip);
        }
    }

    /// <summary>Stops the running build (killing its processes) and cancels waiting requests; nothing is announced.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_lifetime.IsCancellationRequested) return;
        _lifetime.Cancel();
        _wake.TrySetResult();
        foreach (var request in _requests.Values) request.TrySetCanceled(_lifetime.Token);
        _requests.Clear();
        _queue.Clear();
        await _loop;
        _lifetime.Dispose();
    }
}
