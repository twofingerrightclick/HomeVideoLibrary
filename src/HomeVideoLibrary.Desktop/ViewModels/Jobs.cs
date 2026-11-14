using System.Collections.ObjectModel;

namespace HomeVideoLibrary.Desktop.ViewModels;

/// <summary>Which queue a job waits in. Each lane runs one job at a time in FIFO order; lanes run side by side, so an
/// import never waits behind a long render.</summary>
public enum JobLane { Import, Render }

public enum JobState { Queued, Running, Succeeded, Failed, Cancelled }

/// <summary>A progress report from a running job: what it is doing and, when known, how far along it is (0..1). A
/// report without a fraction changes only the stage.</summary>
public readonly record struct JobProgress(string Stage, double? Fraction = null);

/// <summary>How a job ended: a one-line summary and the videos it produced (offered as OPEN).</summary>
public sealed record JobOutcome(string Summary, IReadOnlyList<Guid> Outputs)
{
    public Action? ImportAnyway { get; init; }
    public bool IsSkipped { get; init; }

    public static JobOutcome Done(string summary) => new(summary, []);
}

/// <summary>What a lane finished between two idle moments, reported once when it drains.</summary>
public readonly record struct JobBatch(JobLane Lane, int Succeeded = 0, int Failed = 0, int Cancelled = 0, int Skipped = 0)
{
    public bool IsEmpty => Succeeded + Failed + Cancelled + Skipped == 0;

    internal JobBatch Add(JobState state, bool skipped) => state switch
    {
        JobState.Succeeded when skipped => this with { Skipped = Skipped + 1 },
        JobState.Succeeded => this with { Succeeded = Succeeded + 1 },
        JobState.Failed => this with { Failed = Failed + 1 },
        _ => this with { Cancelled = Cancelled + 1 },
    };
}

/// <summary>The work a job runs, on the thread pool. Report progress through the given sink; observe the token. A
/// job that has already committed its results should finish publishing them even when cancelled late.</summary>
public delegate Task<JobOutcome> JobWork(IProgress<JobProgress> progress, CancellationToken cancellationToken);

/// <summary>Background jobs (imports, renders) shown in the activity drawer. Each lane is drained by one loop that runs
/// its jobs one at a time, first in first out; the others show as "Queued (#n)". All state changes happen on the UI
/// thread: the queue is created there, progress is marshalled back through <see cref="Progress{T}"/>, and awaits
/// resume there.</summary>
public sealed class JobQueue : ViewModelBase
{
    private readonly Dictionary<JobLane, Lane> _lanes = Enum.GetValues<JobLane>().ToDictionary(lane => lane, lane => new Lane(lane));
    private readonly Func<JobViewModel, Task> _open;
    private long _sequence;

    /// <param name="open">Opens a finished job's output (its OPEN key).</param>
    /// <param name="time">The clock for progress estimates.</param>
    public JobQueue(Func<JobViewModel, Task> open, TimeProvider? time = null)
    {
        _open = open; Time = time ?? TimeProvider.System;
        ClearFinishedCommand = new RelayCommand(ClearFinished, () => Jobs.Any(job => job.IsFinished));
    }

    /// <summary>Raised once per job when it ends (succeeded, failed or cancelled), after its <see cref="JobViewModel.Completion"/>.</summary>
    public event Action<JobViewModel>? Finished;

    /// <summary>Raised when a lane has no job left to run but background ones, with what it finished since it last
    /// drained; never with an empty batch.</summary>
    public event Action<JobBatch>? Drained;

    /// <summary>Running jobs, then queued ones in the order they will run, then finished ones newest first. A job
    /// moves within the list when its state changes; the list is never rebuilt.</summary>
    public ObservableCollection<JobViewModel> Jobs { get; } = [];

    public RelayCommand ClearFinishedCommand { get; }

    /// <summary>Jobs that are queued or running.</summary>
    public int ActiveCount => Jobs.Count(job => !job.IsFinished);
    public bool HasActive => ActiveCount > 0;

