using System.Windows.Input;
using HomeVideoLibrary.Infrastructure;

namespace HomeVideoLibrary.Desktop.ViewModels;

/// <summary>The tape shelf: the visible tapes for the current search and album, the selection and the month dividers.
/// The selection is a set of ids independent of the search or album, with an anchor for Shift ranges.</summary>
public sealed class ShelfViewModel : ViewModelBase
{
    private readonly Func<TapeViewModel, TapePreviewSession?> _createPreview;
    private readonly Func<VideoRecord, TimeSpan?, Task> _open;
    private readonly Dictionary<Guid, TapeViewModel> _tapeIndex = [];
    private readonly HashSet<Guid> _selection = [];
    private readonly HashSet<Guid> _visibleIds = [];
    private LibrarySnapshot _snapshot = LibrarySnapshot.Empty;
    private AlbumFilter _filter = AlbumFilter.All;
    private Guid? _anchorId;
    private int _hiddenSelected;
    private string _searchText = "";
    private TapeViewModel? _topVisible;
    private ICommand? _moveToTrashCommand;

    /// <param name="createPreview">Starts a hover preview for a tape, or null when previews are unavailable.</param>
    /// <param name="open">Opens a video, optionally at a position.</param>
    public ShelfViewModel(Func<TapeViewModel, TapePreviewSession?> createPreview, Func<VideoRecord, TimeSpan?, Task> open)
    {
        _createPreview = createPreview; _open = open;
        SelectAllCommand = new RelayCommand(SelectAllVisible, () => Tapes.Count > 0);
        ClearSelectionCommand = new RelayCommand(ClearSelection, () => HasSelection);
        ClearCommand = new RelayCommand(() => { if (HasSelection) ClearSelection(); else SearchText = ""; }, () => HasSelection || SearchText.Length > 0);
        ClearSearchCommand = new RelayCommand(() => SearchText = "", () => SearchText.Length > 0);
    }

    /// <summary>Raised after the selection changes, for commands that depend on it.</summary>
    public event Action? SelectionChanged;

    /// <summary>The visible tapes, in display order (newest first).</summary>
    public PatchableCollection<TapeViewModel> Tapes { get; } = [];

    public bool IsEmpty => Tapes.Count == 0;
    public string CountLabel => $"{Tapes.Count} {(Tapes.Count == 1 ? "video" : "videos")} · newest first";

    public string SearchText { get => _searchText; set { if (SetProperty(ref _searchText, value)) Apply(_snapshot, _filter); } }

    public RelayCommand SelectAllCommand { get; }
    public RelayCommand ClearSelectionCommand { get; }
    /// <summary>Esc: clears the selection first, then the search.</summary>
    public RelayCommand ClearCommand { get; }
    public RelayCommand ClearSearchCommand { get; }

    /// <summary>Moves the selected videos to the trash. Null until a trash feature provides it; its controls hide while null.</summary>
    public ICommand? MoveToTrashCommand
    {
        get => _moveToTrashCommand;
        set { if (SetProperty(ref _moveToTrashCommand, value)) OnPropertyChanged(nameof(HasMoveToTrash)); }
    }
    public bool HasMoveToTrash => MoveToTrashCommand is not null;

    public int SelectedCount => _selection.Count;
    public bool HasSelection => _selection.Count > 0;
    public bool IsSingleSelection => _selection.Count == 1;
    public bool IsMultiSelection => _selection.Count > 1;
    public IReadOnlyCollection<Guid> SelectedIds => _selection;
    /// <summary>How many selected videos this view shows, for actions on what the user can see (add here, remove).</summary>
    public int VisibleSelectedCount => _selection.Count - _hiddenSelected;

    /// <summary>The selected videos this view shows; builds a new array, so call it when acting rather than to enable.</summary>
    public Guid[] VisibleSelectedIds() => _selection.Where(_visibleIds.Contains).ToArray();

    /// <summary>Every selected video, newest first, including ones the current search or album hides.</summary>
    public IReadOnlyList<VideoRecord> SelectedRecords => _snapshot.Chronological.Where(record => _selection.Contains(record.Id)).ToArray();

    public string SelectionLabel => _hiddenSelected == 0 ? $"{_selection.Count} selected" : $"{_selection.Count} selected · {_hiddenSelected} hidden by this view";

    /// <summary>The month of the tape at the top of the viewport, for the sticky month pill.</summary>
    public string StickyMonth => _topVisible?.MonthLabel ?? Tapes.FirstOrDefault()?.MonthLabel ?? "";

    /// <summary>Called by the view as it scrolls, with the first tape whose container crosses the viewport top.</summary>
    public void SetTopVisible(TapeViewModel? tape)
    {
        if (ReferenceEquals(tape, _topVisible)) return;
        _topVisible = tape;
        OnPropertyChanged(nameof(StickyMonth));
    }

