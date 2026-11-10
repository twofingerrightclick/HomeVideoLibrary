using HomeVideoLibrary.Infrastructure;

namespace HomeVideoLibrary.Desktop.ViewModels;

/// <summary>The shell's readers of library files besides the model: hover-strip builds, and a page's scrub-preview
/// frames (the page itself is left before a trash starts). Calls come from the trash job; the strip scheduler lives on the
/// UI thread, so they hop there through <c>post</c>.</summary>
/// <param name="strips">The open library's strip scheduler, read on the UI thread; null when there is none.</param>
public sealed class ShellVideoHolders(Func<HoverStripScheduler?> strips, Func<PreviewCache?> previews, Action<Action> post) : IVideoHolders
{
    public async Task<IAsyncDisposable> HoldAsync(Guid videoId, CancellationToken cancellationToken)
    {
        var hold = await OnUiThreadAsync(async () => strips() is { } scheduler ? await scheduler.HoldAsync(videoId) : null);
        return new UiThreadHold(hold, this);
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
    private sealed class UiThreadHold(IAsyncDisposable? hold, ShellVideoHolders owner) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            if (hold is not null) await owner.OnUiThreadAsync(async () => { await hold.DisposeAsync(); return true; });
        }
    }
}
