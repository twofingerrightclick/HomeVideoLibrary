using System.Collections.ObjectModel;
using HomeVideoLibrary.Core;

namespace HomeVideoLibrary.Desktop.ViewModels;

public sealed class ClipEditorViewModel : ViewModelBase, IDisposable
{
    private static readonly TimeSpan PlanDelay = TimeSpan.FromMilliseconds(250);
    private readonly IClipEditingService _editing;
    private readonly Func<IReadOnlyList<LibraryVideo>, Task>? _outputsCreated;
    private CancellationTokenSource? _cancellation;
    private CancellationTokenSource? _planCancellation;
    private string _title, _start, _end, _split, _status = "Choose an operation. Every save creates new MKV files.";
    private string _trimPlan = "", _splitPlan = "", _mergePlan = "";
    private bool _isBusy, _isPrecise = true, _isProgressIndeterminate, _disposed;
    private double _progress;
    private LibraryVideo? _selectedSource;

    /// <param name="outputsCreated">Runs after the outputs are committed to the catalog; a failure is shown as a warning.</param>
    public ClipEditorViewModel(IReadOnlyList<LibraryVideo> sources, IClipEditingService editing,
        Func<IReadOnlyList<LibraryVideo>, Task>? outputsCreated = null)
    {
        if (sources.Count == 0) throw new ArgumentException("Select at least one video.", nameof(sources));
        Sources = new ObservableCollection<LibraryVideo>(sources); _editing = editing; _outputsCreated = outputsCreated;
        _title = sources.Count > 1 ? "Merged home video" : sources[0].Title.Length > 0 ? sources[0].Title : sources[0].DisplayName;
        _start = Timecode.Format(TimeSpan.Zero);
        _end = Timecode.Format(sources[0].Duration);
        _split = Timecode.Format(sources[0].Duration / 2);
    }

    public ObservableCollection<LibraryVideo> Sources { get; }
    public bool IsSingle => Sources.Count == 1;
    public bool CanMerge => Sources.Count >= 2 && !IsBusy;
    public string OutputTitle { get => _title; set => SetProperty(ref _title, value); }
    public string StartTime { get => _start; set { if (SetProperty(ref _start, value)) SchedulePlan(); } }
    public string EndTime { get => _end; set { if (SetProperty(ref _end, value)) SchedulePlan(); } }
    public string SplitTime { get => _split; set { if (SetProperty(ref _split, value)) SchedulePlan(); } }

    /// <summary>Frame-accurate cuts (re-encoding a fraction of a second at each cut) instead of keyframe-snapped copies.</summary>
    public bool IsPrecise { get => _isPrecise; set { if (SetProperty(ref _isPrecise, value)) SchedulePlan(); } }

