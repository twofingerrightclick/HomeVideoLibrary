using System.Windows.Input;
using HomeVideoLibrary.Core;
using HomeVideoLibrary.Infrastructure;
using HomeVideoLibrary.UI.Logic;

namespace HomeVideoLibrary.Desktop.ViewModels;

/// <summary>One tape on the shelf. Instances are kept per video id and updated in place, so containers, focus and
/// scroll position survive library changes.</summary>
public sealed class TapeViewModel : ViewModelBase
{
    private readonly Action<TapeViewModel, bool> _selectionToggled;
    private readonly Func<TapeViewModel, TapePreviewSession?> _createPreview;
    private VideoRecord _record;
    private bool _isSelected;
    private VideoChapterRecord? _matchedChapter;
    private string? _monthHeader;

    /// <param name="selectionToggled">Called when the user changes <see cref="IsSelected"/> (its checkbox).</param>
    /// <param name="createPreview">Starts a hover preview session, or returns null when previews are unavailable.</param>
    /// <param name="open">Opens the tape, optionally at a position (a matched chapter).</param>
    /// <param name="selectRange">Selects from the selection anchor to this tape.</param>
    public TapeViewModel(VideoRecord record, Action<TapeViewModel, bool> selectionToggled,
        Func<TapeViewModel, TapePreviewSession?> createPreview, Func<VideoRecord, TimeSpan?, Task> open, Action<TapeViewModel> selectRange,
        bool isSelected = false)
    {
        _record = record; _selectionToggled = selectionToggled; _createPreview = createPreview;
        _isSelected = isSelected;
        Spine = SpineStyle.For(record.Id);
        OpenCommand = new AsyncCommand(_ => open(_record, null), context: "Open video");
        ToggleSelectionCommand = new RelayCommand(() => IsSelected = !IsSelected);
        SelectRangeCommand = new RelayCommand(() => selectRange(this));
        OpenAtMatchCommand = new AsyncCommand(_ => open(_record, _matchedChapter?.Start), () => _matchedChapter is not null, context: "Open video");
    }

    /// <summary>A hover preview for this tape's current record; the caller disposes it. Null when previews are off.</summary>
    public TapePreviewSession? CreatePreviewSession() => _createPreview(this);

    public Guid Id => _record.Id;
    public VideoRecord Record => _record;
    public LibraryVideo Video => _record.ToLibraryVideo();
    public SpineStyle Spine { get; }
    public ICommand OpenCommand { get; }
    public AsyncCommand OpenAtMatchCommand { get; }
    public RelayCommand ToggleSelectionCommand { get; }
    public RelayCommand SelectRangeCommand { get; }

    public string Label => _record.Label.Replace('\n', ' ').Replace('\r', ' ');
    public string MonthLabel => _record.RecordedAt.ToLocalTime().ToString("MMMM yyyy");

    /// <summary>The molded text on the cassette shell: the running time, or the problem when the file can't play.</summary>
    public string ShellText => _record.IsMissing ? "MISSING" : _record.State == VideoRecordState.Unreadable ? "UNREADABLE" : FormatRunningTime(_record.Duration);
    public bool HasProblem => _record.IsMissing || _record.State == VideoRecordState.Unreadable;
    public string? ProblemDetail => HasProblem ? _record.Error : null;
    public bool IsMissing => _record.IsMissing;

    /// <summary>The month divider shown above this tape: set when its month differs from the previous visible tape's.</summary>
    public string? MonthHeader { get => _monthHeader; internal set { if (SetProperty(ref _monthHeader, value)) OnPropertyChanged(nameof(HasMonthHeader)); } }
    public bool HasMonthHeader => MonthHeader is not null;

    /// <summary>The first chapter matching the current search, if any.</summary>
    public VideoChapterRecord? MatchedChapter
    {
        get => _matchedChapter;
        internal set
        {
            if (!SetProperty(ref _matchedChapter, value)) return;
            OnPropertyChanged(nameof(MatchedChapterLabel)); OnPropertyChanged(nameof(HasMatchedChapter));
            OpenAtMatchCommand.RaiseCanExecuteChanged();
        }
    }
    public bool HasMatchedChapter => MatchedChapter is not null;
    public string MatchedChapterLabel => MatchedChapter is { } chapter
        ? $"▸ {ScrubTimeline.FormatTimestamp(chapter.Start)} · {(chapter.Title.Length > 0 ? chapter.Title : "Untitled chapter")}" : "";

    /// <summary>User-edited through the tape's checkbox.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set { if (SetProperty(ref _isSelected, value)) _selectionToggled(this, value); }
    }

    internal void SetSelected(bool value) => SetProperty(ref _isSelected, value, nameof(IsSelected));

    internal void Update(VideoRecord record)
    {
        if (ReferenceEquals(record, _record)) return;
        _record = record;
        foreach (var name in new[] { nameof(Record), nameof(Video), nameof(Label), nameof(MonthLabel), nameof(ShellText), nameof(HasProblem),
                     nameof(ProblemDetail), nameof(IsMissing) })
            OnPropertyChanged(name);
    }

    /// <summary>"42 MIN" under an hour, "1H 02M" from one hour.</summary>
    public static string FormatRunningTime(TimeSpan duration) => duration.TotalHours >= 1
        ? $"{(int)duration.TotalHours}H {duration.Minutes:00}M"
        : $"{Math.Max(1, (int)Math.Round(duration.TotalMinutes))} MIN";
}
