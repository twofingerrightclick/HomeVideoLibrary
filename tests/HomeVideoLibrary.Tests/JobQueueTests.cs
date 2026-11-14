using System.Collections.Concurrent;
using HomeVideoLibrary.Desktop.ViewModels;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace HomeVideoLibrary.Tests;

public sealed class JobQueueTests
{
    [Fact]
    public Task JobsInALaneRunOneAtATimeInOrderAndShowTheirPlaceInTheQueue() => UiThread.Run(async () =>
    {
        var queue = new JobQueue(_ => Task.CompletedTask);
        var gates = Enumerable.Range(0, 3).Select(_ => new TaskCompletionSource()).ToArray();
        var started = new List<int>();
        var jobs = Enumerable.Range(0, 3).Select(index => queue.Enqueue($"render {index}", JobLane.Render, async (_, _) =>
        {
            started.Add(index);
            await gates[index].Task;
            return JobOutcome.Done("ok");
        })).ToArray();

        Assert.Equal(JobState.Running, jobs[0].State);
        Assert.Equal(["Queued (#1)", "Queued (#2)"], jobs.Skip(1).Select(job => job.StatusText));
        gates[0].SetResult();
        await jobs[0].Completion;
        await UiThread.Settle();
        Assert.Equal(JobState.Running, jobs[1].State);
        Assert.Equal("Queued (#1)", jobs[2].StatusText);
        gates[1].SetResult(); gates[2].SetResult();
        await queue.WhenIdleAsync();
        Assert.Equal([0, 1, 2], started);
        Assert.All(jobs, job => Assert.Equal(JobState.Succeeded, job.State));
    });

    [Fact]
    public Task CancellingAQueuedJobRemovesItWithoutRunningIt() => UiThread.Run(async () =>
    {
        var queue = new JobQueue(_ => Task.CompletedTask);
        var gate = new TaskCompletionSource();
        var ran = false;
        var first = queue.Enqueue("first", JobLane.Render, async (_, _) => { await gate.Task; return JobOutcome.Done("ok"); });
        var second = queue.Enqueue("second", JobLane.Render, (_, _) => { ran = true; return Task.FromResult(JobOutcome.Done("ok")); });
        var third = queue.Enqueue("third", JobLane.Render, (_, _) => Task.FromResult(JobOutcome.Done("ok")));

        second.CancelCommand.Execute(null);
        Assert.Equal(JobState.Cancelled, second.State);
        Assert.Equal("Queued (#1)", third.StatusText);
        gate.SetResult();
        await queue.WhenIdleAsync();
        Assert.False(ran);
        Assert.Equal(JobState.Succeeded, third.State);
    });

    [Fact]
    public Task ARunningJobEndsCancelledOrFailedAndTheLaneMovesOn() => UiThread.Run(async () =>
    {
        var queue = new JobQueue(_ => Task.CompletedTask);
        var finished = new List<string>();
        queue.Finished += job => finished.Add(job.Title);
        var running = queue.Enqueue("long", JobLane.Render, async (_, token) => { await Task.Delay(Timeout.Infinite, token); return JobOutcome.Done("never"); });
        var failing = queue.Enqueue("broken", JobLane.Render, (_, _) => throw new InvalidOperationException("no space left"));

        running.Cancel();
        await queue.WhenIdleAsync();
        await failing.Completion;

        Assert.Equal(JobState.Cancelled, running.State);
        Assert.Equal(JobState.Failed, failing.State);
        Assert.Equal("no space left", failing.StatusText);
        Assert.Equal(["long", "broken"], finished);
        Assert.False(queue.HasActive);
    });

    [Fact]
    public Task LanesRunSideBySide() => UiThread.Run(async () =>
    {
        var queue = new JobQueue(_ => Task.CompletedTask);
        var render = new TaskCompletionSource();
        var renderJob = queue.Enqueue("render", JobLane.Render, async (_, _) => { await render.Task; return JobOutcome.Done("ok"); });
        var import = queue.Enqueue("import", JobLane.Import, (_, _) => Task.FromResult(JobOutcome.Done("imported")));

        await import.Completion;
        Assert.Equal(JobState.Succeeded, import.State);
        Assert.Equal(JobState.Running, renderJob.State);
        render.SetResult();
        await queue.WhenIdleAsync();
    });

