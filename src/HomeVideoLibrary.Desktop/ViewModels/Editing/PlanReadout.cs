namespace HomeVideoLibrary.Desktop.ViewModels.Editing;

/// <summary>A plan shown before an edit runs: its summary, its explanation and the edit button's label. A request
/// waits <see cref="Delay"/> and cancels any request still waiting or planning, so a burst of input (typing,
/// reordering, repeated I/O presses) starts one probe. A failed plan shows its message.</summary>
public sealed class PlanReadout(TimeProvider time) : ViewModelBase, IDisposable
{
    public static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(250);

    private CancellationTokenSource? _pending;
    private PlanDescription _current = PlanDescription.Message("");

    public PlanDescription Current
    {
        get => _current;
        private set
        {
            if (!SetProperty(ref _current, value)) return;
            OnPropertyChanged(nameof(Summary));
            OnPropertyChanged(nameof(Details));
            OnPropertyChanged(nameof(ActionLabel));
        }
    }

    public string Summary => Current.Summary;
    public string Details => Current.Details;

    /// <summary>The edit button's label: RENDER when the edit really re-encodes, otherwise SAVE.</summary>
    public string ActionLabel => Current.Renders ? "RENDER" : "SAVE";

    /// <summary>Whether <see cref="Summary"/> is about to be replaced (it describes earlier input).</summary>
    public bool IsPending => _pending is not null;

    /// <summary>Completes when the latest request has shown its result or been superseded.</summary>
    public Task Completion { get; private set; } = Task.CompletedTask;

    /// <param name="plan">Runs on the thread pool.</param>
    public void Request(Func<CancellationToken, Task<PlanDescription>> plan) => Completion = RefreshAsync(plan, Delay);

    /// <summary>Plans without waiting, for the first plan of a page.</summary>
    public void RequestNow(Func<CancellationToken, Task<PlanDescription>> plan) => Completion = RefreshAsync(plan, TimeSpan.Zero);

    /// <summary>Drops any pending plan and shows <paramref name="message"/> instead (empty when there is nothing to plan).</summary>
    public void Show(string message)
    {
        _pending?.Cancel();
        Current = PlanDescription.Message(message);
    }

    public void Dispose() => _pending?.Cancel();

    private async Task RefreshAsync(Func<CancellationToken, Task<PlanDescription>> plan, TimeSpan delay)
    {
        _pending?.Cancel();
        using var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        _pending = cancellation;
        OnPropertyChanged(nameof(IsPending));
        try
        {
            if (delay > TimeSpan.Zero) await Task.Delay(delay, time, token);
            PlanDescription description;
            try { description = await Task.Run(() => plan(token), token); }
            catch (Exception exception) when (!token.IsCancellationRequested) { description = PlanDescription.Message(exception.Message); }
            token.ThrowIfCancellationRequested();
            Current = description;
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
