using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using HomeVideoLibrary.Core;
using HomeVideoLibrary.Desktop.Playback;
using HomeVideoLibrary.Infrastructure;

namespace HomeVideoLibrary.Desktop.ViewModels;

/// <summary>The per-library MKV services. Both are the same <see cref="MatroskaMetadataService"/> in the app.</summary>
public sealed record LibraryServices(IMatroskaFileEditor Files, IVideoMetadataService Metadata);

/// <summary>External services of <see cref="MainWindowViewModel"/>; the defaults are the real ones.</summary>
public sealed record MainWindowDependencies
{
    public LibrarySettings Settings { get; init; } = new();
    public Func<LibraryCatalog, LibraryServices> CreateServices { get; init; } = catalog =>
    {
        var service = new MatroskaMetadataService(catalog, MediaTools.Discover());
        return new LibraryServices(service, service);
    };
    public Func<LibraryCatalog, IVideoImporter> CreateImporter { get; init; } = catalog => new VideoImporter(catalog, MediaTools.Discover());
    public Func<LibraryCatalog, IClipEditingService> CreateClipEditing { get; init; } = catalog => new ClipEditingService(catalog, MediaTools.Discover());
    public Func<CancellationToken, Task<IReadOnlyList<string>>> FindUnavailableTools { get; init; } =
        cancellationToken => MediaTools.Discover().FindUnavailableAsync(cancellationToken);
    public Func<LibraryLock, CancellationToken, Task> SweepScratch { get; init; } = ScratchSweeper.SweepAsync;
    public Func<IVideoPlayback> CreatePlayback { get; init; } = () => new LibVlcPlayback();
    /// <summary>Runs an action on the UI thread later. Tests run it inline.</summary>
    public Action<Action> Post { get; init; } = action => Dispatcher.UIThread.Post(action);
}

public enum LeaveChoice { Save, Discard, KeepEditing }

/// <summary>An open "unsaved changes" question. The view shows it while <see cref="MainWindowViewModel.LeavePrompt"/>
/// is set and answers it once; the close flow awaits <see cref="Choice"/>.</summary>
public sealed class LeavePrompt
{
    private readonly TaskCompletionSource<LeaveChoice> _choice = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<LeaveChoice> Choice => _choice.Task;
    public void Answer(LeaveChoice choice) => _choice.TrySetResult(choice);
}

public sealed class MainWindowViewModel : ViewModelBase
{
    private static readonly TimeSpan ActivationStatInterval = TimeSpan.FromSeconds(10);
    private readonly MainWindowDependencies _dependencies;
    private readonly Dictionary<Guid, TapeViewModel> _tapeIndex = [];
    private readonly Dictionary<Guid, TapeRow> _rowIndex = [];
    private readonly HashSet<Guid> _selection = [];
    private readonly HashSet<Guid> _visibleIds = [];
    private LibraryLock? _lock;
    private LibraryModel? _model;
    private LibraryServices? _services;
    private PreviewCache? _previews;
    private LibrarySnapshot _shown = LibrarySnapshot.Empty;
    private Guid? _anchorId;
    private CancellationTokenSource? _importCancellation, _indexCancellation;
    private TaskCompletionSource? _importFinished;
    private Task? _indexRun;
    private Task<bool>? _closing;
    private DateTime _lastActivationStat;
    private IReadOnlyList<string> _unavailableTools = [];
    private IReadOnlyList<string> _importResults = [], _indexErrors = [];
    private string _libraryRoot = "Choose a library folder to begin";
    private string _status = "Your original files stay untouched.";
    private string _searchText = "", _albumName = "", _indexStatus = "", _toolWarning = "", _recoveryNotice = "", _toast = "";
    private bool _isBusy, _isImporting, _isProgressIndeterminate, _isIndexing, _isChoosingAlbumVideos;
    private bool _isDrawerOpen = true, _isDraggingTapes;
    private double _progress;
    private int _tapeColumns = 1;
    private VideoEditorViewModel? _editor;
    private ClipEditorViewModel? _clipEditor;
    private LibraryVideo? _selectedVideo;
    private VideoAlbum? _currentAlbum, _targetAlbum;
    private LeavePrompt? _leavePrompt;

    public MainWindowViewModel() : this(new MainWindowDependencies()) { }

    public MainWindowViewModel(MainWindowDependencies dependencies) => _dependencies = dependencies;

    public string Title => "Home Video Library";
    public string WelcomeMessage => "A home for the moments you want to keep.";

    /// <summary>The open library's model; null until a library is open. UI components read
    /// <see cref="LibraryModel.Snapshot"/> (for example its <see cref="TagIndex"/>) from here.</summary>
    public LibraryModel? Model => _model;
    public TagIndex Tags => _model?.Snapshot.Tags ?? TagIndex.Empty;