    /// <summary>What the trim will really keep, and whether it is lossless; shown before running.</summary>
    public string TrimPlanSummary { get => _trimPlan; private set => SetProperty(ref _trimPlan, value); }
    public string SplitPlanSummary { get => _splitPlan; private set => SetProperty(ref _splitPlan, value); }
    public string MergePlanSummary { get => _mergePlan; private set => SetProperty(ref _mergePlan, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public bool IsBusy { get => _isBusy; private set { SetProperty(ref _isBusy, value); OnPropertyChanged(nameof(CanMerge)); } }
    public double Progress { get => _progress; private set => SetProperty(ref _progress, value); }
    public bool IsProgressIndeterminate { get => _isProgressIndeterminate; private set => SetProperty(ref _isProgressIndeterminate, value); }
    public LibraryVideo? SelectedSource { get => _selectedSource; set => SetProperty(ref _selectedSource, value); }

    public void MoveSelected(int direction)
    {
        if (IsBusy || SelectedSource is not { } source) return;
        var current = Sources.IndexOf(source); var target = current + direction;
        if (target >= 0 && target < Sources.Count) Sources.Move(current, target);
        SelectedSource = source;
        SchedulePlan();
    }

    /// <summary>Plans the operations for the current inputs immediately.</summary>
    public Task LoadFormatAsync() => RefreshPlansAsync(TimeSpan.Zero);

    public Task TrimAsync() => RunAsync((progress, token) => _editing.TrimAsync(Sources[0], ParsePosition(StartTime, "start"),
        ParsePosition(EndTime, "end"), OutputTitle, progress, token, Mode));
    public Task SplitAsync() => RunAsync((progress, token) => _editing.SplitAsync(Sources[0], ParsePosition(SplitTime, "split"), OutputTitle, progress, token));
    public Task MergeAsync() => RunAsync((progress, token) => _editing.MergeAsync(Sources.ToArray(), OutputTitle, progress, token));

    private ClipCutMode Mode => IsPrecise ? ClipCutMode.Precise : ClipCutMode.Fast;

    private void SchedulePlan() => _ = RefreshPlansAsync(PlanDelay);

    /// <summary>Re-plans after <paramref name="delay"/>, cancelling any plan still pending, so typing and reordering
    /// start at most one probe per pause.</summary>
    private async Task RefreshPlansAsync(TimeSpan delay)
    {
        _planCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource();
        _planCancellation = cancellation;
        var token = cancellation.Token;
        try
        {
            if (delay > TimeSpan.Zero) await Task.Delay(delay, token);
            if (IsSingle)
            {
                TrimPlanSummary = await DescribeAsync(() => _editing.PlanTrimAsync(Sources[0], ParsePosition(StartTime, "start"),
                    ParsePosition(EndTime, "end"), Mode, token), plan => $"Keeps {Timecode.Format(plan.ActualStart)} – {Timecode.Format(plan.ActualEnd)} · " +
                    (plan.IsLossless ? "lossless" : "precise") + ". " + plan.Note, token);
                SplitPlanSummary = await DescribeAsync(() => _editing.PlanSplitAsync(Sources[0], ParsePosition(SplitTime, "split"), token),
                    plan => $"Part 2 starts at {Timecode.Format(plan.ActualStart)} · lossless. " + plan.Note, token);
            }
            else
            {
                var sources = Sources.ToArray();
                MergePlanSummary = await DescribeAsync(() => _editing.PlanMergeAsync(sources, token), plan => plan.Note, token);
            }
        }
        catch (OperationCanceledException) { }
        finally { if (_planCancellation == cancellation) _planCancellation = null; }
    }

    private async Task<string> DescribeAsync<T>(Func<Task<T>> plan, Func<T, string> describe, CancellationToken token)
    {
        try
        {
            var result = await Task.Run(plan, token);
            token.ThrowIfCancellationRequested();
            return _disposed ? "" : describe(result);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) { return exception.Message; }
    }

    private async Task RunAsync(Func<IProgress<ClipEditProgress>, CancellationToken, Task<IReadOnlyList<LibraryVideo>>> operation)
    {
        if (IsBusy) return;
        IsBusy = true; Progress = 0; IsProgressIndeterminate = true;
        using var cancellation = new CancellationTokenSource(); _cancellation = cancellation;
        // Warnings are collected on the reporting thread so none can arrive after the final status is written.
        var warnings = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var display = new Progress<ClipEditProgress>(update =>
        {
            Status = update.Stage;
            IsProgressIndeterminate = update.Fraction is null;
            if (update.Fraction is { } fraction) Progress = Math.Max(Progress, fraction * 100);
        });
        var progress = new RelayProgress(update =>
        {
            if (update.Warning is { } warning) warnings.Enqueue(warning);
            else ((IProgress<ClipEditProgress>)display).Report(update);
        });
        try
        {
            var outputs = await Task.Run(() => operation(progress, cancellation.Token));
            if (_outputsCreated is { } outputsCreated)
            {
                try { await Task.Run(() => outputsCreated(outputs)); }
                catch (Exception exception) { warnings.Enqueue("Search refresh needed: " + exception.Message); }
            }
            Status = string.Join(" ", [$"Saved {outputs.Count} new video(s) in the library. Originals unchanged.", .. warnings.Distinct()]);
            IsProgressIndeterminate = false;
            Progress = 100;
        }
        catch (OperationCanceledException) { Status = "Editing cancelled; no new outputs published. Originals unchanged."; }
        catch (Exception exception) { Status = exception.Message; }
        finally { _cancellation = null; IsBusy = false; IsProgressIndeterminate = false; }
    }

    private static TimeSpan ParsePosition(string value, string name) => Timecode.TryParse(value, out var position)
        ? position : throw new ArgumentException($"Enter the {name} as seconds, m:ss or h:mm:ss.fff.");

    public void Cancel() => _cancellation?.Cancel();

    private sealed class RelayProgress(Action<ClipEditProgress> report) : IProgress<ClipEditProgress>
    {
        public void Report(ClipEditProgress value) => report(value);
    }

    public void Dispose() { _disposed = true; Cancel(); _planCancellation?.Cancel(); }
}
