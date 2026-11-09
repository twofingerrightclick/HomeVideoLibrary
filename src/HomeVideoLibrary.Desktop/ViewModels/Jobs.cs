using System.Collections.ObjectModel;

namespace HomeVideoLibrary.Desktop.ViewModels;

/// <summary>Which queue a job waits in. Each lane runs one job at a time in FIFO order; lanes run side by side, so an
/// import never waits behind a long render.</summary>
public enum JobLane { Import, Render }

public enum JobState { Queued, Running, Succeeded, Failed, Cancelled }

/// <summary>A progress report from a running job: what it is doing and, when known, how far along it is (0..1).</summary>
public readonly record struct JobProgress(string Stage, double? Fraction = null);

/// <summary>How a job ended: a one-line summary and the videos it produced (offered as OPEN).</summary>
public sealed record JobOutcome(string Summary, IReadOnlyList<Guid> Outputs)
{
    public static JobOutcome Done(string summary) => new(summary, []);
}

/// <summary>The work a job runs, on the thread pool. Report progress through the given sink; observe the token. A
/// job that has already committed its results should finish publishing them even when cancelled late.</summary>
public delegate Task<JobOutcome> JobWork(IProgress<JobProgress> progress, CancellationToken cancellationToken);

/// <summary>Background jobs (imports, renders) shown in the activity drawer. Jobs in the same lane run one at a time,
/// first in first out; others show as "Queued (#n)". All state changes happen on the UI thread: the queue is created
/// there, progress is marshalled back through <see cref="Progress{T}"/>, and awaits resume there.</summary>
public sealed class JobQueue : ViewModelBase
{
    private readonly Dictionary<JobLane, List<JobViewModel>> _waiting = new() { [JobLane.Import] = [], [JobLane.Render] = [] };
    private readonly Dictionary<JobLane, JobViewModel?> _running = new() { [JobLane.Import] = null, [JobLane.Render] = null };
    private readonly Func<JobViewModel, Task> _open;
    private readonly TimeProvider _time;

    /// <param name="open">Opens a finished job's output (its OPEN key).</param>
    public JobQueue(Func<JobViewModel, Task> open, TimeProvider? time = null)
    {
        _open = open; _time = time ?? TimeProvider.System;
        ClearFinishedCommand = new RelayCommand(ClearFinished, () => Jobs.Any(job => job.IsFinished));
    }

    /// <summary>Raised when a job ends (succeeded, failed or cancelled), for notifications.</summary>
    public event Action<JobViewModel>? Finished;

    /// <summary>Every job, newest first; finished ones stay until cleared.</summary>
    public ObservableCollection<JobViewModel> Jobs { get; } = [];

    public RelayCommand ClearFinishedCommand { get; }

    /// <summary>Jobs that are queued or running.</summary>
    public int ActiveCount => Jobs.Count(job => !job.IsFinished);
    public bool HasActive => ActiveCount > 0;

    /// <summary>One line for the activity key: the running job and how many wait, or the last result.</summary>
    public string Summary
    {
        get
        {
            var running = _running.Values.OfType<JobViewModel>().FirstOrDefault();
            var queued = _waiting.Values.Sum(lane => lane.Count);
            if (running is not null) return $"{running.Title} · {running.StatusText}" + (queued > 0 ? $" · {queued} queued" : "");
            return Jobs.FirstOrDefault() is { } last ? $"{last.Title} · {last.StatusText}" : "No activity";
        }
    }

    /// <summary>Adds a job to the end of <paramref name="lane"/>; it starts when the lane is free.</summary>
    public JobViewModel Enqueue(string title, JobLane lane, JobWork work)
    {
        var job = new JobViewModel(title, lane, work, this);
        Jobs.Insert(0, job);
        _waiting[lane].Add(job);
        Changed(lane);
        Pump(lane);
        return job;
    }