    public bool IsDrawerOpen { get => _isDrawerOpen; set => SetProperty(ref _isDrawerOpen, value); }
    public bool IsDraggingTapes { get => _isDraggingTapes; set => SetProperty(ref _isDraggingTapes, value); }
    /// <summary>The visible tapes, in display order.</summary>
    public PatchableCollection<TapeViewModel> Tapes { get; } = [];
    public PatchableCollection<TapeRow> TapeRows { get; } = [];
    public ObservableCollection<VideoAlbum> Albums { get; } = [];
    public bool IsEmpty => Tapes.Count == 0 && !HasEditor;
    public bool HasLibrary => _model is not null;
    public string EmptyTitle => !HasLibrary ? "A home for your home videos" : !string.IsNullOrWhiteSpace(SearchText) ? "No matching videos" : HasAlbum ? "No videos in this view" : "Your collection starts here";
    public string EmptyMessage => !HasLibrary ? "Choose a library folder, then import your videos. Your originals stay untouched." : !string.IsNullOrWhiteSpace(SearchText) ? "Try another description, tag or chapter, or clear your search to see the collection." : HasAlbum ? "Use Add videos to find videos outside this album, or return to All videos." : "Import your first videos to start your chronological collection. Your originals stay untouched.";
    public string LibraryCount => $"{Tapes.Count} {(Tapes.Count == 1 ? "video" : "videos")} · newest first";

    public string SearchText { get => _searchText; set { if (SetProperty(ref _searchText, value)) RefreshView(); } }
    public string AlbumName { get => _albumName; set => SetProperty(ref _albumName, value); }
    public string IndexStatus { get => _indexStatus; private set => SetProperty(ref _indexStatus, value); }
    /// <summary>Per-file problems from the last library refresh.</summary>
    public IReadOnlyList<string> IndexErrors { get => _indexErrors; private set { if (SetProperty(ref _indexErrors, value)) OnPropertyChanged(nameof(IndexErrorsText)); } }
    public string IndexErrorsText => string.Join(Environment.NewLine, IndexErrors);
    public bool IsIndexing { get => _isIndexing; private set => SetProperty(ref _isIndexing, value); }
    /// <summary>Names the missing media tools and what they disable; empty when every tool answered.</summary>
    public string ToolWarning { get => _toolWarning; private set { if (SetProperty(ref _toolWarning, value)) { OnPropertyChanged(nameof(HasToolWarning)); NotifyCommandState(); } } }
    public bool HasToolWarning => ToolWarning.Length > 0;
    private bool ToolsReady => _unavailableTools.Count == 0;
    /// <summary>Set when <c>.cache/recovery</c> holds snapshots from a save that could not be rolled back; they are
    /// never deleted automatically.</summary>
    public string RecoveryNotice { get => _recoveryNotice; private set { if (SetProperty(ref _recoveryNotice, value)) OnPropertyChanged(nameof(HasRecoveryNotice)); } }
    public bool HasRecoveryNotice => RecoveryNotice.Length > 0;
    /// <summary>A short confirmation, such as an album drop; clears itself.</summary>
    public string Toast { get => _toast; private set { if (SetProperty(ref _toast, value)) OnPropertyChanged(nameof(HasToast)); } }
    public bool HasToast => Toast.Length > 0;

    /// <summary>The album being shown, or null for the whole library. Change it with <see cref="ShowAlbum"/>.</summary>
    public VideoAlbum? CurrentAlbum => _currentAlbum;
    /// <summary>The destination for "Add to album". Change it with <see cref="ChooseTargetAlbum"/>.</summary>
    public VideoAlbum? TargetAlbum => _targetAlbum;

    /// <summary>Shows an album (null: the whole library), leaving add-mode. Choosing the shown album again does nothing.</summary>
    public void ShowAlbum(VideoAlbum? album)
    {
        if (album?.Id == _currentAlbum?.Id) return;
        _currentAlbum = album;
        AlbumName = album?.Name ?? ""; _isChoosingAlbumVideos = false;
        foreach (var name in new[] { nameof(CurrentAlbum), nameof(IsChoosingAlbumVideos), nameof(HasAlbum), nameof(CurrentViewTitle) })
            OnPropertyChanged(name);
        RefreshView();
    }

    public void ChooseTargetAlbum(VideoAlbum? album)
    {
        if (album is null || album.Id == _targetAlbum?.Id) return;
        _targetAlbum = album;
        OnPropertyChanged(nameof(TargetAlbum)); OnPropertyChanged(nameof(CanAddSelected));
    }

    public bool HasAlbum => CurrentAlbum is not null;
    public bool IsChoosingAlbumVideos => _isChoosingAlbumVideos;
    public string CurrentViewTitle => IsChoosingAlbumVideos ? $"Add videos to {CurrentAlbum?.Name}" : CurrentAlbum?.Name ?? "All videos";

