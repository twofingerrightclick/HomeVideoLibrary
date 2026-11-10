using System.Diagnostics;
using Avalonia.Threading;
using HomeVideoLibrary.Core;
using HomeVideoLibrary.Desktop.Playback;
using HomeVideoLibrary.Desktop.ViewModels.Editing;
using HomeVideoLibrary.Desktop.ViewModels.VideoPage;
using HomeVideoLibrary.Infrastructure;
using HomeVideoLibrary.UI.Audio;
using HomeVideoLibrary.UI.Logic;

namespace HomeVideoLibrary.Desktop.ViewModels;

/// <summary>The per-library MKV services. Both are the same <see cref="MatroskaMetadataService"/> in the app.</summary>
public sealed record LibraryServices(IMatroskaFileEditor Files, IVideoMetadataService Metadata);

/// <summary>External services of <see cref="MainWindowViewModel"/>; the defaults are the real ones.</summary>
public sealed record MainWindowDependencies
{
    /// <summary>Modal UI (questions, pickers, settings). Required: every caller decides how they are answered.</summary>
    public required IInteraction Interaction { get; init; }
    public LibrarySettings Settings { get; init; } = new();
    public IClickSoundService Sounds { get; init; } = ClickSounds.Service;
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
    /// <summary>The clock for toasts and job estimates.</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;
}

/// <summary>The window shell and the only adapter from <see cref="LibraryModel"/> to the UI: it opens and releases the
/// library, marshals model changes onto the UI thread once, and composes the shelf, albums, import and settings view
/// models plus the current page.</summary>
public sealed class MainWindowViewModel : ViewModelBase
{
    private static readonly TimeSpan ActivationStatInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ToastDuration = TimeSpan.FromSeconds(3);
    private readonly MainWindowDependencies _dependencies;
    private LibraryLock? _lock;
    private LibraryModel? _model;
    private LibraryServices? _services;
    private PreviewCache? _previews;
    private LibrarySnapshot _shown = LibrarySnapshot.Empty;
    private readonly SemaphoreSlim _navigation = new(1, 1);
    private Task<bool>? _closing;
    private CancellationTokenSource? _quitWait;
    private Task? _opening;
    private object? _shownToast;
    private DateTime _lastActivationStat;
    private IReadOnlyList<string> _unavailableTools = [];
    private string _status = "Your original files stay untouched.";
    private string _toolWarning = "", _recoveryNotice = "", _toast = "";
    private bool _isDrawerOpen = true, _isDraggingTapes;
    private PageViewModel? _page;
    private readonly VideoPageSession _videoPageSession = new();