    /// <summary>Shows <paramref name="snapshot"/> under the current search and <paramref name="filter"/>, patching tapes in
    /// place by id and recomputing the month dividers.</summary>
    public void Apply(LibrarySnapshot snapshot, AlbumFilter filter)
    {
        _snapshot = snapshot; _filter = filter;
        var hits = LibrarySearch.Run(snapshot, SearchText, filter);
        var desired = new List<TapeViewModel>(hits.Count);
        foreach (var hit in hits)
        {
            if (!_tapeIndex.TryGetValue(hit.Video.Id, out var tape))
                _tapeIndex[hit.Video.Id] = tape = new TapeViewModel(hit.Video, SetSelection, _createPreview, _open, SelectRange, _selection.Contains(hit.Video.Id));
            tape.MatchedChapter = hit.Chapter;
            desired.Add(tape);
        }
        foreach (var id in _tapeIndex.Keys.Where(id => !snapshot.Videos.ContainsKey(id)).ToArray()) _tapeIndex.Remove(id);
        _selection.RemoveWhere(id => !snapshot.Videos.ContainsKey(id));
        foreach (var (id, tape) in _tapeIndex) if (snapshot.Find(id) is { } record) tape.Update(record);
        Tapes.Patch(desired);

        string? previousMonth = null;
        foreach (var tape in desired)
        {
            var month = tape.MonthLabel;
            tape.MonthHeader = month == previousMonth ? null : month;
            previousMonth = month;
        }
        _visibleIds.Clear(); _visibleIds.UnionWith(desired.Select(tape => tape.Id));
        if (_topVisible is { } top && !_visibleIds.Contains(top.Id)) _topVisible = null;
        OnPropertyChanged(nameof(StickyMonth)); OnPropertyChanged(nameof(IsEmpty)); OnPropertyChanged(nameof(CountLabel));
        SelectAllCommand.RaiseCanExecuteChanged(); ClearCommand.RaiseCanExecuteChanged(); ClearSearchCommand.RaiseCanExecuteChanged();
        RecountHidden();
    }

    /// <summary>Selects every visible tape between the anchor (the last toggled tape) and <paramref name="tape"/>.</summary>
    public void SelectRange(TapeViewModel tape)
    {
        var end = Tapes.IndexOf(tape);
        if (end < 0) return;
        var start = _anchorId is { } anchor && _tapeIndex.TryGetValue(anchor, out var anchorTape) ? Tapes.IndexOf(anchorTape) : -1;
        if (start < 0) start = end;
        foreach (var item in Tapes.Skip(Math.Min(start, end)).Take(Math.Abs(end - start) + 1))
        {
            _selection.Add(item.Id); item.SetSelected(true);
        }
        _anchorId ??= tape.Id;
        NotifySelection();
    }

    public void SelectAllVisible()
    {
        foreach (var tape in Tapes) { _selection.Add(tape.Id); tape.SetSelected(true); }
        NotifySelection();
    }

    public void ClearSelection()
    {
        foreach (var id in _selection) if (_tapeIndex.TryGetValue(id, out var tape)) tape.SetSelected(false);
        _selection.Clear(); _anchorId = null; _hiddenSelected = 0;
        NotifySelection();
    }

    /// <summary>Raised when keyboard focus should move to a tape.</summary>
    public event Action<TapeViewModel>? FocusRequested;

    /// <summary>The tape to continue from once <paramref name="leaving"/> have left the shelf: the first visible tape after
    /// the first of them that is not leaving, else the nearest one before it. Null when none would remain.</summary>
    public Guid? TapeAfter(IReadOnlyCollection<Guid> leaving)
    {
        var first = -1;
        for (var index = 0; index < Tapes.Count && first < 0; index++)
            if (leaving.Contains(Tapes[index].Id)) first = index;
        if (first < 0) return null;
        for (var index = first + 1; index < Tapes.Count; index++)
            if (!leaving.Contains(Tapes[index].Id)) return Tapes[index].Id;
        for (var index = first - 1; index >= 0; index--)
            if (!leaving.Contains(Tapes[index].Id)) return Tapes[index].Id;
        return null;
    }

    /// <summary>Asks the view to focus the tape of <paramref name="videoId"/> when this view shows it.</summary>
    public void RequestFocus(Guid videoId)
    {
        if (_visibleIds.Contains(videoId) && _tapeIndex.TryGetValue(videoId, out var tape)) FocusRequested?.Invoke(tape);
    }

    /// <summary>The ids a drag starting on <paramref name="tape"/> carries: the selection when the tape is part of it.</summary>
    public IReadOnlyList<Guid> DragVideoIds(TapeViewModel tape) => tape.IsSelected ? [.. _selection] : [tape.Id];

    private void SetSelection(TapeViewModel tape, bool selected)
    {
        tape.SetSelected(selected);
        if (selected) _selection.Add(tape.Id); else _selection.Remove(tape.Id);
        _anchorId = tape.Id;
        NotifySelection();
    }

    // Only visible tapes are toggled one by one, so the hidden count changes only when the view or the library does.
    private void RecountHidden()
    {
        _hiddenSelected = _selection.Count(id => !_visibleIds.Contains(id));
        NotifySelection();
    }

    private void NotifySelection()
    {
        foreach (var name in new[] { nameof(SelectedCount), nameof(HasSelection), nameof(IsSingleSelection), nameof(IsMultiSelection), nameof(SelectedIds), nameof(VisibleSelectedCount), nameof(SelectedRecords), nameof(SelectionLabel) })
            OnPropertyChanged(name);
        ClearSelectionCommand.RaiseCanExecuteChanged(); ClearCommand.RaiseCanExecuteChanged();
        SelectionChanged?.Invoke();
    }
}