    /// <summary>Whether a queued or running job reads <paramref name="videoId"/>. Changes are raised as
    /// <see cref="ActiveCount"/>.</summary>
    public bool Holds(Guid videoId) => Jobs.Any(job => !job.IsFinished && job.Sources.Contains(videoId));

    /// <summary>Failed jobs still in the list; they stay until dismissed or cleared.</summary>
    public int FailedCount => Jobs.Count(job => job.HasFailed);
    public bool HasFailures => FailedCount > 0;
    public string FailedLabel => $"{FailedCount} FAILED";

    /// <summary>One line for the activity key: a running job and how many wait, or the latest result.</summary>
    public string Summary
    {
        get
        {
            // A render is the long job the user waits on, so it names the activity when both lanes run.
            var running = Jobs.Where(job => job.IsRunning).OrderBy(job => job.Lane == JobLane.Render ? 0 : 1).FirstOrDefault();
            var queued = Jobs.Count(job => job.IsQueued);
            if (running is not null) return $"{running.Title} · {running.StatusText}" + (queued > 0 ? $" · {queued} queued" : "");
            return Jobs.FirstOrDefault(job => job.IsFinished) is { } latest ? $"{latest.Title} · {latest.StatusText}" : "No activity";
        }
    }

    /// <summary><see cref="Summary"/> and any failures, whole: the activity key is narrow and trims both.</summary>
    public string FullSummary => HasFailures ? $"{Summary} · {FailedLabel}" : Summary;

    internal TimeProvider Time { get; }

    /// <summary>Adds a job to the end of <paramref name="lane"/>; it starts when the lane is free.</summary>
    /// <param name="sources">The library videos the job reads, which must not be rewritten while it is active.</param>
    /// <param name="background">See <see cref="JobViewModel.IsBackground"/>.</param>
    public JobViewModel Enqueue(string title, JobLane lane, JobWork work, IReadOnlyCollection<Guid>? sources = null, bool background = false)
    {
        var job = new JobViewModel(title, lane, work, this) { Order = ++_sequence, Sources = sources ?? [], IsBackground = background };
        var state = _lanes[lane];
        state.Waiting.Add(job);
        Place(job);
        state.Renumber();
        Notify();
        if (state.Drain.IsCompleted) state.Drain = DrainAsync(state);
        return job;
    }

    /// <summary>Cancels every queued and running job and waits until all have ended. Queued jobs are cancelled first,
    /// so a lane freed by a cancelled running job finds nothing left to start.</summary>
    public Task CancelAllAndWaitAsync()
    {
        var active = Jobs.Where(job => !job.IsFinished).OrderBy(job => job.IsRunning).ToArray();
        foreach (var job in active) job.Cancel();
        return Task.WhenAll(active.Select(job => job.Completion));
    }

    /// <summary>Waits until every job that is active now has ended.</summary>
    public Task WhenIdleAsync() => Task.WhenAll(Jobs.Where(job => !job.IsFinished).Select(job => job.Completion));

    internal Task OpenAsync(JobViewModel job) => _open(job);

    internal void Dismiss(JobViewModel job)
    {
        if (job.IsFinished && Jobs.Remove(job)) Notify();
    }

    /// <summary>Ends a job that is still waiting: it is marked cancelled now, and its lane's loop skips it.</summary>
    internal void Withdraw(JobViewModel job)
    {
        try { job.EndQueued(); }
        finally { job.Complete(); }
        Announce(job);
    }

    internal void StateChanged(JobViewModel job)
    {
        job.Order = ++_sequence;
        Place(job);
        _lanes[job.Lane].Renumber();
        Notify();
    }