    /// <summary>Every selected video, newest first, including ones the current search or album hides.</summary>
    public IReadOnlyList<LibraryVideo> SelectedVideos => _shown.Chronological.Where(record => _selection.Contains(record.Id))
        .Select(record => _tapeIndex.TryGetValue(record.Id, out var tape) ? tape.Video : record.ToLibraryVideo()).ToArray();
    public IReadOnlyCollection<Guid> SelectedIds => _selection;
    public string SelectionLabel
    {
        get
        {
            var hidden = _selection.Count(id => !_visibleIds.Contains(id));
            return hidden == 0 ? $"{_selection.Count} selected" : $"{_selection.Count} selected · {hidden} hidden by this view";
        }
    }
    public bool CanAddSelected => TargetAlbum is not null && _selection.Count > 0;
    public LibraryVideo? SelectedVideo { get => _selectedVideo; set { SetProperty(ref _selectedVideo, value); OnPropertyChanged(nameof(CanOpenSelected)); } }

    public string LibraryRoot { get => _libraryRoot; private set => SetProperty(ref _libraryRoot, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public bool IsBusy { get => _isBusy; private set { if (SetProperty(ref _isBusy, value)) NotifyCommandState(); } }
    public bool IsImporting { get => _isImporting; private set => SetProperty(ref _isImporting, value); }
    public bool CanImport => _model is not null && !IsBusy && ToolsReady;
    /// <summary>0..100 and never decreasing within one import. See <see cref="IsProgressIndeterminate"/>.</summary>
    public double Progress { get => _progress; private set => SetProperty(ref _progress, value); }
    public bool IsProgressIndeterminate { get => _isProgressIndeterminate; private set => SetProperty(ref _isProgressIndeterminate, value); }
    /// <summary>One line per file of the last import, kept when it was cancelled.</summary>
    public IReadOnlyList<string> ImportResults { get => _importResults; private set => SetProperty(ref _importResults, value); }

    public VideoEditorViewModel? Editor { get => _editor; private set { SetProperty(ref _editor, value); OnPropertyChanged(nameof(HasVideoEditor)); NotifyEditorState(); } }
    public ClipEditorViewModel? ClipEditor { get => _clipEditor; private set { SetProperty(ref _clipEditor, value); OnPropertyChanged(nameof(HasClipEditor)); NotifyEditorState(); } }
    public bool HasVideoEditor => Editor is not null;
    public bool HasClipEditor => ClipEditor is not null;
    public bool CanEditClips => _selection.Count > 0 && !HasEditor && !IsBusy && ToolsReady;
    public bool CanOpenSelected => SelectedVideo is not null && !IsBusy && !HasEditor;
    public bool HasEditor => Editor is not null || ClipEditor is not null;
    /// <summary>The unsaved-changes question being asked, or null.</summary>
    public LeavePrompt? LeavePrompt { get => _leavePrompt; private set { if (SetProperty(ref _leavePrompt, value)) OnPropertyChanged(nameof(IsAskingToLeave)); } }
    public bool IsAskingToLeave => LeavePrompt is not null;

    private void NotifyCommandState()
    {
        OnPropertyChanged(nameof(CanImport)); OnPropertyChanged(nameof(CanOpenSelected));
        OnPropertyChanged(nameof(CanAddSelected)); OnPropertyChanged(nameof(CanEditClips));
    }

    private void NotifyEditorState()
    {
        OnPropertyChanged(nameof(HasEditor)); OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(CanOpenSelected)); OnPropertyChanged(nameof(CanEditClips));
    }

    public void SetTapeColumns(int columns)
    {
        if (columns < 1 || _tapeColumns == columns) return;
        _tapeColumns = columns; RebuildRows();
    }

    // Selection is a set of ids independent of the current search or album, with an anchor for Shift ranges.

    public void ToggleTapeSelection(TapeViewModel tape) => SetSelection(tape, !tape.IsSelected);

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
        SelectedVideo = tape.Video;
        SelectionChanged();
    }

    public void SelectAllVisible()
    {
        foreach (var tape in Tapes) { _selection.Add(tape.Id); tape.SetSelected(true); }
        SelectionChanged();
    }

    public void ClearSelection()
    {
        foreach (var id in _selection) if (_tapeIndex.TryGetValue(id, out var tape)) tape.SetSelected(false);
        _selection.Clear(); _anchorId = null;
        SelectionChanged();
    }

    /// <summary>The ids a drag starting on <paramref name="tape"/> carries: the selection when the tape is part of it.</summary>
    public IReadOnlyList<Guid> DragVideoIds(TapeViewModel tape) => tape.IsSelected ? [.. _selection] : [tape.Id];

    private void SetSelection(TapeViewModel tape, bool selected)
    {
        tape.SetSelected(selected);
        if (selected) _selection.Add(tape.Id); else _selection.Remove(tape.Id);
        _anchorId = tape.Id;
        SelectedVideo = selected ? tape.Video : _selection.Count > 0 && _tapeIndex.TryGetValue(_selection.First(), out var other) ? other.Video : null;
        SelectionChanged();
    }

