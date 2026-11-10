using System.Collections.Concurrent;
using HomeVideoLibrary.Desktop.ViewModels;
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