    /// <summary>Cancels every queued and running job and waits until all have ended.</summary>
    public async Task CancelAllAndWaitAsync()
    {
        var active = Jobs.Where(job => !job.IsFinished).ToArray();
        foreach (var job in active) job.Cancel();
        await Task.WhenAll(active.Select(job => job.Completion));
    }

    /// <summary>Waits until every job that is active now has ended.</summary>
    public Task WhenIdleAsync() => Task.WhenAll(Jobs.Where(job => !job.IsFinished).Select(job => job.Completion));

    internal Task OpenAsync(JobViewModel job) => _open(job);

    internal DateTimeOffset Now => _time.GetUtcNow();

    internal void Remove(JobViewModel job)
    {
        if (_waiting[job.Lane].Remove(job)) Changed(job.Lane);
    }

    internal void Dismiss(JobViewModel job)
    {
        if (job.IsFinished && Jobs.Remove(job)) Notify();
    }

    internal void Ended(JobViewModel job)
    {
        if (ReferenceEquals(_running[job.Lane], job)) _running[job.Lane] = null;
        Changed(job.Lane);
        Finished?.Invoke(job);
        Pump(job.Lane);
    }

    internal void ProgressChanged() => OnPropertyChanged(nameof(Summary));

    private void Pump(JobLane lane)
    {
        if (_running[lane] is not null || _waiting[lane] is not { Count: > 0 } waiting) return;
        var next = waiting[0];
        waiting.RemoveAt(0);
        _running[lane] = next;
        Changed(lane);
        next.Start();
    }

    private void Changed(JobLane lane)
    {
        var waiting = _waiting[lane];
        for (var index = 0; index < waiting.Count; index++) waiting[index].QueuePosition = index + 1;
        Notify();
    }

    private void Notify()
    {
        OnPropertyChanged(nameof(ActiveCount)); OnPropertyChanged(nameof(HasActive)); OnPropertyChanged(nameof(Summary));
        ClearFinishedCommand.RaiseCanExecuteChanged();
    }

    private void ClearFinished()
    {
        foreach (var job in Jobs.Where(job => job.IsFinished).ToArray()) Jobs.Remove(job);
        Notify();
    }
}

/// <summary>One job in the activity drawer. Its state only moves forward: Queued, Running, then one of Succeeded,
/// Failed or Cancelled.</summary>
public sealed class JobViewModel : ViewModelBase
{
    private static readonly TimeSpan MinimumElapsedForEta = TimeSpan.FromSeconds(2);
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

    internal JobViewModel(string title, JobLane lane, JobWork work, JobQueue queue)
    {
        Title = title; Lane = lane; _work = work; _queue = queue;
        CancelCommand = new RelayCommand(Cancel, () => !IsFinished);
        OpenCommand = new AsyncCommand(_ => _queue.OpenAsync(this), () => CanOpen, context: "Open");
        DismissCommand = new RelayCommand(() => _queue.Dismiss(this), () => IsFinished);
    }

    public string Title { get; }
    public JobLane Lane { get; }
    public JobState State { get => _state; private set { if (SetProperty(ref _state, value)) StateChanged(); } }
    public bool IsQueued => State == JobState.Queued;
    public bool IsRunning => State == JobState.Running;
    public bool IsFinished => State is JobState.Succeeded or JobState.Failed or JobState.Cancelled;
    public bool HasFailed => State == JobState.Failed;

    /// <summary>1-based place in its lane's queue while queued; null otherwise.</summary>
    public int? QueuePosition { get => _queuePosition; internal set { if (SetProperty(ref _queuePosition, value)) OnPropertyChanged(nameof(StatusText)); } }

    public string Stage { get => _stage; private set { if (SetProperty(ref _stage, value)) OnPropertyChanged(nameof(StatusText)); } }

    /// <summary>0..1 when known; never goes backwards while running.</summary>
    public double? Fraction { get => _fraction; private set { if (SetProperty(ref _fraction, value)) { OnPropertyChanged(nameof(Percent)); OnPropertyChanged(nameof(IsIndeterminate)); } } }
    public double Percent => (Fraction ?? 0) * 100;
    public bool IsIndeterminate => IsRunning && Fraction is null;

