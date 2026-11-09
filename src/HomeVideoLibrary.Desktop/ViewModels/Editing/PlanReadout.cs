namespace HomeVideoLibrary.Desktop.ViewModels.Editing;

/// <summary>A plan summary shown before an edit runs. A request waits <see cref="Delay"/> and cancels any request
/// still waiting or planning, so a burst of input (typing, reordering, repeated I/O presses) starts one probe.
/// A failed plan shows its message as the summary.</summary>
public sealed class PlanReadout(TimeProvider time) : ViewModelBase, IDisposable
{
    public static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(250);

    private CancellationTokenSource? _pending;
    private string _summary = "";

    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }

    /// <summary>Whether <see cref="Summary"/> is about to be replaced (it describes earlier input).</summary>
    public bool IsPending => _pending is not null;

    /// <summary>Completes when the latest request has shown its result or been superseded.</summary>
    public Task Completion { get; private set; } = Task.CompletedTask;

    /// <param name="plan">Runs on the thread pool; returns the summary.</param>
    public void Request(Func<CancellationToken, Task<string>> plan) => Completion = RefreshAsync(plan, Delay);

    /// <summary>Plans without waiting, for the first plan of a page.</summary>
    public void RequestNow(Func<CancellationToken, Task<string>> plan) => Completion = RefreshAsync(plan, TimeSpan.Zero);

    /// <summary>Drops any pending plan and shows <paramref name="summary"/> instead (empty when there is nothing to plan).</summary>
    public void Show(string summary)
    {
        _pending?.Cancel();
        Summary = summary;
    }

    public void Dispose() => _pending?.Cancel();

    private async Task RefreshAsync(Func<CancellationToken, Task<string>> plan, TimeSpan delay)
    {
        _pending?.Cancel();
        using var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        _pending = cancellation;
        OnPropertyChanged(nameof(IsPending));
        try
        {
            if (delay > TimeSpan.Zero) await Task.Delay(delay, time, token);
            string summary;
            try { summary = await Task.Run(() => plan(token), token); }
            catch (Exception exception) when (!token.IsCancellationRequested) { summary = exception.Message; }
            token.ThrowIfCancellationRequested();
            Summary = summary;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            if (_pending == cancellation)
            {
                _pending = null;
                OnPropertyChanged(nameof(IsPending));
            }
        }
    }
}
