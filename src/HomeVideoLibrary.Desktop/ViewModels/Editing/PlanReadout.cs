namespace HomeVideoLibrary.Desktop.ViewModels.Editing;

/// <summary>Where a plan readout stands. Only <see cref="Ready"/> lets an edit start.</summary>
public abstract record PlanState
{
    private PlanState() { }

    /// <summary>Nothing to plan yet; <paramref name="Hint"/> says what the user should do (empty for nothing).</summary>
    public sealed record Idle(string Hint) : PlanState;

    /// <summary>Waiting for input to settle, or probing the file.</summary>
    public sealed record Planning : PlanState;

    public sealed record Failed(string Message) : PlanState;

    public sealed record Ready(PlanDescription Plan) : PlanState;
}

/// <summary>A plan shown before an edit runs: its summary, its explanation and the edit button's label. A request
/// waits <see cref="Delay"/> and cancels any request still waiting or planning, so a burst of input (typing,
/// reordering, repeated I/O presses) starts one probe, and an older plan can never replace a newer one.</summary>
public sealed class PlanReadout(TimeProvider time) : ViewModelBase, IDisposable
{
    public static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(250);

    private CancellationTokenSource? _pending;
    private PlanState _state = new PlanState.Idle("");

    public PlanState State
    {
        get => _state;
        private set
        {
            if (!SetProperty(ref _state, value)) return;
            foreach (var name in new[] { nameof(IsReady), nameof(IsPlanning), nameof(Summary), nameof(Details), nameof(ActionLabel) })
                OnPropertyChanged(name);
        }
    }

    public bool IsReady => State is PlanState.Ready;
    public bool IsPlanning => State is PlanState.Planning;

    public string Summary => State switch
    {
        PlanState.Ready ready => ready.Plan.Summary,
        PlanState.Failed failed => failed.Message,
        PlanState.Idle idle => idle.Hint,
        _ => "Planning…",
    };

    /// <summary>The info popover: the plan's explanation, or the same text as <see cref="Summary"/> without one.</summary>
    public string Details => State is PlanState.Ready ready ? ready.Plan.Details : Summary;

    /// <summary>The edit button's label: RENDER when a ready plan re-encodes the picture, otherwise SAVE.</summary>
    public string ActionLabel => State is PlanState.Ready { Plan.RendersVideo: true } ? "RENDER" : "SAVE";

    /// <summary>Completes when the latest request has shown its result or been superseded.</summary>
    public Task Completion { get; private set; } = Task.CompletedTask;

    /// <param name="plan">Runs on the thread pool.</param>
    public void Request(Func<CancellationToken, Task<PlanDescription>> plan) => Completion = RefreshAsync(plan, Delay);

    /// <summary>Plans without waiting, for the first plan of a page.</summary>
    public void RequestNow(Func<CancellationToken, Task<PlanDescription>> plan) => Completion = RefreshAsync(plan, TimeSpan.Zero);

    /// <summary>Drops any pending plan: there is nothing to plan, and <paramref name="hint"/> says why.</summary>
    public void Clear(string hint)
    {
        _pending?.Cancel();
        _pending = null;
        State = new PlanState.Idle(hint);
    }

    public void Dispose()
    {
        _pending?.Cancel();
        _pending = null;
    }

    private async Task RefreshAsync(Func<CancellationToken, Task<PlanDescription>> plan, TimeSpan delay)
    {
        _pending?.Cancel();
        using var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        _pending = cancellation;
        State = new PlanState.Planning();
        try
        {
            if (delay > TimeSpan.Zero) await Task.Delay(delay, time, token);
            PlanState result;
            try { result = new PlanState.Ready(await Task.Run(() => plan(token), token)); }
            catch (Exception exception) when (!token.IsCancellationRequested) { result = new PlanState.Failed(exception.Message); }
            token.ThrowIfCancellationRequested();
            State = result;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            if (_pending == cancellation) _pending = null;
        }
    }
}