    /// <summary>Estimated time left, from progress so far; null until enough progress is known.</summary>
    public TimeSpan? Remaining { get => _remaining; private set { if (SetProperty(ref _remaining, value)) OnPropertyChanged(nameof(StatusText)); } }

    public JobOutcome? Outcome { get => _outcome; private set => SetProperty(ref _outcome, value); }
    public string? Error { get => _error; private set => SetProperty(ref _error, value); }
    public bool CanOpen => State == JobState.Succeeded && Outcome is { Outputs.Count: > 0 };

    public string StatusText => State switch
    {
        JobState.Queued => $"Queued (#{QueuePosition})",
        JobState.Running => string.Join(" · ", new[] { Stage, Fraction is { } f ? $"{f:0%}" : null, Remaining is { } left ? $"about {FormatRemaining(left)} left" : null }
            .Where(part => !string.IsNullOrEmpty(part))),
        JobState.Succeeded => Outcome?.Summary ?? "Done",
        JobState.Failed => Error ?? "Failed",
        _ => "Cancelled",
    };

    public RelayCommand CancelCommand { get; }
    public AsyncCommand OpenCommand { get; }
    public RelayCommand DismissCommand { get; }

    /// <summary>Completes when the job reaches a final state; never faults.</summary>
    public Task Completion => _completion.Task;

    /// <summary>Removes a queued job, or asks a running one to stop.</summary>
    public void Cancel()
    {
        if (State == JobState.Queued)
        {
            _queue.Remove(this);
            Finish(JobState.Cancelled);
        }
        else if (State == JobState.Running) _cancellation.Cancel();
    }

    internal async void Start()
    {
        // async void is confined here: RunAsync catches everything, so nothing escapes to the dispatcher.
        await RunAsync();
    }

    private async Task RunAsync()
    {
        _startedAt = _queue.Now;
        State = JobState.Running;
        var progress = new Progress<JobProgress>(Report);
        try
        {
            var token = _cancellation.Token;
            Outcome = await Task.Run(() => _work(progress, token), CancellationToken.None);
            Fraction = 1;
            Finish(JobState.Succeeded);
        }
        catch (OperationCanceledException) { Finish(JobState.Cancelled); }
        catch (Exception exception)
        {
            Error = exception.Message;
            Finish(JobState.Failed);
        }
    }

    private void Report(JobProgress update)
    {
        if (State != JobState.Running) return;
        Stage = update.Stage;
        if (update.Fraction is { } fraction) Fraction = Math.Max(Fraction ?? 0, Math.Clamp(fraction, 0, 1));
        else Fraction = null;
        var elapsed = _queue.Now - _startedAt;
        Remaining = Fraction is > 0.02 and < 1 && elapsed >= MinimumElapsedForEta ? elapsed * ((1 - Fraction.Value) / Fraction.Value) : null;
        _queue.ProgressChanged();
    }

    private void Finish(JobState state)
    {
        if (IsFinished) return;
        Remaining = null;
        State = state;
        _cancellation.Dispose();
        _queue.Ended(this);
        _completion.TrySetResult();
    }

    private void StateChanged()
    {
        foreach (var name in new[] { nameof(IsQueued), nameof(IsRunning), nameof(IsFinished), nameof(HasFailed), nameof(IsIndeterminate),
                     nameof(CanOpen), nameof(StatusText) })
            OnPropertyChanged(name);
        CancelCommand.RaiseCanExecuteChanged(); OpenCommand.RaiseCanExecuteChanged(); DismissCommand.RaiseCanExecuteChanged();
    }

    private static string FormatRemaining(TimeSpan left) => left.TotalMinutes >= 1 ? $"{Math.Ceiling(left.TotalMinutes):0} min" : $"{Math.Max(1, Math.Ceiling(left.TotalSeconds)):0} s";
}
