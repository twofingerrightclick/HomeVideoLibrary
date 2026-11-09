using System.ComponentModel;
using HomeVideoLibrary.Core;

namespace HomeVideoLibrary.Desktop.ViewModels.Editing;

/// <summary>The video page's EDIT tab: trim to an in/out range or split at the playhead, each saved as new videos.
/// Times are absolute Matroska timestamps, which equal the displayed playback time for imported videos.</summary>
public sealed class TrimPanelViewModel : ViewModelBase, IDisposable
{
    public const string UnsavedDetailsMessage = "Save or revert your changes first";

    private readonly IClipEditingService _editing;
    private readonly Func<TimeSpan> _playhead;
    private readonly Func<bool> _hasUnsavedDetails;
    private TimeSpan? _in, _out;
    // Text the user committed that is not a time; kept so it can be corrected, and cleared by any valid input.
    private string? _rejectedIn, _rejectedOut;
    private TimeSpan _splitAt;
    private bool _isPrecise = true;
    private string _outputTitle;

    /// <param name="playhead">The player's current position, read when I/O is pressed and when the page reports a move.</param>
    /// <param name="outputsCreated">Publishes committed outputs to the library (see <see cref="ClipEditRun"/>).</param>
    /// <param name="open">Opens one of the new videos (navigates away from this page).</param>
    /// <param name="hasUnsavedDetails">Whether the page has unsaved metadata edits. New clips copy the file's saved
    /// metadata, so edits are blocked until they are saved or reverted. Call <see cref="DetailsDirtyChanged"/> when it changes.</param>
    /// <param name="time">Clock for the plan delay; tests pass a fake.</param>
    public TrimPanelViewModel(LibraryVideo source, IClipEditingService editing, Func<TimeSpan> playhead,
        Func<IReadOnlyList<LibraryVideo>, Task> outputsCreated, Func<LibraryVideo, Task> open, Func<bool> hasUnsavedDetails,
        TimeProvider? time = null)
    {
        Source = source; _editing = editing; _playhead = playhead; _hasUnsavedDetails = hasUnsavedDetails;
        _outputTitle = source.Title.Length > 0 ? source.Title : source.DisplayName;
        TrimPlan = new PlanReadout(time ?? TimeProvider.System);
        SplitPlan = new PlanReadout(time ?? TimeProvider.System);
        Run = new ClipEditRun("Every save creates new videos; the original stays as it is.", outputsCreated);
        SetInCommand = new RelayCommand(() => SetInPoint(_playhead(), null), () => !IsBusy);
        SetOutCommand = new RelayCommand(() => SetOutPoint(_playhead(), null), () => !IsBusy);
        ClearCommand = new RelayCommand(Clear, () => !IsBusy && (HasRange || _rejectedIn is not null || _rejectedOut is not null));
        TrimCommand = new AsyncCommand(TrimAsync, () => CanTrim, context: "Trim");
        SplitCommand = new AsyncCommand(SplitAsync, () => CanSplit, context: "Split");
        CancelCommand = new RelayCommand(() => { TrimCommand.Cancel(); SplitCommand.Cancel(); }, () => IsBusy);
        OpenOutputCommand = new AsyncCommand<LibraryVideo>((video, _) => open(video), _ => !IsBusy, context: "Open video");
        TrimCommand.PropertyChanged += OnRunChanged;
        SplitCommand.PropertyChanged += OnRunChanged;
        _splitAt = playhead();
        RefreshTrimPlan();
        RefreshSplitPlan();
    }

    public LibraryVideo Source { get; }

    /// <summary>Where the kept range starts; null keeps from the beginning.</summary>
    public TimeSpan? In
    {
        get => _in;
        private set { if (SetProperty(ref _in, value)) RangeChanged(); }
    }

    /// <summary>Where the kept range ends; null keeps to the end.</summary>
    public TimeSpan? Out
    {
        get => _out;
        private set { if (SetProperty(ref _out, value)) RangeChanged(); }
    }

    /// <summary>The in point as text. Setting it commits: a time sets <see cref="In"/>, empty text clears it, and
    /// anything else is kept with <see cref="InTextError"/>. Bind it so the view writes only when editing ends.</summary>
    public string InText
    {
        get => _rejectedIn ?? Format(In);
        set { var (time, rejected) = Parse(value); SetInPoint(time, rejected); }
    }

    public string OutText
    {
        get => _rejectedOut ?? Format(Out);
        set { var (time, rejected) = Parse(value); SetOutPoint(time, rejected); }
    }

    public string? InTextError => _rejectedIn is null ? null : "Enter the in point as h:mm:ss.fff, m:ss or seconds.";
    public string? OutTextError => _rejectedOut is null ? null : "Enter the out point as h:mm:ss.fff, m:ss or seconds.";

    public bool HasRange => In is not null || Out is not null;

    /// <summary>The range the trim keeps, for the scrub bar; null until an in or out point is set.</summary>
    public TimeSpan? RangeStart => HasRange ? In ?? TimeSpan.Zero : null;
    public TimeSpan? RangeEnd => HasRange ? Out ?? Source.Duration : null;

    public string? RangeError => RangeStart is { } start && RangeEnd is { } end && end <= start
        ? "The out point must come after the in point." : null;

    /// <summary>Frame-accurate cuts (re-encoding a fraction of a second at each cut) instead of keyframe-snapped copies.</summary>
    public bool IsPrecise
    {
        get => _isPrecise;
        set { if (SetProperty(ref _isPrecise, value)) RefreshTrimPlan(); }
    }

    public string OutputTitle { get => _outputTitle; set => SetProperty(ref _outputTitle, value); }

