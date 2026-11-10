using System.Collections.ObjectModel;
using HomeVideoLibrary.Core;

namespace HomeVideoLibrary.Desktop.ViewModels.VideoPage;

/// <summary>The chosen thumbnails of the open video. Captures are files in the thumbnail cache until a save embeds them;
/// <see cref="Dispose"/> deletes the ones the MKV never received.</summary>
public sealed class FramesViewModel : ViewModelBase, IDisposable
{
    public const int MaxFrames = 20;
    private readonly LibraryVideo _video;
    private readonly IVideoMetadataService _metadata;
    private readonly Func<TimeSpan> _playhead;
    private readonly Action _changed;
    private readonly Action<string> _report;
    private readonly HashSet<string> _captured = new(StringComparer.Ordinal);
    private bool _canEdit;

    /// <param name="seek">Moves playback to a frame.</param>
    /// <param name="changed">Called after the frames change, so the owner can recompute derived state.</param>
    /// <param name="report">Shows a status message on the page.</param>
    public FramesViewModel(LibraryVideo video, IVideoMetadataService metadata, Func<TimeSpan> playhead, Action<TimeSpan> seek,
        Action changed, Action<string> report)
    {
        _video = video; _metadata = metadata; _playhead = playhead; _changed = changed; _report = report;
        CaptureCommand = new AsyncCommand(CaptureAsync, () => CanEdit && Thumbnails.Count < MaxFrames,
            onError: exception => report(exception.Message));
        RemoveCommand = new RelayCommand<ThumbnailEditorViewModel>(Remove, _ => CanEdit);
        SeekCommand = new RelayCommand<ThumbnailEditorViewModel>(frame => seek(frame.Position));
    }

    public ObservableCollection<ThumbnailEditorViewModel> Thumbnails { get; } = [];
    public string CountText => $"{Thumbnails.Count} / {MaxFrames}";

    /// <summary>Whether frames may change: the file's details have been read.</summary>
    public bool CanEdit
    {
        get => _canEdit;
        set { if (SetProperty(ref _canEdit, value)) UpdateCommands(); }
    }

    public AsyncCommand CaptureCommand { get; }
    /// <summary>Parameter: the frame to drop.</summary>
    public RelayCommand<ThumbnailEditorViewModel> RemoveCommand { get; }
    /// <summary>Parameter: the frame to play from.</summary>
    public RelayCommand<ThumbnailEditorViewModel> SeekCommand { get; }

    public IReadOnlyList<VideoThumbnail> ToThumbnails() => Thumbnails.Select(row => row.Thumbnail).ToArray();

    public bool Matches(IReadOnlyList<VideoThumbnail> thumbnails) =>
        Thumbnails.Select(row => row.Thumbnail.Id).SequenceEqual(thumbnails.Select(thumbnail => thumbnail.Id));

    /// <summary>Shows <paramref name="thumbnails"/> as the saved state. Rows are matched by id, so unchanged frames keep
    /// their instances and decoded images.</summary>
    public void ApplyBaseline(IReadOnlyList<VideoThumbnail> thumbnails)
    {
        foreach (var stale in Thumbnails.Where(row => thumbnails.All(thumbnail => thumbnail.Id != row.Thumbnail.Id)).ToArray())
        {
            Thumbnails.Remove(stale);
            stale.Dispose();
        }
        for (var index = 0; index < thumbnails.Count; index++)
        {
            var thumbnail = thumbnails[index];
            if (Thumbnails.FirstOrDefault(row => row.Thumbnail.Id == thumbnail.Id) is not { } row)
                Thumbnails.Insert(index, new ThumbnailEditorViewModel(thumbnail));
            else if (Thumbnails.IndexOf(row) is var current && current != index) Thumbnails.Move(current, index);
        }
        Changed();
    }

    /// <summary>Deletes frames captured on this page that are not in <paramref name="kept"/>.</summary>
    public void PruneCaptures(IReadOnlyList<VideoThumbnail> kept)
    {
        var keep = kept.Select(thumbnail => thumbnail.ImagePath).ToHashSet(StringComparer.Ordinal);
        foreach (var image in _captured.Where(image => !keep.Contains(image)))
        {
            try { File.Delete(image); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Trace.TraceWarning($"Could not delete unsaved capture {image}: {exception.Message}");
            }
        }
        _captured.IntersectWith(keep);
    }

    private async Task CaptureAsync(CancellationToken cancellationToken)
    {
        var thumbnail = await _metadata.CaptureThumbnailAsync(_video, _playhead(), cancellationToken);
        _captured.Add(thumbnail.ImagePath);
        Thumbnails.Add(new ThumbnailEditorViewModel(thumbnail));
        Changed();
        _report("Frame captured. Save to embed it in the video file.");
    }

    private void Remove(ThumbnailEditorViewModel frame)
    {
        if (!Thumbnails.Remove(frame)) return;
        frame.Dispose();
        Changed();
    }

    private void Changed()
    {
        OnPropertyChanged(nameof(CountText));
        UpdateCommands();
        _changed();
    }

    private void UpdateCommands()
    {
        CaptureCommand.RaiseCanExecuteChanged();
        RemoveCommand.RaiseCanExecuteChanged();
    }

    public void Dispose()
    {
        foreach (var frame in Thumbnails) frame.Dispose();
    }
}
