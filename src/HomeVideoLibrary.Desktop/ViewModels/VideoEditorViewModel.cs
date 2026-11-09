using System.Collections.ObjectModel;
using System.Security.Cryptography;
using Avalonia.Threading;
using HomeVideoLibrary.Core;
using HomeVideoLibrary.Desktop.Playback;
using HomeVideoLibrary.Infrastructure;
using LibVLCSharp.Shared;

namespace HomeVideoLibrary.Desktop.ViewModels;

public sealed class VideoEditorViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly LibraryVideo _video;
    private readonly string _path;
    private readonly IVideoMetadataService _metadata;
    private readonly IVideoPlayback _playback;
    private readonly VideoMetadata? _initialMetadata;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<string> _capturedImages = new(StringComparer.Ordinal);
    // The last loaded or saved state; IsDirty is the difference between it and the fields.
    private VideoMetadata _baseline = VideoMetadata.Empty;
    private CancellationTokenSource? _operationCancellation;
    private string _title = "", _description = "", _tags = "", _recordedAt = "", _status = "Loading video…";
    private bool _isDirty, _isBusy, _isPlaying, _metadataLoaded;
    private bool _isInspectorOpen = true;
    private double _position, _seekPosition, _volume = 80;
    private MediaPlayer? _player;

    /// <param name="initialMetadata">Cached metadata to show before the MKV read completes.</param>
    public VideoEditorViewModel(LibraryVideo video, string path, IVideoMetadataService metadata, IVideoPlayback playback,
        VideoMetadata? initialMetadata = null)
    {
        _video = video; _path = path; _metadata = metadata; _playback = playback; _initialMetadata = initialMetadata;
        _playback.Updated += PlaybackUpdated;
        Chapters.CollectionChanged += (_, _) => RefreshDirty();
        Thumbnails.CollectionChanged += (_, _) => { RefreshDirty(); OnPropertyChanged(nameof(CanCapture)); };
    }

    public ObservableCollection<ChapterEditorViewModel> Chapters { get; } = [];
    public ObservableCollection<ThumbnailEditorViewModel> Thumbnails { get; } = [];
    public string Title { get => _title; set { if (SetProperty(ref _title, value)) RefreshDirty(); } }
    /// <summary>Shown while the MKV has no TITLE; it is never written unless the user types it.</summary>
    public string TitlePlaceholder => _video.DisplayName;

    /// <summary>Whether Save closes the player first. An in-place mkvpropedit write is safe under an open reader on macOS and
    /// Linux; Windows sharing rules for that are unverified, so the player is released there.</summary>
    public bool ReleasePlaybackDuringSave { get; init; } = OperatingSystem.IsWindows();
    public string Description { get => _description; set { if (SetProperty(ref _description, value)) RefreshDirty(); } }
    public string Tags { get => _tags; set { if (SetProperty(ref _tags, value)) RefreshDirty(); } }
    public string RecordedAt { get => _recordedAt; set { if (SetProperty(ref _recordedAt, value)) RefreshDirty(); } }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public bool IsDirty { get => _isDirty; private set => SetProperty(ref _isDirty, value); }
    public bool IsBusy { get => _isBusy; private set { SetProperty(ref _isBusy, value); OnPropertyChanged(nameof(CanCapture)); OnPropertyChanged(nameof(CanEdit)); } }
    /// <summary>Editing needs a successful read: saving without it would drop the file's thumbnails and chapters.</summary>
    public bool CanEdit => _metadataLoaded && !IsBusy;
    public bool IsPlaying { get => _isPlaying; private set { SetProperty(ref _isPlaying, value); OnPropertyChanged(nameof(PlayLabel)); } }
    public string PlayLabel => IsPlaying ? "Pause" : "Play";
    public bool IsInspectorOpen { get => _isInspectorOpen; set => SetProperty(ref _isInspectorOpen, value); }
    public double Duration => _video.Duration.TotalSeconds;
    public double Position { get => _position; private set { SetProperty(ref _position, value); OnPropertyChanged(nameof(TimeLabel)); } }
    public double SeekPosition { get => _seekPosition; set => SetProperty(ref _seekPosition, value); }
    public bool IsSeeking { get; set; }
    public double Volume { get => _volume; set => SetProperty(ref _volume, value); }
    public string TimeLabel => $"{TimeSpan.FromSeconds(Position):hh\\:mm\\:ss} / {_video.Duration:hh\\:mm\\:ss}";
    public bool CanCapture => CanEdit && Thumbnails.Count < 20;
    public MediaPlayer? Player { get => _player; private set => SetProperty(ref _player, value); }

    /// <summary>Starts playback and the metadata read together. Editing is enabled as soon as the read succeeds; playback
    /// neither waits for the read nor holds up editing.</summary>
    public async Task LoadAsync()
    {
        using var operation = BeginOperation();
        IsBusy = true;
        if (_initialMetadata is { } initial) ApplyBaseline(initial);
        var playback = OpenPlaybackAsync(TimeSpan.Zero, true, operation.Token);
        try
        {
            ApplyBaseline(await _metadata.ReadAsync(_video, operation.Token));
            _metadataLoaded = true;
            Status = "M adds a chapter at the current position. Save writes changes into this MKV.";
        }
        catch (OperationCanceledException) { Status = "Loading cancelled. Return to the library to reopen this video."; }
        catch (Exception exception) { Status = "Details could not be read, so editing is off: " + exception.Message; }
        finally { _operationCancellation = null; IsBusy = false; }
        try { await playback; }
        catch (OperationCanceledException) { }
        catch (Exception exception) { Status = _metadataLoaded ? exception.Message : Status + " " + exception.Message; }
    }

    /// <summary>Makes <paramref name="metadata"/> the unedited state. Rows are matched by chapter UID and thumbnail id and
    /// only changed fields are touched, so unchanged rows keep their controls, focus and decoded images.</summary>
    private void ApplyBaseline(VideoMetadata metadata)
    {
        _baseline = metadata;
        Title = metadata.Title;
        Description = metadata.Description;
        var tags = string.Join(Environment.NewLine, metadata.Tags);
        if (Tags != tags) Tags = tags;
        RecordedAt = RecordedDate.FormatForEditing(metadata);

        foreach (var stale in Chapters.Where(row => metadata.Chapters.All(chapter => chapter.Uid != row.Uid)).ToArray()) Chapters.Remove(stale);
        for (var index = 0; index < metadata.Chapters.Count; index++)
        {
            var chapter = metadata.Chapters[index];
            if (Chapters.FirstOrDefault(row => row.Uid == chapter.Uid) is not { } row)
            {
                Chapters.Insert(index, new ChapterEditorViewModel(chapter, RefreshDirty));
                continue;
            }
            row.Reset(chapter);
            if (Chapters.IndexOf(row) is var current && current != index) Chapters.Move(current, index);
        }

        foreach (var stale in Thumbnails.Where(row => metadata.Thumbnails.All(thumbnail => thumbnail.Id != row.Thumbnail.Id)).ToArray())
        {
            Thumbnails.Remove(stale);
            stale.Dispose();
        }
        for (var index = 0; index < metadata.Thumbnails.Count; index++)
        {
            var thumbnail = metadata.Thumbnails[index];
            if (Thumbnails.FirstOrDefault(row => row.Thumbnail.Id == thumbnail.Id) is not { } row)
                Thumbnails.Insert(index, new ThumbnailEditorViewModel(thumbnail));
            else if (Thumbnails.IndexOf(row) is var current && current != index) Thumbnails.Move(current, index);
        }
        RefreshDirty();
    }

    private void RefreshDirty() => IsDirty = !MatchesBaseline();

    private bool MatchesBaseline() =>
        Title == _baseline.Title && Description == _baseline.Description && ParseTags(Tags).SequenceEqual(_baseline.Tags) &&
        RecordedAt == RecordedDate.FormatForEditing(_baseline) &&
        Chapters.Count == _baseline.Chapters.Count &&
        Chapters.All(row => _baseline.Chapters.FirstOrDefault(chapter => chapter.Uid == row.Uid) is { } chapter && row.Matches(chapter)) &&
        Thumbnails.Select(row => row.Thumbnail.Id).SequenceEqual(_baseline.Thumbnails.Select(thumbnail => thumbnail.Id));

    /// <summary>Binds a new player to the view before it starts, so LibVLC never opens its own window.</summary>
    private async Task OpenPlaybackAsync(TimeSpan position, bool play, CancellationToken cancellationToken)
    {
        Player = await _playback.CreatePlayerAsync();
        try { await _playback.StartAsync(_path, position, play, cancellationToken); }
        catch
        {
            await ClosePlaybackAsync();
            throw;
        }
        await _playback.SetVolumeAsync((int)Volume);
        IsPlaying = play;
    }

    /// <summary>Unbinds the player from the view, then releases it.</summary>
    private async Task ClosePlaybackAsync()
    {
        Player = null;
        IsPlaying = false;
        await _playback.CloseAsync();
    }

    public async Task TogglePlaybackAsync() => await WithErrorAsync(async () =>
    {
        if (Player is null) await OpenPlaybackAsync(TimeSpan.FromSeconds(Position), true, _lifetime.Token);
        else { var play = !IsPlaying; await _playback.SetPlayingAsync(play); IsPlaying = play; }
    });

    public async Task SeekAsync(double seconds) => await WithErrorAsync(async () =>
    {
        Position = ClampSeek(seconds, Duration); SeekPosition = Position;
        await _playback.SeekAsync(TimeSpan.FromSeconds(Position));
    });

    public Task ApplyVolumeAsync() => WithErrorAsync(() => _playback.SetVolumeAsync((int)Volume));
    public void AddChapter()
    {
        if (!CanEdit) return;
        Span<byte> bytes = stackalloc byte[8];
        ulong uid;
        do { RandomNumberGenerator.Fill(bytes); uid = BitConverter.ToUInt64(bytes); } while (uid == 0 || Chapters.Any(chapter => chapter.Uid == uid));
        Chapters.Add(new ChapterEditorViewModel(new VideoChapter(uid, TimeSpan.FromSeconds(ClampSeek(Position, Duration)), "New chapter", "", []), RefreshDirty));
        Status = "Chapter added. Edit its title and description, then Save changes.";
    }

    public void RemoveChapter(ChapterEditorViewModel chapter) { if (!IsBusy) Chapters.Remove(chapter); }
    public void RemoveThumbnail(ThumbnailEditorViewModel thumbnail) { if (!IsBusy && Thumbnails.Remove(thumbnail)) thumbnail.Dispose(); }
    public async Task CaptureAsync()
    {
        if (!CanCapture) return;
        using var operation = BeginOperation();
        IsBusy = true;
        try
        {
            var thumbnail = await _metadata.CaptureThumbnailAsync(_video, TimeSpan.FromSeconds(ClampSeek(Position, Duration)), operation.Token);
            _capturedImages.Add(thumbnail.ImagePath);
            Thumbnails.Add(new ThumbnailEditorViewModel(thumbnail));
            Status = "Thumbnail chosen. Save changes to embed it in the MKV.";
        }
        catch (OperationCanceledException) { Status = "Thumbnail capture cancelled."; }
        catch (Exception exception) { Status = exception.Message; }
        finally { _operationCancellation = null; IsBusy = false; }
    }

    public async Task SaveAsync()
    {
        if (!CanEdit) return;
        using var operation = BeginOperation();
        IsBusy = true;
        var position = TimeSpan.FromSeconds(Position);
        var resume = IsPlaying;
        var released = false;
        var saved = false;
        try
        {
            var metadata = BuildMetadata();
            if (ReleasePlaybackDuringSave)
            {
                await ClosePlaybackAsync(); // Release native file handles before the write.
                released = true;
            }
            await _metadata.SaveAsync(_video, metadata, operation.Token);
            saved = true;
            // The save verified that the file now reads back as exactly this.
            ApplyBaseline(MetadataText.AsStored(metadata));
            Status = "Changes saved inside the MKV.";
        }
        catch (OperationCanceledException) { Status = "Save cancelled; changes remain unsaved."; }
        catch (Exception exception) { Status = exception.Message; }
        finally
        {
            _operationCancellation = null;
            if (released && !_lifetime.IsCancellationRequested)
            {
                try { await OpenPlaybackAsync(position, resume, _lifetime.Token); }
                catch (Exception exception) { Status = (saved ? "Changes saved. " : "Changes remain unsaved. ") + exception.Message; }
            }
            IsBusy = false;
        }
    }

    /// <summary>Builds and checks the metadata to save without touching playback or the file.</summary>
    /// <exception cref="ArgumentException">A field is invalid; the message names it.</exception>
    private VideoMetadata BuildMetadata()
    {
        var recordedAt = _baseline.RecordedAt;
        var recordedAtText = _baseline.RecordedAtText;
        if (RecordedAt != RecordedDate.FormatForEditing(_baseline))
        {
            recordedAtText = RecordedAt.Trim();
            if (recordedAtText.Length == 0) recordedAt = null;
            else if (RecordedDate.TryParse(recordedAtText, out var parsed)) recordedAt = parsed;
            else throw new ArgumentException("Recorded date must look like 1997, 1997-07 or 1997-07-03.");
        }
        var chapters = Chapters.Select(chapter => chapter.ToChapter()).ToArray();
        if (chapters.FirstOrDefault(chapter => chapter.Position >= _video.Duration) is { } outside)
            throw new ArgumentException($"Chapter \"{outside.Title}\" starts after the end of the video.");
        return new VideoMetadata(Title, Description, ParseTags(Tags), recordedAt, chapters,
            Thumbnails.Select(thumbnail => thumbnail.Thumbnail).ToArray()) { RecordedAtText = recordedAtText };
    }

    public async Task SeekToChapterAsync(ChapterEditorViewModel chapter)
    {
        if (chapter.StartError is { } error) Status = error;
        else await SeekAsync(chapter.Start.TotalSeconds);
    }

    private CancellationTokenSource BeginOperation() => _operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
    public void CancelOperation() => _operationCancellation?.Cancel();

    public static double ClampSeek(double seconds, double duration) => double.IsFinite(seconds) && duration > 0
        ? Math.Clamp(seconds, 0, Math.Max(0, duration - 0.001)) : 0;
    public static bool ShouldAddMarker(bool isM, bool hasModifiers, bool textInputFocused, bool busy) => isM && !hasModifiers && !textInputFocused && !busy;
    /// <summary>One tag per line or comma; duplicates differing only in case are dropped.</summary>
    public static IReadOnlyList<string> ParseTags(string text) => MetadataText.NormalizeTags([text]);
    private async Task WithErrorAsync(Func<Task> action)
    {
        if (IsBusy) return;
        try { await action(); }
        catch (Exception exception) { Status = exception.Message; }
    }
    private void PlaybackUpdated(object? sender, PlaybackUpdate update) => Dispatcher.UIThread.Post(() =>
    {
        if (_lifetime.IsCancellationRequested || update.Version != _playback.Version) return;
        if (update.Milliseconds is { } time)
        {
            Position = ClampSeek(time / 1000d, Duration);
            if (!IsSeeking) SeekPosition = Position;
        }
        if (update.IsPlaying is { } playing) IsPlaying = playing;
        if (update.Error is { } error) Status = error;
    });
    public void Cancel() => _lifetime.Cancel();
    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        _playback.Updated -= PlaybackUpdated;
        await ClosePlaybackAsync();
        await _playback.DisposeAsync();
        foreach (var thumbnail in Thumbnails) thumbnail.Dispose();
        PruneUnsavedCaptures();
        _lifetime.Dispose();
    }

    /// <summary>Deletes frames captured in this session that the MKV never received.</summary>
    private void PruneUnsavedCaptures()
    {
        var kept = _baseline.Thumbnails.Select(thumbnail => thumbnail.ImagePath).ToHashSet(StringComparer.Ordinal);
        foreach (var image in _capturedImages.Where(image => !kept.Contains(image)))
        {
            try { File.Delete(image); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Trace.TraceWarning($"Could not delete unsaved capture {image}: {exception.Message}");
            }
        }
    }
}
