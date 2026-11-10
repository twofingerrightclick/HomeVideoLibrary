using System.Diagnostics;
using HomeVideoLibrary.Infrastructure;

namespace HomeVideoLibrary.Desktop.ViewModels;

/// <summary>The shell's readers of library files besides the model: hover-strip builds, and a page's scrub-preview
/// frames (the page itself is left before a trash starts). Calls come from the trash job; the strip scheduler lives on the
/// UI thread, so they hop there through <c>post</c>, which runs posts in order. The job waits for the UI thread only to
/// take a hold, and stops waiting when cancelled; releasing one is posted and not awaited, so a UI thread that has stopped
/// (the app is closing) cannot hang the job.</summary>
/// <param name="strips">The open library's strip scheduler, read on the UI thread; null when there is none.</param>
public sealed class ShellVideoHolders(Func<HoverStripScheduler?> strips, Func<PreviewCache?> previews, Action<Action> post) : IVideoHolders
{
    public async Task<IAsyncDisposable> HoldAsync(Guid videoId, CancellationToken cancellationToken)
    {
        var holding = OnUiThreadAsync(async () => strips() is { } scheduler ? await scheduler.HoldAsync(videoId) : null);
        try { return new UiThreadHold(await holding.WaitAsync(cancellationToken), post); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The hold may still be taken; it must not outlive this call.
            _ = holding.ContinueWith(taken => new UiThreadHold(taken.Result, post).DisposeAsync(), CancellationToken.None,
                TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
            throw;
        }
    }

    public void Forget(Guid videoId)
    {
        previews()?.Forget(videoId);
        post(() => strips()?.Forget(videoId));
    }

    private Task<T> OnUiThreadAsync<T>(Func<Task<T>> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        post(async () =>
        {
            try { done.SetResult(await work()); }
            catch (Exception exception) { done.SetException(exception); }
        });
        return done.Task;
    }

    /// <summary>Releases the scheduler's hold on the UI thread.</summary>
    private sealed class UiThreadHold(IAsyncDisposable? hold, Action<Action> post) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            if (hold is null) return ValueTask.CompletedTask;
            post(async () =>
            {
                try { await hold.DisposeAsync(); }
                catch (Exception exception) { Trace.TraceWarning($"Could not release a hover-strip hold: {exception}"); }
            });
            return ValueTask.CompletedTask;
        }
    }
}