    private void SelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedVideos)); OnPropertyChanged(nameof(SelectedIds)); OnPropertyChanged(nameof(SelectionLabel));
        OnPropertyChanged(nameof(CanAddSelected)); OnPropertyChanged(nameof(CanEditClips));
    }

    private TapePreviewSession? CreatePreviewSession(TapeViewModel tape)
    {
        if (_model is not { } model || _services is not { } services || _previews is not { } previews || !ToolsReady || tape.IsMissing) return null;
        return new TapePreviewSession(tape.Record, model.GetVideoPath(tape.Record), services.Files, previews);
    }

    public async Task OpenVideoAsync(LibraryVideo video)
    {
        if (_model is not { } model || _services is not { } services || IsBusy || HasEditor) return;
        var record = model.Snapshot.Find(video.Id);
        if (record is null) return;
        if (record.IsMissing) { Status = $"The file for “{record.Label}” is missing, so it cannot play."; return; }
        var indexed = new IndexedMetadataService(services.Metadata, model);
        indexed.IndexingFailed += warning => Status = warning;
        Editor = new VideoEditorViewModel(record.ToLibraryVideo(), model.GetVideoPath(record), indexed, _dependencies.CreatePlayback(), record.ToMetadata());
        await Editor.LoadAsync();
    }

    public Task OpenSelectedAsync() => SelectedVideo is { } video ? OpenVideoAsync(video) : Task.CompletedTask;

    public async Task OpenClipEditorAsync()
    {
        if (!CanEditClips || _model is not { } model) return;
        ClipEditor = new ClipEditorViewModel(SelectedVideos, _dependencies.CreateClipEditing(model.Catalog),
            outputs => PublishOutputsAsync(model, outputs));
        await ClipEditor.LoadFormatAsync();
    }

    private static async Task PublishOutputsAsync(LibraryModel model, IReadOnlyList<LibraryVideo> videos)
    {
        var errors = new List<string>();
        foreach (var video in videos)
        {
            var record = await model.UpsertFromFileAsync(video, CancellationToken.None);
            if (record.State != VideoRecordState.Ok) errors.Add($"{record.Label}: {record.Error}");
        }
        if (errors.Count > 0) throw new InvalidOperationException(string.Join(" ", errors));
    }

    /// <summary>Closes the open editor, asking first when it has unsaved changes. Returns false when it stays open (busy,
    /// Keep editing, a failed save, or a newer close request superseding this one).</summary>
    /// <param name="finishRunningWork">When true (closing the window), a running clip edit is cancelled and a running
    /// save is waited for, instead of keeping the editor open.</param>
    public async Task<bool> CloseEditorAsync(bool finishRunningWork = false)
    {
        if (ClipEditor is { } clips)
        {
            if (clips.IsBusy)
            {
                if (!finishRunningWork) return false;
                Status = "Cancelling the clip edit, then closing…";
                clips.Cancel();
                await WhenIdleAsync(clips, () => clips.IsBusy);
            }
            if (ClipEditor != clips) return ClipEditor is null;
            ClipEditor = null; clips.Dispose();
            return true;
        }
        if (Editor is not { } editor) return true;
        if (editor.IsBusy)
        {
            if (!finishRunningWork) return false;
            Status = "Finishing the current operation, then closing…";
            await WhenIdleAsync(editor, () => editor.IsBusy);
        }
        if (editor.IsDirty)
        {
            switch (await AskToLeaveAsync())
            {
                case LeaveChoice.KeepEditing: return false;
                case LeaveChoice.Save:
                    await editor.SaveAsync();
                    if (editor.IsDirty) return false;
                    break;
            }
        }
        if (Editor != editor) return Editor is null;
        Editor = null;
        await editor.DisposeAsync();
        return true;
    }

    private static async Task WhenIdleAsync(ViewModelBase viewModel, Func<bool> isBusy)
    {
        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(object? sender, System.ComponentModel.PropertyChangedEventArgs args) { if (!isBusy()) idle.TrySetResult(); }
        viewModel.PropertyChanged += Changed;
        try { if (isBusy()) await idle.Task; }
        finally { viewModel.PropertyChanged -= Changed; }
    }

    /// <summary>Everything that must happen before the window closes: cancels an import, closes the editor (asking about
    /// unsaved changes, finishing a save, cancelling a clip edit), stops the library refresh and releases the library.
    /// Returns false when the user chose to keep editing. Repeated calls while one is running share it.</summary>
    public Task<bool> RequestCloseAsync() => _closing is { IsCompleted: false } running ? running : _closing = CloseAsync();

    private async Task<bool> CloseAsync()
    {
        await CancelImportAndWaitAsync();
        if (!await CloseEditorAsync(finishRunningWork: true)) return false;
        await ReleaseLibraryAsync();
        return true;
    }

    /// <summary>Stops work on the open library and gives up its lock.</summary>
    private async Task ReleaseLibraryAsync()
    {
        CancelIndex();
        if (_indexRun is { } run) await run;
        if (_model is { } model) model.Changed -= OnModelChanged;
        _lock?.Dispose(); _lock = null;
    }

    /// <summary>Asks the unsaved-changes question. A newer request supersedes an unanswered one as Keep editing, so only
    /// the latest close intent (Back or closing the window) acts on the answer.</summary>
    private async Task<LeaveChoice> AskToLeaveAsync()
    {
        LeavePrompt?.Answer(LeaveChoice.KeepEditing);
        var prompt = LeavePrompt = new LeavePrompt();
        var choice = await prompt.Choice;
        if (LeavePrompt == prompt) LeavePrompt = null;
        return choice;
    }

    public async Task RestoreLibraryAsync()
    {
        try
        {
            var root = await _dependencies.Settings.LoadRootAsync();
            if (root is not null)
            {
                if (!Directory.Exists(root))
                    throw new DirectoryNotFoundException("The saved library folder is unavailable. Reconnect its drive or choose another folder.");
                await OpenLibraryAsync(root);
            }
        }
        catch (Exception exception) { Status = exception.Message; }
    }

    /// <summary>Opens (and if needed migrates) a library. Nothing about the current library changes unless the new one
    /// is free (not open in another instance), initializes and loads; only then is the old one released and the
    /// background refresh started. Reopening the open library just refreshes it.</summary>
    public async Task OpenLibraryAsync(string root)
    {
        if (IsBusy || HasEditor) return;
        var paths = new LibraryPaths(root);
        if (_lock is { } held && string.Equals(held.Paths.RootDirectory, paths.RootDirectory,
                OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
        {
            await RefreshIndexAsync();
            return;
        }
        IsBusy = true;
        LibraryLock? owner = null;
        LibraryModel model;
        LibraryServices services;
        LibraryLoadResult load;
        IReadOnlyList<string> unavailable;
        int recoverySnapshots;
        try
        {
            owner = await Task.Run(() => LibraryLock.TryAcquire(paths));
            if (owner is null)
            {
                Status = "This library is open in another Home Video Library window. Close it there, then open it here.";
                return;
            }
            var catalog = new LibraryCatalog(paths);
            await Task.Run(() => catalog.InitializeAsync());
            var tools = FindUnavailableToolsAsync();
            try { await _dependencies.SweepScratch(owner, CancellationToken.None); }
            catch (Exception exception) { Trace.TraceWarning($"Could not sweep temporary files: {exception}"); }
            services = _dependencies.CreateServices(catalog);
            model = new LibraryModel(catalog, services.Files);
            load = await Task.Run(() => model.LoadAsync());
            recoverySnapshots = CountRecoverySnapshots(paths);
            unavailable = await tools;
            await _dependencies.Settings.SaveRootAsync(root);
        }
        catch (Exception exception)
        {
            owner?.Dispose();
            Status = exception.Message;
            return;
        }
        finally { IsBusy = false; }

        await ReleaseLibraryAsync();
        _lock = owner; _model = model; _services = services; _previews = new PreviewCache(paths, services.Files);
        _unavailableTools = unavailable;
        ToolWarning = DescribeUnavailable(unavailable);
        RecoveryNotice = recoverySnapshots == 0 ? "" :
            $"{recoverySnapshots} metadata recovery snapshot(s) are in {MatroskaMetadataService.RecoveryDirectory(paths)}. " +
            "A save could not restore its video's previous metadata; the README explains how to restore it by hand.";
        LibraryRoot = paths.RootDirectory;
        _tapeIndex.Clear(); _rowIndex.Clear(); _selection.Clear(); _anchorId = null; _shown = LibrarySnapshot.Empty;
        _currentAlbum = null; _targetAlbum = null; _isChoosingAlbumVideos = false; _searchText = "";
        foreach (var name in new[] { nameof(CurrentAlbum), nameof(TargetAlbum), nameof(HasAlbum), nameof(CurrentViewTitle), nameof(IsChoosingAlbumVideos),
                     nameof(SearchText), nameof(HasLibrary), nameof(Model) })
            OnPropertyChanged(name);
        SelectedVideo = null;
        model.Changed += OnModelChanged;
        RefreshView();
        Status = $"{load.Videos} videos in your library" + (load.Pending > 0 ? $" · reading {load.Pending} for the first time…" : "")
            + string.Concat(load.Issues.Select(issue => Environment.NewLine + issue));
        _ = RefreshIndexAsync();
    }

    private static int CountRecoverySnapshots(LibraryPaths paths)
    {
        var directory = MatroskaMetadataService.RecoveryDirectory(paths);
        try { return Directory.Exists(directory) ? Directory.EnumerateFileSystemEntries(directory).Count() : 0; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return 0; }
    }

    private async Task<IReadOnlyList<string>> FindUnavailableToolsAsync()
    {
        try { return await _dependencies.FindUnavailableTools(CancellationToken.None); }
        catch (Exception exception)
        {
            Trace.TraceWarning($"Could not check media tools: {exception}");
            return [];
        }
    }

    private static string DescribeUnavailable(IReadOnlyList<string> tools) => tools.Count == 0 ? "" :
        $"Missing {string.Join(", ", tools)}. Install FFmpeg and MKVToolNix (see README), then reopen the library. " +
        "Until then importing, clip editing, library refresh and hover previews are off.";

    /// <param name="unavailablePicks">Picked items that were skipped because they are not local files.</param>
    public async Task ImportAsync(IReadOnlyList<string> sources, int unavailablePicks = 0)
    {
        var skipped = unavailablePicks > 0 ? $"{unavailablePicks} picked item(s) are not files on this computer and were skipped." : null;
        if (sources.Count == 0 && skipped is not null) Status = skipped;
        if (!CanImport || _model is not { } model || sources.Count == 0) return;
        IsBusy = true; IsImporting = true; Progress = 0; IsProgressIndeterminate = false;
        using var cancellation = new CancellationTokenSource();
        _importCancellation = cancellation;
        var finished = _importFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var results = skipped is null ? new List<string>() : [skipped];
        int imported = 0, alreadyThere = 0, failed = 0;
        try
        {
            var importer = _dependencies.CreateImporter(model.Catalog);
            for (var index = 0; index < sources.Count; index++)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var name = Path.GetFileName(sources[index]);
                var prefix = sources.Count == 1 ? name : $"File {index + 1} of {sources.Count} · {name}";
                Status = prefix;
                var first = index;
                var progress = new Progress<ImportProgress>(update =>
                {
                    if (!ReferenceEquals(_importCancellation, cancellation)) return;
                    Status = $"{prefix} — {update.Stage}";
                    IsProgressIndeterminate = update.Fraction is null;
                    if (update.Fraction is { } fraction)
                        Progress = Math.Max(Progress, (first + Math.Clamp(fraction, 0, 1)) / sources.Count * 100);
                });
                try
                {
                    var source = sources[index];
                    var result = await Task.Run(() => importer.ImportAsync(source, progress, cancellation.Token));
                    results.AddRange(result.Warnings.Select(warning => $"{name}: {warning}"));
                    if (result.Outcome == ImportOutcome.AlreadyInLibrary)
                    {
                        alreadyThere++;
                        results.Add($"{name}: already in library");
                    }
                    else
                    {
                        imported++;
                        // The video is committed; publish it even if the import is being cancelled.
                        var record = await Task.Run(() => model.UpsertFromFileAsync(result.Video, CancellationToken.None));
                        results.Add(record.State == VideoRecordState.Ok ? $"{name}: imported" : $"{name}: imported; details could not be read yet ({record.Error})");
                    }
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    failed++;
                    results.Add($"{name}: {exception.Message}");
                }
                Progress = Math.Max(Progress, (index + 1.0) / sources.Count * 100);
                IsProgressIndeterminate = false;
            }
            var summary = failed == 0 ? $"Imported {imported} video(s)." : $"Imported {imported}; {failed} could not be imported.";
            if (alreadyThere > 0) summary += $" {alreadyThere} already in library.";
            Status = summary + string.Concat(results.Select(line => Environment.NewLine + line));
        }
        catch (OperationCanceledException)
        {
            Status = $"Import cancelled. {imported} imported video(s) kept; originals unchanged." + string.Concat(results.Select(line => Environment.NewLine + line));
        }
        finally
        {
            ImportResults = results;
            _importCancellation = null; IsImporting = false; IsProgressIndeterminate = false; IsBusy = false;
            finished.TrySetResult();
        }
    }

    public void CancelImport() => _importCancellation?.Cancel();

    /// <summary>Cancels a running import and waits until it has cleaned up.</summary>
    public async Task CancelImportAndWaitAsync()
    {
        var finished = _importFinished;
        CancelImport();
        if (finished is not null) await finished.Task;
    }

    /// <summary>Reconciles the model with the files: reads new or changed MKVs (every MKV when <paramref name="force"/>).
    /// While a refresh is already running this waits for it instead.</summary>
    public Task RefreshIndexAsync(bool force = false)
    {
        if (IsIndexing && _indexRun is { } running) return running;
        return _indexRun = RunIndexAsync(force);
    }

    private async Task RunIndexAsync(bool force)
    {
        if (_model is not { } model) return;
        if (!ToolsReady) { IndexStatus = "Library refresh needs the missing media tools."; return; }
        using var cancellation = new CancellationTokenSource(); _indexCancellation = cancellation;
        IsIndexing = true;
        var progress = new Progress<IndexProgress>(update =>
        {
            if (model == _model) IndexStatus = $"Reading {update.Completed}/{update.Total} · {update.VideoName}";
        });
        try
        {
            var result = await Task.Run(() => model.ReconcileAsync(force, progress, cancellation.Token));
            if (model != _model) return;
            IndexErrors = result.Errors.Select(error => $"{error.VideoName}: {error.Message}").ToArray();
            IndexStatus = (result.Read == 0 ? "Library is current" : $"Library is current · read {result.Read}")
                + (result.Missing > 0 ? $" · {result.Missing} missing" : "") + (result.Errors.Count > 0 ? $" · {result.Errors.Count} unreadable" : "");
            if (result.Missing > 0 || result.Errors.Count > 0)
                Status = (result.Missing > 0 ? $"{result.Missing} video file(s) are missing. " : "") +
                         (result.Errors.Count > 0 ? $"{result.Errors.Count} video(s) could not be read; see Library settings." : "");
        }
        catch (OperationCanceledException) { if (model == _model) IndexStatus = "Refresh cancelled; completed entries kept. Refresh again to continue."; }
        catch (Exception exception) { if (model == _model) IndexStatus = exception.Message; }
        finally { if (_indexCancellation == cancellation) { _indexCancellation = null; IsIndexing = false; } }
    }

    public void CancelIndex() => _indexCancellation?.Cancel();

    /// <summary>Checks file stamps when the window regains focus (at most every 10 s) and reads what changed.</summary>
    public async Task WindowActivatedAsync()
    {
        if (_model is not { } model || IsIndexing || IsBusy || !ToolsReady || DateTime.UtcNow - _lastActivationStat < ActivationStatInterval) return;
        _lastActivationStat = DateTime.UtcNow;
        try
        {
            if (await Task.Run(() => model.StatPassAsync()) > 0 && model == _model) await RefreshIndexAsync();
        }
        catch (Exception exception) { Trace.TraceWarning($"Activation refresh failed: {exception}"); }
    }

    /// <summary>Shows an unexpected error reported by <see cref="AppErrors"/>.</summary>
    public void ShowError(string message) => _dependencies.Post(() => Status = message);

    // View synchronization: the model publishes snapshots; the shelf is patched to match, keeping tape instances.

    private void OnModelChanged(LibraryDelta delta) => _dependencies.Post(() =>
    {
        // Deltas queue up while the UI is busy; the first posted refresh shows the latest snapshot and the rest find
        // nothing new.
        if (_model is { } model && !ReferenceEquals(model.Snapshot, _shown)) RefreshView();
    });

    /// <summary>Shows the model's current snapshot under the current search and album, patching tapes in place.</summary>
    private void RefreshView()
    {
        var snapshot = _model?.Snapshot ?? LibrarySnapshot.Empty;
        SyncAlbums(snapshot);
        var filter = CurrentAlbum is { } album ? new AlbumFilter(album.Id, IsChoosingAlbumVideos) : AlbumFilter.All;
        var hits = LibrarySearch.Run(snapshot, SearchText, filter);
        var desired = new List<TapeViewModel>(hits.Count);
        foreach (var hit in hits)
        {
            if (_tapeIndex.TryGetValue(hit.Video.Id, out var tape)) tape.Update(hit.Video);
            else _tapeIndex[hit.Video.Id] = tape = new TapeViewModel(hit.Video, (item, selected) => SetSelection(item, selected),
                CreatePreviewSession, _selection.Contains(hit.Video.Id));
            tape.MatchedChapter = hit.Chapter;
            desired.Add(tape);
        }
        foreach (var id in _tapeIndex.Keys.Where(id => !snapshot.Videos.ContainsKey(id)).ToArray()) _tapeIndex.Remove(id);
        _selection.RemoveWhere(id => !snapshot.Videos.ContainsKey(id));
        foreach (var (id, tape) in _tapeIndex) if (snapshot.Find(id) is { } record) tape.Update(record);
        Tapes.Patch(desired);
        _visibleIds.Clear(); _visibleIds.UnionWith(desired.Select(tape => tape.Id));
        _shown = snapshot;
        RebuildRows();
        if (SelectedVideo is { } focused && !snapshot.Videos.ContainsKey(focused.Id)) SelectedVideo = null;
        else if (SelectedVideo is { } current && _tapeIndex.TryGetValue(current.Id, out var focusedTape)) SelectedVideo = focusedTape.Video;
        foreach (var name in new[] { nameof(IsEmpty), nameof(LibraryCount), nameof(EmptyTitle), nameof(EmptyMessage), nameof(Tags) })
            OnPropertyChanged(name);
        SelectionChanged();
    }

    private void RebuildRows()
    {
        var rows = new List<TapeRow>((Tapes.Count + 1) / _tapeColumns);
        var used = new HashSet<Guid>();
        for (var index = 0; index < Tapes.Count; index += _tapeColumns)
        {
            var left = Tapes[index];
            if (!_rowIndex.TryGetValue(left.Id, out var row) || !ReferenceEquals(row.Left, left)) _rowIndex[left.Id] = row = new TapeRow(left);
            row.Right = _tapeColumns == 2 && index + 1 < Tapes.Count ? Tapes[index + 1] : null;
            rows.Add(row); used.Add(left.Id);
        }
        foreach (var id in _rowIndex.Keys.Where(id => !used.Contains(id)).ToArray()) _rowIndex.Remove(id);
        TapeRows.Patch(rows);
    }

    /// <summary>Updates <see cref="Albums"/> in place by id and re-points the shown and target albums at the current
    /// instances. Both are bound one way, so the list's transient selection changes never feed back into them.</summary>
    private void SyncAlbums(LibrarySnapshot snapshot)
    {
        var albums = snapshot.Albums;
        for (var index = Albums.Count - 1; index >= 0; index--)
            if (!albums.Any(album => album.Id == Albums[index].Id)) Albums.RemoveAt(index);
        for (var index = 0; index < albums.Length; index++)
        {
            var id = albums[index].Id;
            var existing = Albums.Select(album => album.Id).ToList().IndexOf(id);
            if (existing < 0) Albums.Insert(index, albums[index]);
            else if (existing != index) Albums.Move(existing, index);
            if (!Equals(Albums[index], albums[index])) Albums[index] = albums[index];
        }
        var current = _currentAlbum is { } shown ? albums.FirstOrDefault(album => album.Id == shown.Id) : null;
        if (_currentAlbum is not null && current is null)
        {
            _isChoosingAlbumVideos = false; AlbumName = "";
            OnPropertyChanged(nameof(HasAlbum)); OnPropertyChanged(nameof(IsChoosingAlbumVideos));
        }
        _currentAlbum = current;
        _targetAlbum = (_targetAlbum is { } chosen ? albums.FirstOrDefault(album => album.Id == chosen.Id) : null) ?? albums.FirstOrDefault();
        OnPropertyChanged(nameof(CurrentAlbum)); OnPropertyChanged(nameof(TargetAlbum));
        OnPropertyChanged(nameof(CurrentViewTitle)); OnPropertyChanged(nameof(CanAddSelected));
    }

    // Albums. Operations are serialized by the model, so they may run during an import.

    public Task CreateAlbumAsync()
    {
        var ids = _selection.ToArray();
        var name = AlbumName;
        return AlbumActionAsync(async model =>
        {
            var album = await Task.Run(() => model.CreateAlbumAsync(name, ids));
            RefreshView();
            ShowAlbum(album);
            Status = ids.Length == 0 ? $"Created album “{album.Name}”." : $"Created album “{album.Name}” with {ids.Length} video(s).";
        });
    }

    public Task RenameAlbumAsync() => CurrentAlbum is { } album
        ? AlbumActionAsync(async model => { await Task.Run(() => model.RenameAlbumAsync(album.Id, AlbumName)); RefreshView(); })
        : Task.CompletedTask;

    public Task DeleteAlbumAsync() => CurrentAlbum is { } album
        ? AlbumActionAsync(async model =>
        {
            await Task.Run(() => model.DeleteAlbumAsync(album.Id));
            RefreshView();
            Status = "Album removed. All video files remain in the library.";
        })
        : Task.CompletedTask;

    public Task AddSelectedToAlbumAsync() => TargetAlbum is { } album ? AddToAlbumAsync(album.Id, _selection) : Task.CompletedTask;
    public Task AddSelectedToCurrentAsync() => CurrentAlbum is { } album ? AddToAlbumAsync(album.Id, _selection) : Task.CompletedTask;

    public Task RemoveSelectedFromCurrentAsync()
    {
        if (CurrentAlbum is not { } album) return Task.CompletedTask;
        var ids = _selection.ToArray();
        return AlbumActionAsync(async model =>
        {
            var change = await Task.Run(() => model.RemoveFromAlbumAsync(album.Id, ids));
            RefreshView();
            ShowToast($"Removed {change.Changed} video(s) from “{change.Album.Name}”.");
        });
    }

    public Task AddToAlbumAsync(Guid albumId, IEnumerable<Guid> videoIds)
    {
        var ids = videoIds.ToArray();
        if (ids.Length == 0) { ShowToast("Select videos first, then add them to an album."); return Task.CompletedTask; }
        return AlbumActionAsync(async model =>
        {
            var change = await Task.Run(() => model.AddToAlbumAsync(albumId, ids));
            RefreshView();
            ShowToast($"Added {change.Changed} video(s) to “{change.Album.Name}”" + (change.Unchanged > 0 ? $"; {change.Unchanged} already there." : "."));
        });
    }

    public void ShowLibrary()
    {
        _isChoosingAlbumVideos = false; OnPropertyChanged(nameof(IsChoosingAlbumVideos));
        if (CurrentAlbum is null) RefreshView(); else ShowAlbum(null);
    }

    public void ToggleAlbumAddMode()
    {
        if (!HasAlbum) return;
        _isChoosingAlbumVideos = !_isChoosingAlbumVideos;
        OnPropertyChanged(nameof(IsChoosingAlbumVideos)); OnPropertyChanged(nameof(CurrentViewTitle));
        RefreshView();
    }

    private async Task AlbumActionAsync(Func<LibraryModel, Task> action)
    {
        if (_model is not { } model) return;
        try { await action(model); }
        catch (Exception exception) { Status = exception.Message; }
    }

    private void ShowToast(string message)
    {
        Toast = message; Status = message;
        _ = HideToastAsync(message);
    }

    private async Task HideToastAsync(string message)
    {
        await Task.Delay(TimeSpan.FromSeconds(3));
        if (Toast == message) Toast = "";
    }
}