    internal void StatusChanged()
    {
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(FullSummary));
    }

    private async Task DrainAsync(Lane lane)
    {
        while (lane.Next() is { } job)
        {
            try { await job.RunAsync(); }
            catch (Exception exception) { AppErrors.Report(exception, "Background job"); }
            finally { job.Complete(); }
            Announce(job);
            // Raised inside the loop, so a job a handler enqueues is picked up by this loop rather than stranded.
            if (!lane.HasWaiting && lane.TakeBatch() is { IsEmpty: false } batch) Raise(Drained, batch, "Jobs drained");
        }
    }

    private void Announce(JobViewModel job)
    {
        if (!job.IsBackground) _lanes[job.Lane].Count(job.State, job.Outcome?.IsSkipped == true);
        else if (job.State == JobState.Succeeded) Dismiss(job);
        Raise(Finished, job, "Job finished");
    }

    private static void Raise<T>(Action<T>? handler, T value, string context)
    {
        try { handler?.Invoke(value); }
        catch (Exception exception) { AppErrors.Report(exception, context); }
    }

    /// <summary>Moves (or inserts) <paramref name="job"/> to its place in <see cref="Jobs"/>; only its key changed, so one
    /// move restores the order.</summary>
    private void Place(JobViewModel job)
    {
        var target = Jobs.Count(other => !ReferenceEquals(other, job) && Precedes(other, job));
        var current = Jobs.IndexOf(job);
        if (current < 0) Jobs.Insert(target, job);
        else if (current != target) Jobs.Move(current, target);
    }

    private static bool Precedes(JobViewModel first, JobViewModel second)
    {
        int Group(JobViewModel job) => job.IsRunning ? 0 : job.IsQueued ? 1 : 2;
        var (a, b) = (Group(first), Group(second));
        if (a != b) return a < b;
        return a == 2 ? first.Order > second.Order : first.Order < second.Order;
    }

    private void Notify()
    {
        foreach (var name in new[] { nameof(ActiveCount), nameof(HasActive), nameof(FailedCount), nameof(HasFailures), nameof(FailedLabel), nameof(Summary), nameof(FullSummary) })
            OnPropertyChanged(name);
        ClearFinishedCommand.RaiseCanExecuteChanged();
    }

    private void ClearFinished()
    {
        foreach (var job in Jobs.Where(job => job.IsFinished).ToArray()) Jobs.Remove(job);
        Notify();
    }

    /// <summary>One lane's waiting jobs in FIFO order, the loop draining them and what it has finished since it last
    /// drained. A job cancelled while waiting stays in <see cref="Waiting"/> until the loop reaches and skips it.</summary>
    private sealed class Lane(JobLane kind)
    {
        private JobBatch _batch = new(kind);

        public List<JobViewModel> Waiting { get; } = [];
        public Task Drain { get; set; } = Task.CompletedTask;
        /// <summary>Whether a job the user is waiting for is queued; background jobs are not counted.</summary>
        public bool HasWaiting => Waiting.Any(job => job.IsQueued && !job.IsBackground);

        public JobViewModel? Next()
        {
            while (Waiting.Count > 0)
            {
                var job = Waiting[0];
                Waiting.RemoveAt(0);
                if (job.IsQueued) return job;
            }
            return null;
        }

        public void Renumber()
        {
            var position = 0;
            foreach (var job in Waiting.Where(job => job.IsQueued)) job.QueuePosition = ++position;
        }

        public void Count(JobState state, bool skipped) => _batch = _batch.Add(state, skipped);

        public JobBatch TakeBatch()
        {
            var batch = _batch;
            _batch = new JobBatch(kind);
            return batch;
        }
    }
}

/// <summary>One job in the activity drawer. Its state only moves forward: Queued, Running, then one of Succeeded,
/// Failed or Cancelled.</summary>
public sealed class JobViewModel : ViewModelBase
{
    // An estimate from the first few percent or seconds jumps around too much to be useful.
    private const double MinimumFractionForEta = 0.05;
    private static readonly TimeSpan MinimumElapsedForEta = TimeSpan.FromSeconds(3);
    private readonly JobWork _work;
    private readonly JobQueue _queue;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _cancellation = new();
    private JobState _state = JobState.Queued;
    private int? _queuePosition;
    private string _stage = "";
    private double? _fraction;
    private DateTimeOffset _startedAt;
    private TimeSpan? _remaining;
    private JobOutcome? _outcome;
    private string? _error;
    private bool _importAnywayUsed;

