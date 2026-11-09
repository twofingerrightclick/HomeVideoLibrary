using System.ComponentModel;
using System.Windows.Input;
using HomeVideoLibrary.Core;
using HomeVideoLibrary.Desktop.Playback;
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
    /// <summary>Where playback starts (for example a chapter a search matched).</summary>
    public TimeSpan StartAt { get; init; }
    /// <summary>Frames for the scrub bar's hover preview; null shows none.</summary>
    public PreviewCache? Previews { get; init; }
    /// <summary>Returns to the library through the shell's leave flow.</summary>
    public Func<Task> Back { get; init; } = () => Task.CompletedTask;
    /// <summary>Shows an album in the library through the shell's leave flow.</summary>
    public Func<VideoAlbum, Task> ShowAlbum { get; init; } = _ => Task.CompletedTask;
    public Func<VideoAlbum, Task> AddToAlbum { get; init; } = _ => Task.CompletedTask;
    /// <summary>Publishes clips the page created to the library.</summary>
    public Func<IReadOnlyList<LibraryVideo>, Task> OutputsCreated { get; init; } = _ => Task.CompletedTask;
    public Action<string> RevealFile { get; init; } = FileManager.Reveal;
    /// <summary>Whether Save closes the player first. An in-place mkvpropedit write is safe under an open reader on macOS
    /// and Linux; Windows sharing rules for that are unverified, so the player is released there.</summary>
    public bool ReleasePlaybackDuringSave { get; init; } = OperatingSystem.IsWindows();
    /// <summary>Timed waits (preview debounce, idle controls); tests replace it.</summary>
    public Func<TimeSpan, CancellationToken, Task>? Delay { get; init; }
}

/// <summary>One open video: playback, its editable details and the inspector around them. The shell creates one per
/// visit and disposes it on leaving, so drafts, the editing row and native playback all end with the visit. Its view is
/// built before <see cref="LoadAsync"/>, so the video surface is bound before playback starts.</summary>
public sealed class VideoPageViewModel : PageViewModel
{
    private readonly VideoPageDependencies _dependencies;
    private readonly VideoPageSession _session;
    private readonly CancellationTokenSource _lifetime = new();
    private LibrarySnapshot _snapshot;
    private string _status = "Loading video…";
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
        Player = new PlayerViewModel(dependencies.Playback, dependencies.Path, record.Duration, dependencies.Post, Report, _session.Volume, _session.IsMuted);
        Details = new VideoDetailsViewModel(record, dependencies.Metadata, dependencies.SuggestTags, () => Player.Position, Player.Seek,
            dependencies.ReleasePlaybackDuringSave ? Player.WhileReleasedAsync : work => work(), Report, _lifetime.Token);
        Details.Tags.ShowPopularFrom(_snapshot.Tags);
        Preview = new ScrubPreviewViewModel(record, dependencies.Path, dependencies.Previews, dependencies.Delay);
        Fullscreen = new FullscreenViewModel(() => Player.IsPlaying, dependencies.Delay);
        Player.PropertyChanged += PlayerChanged;
        Fullscreen.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(FullscreenViewModel.IsFullscreen)) OnPropertyChanged(nameof(IsInspectorShown));
        };
        Details.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(VideoDetailsViewModel.Title)) OnPropertyChanged(nameof(DisplayTitle)); };
        Details.Chapters.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ChaptersPanelViewModel.CanAddChapter)) OnPropertyChanged(nameof(ScrubMarker));
        };
        BackCommand = new AsyncCommand(_ => dependencies.Back(), onError: Report);
        ShowAlbumCommand = new AsyncCommand<VideoAlbum>((album, _) => dependencies.ShowAlbum(album), onError: Report);
        AddToAlbumCommand = new AsyncCommand<VideoAlbum>((album, _) => dependencies.AddToAlbum(album), onError: Report);
        RevealFileCommand = new RelayCommand(() => dependencies.RevealFile(dependencies.Path));
        ToggleInspectorCommand = new RelayCommand(() => IsInspectorOpen = !IsInspectorOpen);
        SelectTabCommand = new RelayCommand<int>(tab => SelectedTab = tab);
    }

    public Guid Id { get; }
    public PlayerViewModel Player { get; }
    public VideoDetailsViewModel Details { get; }
    public ScrubPreviewViewModel Preview { get; }
    public FullscreenViewModel Fullscreen { get; }

    /// <summary>The EDIT tab's content (trim and split); none yet.</summary>
    public object? Edit => null;

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

    /// <summary>Shows the library's record of this video immediately, then starts playback and reads the MKV together:
    /// playback never waits for the read, and editing starts when the read is done.</summary>
    public override async Task LoadAsync()
    {
        var token = _lifetime.Token;
        var playback = Player.OpenAsync(_dependencies.StartAt, true, token);
        await Details.LoadAsync(token);
        if (Details.CanEdit) Report("M adds a chapter at the playhead. Save writes your changes into the video file.");
        try { await playback; }
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
    }

    /// <summary>Waits for a running save, then asks about unsaved changes. Save leaves only when the save succeeded.</summary>
    public override async Task<bool> CanLeaveAsync(IInteraction interaction)
    {
        await Details.SaveCommand.Completion;
        if (!Details.IsDirty) return true;
        switch (await interaction.AskLeaveAsync(DisplayTitle))
        {
            case LeaveChoice.Save:
                if (Details.RecordedDateError is { } error)
                {
                    Report("Recorded date: " + error);
                    return false;
                }
                await Details.SaveCommand.ExecuteAsync();
                return !Details.IsDirty;
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

    public override async ValueTask DisposeAsync()
    {
        if (_lifetime.IsCancellationRequested) return;
        _lifetime.Cancel();
        Fullscreen.Dispose();
        Preview.Dispose();
        await Player.DisposeAsync();
        Details.PruneCaptures();
        Details.Frames.Dispose();
    }

    private void PlayerChanged(object? sender, PropertyChangedEventArgs args)
    {
        switch (args.PropertyName)
        {
            case nameof(PlayerViewModel.Position):
                Details.Chapters.SetPlayhead(Player.Position);
                OnPropertyChanged(nameof(ScrubMarker));
                break;
            case nameof(PlayerViewModel.IsPlaying):
                Fullscreen.PlaybackChanged();
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