    public MainWindowViewModel(MainWindowDependencies dependencies)
    {
        _dependencies = dependencies;
        Jobs = new JobQueue(OpenJobOutputAsync, dependencies.Time);
        Jobs.Finished += JobFinished;
        Jobs.Drained += LaneDrained;
        Jobs.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(JobQueue.ActiveCount)) UpdateCommands(); };
        Shelf = new ShelfViewModel(CreatePreviewSession, (record, at) => OpenVideoAsync(record, at));
        Albums = new AlbumsViewModel(() => _model, Shelf, ShowToast, message => Status = message, RefreshView);
        Import = new ImportViewModel(() => _model, dependencies.CreateImporter, dependencies.Interaction, Jobs, () => ToolsReady && IsSettled,
            message => Status = message);
        Settings = new SettingsViewModel(dependencies.Settings, dependencies.Sounds, dependencies.Interaction, () => _model, () => ToolsReady,
            OpenLibraryAsync, () => IsSettled && !Jobs.HasActive, message => Status = message);
        Shelf.SelectionChanged += UpdateCommands;
        Shelf.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(ShelfViewModel.IsEmpty)) NotifyViewState(); };
        Albums.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(AlbumsViewModel.Current) or nameof(AlbumsViewModel.IsAddingVideos)) UpdateCommands();
        };

        OpenSettingsCommand = new AsyncCommand(_ => dependencies.Interaction.ShowSettingsAsync(Settings), context: "Settings");
        PlaySelectedCommand = new AsyncCommand(_ => FirstSelected() is { } record ? OpenVideoAsync(record) : Task.CompletedTask,
            () => Shelf.HasSelection, context: "Open video");
        TrimSelectedCommand = new AsyncCommand(_ => OpenClipEditorAsync(), () => Shelf.SelectedCount == 1 && CanEditClips, context: "Trim or split");
        MergeSelectedCommand = new AsyncCommand(_ => OpenMergeAsync(), () => Shelf.SelectedCount >= 2 && CanEditClips, context: "Merge");
        DeleteSelectedCommand = new RelayCommand(() => DeleteTarget?.Execute(null), () => DeleteTarget?.CanExecute(null) == true);
        BackCommand = new AsyncCommand(_ => NavigateAsync(() => null), () => Page is not null, context: "Back to library");
        FocusSearchCommand = new AsyncCommand(_ => FocusSearchAsync(), context: "Search");
        CancelQuitCommand = new RelayCommand(() => _quitWait?.Cancel(), () => IsWaitingToQuit);
    }

    public string Title => "Home Video Library";

    /// <summary>The open library's model; null until a library is open.</summary>
    public LibraryModel? Model => _model;
    public TagIndex Tags => _model?.Snapshot.Tags ?? TagIndex.Empty;

    /// <summary>Tag autocomplete over the current snapshot's index: a pure read on the UI thread, so a tag saved on one
    /// video is suggested on the next as soon as its delta lands.</summary>
    public TagSuggestionProvider SuggestTags => (query, exclude, max) =>
        Tags.Suggest(query, max, exclude).Select(suggestion => new TagFrequency(suggestion.Tag, suggestion.Uses)).ToArray();

    public ShelfViewModel Shelf { get; }
    public AlbumsViewModel Albums { get; }
    public ImportViewModel Import { get; }
    public SettingsViewModel Settings { get; }
    /// <summary>Background imports and edits, shown in the activity drawer.</summary>
    public JobQueue Jobs { get; }

    public AsyncCommand OpenSettingsCommand { get; }
    public AsyncCommand PlaySelectedCommand { get; }
    public AsyncCommand TrimSelectedCommand { get; }
    public AsyncCommand MergeSelectedCommand { get; }
    /// <summary>Delete: removes the selection from the shown album, or moves it to the trash when a trash is available.</summary>
    public RelayCommand DeleteSelectedCommand { get; }
    public AsyncCommand BackCommand { get; }
    /// <summary>Returns to the shelf (through the leave prompt) and asks the view to focus the search field.</summary>
    public AsyncCommand FocusSearchCommand { get; }

    /// <summary>Keeps the app open while a close waits for jobs to finish ("quit when done").</summary>
    public RelayCommand CancelQuitCommand { get; }
    public bool IsWaitingToQuit => QuitWait is not null;

    /// <summary>The wait of a close for jobs to finish; cancelling it keeps the app open.</summary>
    private CancellationTokenSource? QuitWait
    {
        get => _quitWait;
        set
        {
            _quitWait = value;
            OnPropertyChanged(nameof(IsWaitingToQuit));
            CancelQuitCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Raised when the view should move keyboard focus to the search field.</summary>
    public event Action? SearchFocusRequested;

    public bool IsDrawerOpen { get => _isDrawerOpen; set => SetProperty(ref _isDrawerOpen, value); }
    public bool IsDraggingTapes { get => _isDraggingTapes; set => SetProperty(ref _isDraggingTapes, value); }
    public bool HasLibrary => _model is not null;
    public string EmptyTitle => !HasLibrary ? "A home for your home videos" : Shelf.SearchText.Trim().Length > 0 ? "No matching videos"
        : !Albums.IsAllVideos ? "No videos in this view" : "Your collection starts here";
    public string EmptyMessage => !HasLibrary ? "Choose a library folder, then import your videos. Your originals stay untouched."
        : Shelf.SearchText.Trim().Length > 0 ? "Try another description, tag or chapter, or clear your search to see the collection."
        : !Albums.IsAllVideos ? "Use Add videos to find videos outside this album, or return to All videos."
        : "Import your first videos to start your chronological collection. Your originals stay untouched.";
    public int TotalCount => _shown.Videos.Count;

    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    /// <summary>Names the missing media tools and what they disable; empty when every tool answered.</summary>
    public string ToolWarning { get => _toolWarning; private set { if (SetProperty(ref _toolWarning, value)) OnPropertyChanged(nameof(HasToolWarning)); } }
    public bool HasToolWarning => ToolWarning.Length > 0;
    private bool ToolsReady => _unavailableTools.Count == 0;
    private bool CanEditClips => _model is not null && ToolsReady && IsSettled;
    private System.Windows.Input.ICommand? DeleteTarget => Albums.IsAllVideos ? Shelf.MoveToTrashCommand : Albums.RemoveSelectedCommand;

    /// <summary>Why new work must wait (the library is being switched or the window is closing), or null when it need not.</summary>
    private string? WaitReason => _closing is { IsCompleted: false } ? "The app is closing." : _opening is { IsCompleted: false } ? "A library is still opening." : null;
    private bool IsSettled => WaitReason is null;
    /// <summary>Set when <c>.cache/recovery</c> holds snapshots from a save that could not be rolled back; they are
    /// never deleted automatically.</summary>
    public string RecoveryNotice { get => _recoveryNotice; private set { if (SetProperty(ref _recoveryNotice, value)) OnPropertyChanged(nameof(HasRecoveryNotice)); } }
    public bool HasRecoveryNotice => RecoveryNotice.Length > 0;
    /// <summary>A short confirmation, such as an album drop; clears itself.</summary>
    public string Toast { get => _toast; private set { if (SetProperty(ref _toast, value)) OnPropertyChanged(nameof(HasToast)); } }
    public bool HasToast => Toast.Length > 0;

    /// <summary>The page shown instead of the shelf, or null for the shelf. Change it with <see cref="NavigateAsync"/>.</summary>
    public PageViewModel? Page
    {
        get => _page;
        private set
        {
            var previous = _page;
            if (!SetProperty(ref _page, value)) return;
            if (previous is not null) previous.PropertyChanged -= PagePropertyChanged;
            if (value is not null) value.PropertyChanged += PagePropertyChanged;
            OnPropertyChanged(nameof(HasPage)); OnPropertyChanged(nameof(IsShelfShown)); OnPropertyChanged(nameof(IsChromeShown));
            OnPropertyChanged(nameof(ShowsShellBackKey));
            UpdateCommands();
        }
    }

    /// <summary>Whether the shell's own status bar shows; hidden while a page is immersive (fullscreen video).</summary>
    public bool IsChromeShown => Page is not { IsImmersive: true };

    /// <summary>Whether the shell shows its own ◀ LIBRARY key: for pages without one.</summary>
    public bool ShowsShellBackKey => Page is { HasBackKey: false };

    private void PagePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(PageViewModel.IsImmersive)) OnPropertyChanged(nameof(IsChromeShown));
    }
    public bool HasPage => Page is not null;
    public bool IsShelfShown => Page is null;

    private VideoRecord? FirstSelected() => Shelf.SelectedRecords.FirstOrDefault();

    private TapePreviewSession? CreatePreviewSession(TapeViewModel tape)
    {
        if (_model is not { } model || _services is not { } services || _previews is not { } previews || !ToolsReady || tape.IsMissing) return null;
        return new TapePreviewSession(tape.Record, model.GetVideoPath(tape.Record), services.Files, previews);
    }

    /// <summary>Opens a video's page, optionally starting at <paramref name="at"/> (a matched chapter). When it cannot (the
    /// app is busy switching or closing, or the video or its file is gone) the status line says why.</summary>
    public Task OpenVideoAsync(VideoRecord video, TimeSpan? at = null)
    {
        if (WaitReason is { } reason) return Refuse($"“{video.Label}” cannot open yet. {reason}");
        if (_model is not { } model || _services is not { } services || model.Snapshot.Find(video.Id) is not { } record)
            return Refuse($"“{video.Label}” is no longer in the library.");
        if (record.IsMissing) return Refuse($"The file for “{record.Label}” is missing, so it cannot play.");
        return NavigateAsync(() =>
        {
            var indexed = new IndexedMetadataService(services.Metadata, model);
            indexed.IndexingFailed += warning => Status = warning;
            return new VideoPageViewModel(new VideoPageDependencies
            {
                Record = record, Path = model.GetVideoPath(record), Metadata = indexed, Playback = _dependencies.CreatePlayback(),
                Snapshot = model.Snapshot, SuggestTags = SuggestTags, Session = _videoPageSession, Post = _dependencies.Post,
                StartAt = at ?? TimeSpan.Zero,
                Previews = ToolsReady ? _previews : null,
                Back = () => NavigateAsync(() => null),
                ShowAlbum = async album =>
                {
                    if (await NavigateAsync(() => null)) Albums.Current = Albums.Rows.FirstOrDefault(row => row.Id == album.Id);
                },
                AddToAlbum = album => Albums.AddToAlbumAsync(album.Id, [record.Id]),
            });
        });
    }

    private Task Refuse(string reason)
    {
        Status = reason;
        return Task.CompletedTask;
    }

    public Task OpenClipEditorAsync()
    {
        if (!CanEditClips || _model is not { } model || !Shelf.HasSelection) return Task.CompletedTask;
        var sources = Shelf.SelectedRecords.Select(record => record.ToLibraryVideo()).ToArray();
        return NavigateAsync(() => new ClipEditorPage(new ClipEditorViewModel(sources, _dependencies.CreateClipEditing(model.Catalog),
            outputs => PublishOutputsAsync(model, outputs))));
    }

    /// <summary>Opens the merge page for the selected videos, in shelf order.</summary>
    public Task OpenMergeAsync()
    {
        if (!CanEditClips || _model is not { } model || Shelf.SelectedCount < 2) return Task.CompletedTask;
        var sources = Shelf.SelectedRecords.Select(record => record.ToLibraryVideo()).ToArray();
        return NavigateAsync(() => new MergePageViewModel(sources, _dependencies.CreateClipEditing(model.Catalog), Jobs,
            outputs => PublishOutputsAsync(model, outputs), () => NavigateAsync(() => null)));
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

    private async Task FocusSearchAsync()
    {
        if (!await NavigateAsync(() => null)) return;
        IsDrawerOpen = true;
        SearchFocusRequested?.Invoke();
    }

    /// <summary>Leaves the current page (asking about unsaved changes) and shows <paramref name="next"/>'s page, or the
    /// shelf when it returns null. Returns false when the current page stays. Navigations run one at a time, so two
    /// prompts never stack; a page must not navigate from its own <see cref="PageViewModel.LoadAsync"/>.</summary>
    public async Task<bool> NavigateAsync(Func<PageViewModel?> next)
    {
        await _navigation.WaitAsync();
        try
        {
            if (Page is { } current)
            {
                if (!await current.CanLeaveAsync(_dependencies.Interaction)) return false;
                Page = null;
                await current.DisposeAsync();
            }
            if (next() is not { } page) return true;
            Page = page;
            try { await page.LoadAsync(); }
            catch
            {
                Page = null;
                await page.DisposeAsync();
                throw;
            }
            return true;
        }
        finally { _navigation.Release(); }
    }

    /// <summary>Everything that must happen before the window closes, in order: waits for a library switch; asks whether
    /// to wait for or cancel background jobs (only a question, so keeping the app open there changes nothing); settles
    /// the page (finishing a save) and leaves it, asking about unsaved changes; cancels or waits for the jobs; then stops
    /// the library refresh and releases the library. Returns false when the user keeps the app open, including by
    /// <see cref="CancelQuitCommand"/> while waiting for jobs. Repeated calls while one is running share it; new work is
    /// refused meanwhile.</summary>
    public Task<bool> RequestCloseAsync()
    {
        if (_closing is not { IsCompleted: false })
        {
            _closing = CloseAsync();
            UpdateCommands();
        }
        return UpdateCommandsAfterAsync(_closing);
    }

    private async Task<bool> CloseAsync()
    {
        var status = Status;
        try
        {
            if (_opening is { } opening) await opening;
            var jobs = Jobs.HasActive ? await _dependencies.Interaction.AskCloseWithJobsAsync(Jobs.ActiveCount) : CloseChoice.WaitForJobs;
            if (jobs == CloseChoice.KeepOpen) return false;
            if (Page is { } page)
            {
                Status = "Finishing the current operation, then closing…";
                await page.SettleForCloseAsync();
            }
            if (!await NavigateAsync(() => null)) return false;
            if (jobs == CloseChoice.CancelJobs)
            {
                Status = "Cancelling imports and edits, then closing…";
                await Jobs.CancelAllAndWaitAsync();
            }
            else if (!await WaitForJobsAsync()) return false;
            status = Status;
            await ReleaseLibraryAsync();
            return true;
        }
        finally { Status = status; }
    }

    /// <summary>Waits for the active jobs to end; false when the user cancels the quit meanwhile.</summary>
    private async Task<bool> WaitForJobsAsync()
    {
        if (!Jobs.HasActive) return true;
        Status = "Closing when the remaining imports and edits finish…";
        using var quit = new CancellationTokenSource();
        QuitWait = quit;
        try
        {
            await Jobs.WhenIdleAsync().WaitAsync(quit.Token);
            return true;
        }
        catch (OperationCanceledException) when (quit.IsCancellationRequested) { return false; }
        finally { QuitWait = null; }
    }

    /// <summary>Awaits a library switch or close and then re-checks the commands it held off: only once it has
    /// completed does <see cref="IsSettled"/> read true.</summary>
    private async Task<T> UpdateCommandsAfterAsync<T>(Task<T> flow)
    {
        try { return await flow; }
        finally { UpdateCommands(); }
    }

    private async Task UpdateCommandsAfterAsync(Task flow)
    {
        try { await flow; }
        finally { UpdateCommands(); }
    }

    /// <summary>Stops work on the open library and gives up its lock. The lock is released even if stopping fails.</summary>
    private async Task ReleaseLibraryAsync()
    {
        try { await Settings.StopIndexAsync(); }
        finally
        {
            if (_model is { } model) model.Changed -= OnModelChanged;
            _lock?.Dispose(); _lock = null;
        }
    }

    /// <summary>Applies the saved settings and reopens the saved library.</summary>
    public async Task RestoreLibraryAsync()
    {
        try
        {
            await Settings.LoadAsync();
            var root = await _dependencies.Settings.LoadRootAsync();
            if (root is not null)
            {
                if (!Directory.Exists(root))
                    throw new DirectoryNotFoundException("The saved library folder is unavailable. Reconnect its drive or choose another folder.");
                await OpenLibraryAsync(root);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { Status = exception.Message; }
    }

    /// <summary>Opens (and if needed migrates) a library. The new library is locked (refused if another instance has
    /// it), initialized and loaded first; a failure there leaves the current library as it was. Then the open page is
    /// left through the leave prompt (keeping it releases the new lock), and only then is the old library released and
    /// the background refresh started. Reopening the open library just refreshes it. Background jobs must have
    /// finished, and new work waits until the switch completes. The returned task never faults: failures go to the
    /// status line.</summary>
    public Task OpenLibraryAsync(string root)
    {
        if (!IsSettled) return Task.CompletedTask;
        if (Jobs.HasActive)
        {
            Status = "Wait for the running imports and edits to finish (see Activity), then switch libraries.";
            return Task.CompletedTask;
        }
        _opening = SwitchLibraryAsync(root);
        UpdateCommands();
        return UpdateCommandsAfterAsync(_opening);
    }

    private async Task SwitchLibraryAsync(string root)
    {
        try { await SwitchLibraryCoreAsync(root); }
        catch (Exception exception)
        {
            Status = "The library could not be opened: " + exception.Message;
            AppErrors.Report(exception, "Open library");
        }
    }

    private async Task SwitchLibraryCoreAsync(string root)
    {
        var paths = new LibraryPaths(root);
        if (_lock is { } held && string.Equals(held.Paths.RootDirectory, paths.RootDirectory,
                OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
        {
            await Settings.RefreshIndexAsync();
            return;
        }
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
            try { await LibraryPublisher.RecoverAsync(catalog, owner); }
            catch (Exception exception) { Trace.TraceWarning($"Could not finish interrupted publications: {exception}"); }
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

        try
        {
            if (!await NavigateAsync(() => null))
            {
                owner.Dispose();
                Status = "The library was not switched; the open page has work to finish or save first.";
                return;
            }
            await ReleaseLibraryAsync();
        }
        catch
        {
            owner.Dispose();
            throw;
        }
        _lock = owner; _model = model; _services = services; _previews = new PreviewCache(paths, services.Files);
        _unavailableTools = unavailable;
        ToolWarning = DescribeUnavailable(unavailable);
        Settings.ToolHealth = unavailable.Count == 0 ? "FFmpeg and MKVToolNix are installed." : ToolWarning;
        RecoveryNotice = recoverySnapshots == 0 ? "" :
            $"{recoverySnapshots} metadata recovery snapshot(s) are in {MatroskaMetadataService.RecoveryDirectory(paths)}. " +
            "A save could not restore its video's previous metadata; the README explains how to restore it by hand.";
        Settings.LibraryRoot = paths.RootDirectory;
        _shown = LibrarySnapshot.Empty;
        Shelf.ClearSelection();
        Shelf.SearchText = "";
        Albums.Current = null;
        OnPropertyChanged(nameof(HasLibrary)); OnPropertyChanged(nameof(Model));
        model.Changed += OnModelChanged;
        RefreshView();
        Status = $"{load.Videos} videos in your library" + (load.Pending > 0 ? $" · reading {load.Pending} for the first time…" : "")
            + string.Concat(load.Issues.Select(issue => Environment.NewLine + issue));
        _ = Settings.RefreshIndexAsync();
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

    /// <summary>Checks file stamps when the window regains focus (at most every 10 s) and reads what changed.</summary>
    public async Task WindowActivatedAsync()
    {
        if (_model is not { } model || Settings.IsIndexing || !IsSettled || !ToolsReady
            || DateTime.UtcNow - _lastActivationStat < ActivationStatInterval) return;
        _lastActivationStat = DateTime.UtcNow;
        try
        {
            if (await Task.Run(() => model.StatPassAsync()) > 0 && model == _model) await Settings.RefreshIndexAsync();
        }
        catch (Exception exception) { Trace.TraceWarning($"Activation refresh failed: {exception}"); }
    }

    /// <summary>Shows an unexpected error reported by <see cref="AppErrors"/>.</summary>
    public void ShowError(string message) => _dependencies.Post(() => Status = message);

    // The model publishes snapshots on its writer thread; this is the single hop onto the UI thread.
    private void OnModelChanged(LibraryDelta delta) => _dependencies.Post(() =>
    {
        // Deltas queue up while the UI is busy; the first posted refresh shows the latest snapshot and the rest find
        // nothing new.
        if (_model is { } model && !ReferenceEquals(model.Snapshot, _shown)) RefreshView();
    });

    /// <summary>Shows the model's current snapshot in every part of the window.</summary>
    private void RefreshView()
    {
        var snapshot = _model?.Snapshot ?? LibrarySnapshot.Empty;
        _shown = snapshot;
        Albums.Apply(snapshot);
        Shelf.Apply(snapshot, Albums.Filter);
        (Page as VideoPageViewModel)?.OnLibraryChanged(snapshot);
        OnPropertyChanged(nameof(Tags)); OnPropertyChanged(nameof(TotalCount));
    }

    private void NotifyViewState()
    {
        foreach (var name in new[] { nameof(EmptyTitle), nameof(EmptyMessage) })
            OnPropertyChanged(name);
    }

    private void UpdateCommands()
    {
        Import.UpdateCommands();
        Settings.UpdateCommands();
        foreach (var command in new[] { PlaySelectedCommand, TrimSelectedCommand, MergeSelectedCommand, BackCommand })
            command.RaiseCanExecuteChanged();
        DeleteSelectedCommand.RaiseCanExecuteChanged();
    }

    private async Task OpenJobOutputAsync(JobViewModel job)
    {
        if (job.Outcome?.Outputs.FirstOrDefault() is not { } id) return;
        if (_model?.Snapshot.Find(id) is { } record) await OpenVideoAsync(record);
        else Status = $"The video from “{job.Title}” is no longer in the library.";
    }

    /// <summary>A failure is announced at once; successes wait for the lane's summary.</summary>
    private void JobFinished(JobViewModel job)
    {
        if (!job.HasFailed) return;
        ShowToast($"{job.Title}: {job.StatusText}");
        Play(ClickSoundKind.Rubber);
    }

    /// <summary>One toast and one chime per batch of jobs, for example "Imported 48 · 2 failed".</summary>
    private void LaneDrained(JobBatch batch)
    {
        if (batch.Succeeded + batch.Failed == 0) return;
        var done = (batch.Lane == JobLane.Import ? "Imported " : "Rendered ") + batch.Succeeded;
        ShowToast(batch.Failed == 0 ? done : $"{done} · {batch.Failed} failed");
        if (batch.Succeeded > 0) Play(ClickSoundKind.Chime);
    }

    private void Play(ClickSoundKind sound)
    {
        if (_dependencies.Sounds.IsEnabled) _dependencies.Sounds.Play(sound);
    }

    /// <summary>Shows <paramref name="message"/> as a toast (and on the status line) for a few seconds; a newer toast
    /// replaces it and restarts the time.</summary>
    private void ShowToast(string message)
    {
        var shown = _shownToast = new object();
        Toast = message; Status = message;
        _ = HideToastAsync(shown);
    }

    private async Task HideToastAsync(object shown)
    {
        await Task.Delay(ToastDuration, _dependencies.Time);
        // An older toast's delay can end after a newer toast is shown; only the newest one's clears it.
        if (ReferenceEquals(_shownToast, shown)) Toast = "";
    }
}