    [Fact]
    public Task ProgressNeverGoesBackwardsAndOpenOffersTheOutputs() => UiThread.Run(async () =>
    {
        var opened = new List<JobViewModel>();
        var queue = new JobQueue(job => { opened.Add(job); return Task.CompletedTask; });
        var output = Guid.NewGuid();
        var reported = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var job = queue.Enqueue("trim", JobLane.Render, async (progress, _) =>
        {
            progress.Report(new JobProgress("Copying", 0.6));
            progress.Report(new JobProgress("Copying", 0.4));
            reported.SetResult();
            await release.Task;
            return new JobOutcome("Saved 1 new video.", [output]);
        });

        await reported.Task;
        await UiThread.Settle();
        Assert.Equal(60, job.Percent, precision: 3);
        release.SetResult();
        await job.Completion;
        Assert.True(job.CanOpen);
        await job.OpenCommand.ExecuteAsync();
        Assert.Same(job, Assert.Single(opened));
    });

    [Fact]
    public Task SummaryNamesTheRunningJobImmediately() => UiThread.Run(async () =>
    {
        var queue = new JobQueue(_ => Task.CompletedTask);
        var gate = new TaskCompletionSource<JobOutcome>();
        var summaries = new List<string>();
        queue.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(JobQueue.Summary)) summaries.Add(queue.Summary); };

        queue.Enqueue("Import a.mov", JobLane.Import, (_, _) => gate.Task);
        Assert.Equal("Import a.mov · Starting…", queue.Summary);
        Assert.Equal("Import a.mov · Starting…", summaries[^1]);
        queue.Enqueue("Import b.mov", JobLane.Import, (_, _) => gate.Task);
        Assert.Equal("Import a.mov · Starting… · 1 queued", queue.Summary);
        queue.Enqueue("Trim", JobLane.Render, (_, _) => gate.Task);
        Assert.Equal("Trim · Starting… · 1 queued", queue.Summary);

