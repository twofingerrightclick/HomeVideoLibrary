using System.Diagnostics;
using Avalonia.Threading;
using HomeVideoLibrary.Core;
using HomeVideoLibrary.Desktop.Playback;
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
    private Task<bool>? _closing;
    private Task<bool>? _navigating;
    private DateTime _lastActivationStat;
    private IReadOnlyList<string> _unavailableTools = [];
    private string _status = "Your original files stay untouched.";
    private string _toolWarning = "", _recoveryNotice = "", _toast = "";
    private bool _isOpening, _isDrawerOpen = true, _isDraggingTapes;
    private PageViewModel? _page;

    public MainWindowViewModel(MainWindowDependencies dependencies)
    {
        _dependencies = dependencies;
        Shelf = new ShelfViewModel(CreatePreviewSession, (record, at) => OpenVideoAsync(record, at));
        Albums = new AlbumsViewModel(() => _model, Shelf, ShowToast, message => Status = message, RefreshView);
        Import = new ImportViewModel(() => _model, dependencies.CreateImporter, dependencies.Interaction, () => ToolsReady && !_isOpening,
            message => Status = message);
        Settings = new SettingsViewModel(dependencies.Settings, dependencies.Sounds, dependencies.Interaction, () => _model, () => ToolsReady,
            OpenLibraryAsync, message => Status = message);
        Shelf.SelectionChanged += UpdateCommands;
        Shelf.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(ShelfViewModel.SearchText)) NotifyViewState(); };
        Import.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(ImportViewModel.IsImporting)) UpdateCommands(); };

        ToggleDrawerCommand = new RelayCommand(() => IsDrawerOpen = !IsDrawerOpen);
        OpenSettingsCommand = new AsyncCommand(_ => dependencies.Interaction.ShowSettingsAsync(Settings), context: "Settings");
        PlaySelectedCommand = new AsyncCommand(_ => FirstSelected() is { } record ? OpenVideoAsync(record) : Task.CompletedTask,
            () => Shelf.HasSelection, context: "Open video");
        TrimSelectedCommand = new AsyncCommand(_ => OpenClipEditorAsync(), () => Shelf.SelectedCount == 1 && CanEditClips, context: "Trim or split");
        MergeSelectedCommand = new AsyncCommand(_ => OpenClipEditorAsync(), () => Shelf.SelectedCount >= 2 && CanEditClips, context: "Merge");
        BackCommand = new AsyncCommand(_ => NavigateAsync(() => null), () => Page is not null, context: "Back to library");
        FocusSearchCommand = new AsyncCommand(_ => FocusSearchAsync(), context: "Search");
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

    public RelayCommand ToggleDrawerCommand { get; }
    public AsyncCommand OpenSettingsCommand { get; }
    public AsyncCommand PlaySelectedCommand { get; }
    public AsyncCommand TrimSelectedCommand { get; }
    public AsyncCommand MergeSelectedCommand { get; }
    public AsyncCommand BackCommand { get; }
    /// <summary>Returns to the shelf (through the leave prompt) and asks the view to focus the search field.</summary>
    public AsyncCommand FocusSearchCommand { get; }

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
    public string LibraryCount => $"{Shelf.Tapes.Count} {(Shelf.Tapes.Count == 1 ? "video" : "videos")} · newest first";
    public int TotalCount => _shown.Videos.Count;

    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    /// <summary>Names the missing media tools and what they disable; empty when every tool answered.</summary>
    public string ToolWarning { get => _toolWarning; private set { if (SetProperty(ref _toolWarning, value)) OnPropertyChanged(nameof(HasToolWarning)); } }
    public bool HasToolWarning => ToolWarning.Length > 0;
    private bool ToolsReady => _unavailableTools.Count == 0;
    private bool CanEditClips => _model is not null && ToolsReady && !Import.IsImporting;
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
            if (!SetProperty(ref _page, value)) return;
            OnPropertyChanged(nameof(HasPage)); OnPropertyChanged(nameof(IsShelfShown));
            UpdateCommands();
        }
    }
    public bool HasPage => Page is not null;
    public bool IsShelfShown => Page is null;

    private VideoRecord? FirstSelected() => Shelf.SelectedRecords.FirstOrDefault();

    private TapePreviewSession? CreatePreviewSession(TapeViewModel tape)
    {
        if (_model is not { } model || _services is not { } services || _previews is not { } previews || !ToolsReady || tape.IsMissing) return null;
        return new TapePreviewSession(tape.Record, model.GetVideoPath(tape.Record), services.Files, previews);
    }

    /// <summary>Opens a video's page, optionally starting at <paramref name="at"/> (a matched chapter).</summary>
    public Task OpenVideoAsync(VideoRecord video, TimeSpan? at = null)
    {
        if (_model is not { } model || _services is not { } services) return Task.CompletedTask;
        if (model.Snapshot.Find(video.Id) is not { } record) return Task.CompletedTask;
        if (record.IsMissing) { Status = $"The file for “{record.Label}” is missing, so it cannot play."; return Task.CompletedTask; }
        return NavigateAsync(() =>
        {
            var indexed = new IndexedMetadataService(services.Metadata, model);
            indexed.IndexingFailed += warning => Status = warning;
            return new VideoEditorPage(new VideoEditorViewModel(record.ToLibraryVideo(), model.GetVideoPath(record), indexed,
                _dependencies.CreatePlayback(), record.ToMetadata()));
        });
    }

    public Task OpenClipEditorAsync()
    {
        if (!CanEditClips || _model is not { } model || !Shelf.HasSelection) return Task.CompletedTask;
        var sources = Shelf.SelectedRecords.Select(record => record.ToLibraryVideo()).ToArray();
        return NavigateAsync(() => new ClipEditorPage(new ClipEditorViewModel(sources, _dependencies.CreateClipEditing(model.Catalog),
            outputs => PublishOutputsAsync(model, outputs))));
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
    /// shelf when it returns null. Returns false when the current page stays. A request made while another is still
    /// asking waits for it, so two prompts never stack.</summary>
    public async Task<bool> NavigateAsync(Func<PageViewModel?> next)
    {
        while (_navigating is { IsCompleted: false } running) await running;
        var navigation = _navigating = NavigateCoreAsync(next);
        return await navigation;
    }

    private async Task<bool> NavigateCoreAsync(Func<PageViewModel?> next)
    {
        if (Page is { } current)
        {
            if (!await current.CanLeaveAsync(_dependencies.Interaction)) return false;
            Page = null;
            await current.DisposeAsync();
        }
        var page = next();
        Page = page;
        if (page is not null) await page.LoadAsync();
        return true;
    }

    /// <summary>Everything that must happen before the window closes: cancels an import, settles the page (cancelling a
    /// clip edit, finishing a save), asks about unsaved changes, stops the library refresh and releases the library.
    /// Returns false when the user chose to keep editing. Repeated calls while one is running share it.</summary>
    public Task<bool> RequestCloseAsync() => _closing is { IsCompleted: false } running ? running : _closing = CloseAsync();

    private async Task<bool> CloseAsync()
    {
        await Import.CancelAndWaitAsync();
        if (Page is { } page)
        {
            Status = "Finishing the current operation, then closing…";
            await page.SettleForCloseAsync();
        }
        if (!await NavigateAsync(() => null)) return false;
        await ReleaseLibraryAsync();
        return true;
    }

    /// <summary>Stops work on the open library and gives up its lock.</summary>
    private async Task ReleaseLibraryAsync()
    {
        await Settings.StopIndexAsync();
        if (_model is { } model) model.Changed -= OnModelChanged;
        _lock?.Dispose(); _lock = null;
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

    /// <summary>Opens (and if needed migrates) a library. Nothing about the current library changes unless the new one
    /// is free (not open in another instance), initializes and loads; only then is the old one released and the
    /// background refresh started. Reopening the open library just refreshes it. A page must be closed first.</summary>
    public async Task OpenLibraryAsync(string root)
    {
        if (_isOpening || Import.IsImporting || Page is not null) return;
        var paths = new LibraryPaths(root);
        if (_lock is { } held && string.Equals(held.Paths.RootDirectory, paths.RootDirectory,
                OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
        {
            await Settings.RefreshIndexAsync();
            return;
        }
        _isOpening = true;
        UpdateCommands();
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
        finally { _isOpening = false; UpdateCommands(); }

        await ReleaseLibraryAsync();
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
        if (_model is not { } model || Settings.IsIndexing || _isOpening || Import.IsImporting || !ToolsReady
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
        NotifyViewState();
        OnPropertyChanged(nameof(Tags)); OnPropertyChanged(nameof(TotalCount));
    }

    private void NotifyViewState()
    {
        foreach (var name in new[] { nameof(LibraryCount), nameof(EmptyTitle), nameof(EmptyMessage) })
            OnPropertyChanged(name);
    }

    private void UpdateCommands()
    {
        Import.UpdateCommands();
        Settings.UpdateCommands();
        foreach (var command in new[] { PlaySelectedCommand, TrimSelectedCommand, MergeSelectedCommand, BackCommand })
            command.RaiseCanExecuteChanged();
    }

    /// <summary>Shows <paramref name="message"/> as a toast (and on the status line); completes when the toast hides.</summary>
    public Task ShowToastAsync(string message)
    {
        Toast = message; Status = message;
        return HideToastAsync(message);
    }

    private void ShowToast(string message) => _ = ShowToastAsync(message);

    private async Task HideToastAsync(string message)
    {
        await Task.Delay(ToastDuration);
        if (Toast == message) Toast = "";
    }
}
