using System.Collections.ObjectModel;
using System.Windows.Input;
using HomeVideoLibrary.UI.Logic;

namespace HomeVideoLibrary.Gallery.Demo;

/// <summary>One chapter row. Its identity is stable for the life of the video, so an open title edit
/// (IsEditing + DraftTitle) survives every playhead update.</summary>
public sealed class ChapterRowViewModel : ObservableObject, IChapterMark
{
    private string _title;
    private bool _isCurrent;
    private bool _isEditing;
    private string? _draftTitle;
    private bool _isExpanded;

    public ChapterRowViewModel(TimeSpan start, string title, Action<ChapterRowViewModel> jump, Action<ChapterRowViewModel> toggle,
        Action<ChapterRowViewModel, string> rename)
    {
        Start = start;
        _title = title;
        Jump = new RelayCommand(() => jump(this));
        ToggleExpanded = new RelayCommand(() => toggle(this));
        CommitTitle = new RelayCommand<string>(title => rename(this, title));
    }

    public TimeSpan Start { get; }

    public string Title
    {
        get => _title;
        set => SetField(ref _title, value);
    }

    public bool IsCurrent
    {
        get => _isCurrent;
        set => SetField(ref _isCurrent, value);
    }

    public bool IsEditing
    {
        get => _isEditing;
        set => SetField(ref _isEditing, value);
    }

    public string? DraftTitle
    {
        get => _draftTitle;
        set => SetField(ref _draftTitle, value);
    }

    /// <summary>Whether this row is open into its editor. The panel keeps one row expanded at a time.</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetField(ref _isExpanded, value))
            {
                OnPropertyChanged(nameof(Editor));
            }
        }
    }

    /// <summary>The expanded editor's content: this row while expanded, else nothing, so the editor's controls exist
    /// only while it shows.</summary>
    public ChapterRowViewModel? Editor => IsExpanded ? this : null;

    public ICommand Jump { get; }

    public ICommand ToggleExpanded { get; }

    public ICommand CommitTitle { get; }
}

/// <summary>The single "new chapter goes here" row; it moves through the list, it is never promoted.</summary>
public sealed class PlaceholderRowViewModel : ObservableObject
{
    private TimeSpan _start;

    public TimeSpan Start
    {
        get => _start;
        set => SetField(ref _start, value);
    }
}

/// <summary>
/// The Chapters tab of one video. <see cref="Rows"/> holds the chapter rows plus one placeholder, patched
/// in place: a playhead change only updates <see cref="ChapterRowViewModel.IsCurrent"/> and moves the
/// placeholder, so rows (and any edit in progress) are never rebuilt. <see cref="Marks"/> is the
/// immutable snapshot the scrub bar draws, republished whenever chapters change.
/// </summary>
public sealed class ChaptersPanelViewModel : ObservableObject
{
    private static readonly TimeSpan SameChapterTolerance = TimeSpan.FromSeconds(1);

    private readonly List<ChapterRowViewModel> _chapters = [];
    private readonly PlaceholderRowViewModel _placeholder = new();
    private TimeSpan _position;
    private IReadOnlyList<IChapterMark> _marks = [];
    private bool _canAddChapter;
    private ChapterRowViewModel? _expanded;

    public ChaptersPanelViewModel(TimeSpan duration, IEnumerable<(TimeSpan Start, string Title)> chapters)
    {
        Duration = duration;
        foreach (var (start, title) in chapters.OrderBy(c => c.Start))
        {
            _chapters.Add(NewRow(start, title));
        }

        Rows = new ObservableCollection<ObservableObject>(_chapters);
        AddChapter = new RelayCommand(AddChapterAtPlayhead);
        Seek = new RelayCommand<TimeSpan>(time => Position = time);
        PublishMarks();
        UpdateForPosition();
    }

    public TimeSpan Duration { get; }

    public ObservableCollection<ObservableObject> Rows { get; }

    public IReadOnlyList<IChapterMark> Marks
    {
        get => _marks;
        private set => SetField(ref _marks, value);
    }

    /// <summary>Playback position as reported by the player. The demo player is this setter.</summary>
    public TimeSpan Position
    {
        get => _position;
        set
        {
            if (SetField(ref _position, value))
            {
                UpdateForPosition();
            }
        }
    }

    public bool CanAddChapter
    {
        get => _canAddChapter;
        private set => SetField(ref _canAddChapter, value);
    }

    public ICommand AddChapter { get; }

    public ICommand Seek { get; }

    private ChapterRowViewModel NewRow(TimeSpan start, string title) =>
        new(start, title, row => Position = row.Start, row => Expand(_expanded == row ? null : row), (row, title) =>
        {
            row.Title = title;
            row.DraftTitle = null;
            PublishMarks();
        });

    // One row open at a time: opening a row closes the previous one.
    private void Expand(ChapterRowViewModel? row)
    {
        if (_expanded is not null)
        {
            _expanded.IsExpanded = false;
        }

        _expanded = row;
        if (row is not null)
        {
            row.IsExpanded = true;
        }
    }

    private void AddChapterAtPlayhead()
    {
        if (!CanAddChapter)
        {
            return;
        }

        var index = ChapterPlacement.PlaceholderIndex([.. _chapters.Select(c => c.Start)], Position);
        var row = NewRow(Position, string.Empty);
        _chapters.Insert(index, row);
        Rows.Insert(Rows.IndexOf(_placeholder), row);
        Expand(row);
        PublishMarks();
        UpdateForPosition();
    }

    private void UpdateForPosition()
    {
        var starts = _chapters.Select(c => c.Start).ToList();
        for (var i = 0; i < _chapters.Count; i++)
        {
            var end = i + 1 < _chapters.Count ? _chapters[i + 1].Start : Duration;
            _chapters[i].IsCurrent = Position >= _chapters[i].Start && Position < end;
        }

        CanAddChapter = !ChapterPlacement.HasChapterAt(starts, Position, SameChapterTolerance);
        _placeholder.Start = Position;
        PlacePlaceholder(CanAddChapter ? ChapterPlacement.PlaceholderIndex(starts, Position) : -1);
    }

    // The placeholder sits before chapter `chapterIndex` (or at the end); -1 hides it.
    private void PlacePlaceholder(int chapterIndex)
    {
        var current = Rows.IndexOf(_placeholder);
        if (chapterIndex < 0)
        {
            if (current >= 0)
            {
                Rows.RemoveAt(current);
            }

            return;
        }

        var target = chapterIndex < _chapters.Count ? Rows.IndexOf(_chapters[chapterIndex]) : Rows.Count;
        if (current < 0)
        {
            Rows.Insert(target, _placeholder);
        }
        else if (current != target && current != target - 1)
        {
            Rows.Move(current, current < target ? target - 1 : target);
        }
    }

    private void PublishMarks() => Marks = [.. _chapters.Select(c => new ChapterMark(c.Start, c.Title))];
}