        gate.SetResult(JobOutcome.Done("ok"));
        await queue.WhenIdleAsync();
        await UiThread.Settle();
        Assert.Equal(queue.Summary, summaries[^1]);
        Assert.EndsWith("· ok", queue.Summary);
    });

    [Fact]
    public Task ThrowingFinishedHandlerStillCompletesTheJobAndStartsTheNext() => UiThread.Run(async () =>
    {
        var queue = new JobQueue(_ => Task.CompletedTask);
        queue.Finished += _ => throw new InvalidOperationException("handler bug");
        var first = queue.Enqueue("first", JobLane.Render, (_, _) => Task.FromResult(JobOutcome.Done("ok")));
        var second = queue.Enqueue("second", JobLane.Render, (_, _) => Task.FromResult(JobOutcome.Done("ok")));

        await Task.WhenAll(first.Completion, second.Completion).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(JobState.Succeeded, second.State);
        Assert.False(queue.HasActive);
    });

    [Fact]
    public Task FinishedRaisedExactlyOncePerJob() => UiThread.Run(async () =>
    {
        var queue = new JobQueue(_ => Task.CompletedTask);
        var finished = new List<JobViewModel>();
        queue.Finished += finished.Add;
        var gate = new TaskCompletionSource();
        var running = queue.Enqueue("running", JobLane.Render, async (_, token) => { await gate.Task.WaitAsync(token); return JobOutcome.Done("ok"); });
        var queued = queue.Enqueue("queued", JobLane.Render, (_, _) => Task.FromResult(JobOutcome.Done("ok")));
        var failing = queue.Enqueue("failing", JobLane.Render, (_, _) => throw new IOException("disk"));
        var succeeding = queue.Enqueue("succeeding", JobLane.Render, (_, _) => Task.FromResult(JobOutcome.Done("ok")));

        queued.Cancel();
        queued.Cancel();
        running.Cancel();
        running.Cancel();
        await queue.WhenIdleAsync();
        await UiThread.Settle();

        Assert.Equal([queued, running, failing, succeeding], finished);
    });

    [Fact]
    public Task QueuePositionIsNullOnceRunningOrFinished() => UiThread.Run(async () =>
    {
        var queue = new JobQueue(_ => Task.CompletedTask);
        var gate = new TaskCompletionSource<JobOutcome>();
        var jobs = Enumerable.Range(0, 3).Select(index => queue.Enqueue($"job {index}", JobLane.Import, (_, _) => gate.Task)).ToArray();
        Assert.Equal(new int?[] { null, 1, 2 }, jobs.Select(job => job.QueuePosition));

        jobs[1].Cancel();
        Assert.Equal(new int?[] { null, null, 1 }, jobs.Select(job => job.QueuePosition));
        gate.SetResult(JobOutcome.Done("ok"));
        await queue.WhenIdleAsync();
        Assert.All(jobs, job => Assert.Null(job.QueuePosition));
    });

    [Fact]
    public Task CancelOnRunningJobStaysRunningUntilWorkObservesToken() => UiThread.Run(async () =>
    {
        var queue = new JobQueue(_ => Task.CompletedTask);
        var observe = new TaskCompletionSource();
        var job = queue.Enqueue("render", JobLane.Render, async (_, token) =>
        {
            await observe.Task;
            token.ThrowIfCancellationRequested();
            return JobOutcome.Done("ok");
        });

        job.CancelCommand.Execute(null);
        Assert.Equal(JobState.Running, job.State);
        Assert.True(job.IsCancelling);
        Assert.Equal("Cancelling…", job.StatusText);
        Assert.Equal("render · Cancelling…", queue.Summary);
        Assert.False(job.CancelCommand.CanExecute(null));

        observe.SetResult();
        await job.Completion;
        Assert.Equal(JobState.Cancelled, job.State);
        Assert.False(job.IsCancelling);
    });

    [Fact]
    public Task ForeignOperationCanceledIsFailed() => UiThread.Run(async () =>
    {
        var queue = new JobQueue(_ => Task.CompletedTask);
        var job = queue.Enqueue("render", JobLane.Render, (_, _) => throw new OperationCanceledException("The tool timed out."));

        await job.Completion;
        Assert.Equal(JobState.Failed, job.State);
        Assert.Equal("The tool timed out.", job.StatusText);
    });

    [Fact]
    public Task ProgressWithNullFractionKeepsLastFraction() => UiThread.Run(async () =>
    {
        var queue = new JobQueue(_ => Task.CompletedTask);
        var (job, progress, release) = StartReporting(queue);

        (await progress).Report(new JobProgress("Copying", 0.5));
        await UiThread.Settle();
        (await progress).Report(new JobProgress("Verifying"));
        await UiThread.Settle();

        Assert.Equal("Verifying", job.Stage);
        Assert.Equal(0.5, job.Fraction);
        Assert.False(job.IsIndeterminate);
        Assert.Equal("Verifying · 50%", job.StatusText);
        release.SetResult();
        await job.Completion;
    });

    [Fact]
    public Task EtaNeedsFivePercentAndThreeSecondsAndSurvivesStageOnlyReports() => UiThread.Run(async () =>
    {
        var time = new FakeTimeProvider();
        var queue = new JobQueue(_ => Task.CompletedTask, time);
        var (job, started, release) = StartReporting(queue);
        var progress = await started;

        time.Advance(TimeSpan.FromSeconds(2));
        progress.Report(new JobProgress("Copying", 0.2));
        await UiThread.Settle();
        Assert.Null(job.Remaining); // Too early.

        time.Advance(TimeSpan.FromSeconds(2));
        progress.Report(new JobProgress("Copying", 0.04));
        await UiThread.Settle();
        Assert.Equal(TimeSpan.FromSeconds(16), job.Remaining); // 4 s for 20%; the lower report is ignored.

        progress.Report(new JobProgress("Verifying"));
        await UiThread.Settle();
        Assert.Equal("Verifying · 20% · about 16 s left", job.StatusText);
        release.SetResult();
        await job.Completion;
        Assert.Null(job.Remaining);

        var (slow, slowStarted, slowRelease) = StartReporting(queue);
        time.Advance(TimeSpan.FromMinutes(1));
        (await slowStarted).Report(new JobProgress("Copying", 0.04));
        await UiThread.Settle();
        Assert.Null(slow.Remaining); // Too little progress.
        slowRelease.SetResult();
        await slow.Completion;
    });

    [Fact]
    public Task CancelAllAndWaitNeverRunsQueuedWork() => UiThread.Run(async () =>
    {
        var queue = new JobQueue(_ => Task.CompletedTask);
        var ran = 0;
        var running = queue.Enqueue("running", JobLane.Render, async (_, token) => { await Task.Delay(Timeout.Infinite, token); return JobOutcome.Done("never"); });
        var queued = Enumerable.Range(0, 2).Select(index => queue.Enqueue($"queued {index}", JobLane.Render, (_, _) =>
        {
            Interlocked.Increment(ref ran);
            return Task.FromResult(JobOutcome.Done("ok"));
        })).ToArray();

        await queue.CancelAllAndWaitAsync();
        await UiThread.Settle();
        Assert.Equal(0, ran);
        Assert.All(queued.Append(running), job => Assert.Equal(JobState.Cancelled, job.State));
        Assert.False(queue.HasActive);
    });

    [Fact]
    public Task DismissAndClearFinished() => UiThread.Run(async () =>
    {
        var queue = new JobQueue(_ => Task.CompletedTask);
        var gate = new TaskCompletionSource<JobOutcome>();
        var failed = queue.Enqueue("failed", JobLane.Import, (_, _) => throw new IOException("disk"));
        var done = queue.Enqueue("done", JobLane.Import, (_, _) => Task.FromResult(JobOutcome.Done("ok")));
        var running = queue.Enqueue("running", JobLane.Render, (_, _) => gate.Task);
        await Task.WhenAll(failed.Completion, done.Completion);
        Assert.Equal(1, queue.FailedCount);
        Assert.False(running.DismissCommand.CanExecute(null));

        failed.DismissCommand.Execute(null);
        Assert.DoesNotContain(failed, queue.Jobs);
        Assert.False(queue.HasFailures);
        queue.ClearFinishedCommand.Execute(null);
        Assert.Same(running, Assert.Single(queue.Jobs));
        Assert.False(queue.ClearFinishedCommand.CanExecute(null));
        gate.SetResult(JobOutcome.Done("ok"));
        await running.Completion;
    });

    [Fact]
    public Task TheListShowsRunningThenQueuedThenNewestFinishedAndOnlyMovesJobs() => UiThread.Run(async () =>
    {
        var queue = new JobQueue(_ => Task.CompletedTask);
        var actions = new List<System.Collections.Specialized.NotifyCollectionChangedAction>();
        queue.Jobs.CollectionChanged += (_, args) => actions.Add(args.Action);
        var gates = Enumerable.Range(0, 3).Select(_ => new TaskCompletionSource<JobOutcome>()).ToArray();
        var jobs = Enumerable.Range(0, 3).Select(index => queue.Enqueue($"job {index}", JobLane.Import, (_, _) => gates[index].Task)).ToArray();
        Assert.Equal(jobs, queue.Jobs);

        gates[0].SetResult(JobOutcome.Done("ok"));
        await jobs[0].Completion;
        Assert.Equal([jobs[1], jobs[2], jobs[0]], queue.Jobs);
        gates[1].SetResult(JobOutcome.Done("ok"));
        await jobs[1].Completion;
        Assert.Equal([jobs[2], jobs[1], jobs[0]], queue.Jobs);
        gates[2].SetResult(JobOutcome.Done("ok"));
        await queue.WhenIdleAsync();
        Assert.DoesNotContain(System.Collections.Specialized.NotifyCollectionChangedAction.Reset, actions);
        Assert.DoesNotContain(System.Collections.Specialized.NotifyCollectionChangedAction.Remove, actions);
    });

    [Fact]
    public Task ALaneReportsOneBatchWhenItDrains() => UiThread.Run(async () =>
    {
        var queue = new JobQueue(_ => Task.CompletedTask);
        var batches = new List<JobBatch>();
        queue.Drained += batches.Add;
        var gate = new TaskCompletionSource<JobOutcome>();
        queue.Enqueue("a", JobLane.Import, (_, _) => gate.Task);
        queue.Enqueue("b", JobLane.Import, (_, _) => throw new IOException("disk"));
        queue.Enqueue("c", JobLane.Import, (_, _) => gate.Task).Cancel();
        queue.Enqueue("d", JobLane.Import, (_, _) => gate.Task);
        queue.Enqueue("skipped", JobLane.Import, (_, _) => Task.FromResult(JobOutcome.Done("Skipped") with { IsSkipped = true }));

        gate.SetResult(JobOutcome.Done("ok"));
        await queue.WhenIdleAsync();
        await UiThread.Settle();
        Assert.Equal([new JobBatch(JobLane.Import, Succeeded: 2, Failed: 1, Cancelled: 1, Skipped: 1)], batches);
    });

    [Fact]
    public Task BackgroundJobsCountInNoBatchAndLeaveTheDrawerWhenTheySucceed() => UiThread.Run(async () =>
    {
        var queue = new JobQueue(_ => Task.CompletedTask);
        var batches = new List<JobBatch>();
        queue.Drained += batches.Add;
        var gate = new TaskCompletionSource<JobOutcome>();
        queue.Enqueue("slice 1", JobLane.Render, (_, _) => gate.Task, background: true);
        queue.Enqueue("trim", JobLane.Render, (_, _) => gate.Task);
        var drainedAfterTrim = new TaskCompletionSource<int>();
        queue.Drained += _ => drainedAfterTrim.TrySetResult(queue.Jobs.Count(job => !job.IsFinished));
        queue.Enqueue("slice 2", JobLane.Render, (_, _) => gate.Task, background: true);
        var failing = queue.Enqueue("slice 3", JobLane.Render, (_, _) => throw new IOException("disk"), background: true);

        gate.SetResult(JobOutcome.Done("ok"));
        await queue.WhenIdleAsync();
        await UiThread.Settle();

        Assert.Equal([new JobBatch(JobLane.Render, Succeeded: 1)], batches);
        Assert.Equal(2, await drainedAfterTrim.Task);
        Assert.Equal(["slice 3", "trim"], queue.Jobs.Select(job => job.Title).Order());
        Assert.True(failing.HasFailed);
    });

    /// <summary>Starts a render that hands out its progress sink and runs until released.</summary>
    private static (JobViewModel Job, Task<IProgress<JobProgress>> Progress, TaskCompletionSource Release) StartReporting(JobQueue queue)
    {
        var progress = new TaskCompletionSource<IProgress<JobProgress>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource();
        var job = queue.Enqueue("render", JobLane.Render, async (sink, _) =>
        {
            progress.SetResult(sink);
            await release.Task;
            return JobOutcome.Done("ok");
        });
        return (job, progress.Task, release);
    }
}

