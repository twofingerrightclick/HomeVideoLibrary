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
    private readonly ClipEdits _edits;
    /// <summary>The sources' ids; a set that never changes, so <see cref="Shows"/> can read it from any thread.</summary>
    private readonly IReadOnlySet<Guid> _sourceIds;
    private MergeSource? _selectedSource;
    private string _outputTitle = "";
    private JobViewModel? _job;

    /// <param name="edits">Plans the merge and runs it as a render job, which keeps running if the page closes.</param>
    /// <param name="back">Returns to the library.</param>
    /// <param name="time">Clock for the plan delay; tests pass a fake.</param>
    public MergePageViewModel(IReadOnlyList<LibraryVideo> sources, ClipEdits edits, Func<Task> back, TimeProvider? time = null)
    {
        if (sources.Count < 2) throw new ArgumentException("Select at least two videos to merge.", nameof(sources));
        _edits = edits;
        Sources = new ObservableCollection<MergeSource>(sources.Select(video => new MergeSource(video)));
        _sourceIds = sources.Select(video => video.Id).ToHashSet();
        Plan = new PlanReadout(time ?? TimeProvider.System);
        MoveUpCommand = new RelayCommand(() => MoveSelected(-1), () => CanMove(-1));
        MoveDownCommand = new RelayCommand(() => MoveSelected(1), () => CanMove(1));
        SaveCommand = new RelayCommand(Save, () => Plan.IsReady && Job is not { IsFinished: false });
        Plan.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(PlanReadout.State)) SaveCommand.RaiseCanExecuteChanged(); };
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
            OnPropertyChanged(nameof(SelectedIndex));
            MoveUpCommand.RaiseCanExecuteChanged();
            MoveDownCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Where <see cref="SelectedSource"/> is in the order, or -1; a list shows the selection by it, so a
    /// reorder moves the highlight with the clip.</summary>
    public int SelectedIndex => SelectedSource is { } source ? Sources.IndexOf(source) : -1;

    public string TotalDurationText => ScrubTimeline.FormatTimestamp(Sources.Aggregate(TimeSpan.Zero, (total, source) => total + source.Video.Duration));

    /// <summary>The new video's title; empty lets the service name it.</summary>
    public string OutputTitle { get => _outputTitle; set => SetProperty(ref _outputTitle, value); }

    /// <summary>Whether the merge is lossless or which clips it re-encodes, refreshed after reordering pauses.</summary>
    public PlanReadout Plan { get; }

    /// <summary>The latest merge started here, shown with its progress, CANCEL and OPEN; null until one starts.</summary>
    public JobViewModel? Job { get => _job; private set => SetProperty(ref _job, value); }

    /// <summary>The page's toolbar has its own ◀ LIBRARY key (and Esc).</summary>
    public override bool HasBackKey => true;

    public override bool Shows(Guid videoId) => _sourceIds.Contains(videoId);

    public RelayCommand MoveUpCommand { get; }
    public RelayCommand MoveDownCommand { get; }

    /// <summary>Queues a render job for the planned order. Disabled while the plan is not ready, and while this page's
    /// merge is queued or running, so pressing twice cannot queue the same merge twice.</summary>
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
        OnPropertyChanged(nameof(SelectedIndex));
        MoveUpCommand.RaiseCanExecuteChanged();
        MoveDownCommand.RaiseCanExecuteChanged();
        Plan.Request(PlanAsync(Sources.Select(source => source.Video).ToArray()));
    }

    private Func<CancellationToken, Task<PlanDescription>> PlanAsync(IReadOnlyList<LibraryVideo> order) =>
        async token => PlanDescription.Merge(await _edits.Service.PlanMergeAsync(order, token), order);

    private void Save()
    {
        // Captured now: the job may start later, and runs on the thread pool.
        var (order, title) = (Sources.Select(source => source.Video).ToArray(), OutputTitle);
        Job = ClipEditJob.Enqueue(_edits, $"Merge {order.Length} videos", order.Select(video => video.Id).ToArray(),
            (editing, progress, token) => editing.MergeAsync(order, title, progress, token));
        _ = UpdateSaveWhenDoneAsync(Job);
    }

    private async Task UpdateSaveWhenDoneAsync(JobViewModel job)
    {
        SaveCommand.RaiseCanExecuteChanged();
        await job.Completion;
        SaveCommand.RaiseCanExecuteChanged();
    }
}