    internal JobViewModel(string title, JobLane lane, JobWork work, JobQueue queue)
    {
        Title = title; Lane = lane; _work = work; _queue = queue;
        CancelCommand = new RelayCommand(Cancel, () => IsQueued || IsRunning && !IsCancelling);
        OpenCommand = new AsyncCommand(_ => _queue.OpenAsync(this), () => CanOpen, context: "Open");
        ImportAnywayCommand = new RelayCommand(() =>
        {
            if (!CanImportAnyway) return;
            _importAnywayUsed = true;
            OnPropertyChanged(nameof(CanImportAnyway));
            ImportAnywayCommand!.RaiseCanExecuteChanged();
            Outcome!.ImportAnyway!();
        }, () => CanImportAnyway);
        DismissCommand = new RelayCommand(() => _queue.Dismiss(this), () => IsFinished);
    }

    public string Title { get; }
    public JobLane Lane { get; }

    /// <summary>The library videos the job reads; see <see cref="JobQueue.Holds"/>.</summary>
    public IReadOnlyCollection<Guid> Sources { get; internal init; } = [];

    /// <summary>Maintenance the user did not ask for. It shows in the drawer while active and when it fails, but a success
    /// leaves the drawer at once and no background job counts in a lane's <see cref="JobBatch"/>, so it raises no
    /// "Finished" toast or chime.</summary>
    public bool IsBackground { get; internal init; }
    public JobState State => _state;
    public bool IsQueued => State == JobState.Queued;
    public bool IsRunning => State == JobState.Running;
    public bool IsFinished => State is JobState.Succeeded or JobState.Failed or JobState.Cancelled;
    public bool HasFailed => State == JobState.Failed;
    /// <summary>Cancel was requested and the running work has not stopped yet.</summary>
    public bool IsCancelling => IsRunning && _cancellation.IsCancellationRequested;

    /// <summary>1-based place in its lane's queue while queued; null otherwise.</summary>
    public int? QueuePosition { get => _queuePosition; internal set { if (SetProperty(ref _queuePosition, value)) OnPropertyChanged(nameof(StatusText)); } }

    public string Stage => _stage;

    /// <summary>0..1 once known; never goes backwards and is kept through stages that report no fraction.</summary>
    public double? Fraction => _fraction;
    public double Percent => (Fraction ?? 0) * 100;
    public bool IsIndeterminate => IsRunning && Fraction is null;

    /// <summary>Estimated time left, recomputed at each progress report; null until enough progress is known.</summary>
    public TimeSpan? Remaining => _remaining;

    public JobOutcome? Outcome => _outcome;
    public string? Error => _error;
    public bool CanImportAnyway => IsFinished && !_importAnywayUsed && _queue.Jobs.Contains(this) && Outcome?.ImportAnyway is not null;
    public RelayCommand ImportAnywayCommand { get; }
    public bool CanOpen => State == JobState.Succeeded && Outcome is { Outputs.Count: > 0 };

    /// <summary>A queued job is removed from its queue; a running one is asked to stop.</summary>
    public string CancelLabel => IsQueued ? "REMOVE" : "CANCEL";

    public string StatusText => State switch
    {
        JobState.Queued => $"Queued (#{QueuePosition})",
        JobState.Running when IsCancelling => "Cancelling…",
        JobState.Running => string.Join(" · ", new[] { Stage, Fraction is { } f ? $"{f:0%}" : null, Remaining is { } left ? $"about {FormatRemaining(left)} left" : null }
            .Where(part => !string.IsNullOrEmpty(part))),
        JobState.Succeeded => Outcome?.Summary ?? "Done",
        JobState.Failed => Error ?? "Failed",
        _ => "Cancelled",
    };

    public RelayCommand CancelCommand { get; }
    public AsyncCommand OpenCommand { get; }
    public RelayCommand DismissCommand { get; }

    /// <summary>Completes when the job has ended; never faults.</summary>
    public Task Completion => _completion.Task;

    /// <summary>The queue's sort key within the job's state group: when the job last changed state.</summary>
    internal long Order { get; set; }

