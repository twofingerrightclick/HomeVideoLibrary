using System.ComponentModel;
using System.Windows.Input;
using HomeVideoLibrary.Core;
using HomeVideoLibrary.Desktop.Playback;
using HomeVideoLibrary.Desktop.ViewModels.Editing;
using HomeVideoLibrary.Infrastructure;
using HomeVideoLibrary.UI.Logic;

namespace HomeVideoLibrary.Desktop.ViewModels.VideoPage;

/// <summary>What a video page needs from the shell. The shell builds one per visit.</summary>
public sealed record VideoPageDependencies
{
    public required VideoRecord Record { get; init; }
    /// <summary>The video file's full path.</summary>
    public required string Path { get; init; }
    public required IVideoMetadataService Metadata { get; init; }
    /// <summary>The page's own playback; the page disposes it.</summary>
    public required IVideoPlayback Playback { get; init; }
    /// <summary>The library as the shell last showed it; later ones arrive through <see cref="VideoPageViewModel.OnLibraryChanged"/>.</summary>
    public required LibrarySnapshot Snapshot { get; init; }
    public required TagSuggestionProvider SuggestTags { get; init; }
    public required VideoPageSession Session { get; init; }
    /// <summary>Runs an action on the UI thread later.</summary>
    public required Action<Action> Post { get; init; }
    /// <summary>Trim and split for the EDIT tab, as render jobs that outlive the page; null when the editing tools are
    /// missing.</summary>
    public ClipEdits? Edits { get; init; }
    /// <summary>The app's background jobs; while one reads this video, the page cannot save over it.</summary>
    public JobQueue? Jobs { get; init; }
    /// <summary>Where playback starts (for example a chapter a search matched).</summary>
    public TimeSpan StartAt { get; init; }
    /// <summary>The inspector tab to open on (for example EDIT from TRIM/SPLIT); null keeps the session's last tab.</summary>
    public InspectorTab? StartTab { get; init; }
    /// <summary>Frames for the scrub bar's hover preview; null shows none.</summary>
    public PreviewCache? Previews { get; init; }
    /// <summary>Returns to the library through the shell's leave flow.</summary>
    public Func<Task> Back { get; init; } = () => Task.CompletedTask;
    /// <summary>Shows an album in the library through the shell's leave flow.</summary>
    public Func<VideoAlbum, Task> ShowAlbum { get; init; } = _ => Task.CompletedTask;
    public Func<VideoAlbum, Task> AddToAlbum { get; init; } = _ => Task.CompletedTask;
    public Action<string> RevealFile { get; init; } = FileManager.Reveal;
    /// <summary>Whether Save closes the player first. An in-place mkvpropedit write is safe under an open reader on macOS
    /// and Linux; Windows sharing rules for that are unverified, so the player is released there.</summary>
    public bool ReleasePlaybackDuringSave { get; init; } = OperatingSystem.IsWindows();
    /// <summary>The clock for timed behaviour (preview debounce, idle controls, seek settling); tests replace it.</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;
}

/// <summary>One open video: playback, its editable details and the inspector around them. The shell creates one per
/// visit and disposes it on leaving, so drafts, the editing row and native playback all end with the visit. Its view is
/// built before <see cref="LoadAsync"/>, so the video surface is bound before playback starts.</summary>
public sealed class VideoPageViewModel : PageViewModel, IEditHost
{
    public const string HeldBySaveMessage = "A trim, split or merge of this video is running; save when it finishes";
    private const string UnsavedDetailsMessage = "Save or revert your changes first";

    private readonly VideoPageDependencies _dependencies;
    private readonly VideoPageSession _session;
    private readonly CancellationTokenSource _lifetime = new();
    private LibrarySnapshot _snapshot;
    private string _status = "";
    private Task _opening = Task.CompletedTask;
    private bool _isInspectorOpen;
    private double _inspectorWidth;
    private int _selectedTab;

