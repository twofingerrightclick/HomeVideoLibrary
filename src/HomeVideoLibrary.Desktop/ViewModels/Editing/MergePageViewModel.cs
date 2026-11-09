using System.Collections.ObjectModel;
using System.ComponentModel;
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

/// <summary>Joins two or more videos, in an order the user arranges, into one new video.</summary>
public sealed class MergePageViewModel : PageViewModel
{
    private readonly IClipEditingService _editing;
    private MergeSource? _selectedSource;
    private string _outputTitle = "Merged home video";

    /// <param name="outputsCreated">Publishes committed outputs to the library (see <see cref="ClipEditRun"/>).</param>
    /// <param name="open">Opens the new video (navigates away from this page).</param>
    /// <param name="back">Returns to the library.</param>
    /// <param name="time">Clock for the plan delay; tests pass a fake.</param>
    public MergePageViewModel(IReadOnlyList<LibraryVideo> sources, IClipEditingService editing,
        Func<IReadOnlyList<LibraryVideo>, Task> outputsCreated, Func<LibraryVideo, Task> open, Func<Task> back,
        TimeProvider? time = null)
    {
        if (sources.Count < 2) throw new ArgumentException("Select at least two videos to merge.", nameof(sources));
        _editing = editing;
        Sources = new ObservableCollection<MergeSource>(sources.Select(video => new MergeSource(video)));
        Plan = new PlanReadout(time ?? TimeProvider.System);
        Run = new ClipEditRun("Arrange the clips in the order they should play. The originals stay as they are.", outputsCreated);
        MoveUpCommand = new RelayCommand(() => MoveSelected(-1), () => CanMove(-1));
        MoveDownCommand = new RelayCommand(() => MoveSelected(1), () => CanMove(1));
        SaveCommand = new AsyncCommand(SaveAsync, context: "Merge");
        CancelCommand = new RelayCommand(SaveCommand.Cancel, () => IsBusy);
        OpenOutputCommand = new AsyncCommand<LibraryVideo>((video, _) => open(video), _ => !IsBusy, context: "Open video");
        BackCommand = new AsyncCommand(_ => back(), context: "Back to library");
        SaveCommand.PropertyChanged += OnSaveChanged;
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

    public ClipEditRun Run { get; }

    public bool IsBusy => SaveCommand.IsRunning;

    public RelayCommand MoveUpCommand { get; }
    public RelayCommand MoveDownCommand { get; }
    public AsyncCommand SaveCommand { get; }
    public RelayCommand CancelCommand { get; }
    public AsyncCommand<LibraryVideo> OpenOutputCommand { get; }
    public AsyncCommand BackCommand { get; }

    public override Task LoadAsync()
    {
        Plan.RequestNow(PlanAsync(Sources.Select(source => source.Video).ToArray()));
        return Task.CompletedTask;
    }

    public override Task<bool> CanLeaveAsync(IInteraction interaction)
    {
        if (IsBusy) Run.Notify("The merge is still running. Cancel it or wait for it to finish before leaving.");
        return Task.FromResult(!IsBusy);
    }

    public override Task SettleForCloseAsync()
    {
        SaveCommand.Cancel();
        return SaveCommand.Completion;
    }

    public override ValueTask DisposeAsync()
    {
        SaveCommand.Cancel();
        Plan.Dispose();
        return ValueTask.CompletedTask;
    }

    private bool CanMove(int direction) =>
        !IsBusy && SelectedSource is { } source && Sources.IndexOf(source) + direction is var target && target >= 0 && target < Sources.Count;

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

    private Task SaveAsync(CancellationToken cancellationToken)
    {
        var (order, title) = (Sources.Select(source => source.Video).ToArray(), OutputTitle);
        return Run.RunAsync((progress, token) => _editing.MergeAsync(order, title, progress, token), cancellationToken);
    }

    private void OnSaveChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(AsyncCommandBase.IsRunning)) return;
        OnPropertyChanged(nameof(IsBusy));
        MoveUpCommand.RaiseCanExecuteChanged();
        MoveDownCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        OpenOutputCommand.RaiseCanExecuteChanged();
    }
}