    /// <summary>Withdraws a queued job, or asks a running one to stop (it shows "Cancelling…" until its work observes it).</summary>
    public void Cancel()
    {
        if (IsQueued) _queue.Withdraw(this);
        else if (IsRunning && !_cancellation.IsCancellationRequested)
        {
            _cancellation.Cancel();
            if (!IsRunning) return; // The work observed the token inline and has already ended.
            OnPropertyChanged(nameof(IsCancelling)); OnPropertyChanged(nameof(StatusText));
            CancelCommand.RaiseCanExecuteChanged();
            _queue.StatusChanged();
        }
    }

    /// <summary>Runs the work and leaves the job in a final state. The work's own failures end the job; only a throwing
    /// subscriber to the job's notifications can make this throw, and the state is final even then.</summary>
    internal async Task RunAsync()
    {
        var (state, outcome, error) = (JobState.Failed, (JobOutcome?)null, (string?)null);
        try
        {
            _startedAt = _queue.Time.GetUtcNow();
            _stage = "Starting…";
            ChangeState(JobState.Running);
            (state, outcome, error) = await RunWorkAsync();
        }
        finally { Finish(state, outcome, error); }
    }

    internal void EndQueued() => Finish(JobState.Cancelled, null, null);

    /// <summary>Completes <see cref="Completion"/>; the job is final by now.</summary>
    internal void Complete()
    {
        _cancellation.Dispose();
        _completion.TrySetResult();
    }

    private async Task<(JobState, JobOutcome?, string?)> RunWorkAsync()
    {
        var progress = new Progress<JobProgress>(Report);
        var token = _cancellation.Token;
        try { return (JobState.Succeeded, await Task.Run(() => _work(progress, token), CancellationToken.None), null); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return (JobState.Cancelled, null, null); }
        catch (Exception exception) { return (JobState.Failed, null, exception.Message); }
    }

    private void Report(JobProgress update)
    {
        if (!IsRunning) return;
        _stage = update.Stage;
        if (update.Fraction is { } fraction)
        {
            _fraction = Math.Max(_fraction ?? 0, Math.Clamp(fraction, 0, 1));
            _remaining = EstimateRemaining(_fraction.Value, _queue.Time.GetUtcNow() - _startedAt);
        }
        foreach (var name in new[] { nameof(Stage), nameof(Fraction), nameof(Percent), nameof(IsIndeterminate), nameof(Remaining), nameof(StatusText) })
            OnPropertyChanged(name);
        _queue.StatusChanged();
    }

    private static TimeSpan? EstimateRemaining(double done, TimeSpan elapsed) =>
        done is >= MinimumFractionForEta and < 1 && elapsed >= MinimumElapsedForEta ? elapsed * ((1 - done) / done) : null;

    private void Finish(JobState state, JobOutcome? outcome, string? error)
    {
        _outcome = outcome; _error = error; _remaining = null;
        if (state == JobState.Succeeded) _fraction = 1;
        ChangeState(state);
    }

    /// <summary>Moves to <paramref name="state"/>. Every field is set before any notification, so a throwing subscriber
    /// cannot leave the job half changed.</summary>
    private void ChangeState(JobState state)
    {
        _state = state;
        _queuePosition = null;
        foreach (var name in new[] { nameof(State), nameof(IsQueued), nameof(IsRunning), nameof(IsFinished), nameof(HasFailed), nameof(IsCancelling),
                     nameof(QueuePosition), nameof(Stage), nameof(Fraction), nameof(Percent), nameof(IsIndeterminate), nameof(Remaining),
                     nameof(Outcome), nameof(Error), nameof(CanImportAnyway), nameof(CanOpen), nameof(CancelLabel), nameof(StatusText) })
            OnPropertyChanged(name);
        ImportAnywayCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged(); OpenCommand.RaiseCanExecuteChanged(); DismissCommand.RaiseCanExecuteChanged();
        _queue.StateChanged(this);
    }

    private static string FormatRemaining(TimeSpan left) => left.TotalMinutes >= 1 ? $"{Math.Ceiling(left.TotalMinutes):0} min" : $"{Math.Max(1, Math.Ceiling(left.TotalSeconds)):0} s";
}