    public VideoPageViewModel(VideoPageDependencies dependencies)
    {
        _dependencies = dependencies;
        _session = dependencies.Session;
        _snapshot = dependencies.Snapshot;
        _isInspectorOpen = _session.IsInspectorOpen;
        _inspectorWidth = Math.Clamp(_session.InspectorWidth, VideoPageSession.MinInspectorWidth, VideoPageSession.MaxInspectorWidth);
        _selectedTab = _session.SelectedTab;
        var record = dependencies.Record;
        Id = record.Id;
        Player = new PlayerViewModel(dependencies.Playback, dependencies.Path, record.Duration, dependencies.Post, Report, _session.Volume,
            _session.IsMuted, dependencies.Time);
        Details = new VideoDetailsViewModel(record, dependencies.Metadata, dependencies.SuggestTags, () => Player.Position, Player.Seek,
            dependencies.ReleasePlaybackDuringSave ? Player.WhileReleasedAsync : work => work(), Report, () => SaveBlocker, _lifetime.Token);
        Details.Tags.ShowPopularFrom(_snapshot.Tags);
        Preview = new ScrubPreviewViewModel(record, dependencies.Path, dependencies.Previews, dependencies.Time);
        Fullscreen = new FullscreenViewModel(() => Player.IsPlaying, dependencies.Time);
        BackCommand = new AsyncCommand(_ => dependencies.Back(), onError: Report);
        ShowAlbumCommand = new AsyncCommand<VideoAlbum>((album, _) => dependencies.ShowAlbum(album), onError: Report);
        AddToAlbumCommand = new AsyncCommand<VideoAlbum>((album, _) => dependencies.AddToAlbum(album), onError: Report);
        RevealFileCommand = new RelayCommand(() => dependencies.RevealFile(dependencies.Path));
        ToggleInspectorCommand = new RelayCommand(() => IsInspectorOpen = !IsInspectorOpen);
        SelectTabCommand = new RelayCommand<int>(tab => SelectedTab = tab);
        AddChapterCommand = new RelayCommand(AddChapter, () => Details.Chapters.CanAddChapter);
        MarkInCommand = new RelayCommand(() => { Edit?.SetInAtPlayhead(); ShowTab(InspectorTab.Edit); }, () => Edit is not null);
        MarkOutCommand = new RelayCommand(() => { Edit?.SetOutAtPlayhead(); ShowTab(InspectorTab.Edit); }, () => Edit is not null);
        Player.PropertyChanged += PlayerChanged;
        Fullscreen.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(FullscreenViewModel.IsFullscreen)) return;
            IsImmersive = Fullscreen.IsFullscreen;
            OnPropertyChanged(nameof(IsInspectorShown));
            OnPropertyChanged(nameof(IEditHost.IsEditShown));
        };
        Details.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(VideoDetailsViewModel.Title)) OnPropertyChanged(nameof(DisplayTitle));
            if (args.PropertyName == nameof(VideoDetailsViewModel.IsDirty)) OnPropertyChanged(nameof(IEditHost.EditBlocker));
        };
        Details.Chapters.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(ChaptersPanelViewModel.CanAddChapter)) return;
            OnPropertyChanged(nameof(ScrubMarker));
            AddChapterCommand.RaiseCanExecuteChanged();
        };
        if (dependencies.Jobs is { } jobs) jobs.PropertyChanged += JobsChanged;
        // Last: the panel reads the page as its host.
        Edit = dependencies.Edits is { } edits ? new TrimPanelViewModel(edits, this, dependencies.Time) : null;
        if (dependencies.StartTab is { } startTab) ShowTab(startTab);
    }

    /// <summary>The page's toolbar has its own ◀ LIBRARY key (and Esc).</summary>
    public override bool HasBackKey => true;

    public Guid Id { get; }
    public PlayerViewModel Player { get; }
    public VideoDetailsViewModel Details { get; }
    public ScrubPreviewViewModel Preview { get; }
    public FullscreenViewModel Fullscreen { get; }

    /// <summary>The EDIT tab: trim to the in/out range or split at the playhead; null when the editing tools are missing.</summary>
    public TrimPanelViewModel? Edit { get; }

    /// <summary>Why SAVE is unavailable even with changes: an edit job is reading this video's file. Null otherwise.</summary>
    public string? SaveBlocker => _dependencies.Jobs?.Holds(Id) == true ? HeldBySaveMessage : null;

    LibraryVideo IEditHost.Source => (_snapshot.Find(Id) ?? _dependencies.Record).ToLibraryVideo();
    TimeSpan IEditHost.Playhead => Player.Position;
    bool IEditHost.IsPlayheadResting => !Player.IsPlaying;
    bool IEditHost.IsEditShown => SelectedTab == (int)InspectorTab.Edit && IsInspectorShown;
    string? IEditHost.EditBlocker => Details.IsDirty ? UnsavedDetailsMessage : null;

    /// <summary>Move to Trash, when the library offers it; hidden while null.</summary>
    public ICommand? MoveToTrashCommand => null;

    public string DisplayTitle => string.IsNullOrWhiteSpace(Details.Title) ? Details.TitleWatermark : Details.Title;

    /// <summary>The page's status line: the latest playback, save or capture message.</summary>
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    /// <summary>Albums this video is in.</summary>
    public IReadOnlyList<VideoAlbum> Albums => [.. _snapshot.AlbumsContaining(Id)];

    /// <summary>Albums this video could be added to.</summary>
    public IReadOnlyList<VideoAlbum> OtherAlbums => _snapshot.Albums.Where(album => !_snapshot.MembersOf(album.Id).Contains(Id)).ToArray();

    public int SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (!SetProperty(ref _selectedTab, value)) return;
            _session.SelectedTab = value;
            OnPropertyChanged(nameof(ScrubMarker));
            OnPropertyChanged(nameof(IEditHost.IsEditShown));
        }
    }

    public bool IsInspectorOpen
    {
        get => _isInspectorOpen;
        set
        {
            if (!SetProperty(ref _isInspectorOpen, value)) return;
            _session.IsInspectorOpen = value;
            OnPropertyChanged(nameof(IsInspectorShown));
            OnPropertyChanged(nameof(IEditHost.IsEditShown));
        }
    }

    /// <summary>The inspector shows when it is open and the page is not fullscreen.</summary>
    public bool IsInspectorShown => IsInspectorOpen && !Fullscreen.IsFullscreen;

    /// <summary>The inspector's width, clamped to its allowed range.</summary>
    public double InspectorWidth
    {
        get => _inspectorWidth;
        set
        {
            var width = Math.Clamp(value, VideoPageSession.MinInspectorWidth, VideoPageSession.MaxInspectorWidth);
            if (!SetProperty(ref _inspectorWidth, width)) return;
            _session.InspectorWidth = width;
        }
    }

    /// <summary>Where a new chapter would go, marked on the scrub bar while the CHAPTERS tab is open.</summary>
    public TimeSpan? ScrubMarker => SelectedTab == (int)InspectorTab.Chapters && Details.Chapters.CanAddChapter ? Player.Position : null;

    public AsyncCommand BackCommand { get; }
    /// <summary>Parameter: the album to show.</summary>
    public AsyncCommand<VideoAlbum> ShowAlbumCommand { get; }
    /// <summary>Parameter: the album to add this video to.</summary>
    public AsyncCommand<VideoAlbum> AddToAlbumCommand { get; }
    public RelayCommand RevealFileCommand { get; }
    public RelayCommand ToggleInspectorCommand { get; }
    /// <summary>Parameter: the tab index (<see cref="InspectorTab"/>).</summary>
    public RelayCommand<int> SelectTabCommand { get; }

    /// <summary>Adds a chapter at the playhead from anywhere on the page (MARK, M): shows it in the CHAPTERS tab with its
    /// title open for typing. In fullscreen, where the inspector is hidden, the chapter is added untitled and the status
    /// over the video says so.</summary>
    public RelayCommand AddChapterCommand { get; }

    /// <summary>Sets the trim's in point (I) or out point (O) at the playhead from anywhere on the page, and shows it in
    /// the EDIT tab.</summary>
    public RelayCommand MarkInCommand { get; }
    public RelayCommand MarkOutCommand { get; }

    /// <summary>Starts playback and reads the MKV together; the library's record is already shown. Completes when the
    /// read is done and editing can start. Playback is not awaited: the shell navigates one page at a time, and a slow or
    /// stuck start must not hold that up. It belongs to the page and ends with it.</summary>
    public override async Task LoadAsync()
    {
        var token = _lifetime.Token;
        _opening = OpenPlaybackAsync(token);
        await Details.LoadAsync(token);
    }

    private async Task OpenPlaybackAsync(CancellationToken cancellationToken)
    {
        try { await Player.OpenAsync(_dependencies.StartAt, true, cancellationToken); }
        catch (OperationCanceledException) { }
        catch (Exception exception) { Report(exception.Message); }
    }

    /// <summary>Shows a new library snapshot: this video's record (when it changed outside the page), its albums and
    /// the library's tags.</summary>
    public void OnLibraryChanged(LibrarySnapshot snapshot)
    {
        if (ReferenceEquals(snapshot, _snapshot)) return;
        _snapshot = snapshot;
        Details.OnRecordChanged(snapshot.Find(Id));
        Details.Tags.ShowPopularFrom(snapshot.Tags);
        OnPropertyChanged(nameof(Albums));
        OnPropertyChanged(nameof(OtherAlbums));
        OnPropertyChanged(nameof(IEditHost.Source));
    }

    /// <summary>Waits for a running save, then asks about unsaved changes. Save leaves only when the save succeeded.</summary>
    public override async Task<bool> CanLeaveAsync(IInteraction interaction)
    {
        await Details.SaveCommand.Completion;
        Details.CommitOpenEdits();
        if (!Details.IsDirty) return true;
        switch (await interaction.AskLeaveAsync(DisplayTitle))
        {
            case LeaveChoice.Save:
                if (Details.RecordedDateError is { } error)
                {
                    await interaction.ShowErrorAsync("NOT SAVED", "Recorded date: " + error);
                    return false;
                }
                await Details.SaveCommand.ExecuteAsync();
                if (!Details.IsDirty) return true;
                // The prompt has closed; say why the page stayed where it can be seen above the video.
                await interaction.ShowErrorAsync("NOT SAVED", Status);
                return false;
            case LeaveChoice.Discard:
                return true;
            default:
                return false;
        }
    }

    public override Task SettleForCloseAsync()
    {
        Details.Frames.CaptureCommand.Cancel();
        return Task.WhenAll(Details.SaveCommand.Completion, Details.Frames.CaptureCommand.Completion);
    }

    /// <summary>Clears the video surface before anything else (the view must never show a closing player), then lets
    /// the playback start, a save and a frame capture (cancelled) finish, and releases the player, the decoded frames and
    /// the captures the file never received.</summary>
    public override async ValueTask DisposeAsync()
    {
        if (_lifetime.IsCancellationRequested) return;
        _lifetime.Cancel();
        var closing = Player.DisposeAsync().AsTask();
        Details.Frames.CaptureCommand.Cancel();
        try { await Task.WhenAll(_opening, Details.SaveCommand.Completion, Details.Frames.CaptureCommand.Completion); }
        finally
        {
            try { await closing; }
            finally
            {
                Fullscreen.Dispose();
                Preview.Dispose();
                Edit?.Dispose();
                if (_dependencies.Jobs is { } jobs) jobs.PropertyChanged -= JobsChanged;
                Details.PruneCaptures();
                Details.Frames.Dispose();
                _lifetime.Dispose();
            }
        }
    }

    private void AddChapter()
    {
        if (Fullscreen.IsFullscreen)
        {
            if (Details.Chapters.AddAtPlayhead(expand: false) is { } untitled)
                Report($"Chapter added at {Timecode.Format(untitled.Start)}. Name it in CHAPTERS after leaving fullscreen.");
            return;
        }
        ShowTab(InspectorTab.Chapters);
        if (Details.Chapters.AddAtPlayhead(expand: true) is { } added)
            Report($"Chapter added at {Timecode.Format(added.Start)}. Type its title and press Enter.");
    }

    /// <summary>Selects <paramref name="tab"/> and opens the inspector, unless fullscreen hides it.</summary>
    public void ShowTab(InspectorTab tab)
    {
        SelectedTab = (int)tab;
        if (!Fullscreen.IsFullscreen) IsInspectorOpen = true;
    }

    private void JobsChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(JobQueue.ActiveCount)) return;
        OnPropertyChanged(nameof(SaveBlocker));
        Details.SaveCommand.RaiseCanExecuteChanged();
    }

    private void PlayerChanged(object? sender, PropertyChangedEventArgs args)
    {
        switch (args.PropertyName)
        {
            case nameof(PlayerViewModel.Position):
                Details.Chapters.SetPlayhead(Player.Position);
                OnPropertyChanged(nameof(ScrubMarker));
                OnPropertyChanged(nameof(IEditHost.Playhead));
                break;
            case nameof(PlayerViewModel.IsPlaying):
                Fullscreen.PlaybackChanged();
                OnPropertyChanged(nameof(IEditHost.IsPlayheadResting));
                break;
            case nameof(PlayerViewModel.Volume):
                _session.Volume = Player.Volume;
                break;
            case nameof(PlayerViewModel.IsMuted):
                _session.IsMuted = Player.IsMuted;
                break;
        }
    }

    private void Report(string message) => Status = message;
    private void Report(Exception exception) => Status = exception.Message;
}