    /// <summary>Where a split would start the second part: the playhead when the page last reported it.</summary>
    public TimeSpan SplitAt
    {
        get => _splitAt;
        private set
        {
            if (!SetProperty(ref _splitAt, value)) return;
            RefreshSplitPlan();
            UpdateCommands();
        }
    }

    /// <summary>What the trim will really keep, whether it copies or renders, and why; refreshed after input pauses.</summary>
    public PlanReadout TrimPlan { get; }

    public PlanReadout SplitPlan { get; }

    public ClipEditRun Run { get; }

    public bool IsBusy => TrimCommand.IsRunning || SplitCommand.IsRunning;

    /// <summary>Why saving is blocked by the page's metadata edits, or null.</summary>
    public string? UnsavedDetailsHint => _hasUnsavedDetails() ? UnsavedDetailsMessage : null;

    public RelayCommand SetInCommand { get; }
    public RelayCommand SetOutCommand { get; }
    public RelayCommand ClearCommand { get; }
    public AsyncCommand TrimCommand { get; }
    public AsyncCommand SplitCommand { get; }
    public RelayCommand CancelCommand { get; }
    public AsyncCommand<LibraryVideo> OpenOutputCommand { get; }

    private bool CanTrim => !IsBusy && !_hasUnsavedDetails() && HasRange && RangeError is null && _rejectedIn is null && _rejectedOut is null;
    private bool CanSplit => !IsBusy && !_hasUnsavedDetails() && IsInside(SplitAt);
    private ClipCutMode Mode => IsPrecise ? ClipCutMode.Precise : ClipCutMode.Fast;

    /// <summary>The page calls this when the playhead moves; the split plan follows once it rests.</summary>
    public void PlayheadMoved() => SplitAt = _playhead();

    /// <summary>The page calls this when its unsaved-details state changes.</summary>
    public void DetailsDirtyChanged()
    {
        OnPropertyChanged(nameof(UnsavedDetailsHint));
        UpdateCommands();
    }

    /// <summary>Cancels a running edit and waits for it to finish, including publishing anything already committed.</summary>
    public Task SettleAsync()
    {
        CancelCommand.Execute(null);
        return Task.WhenAll(TrimCommand.Completion, SplitCommand.Completion);
    }

    public void Dispose()
    {
        TrimCommand.Cancel();
        SplitCommand.Cancel();
        TrimPlan.Dispose();
        SplitPlan.Dispose();
    }

    private void Clear()
    {
        SetInPoint(null, null);
        SetOutPoint(null, null);
    }

    /// <summary>Sets the in point, or keeps it and shows <paramref name="rejected"/> text that is not a time.</summary>
    private void SetInPoint(TimeSpan? time, string? rejected)
    {
        _rejectedIn = rejected;
        if (rejected is null) In = time;
        OnPropertyChanged(nameof(InText));
        OnPropertyChanged(nameof(InTextError));
        UpdateCommands();
    }

    private void SetOutPoint(TimeSpan? time, string? rejected)
    {
        _rejectedOut = rejected;
        if (rejected is null) Out = time;
        OnPropertyChanged(nameof(OutText));
        OnPropertyChanged(nameof(OutTextError));
        UpdateCommands();
    }

    private static (TimeSpan? Time, string? Rejected) Parse(string? text) =>
        string.IsNullOrWhiteSpace(text) ? (null, null) : Timecode.TryParse(text, out var time) ? (time, null) : (null, text);

    private void RangeChanged()
    {
        OnPropertyChanged(nameof(HasRange)); OnPropertyChanged(nameof(RangeStart)); OnPropertyChanged(nameof(RangeEnd));
        OnPropertyChanged(nameof(RangeError));
        RefreshTrimPlan();
        UpdateCommands();
    }

    private void RefreshTrimPlan()
    {
        if (RangeStart is not { } start || RangeEnd is not { } end) { TrimPlan.Show("Set an in or out point to keep part of this video."); return; }
        if (RangeError is not null) { TrimPlan.Show(""); return; }
        var mode = Mode;
        TrimPlan.Request(async token => PlanDescription.Trim(await _editing.PlanTrimAsync(Source, start, end, mode, token)));
    }

    private void RefreshSplitPlan()
    {
        var position = SplitAt;
        if (!IsInside(position)) { SplitPlan.Show("Move the playhead to where the second part should start."); return; }
        SplitPlan.Request(async token => PlanDescription.Split(await _editing.PlanSplitAsync(Source, position, token)));
    }

    private Task TrimAsync(CancellationToken cancellationToken)
    {
        // Read on the UI thread: the operation runs on the thread pool.
        var (start, end, title, mode) = (RangeStart!.Value, RangeEnd!.Value, OutputTitle, Mode);
        return Run.RunAsync((progress, token) => _editing.TrimAsync(Source, start, end, title, progress, token, mode), cancellationToken);
    }

    private Task SplitAsync(CancellationToken cancellationToken)
    {
        var (position, title) = (SplitAt, OutputTitle);
        return Run.RunAsync((progress, token) => _editing.SplitAsync(Source, position, title, progress, token), cancellationToken);
    }

    private bool IsInside(TimeSpan position) => position > TimeSpan.Zero && position < Source.Duration;

    private void OnRunChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(AsyncCommandBase.IsRunning)) return;
        OnPropertyChanged(nameof(IsBusy));
        UpdateCommands();
    }

    private void UpdateCommands()
    {
        SetInCommand.RaiseCanExecuteChanged();
        SetOutCommand.RaiseCanExecuteChanged();
        ClearCommand.RaiseCanExecuteChanged();
        TrimCommand.RaiseCanExecuteChanged();
        SplitCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        OpenOutputCommand.RaiseCanExecuteChanged();
    }

    private static string Format(TimeSpan? time) => time is { } value ? Timecode.Format(value) : "";
}