/// <summary>Runs a test body on one thread with a synchronization context, as the app's view models run on the UI
/// thread: awaits and <see cref="Progress{T}"/> callbacks come back to it.</summary>
internal static class UiThread
{
    public static Task Run(Func<Task> body)
    {
        var result = new TaskCompletionSource();
        var thread = new Thread(() =>
        {
            var context = new QueueContext();
            SynchronizationContext.SetSynchronizationContext(context);
            var task = body();
            task.ContinueWith(_ => context.Complete(), TaskScheduler.Default);
            context.RunUntilComplete();
            if (task.Exception is { } failure) result.SetException(failure.InnerExceptions);
            else result.SetResult();
        });
        thread.Start();
        return result.Task;
    }

    /// <summary>Lets callbacks already posted to the test thread run.</summary>
    public static async Task Settle()
    {
        for (var i = 0; i < 5; i++) await Task.Yield();
        await Task.Delay(20);
    }

    private sealed class QueueContext : SynchronizationContext
    {
        private readonly BlockingCollection<(SendOrPostCallback, object?)> _work = [];
        // Work posted after the test body finished has no one left to observe it.
        public override void Post(SendOrPostCallback callback, object? state)
        {
            try { _work.Add((callback, state)); }
            catch (InvalidOperationException) { }
        }
        public void Complete() => _work.CompleteAdding();
        public void RunUntilComplete()
        {
            foreach (var (callback, state) in _work.GetConsumingEnumerable()) callback(state);
        }
    }
}
