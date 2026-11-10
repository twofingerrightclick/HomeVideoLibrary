using System.Collections.ObjectModel;
using HomeVideoLibrary.Core;
using HomeVideoLibrary.UI.Logic;

namespace HomeVideoLibrary.Desktop.ViewModels.Editing;

/// <summary>One clip in the merge order.</summary>
public sealed record MergeSource(LibraryVideo Video)
{
    public string Label => Video.Label;
    public string DurationText => ScrubTimeline.FormatTimestamp(Video.Duration);

    /// <summary>The resource key of the clip's spine accent, so the row matches its tape on the shelf.</summary>
    public string AccentKey => SpineStyle.For(Video.Id).AccentKey;
}

/// <summary>Joins two or more videos, in an order the user arranges, into one new video made by a render job.</summary>
public sealed class MergePageViewModel : PageViewModel
{
    private readonly IClipEditingService _editing;
    private readonly JobQueue _jobs;
    private readonly Func<IReadOnlyList<LibraryVideo>, Task> _outputsCreated;
    private MergeSource? _selectedSource;
    private string _outputTitle = "Merged home video";
    private JobViewModel? _job;

    /// <param name="jobs">Runs the merge; it keeps running if the page closes.</param>
    /// <param name="outputsCreated">Publishes committed outputs to the library (see <see cref="ClipEditJob"/>).</param>
    /// <param name="back">Returns to the library.</param>
    /// <param name="time">Clock for the plan delay; tests pass a fake.</param>
    public MergePageViewModel(IReadOnlyList<LibraryVideo> sources, IClipEditingService editing, JobQueue jobs,
        Func<IReadOnlyList<LibraryVideo>, Task> outputsCreated, Func<Task> back, TimeProvider? time = null)
    {
        if (sources.Count < 2) throw new ArgumentException("Select at least two videos to merge.", nameof(sources));
        _editing = editing; _jobs = jobs; _outputsCreated = outputsCreated;
        Sources = new ObservableCollection<MergeSource>(sources.Select(video => new MergeSource(video)));
        Plan = new PlanReadout(time ?? TimeProvider.System);
        MoveUpCommand = new RelayCommand(() => MoveSelected(-1), () => CanMove(-1));
        MoveDownCommand = new RelayCommand(() => MoveSelected(1), () => CanMove(1));
        SaveCommand = new RelayCommand(Save);
        BackCommand = new AsyncCommand(_ => back(), context: "Back to library");
    }

    /// <summary>The clips in output order. Reordering moves items, so rows and the selection survive it.</summary>
    public ObservableCollection<MergeSource> Sources { get; }

    public MergeSource? SelectedSource
    {
        get => _selectedSource;
        set
        {
            if (!SetProperty(ref _selectedSource, value)) return;
            MoveUpCommand.RaiseCanExecuteChanged();
            MoveDownCommand.RaiseCanExecuteChanged();
        }
    }

    public string TotalDurationText => ScrubTimeline.FormatTimestamp(Sources.Aggregate(TimeSpan.Zero, (total, source) => total + source.Video.Duration));

    public string OutputTitle { get => _outputTitle; set => SetProperty(ref _outputTitle, value); }

    /// <summary>Whether the merge is lossless or which clips it re-encodes, refreshed after reordering pauses.</summary>
    public PlanReadout Plan { get; }

    /// <summary>The latest merge started here, shown with its progress, CANCEL and OPEN; null until one starts.</summary>
    public JobViewModel? Job { get => _job; private set => SetProperty(ref _job, value); }

    public RelayCommand MoveUpCommand { get; }
    public RelayCommand MoveDownCommand { get; }

    /// <summary>Queues a render job for the current order; another edit may already be running or queued.</summary>
    public RelayCommand SaveCommand { get; }
    public AsyncCommand BackCommand { get; }

    public override Task LoadAsync()
    {
        Plan.RequestNow(PlanAsync(Sources.Select(source => source.Video).ToArray()));
        return Task.CompletedTask;
    }

    public override ValueTask DisposeAsync()
    {
        Plan.Dispose();
        return ValueTask.CompletedTask;
    }

    private bool CanMove(int direction) =>
        SelectedSource is { } source && Sources.IndexOf(source) + direction is var target && target >= 0 && target < Sources.Count;

    private void MoveSelected(int direction)
    {
        var source = SelectedSource!;
        var current = Sources.IndexOf(source);
        Sources.Move(current, current + direction);
        // Avalonia's list selection treats a Move as a remove and an add, so the bound list has just deselected the
        // item and written null back here. The moved clip is still the one the user is arranging.
        SelectedSource = source;
        MoveUpCommand.RaiseCanExecuteChanged();
        MoveDownCommand.RaiseCanExecuteChanged();
        Plan.Request(PlanAsync(Sources.Select(source => source.Video).ToArray()));
    }

    private Func<CancellationToken, Task<PlanDescription>> PlanAsync(IReadOnlyList<LibraryVideo> order) =>
        async token => PlanDescription.Merge(await _editing.PlanMergeAsync(order, token), order);

    private void Save()
    {
        // Captured now: the job may start later, and runs on the thread pool.
        var (order, title) = (Sources.Select(source => source.Video).ToArray(), OutputTitle);
        Job = ClipEditJob.Enqueue(_jobs, $"Merge {order.Length} videos",
            (progress, token) => _editing.MergeAsync(order, title, progress, token), _outputsCreated);
    }
}
