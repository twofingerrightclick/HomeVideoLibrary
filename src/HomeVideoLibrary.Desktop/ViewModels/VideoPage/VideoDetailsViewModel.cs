using HomeVideoLibrary.Core;
using HomeVideoLibrary.Infrastructure;
using HomeVideoLibrary.UI.Logic;

namespace HomeVideoLibrary.Desktop.ViewModels.VideoPage;

/// <summary>Everything editable about the open video, as drafts over a baseline: the last state read from or saved to
/// the MKV. <see cref="IsDirty"/> is the difference between the two, so it needs no tracking. Editing starts once the MKV
/// has been read, because saving without that read would drop the file's thumbnails.</summary>
public sealed class VideoDetailsViewModel : ViewModelBase
{
    private readonly LibraryVideo _video;
    private readonly IVideoMetadataService _metadata;
    private readonly Func<Func<Task>, Task> _whileSaving;
    private readonly Action<string> _report;
    private readonly CancellationToken _lifetime;
    private VideoMetadata _baseline = VideoMetadata.Empty;
    private VideoRecord? _external;
    private string _title = "", _description = "", _recordedAtText = "";
    private bool _isLoaded;

    /// <param name="record">The library's view of the video; it fills the drafts before the MKV read finishes.</param>
    /// <param name="whileSaving">Runs the file write; the page closes playback around it where the platform needs that.</param>
    /// <param name="report">Shows a status message on the page.</param>
    /// <param name="lifetime">Cancelled when the page closes.</param>
    public VideoDetailsViewModel(VideoRecord record, IVideoMetadataService metadata, TagSuggestionProvider suggest,
        Func<TimeSpan> playhead, Action<TimeSpan> seek, Func<Func<Task>, Task> whileSaving, Action<string> report,
        CancellationToken lifetime)
    {
        _video = record.ToLibraryVideo();
        _metadata = metadata; _whileSaving = whileSaving; _report = report; _lifetime = lifetime;
        TitleWatermark = record.DisplayName;
        var initial = record.ToMetadata() ?? VideoMetadata.Empty;
        Chapters = new ChaptersPanelViewModel(record.Duration, initial.Chapters, suggest, seek, Changed);
        Tags = new TagListViewModel(initial.Tags, suggest, Changed);
        Frames = new FramesViewModel(_video, metadata, playhead, seek, Changed, report);
        SaveCommand = new AsyncCommand(SaveAsync, () => CanEdit && IsDirty && IsValid, onError: exception => _report(exception.Message));
        SaveCommand.PropertyChanged += (_, _) => UpdateCommands();
        RevertCommand = new RelayCommand(Revert, () => (IsDirty || HasExternalChange) && !SaveCommand.IsRunning);
        ApplyBaseline(initial);
    }

    public string TitleWatermark { get; }
    public string DurationText => Timecode.Format(_video.Duration);
    /// <summary>When the video was imported, in local time.</summary>
    public DateTimeOffset ImportedAt => _video.ImportedAt.ToLocalTime();
    public string FileName => Path.GetFileName(_video.RelativePath);

    public string Title { get => _title; set { if (SetProperty(ref _title, value)) Changed(); } }
    public string Description { get => _description; set { if (SetProperty(ref _description, value)) Changed(); } }

    /// <summary>The recorded date as typed: a year, year-month or full date, or empty for none.</summary>
    public string RecordedAtText { get => _recordedAtText; set { if (SetProperty(ref _recordedAtText, value)) Changed(); } }

    public ChaptersPanelViewModel Chapters { get; }
    public TagListViewModel Tags { get; }
    public FramesViewModel Frames { get; }

    /// <summary>Whether the MKV has been read, which editing requires.</summary>
    public bool CanEdit
    {
        get => _isLoaded;
        private set
        {
            if (!SetProperty(ref _isLoaded, value)) return;
            Chapters.CanEdit = value;
            Frames.CanEdit = value;
            UpdateCommands();
        }
    }

    /// <summary>Whether anything differs from the file, including a title or tag still being typed.</summary>
    public bool IsDirty => HasOpenEdits || !Matches(_baseline);

    private bool HasOpenEdits => Tags.HasOpenDraft || Chapters.HasOpenEdits;

    /// <summary>Commits text still being typed (a chapter title, tag entry text), as Enter or leaving the field would.</summary>
    public void CommitOpenEdits()
    {
        Tags.CommitDraft();
        Chapters.CommitOpenEdits();
    }

    public string? RecordedDateError => RecordedAtText.Trim() is var text && (text.Length == 0 || RecordedDate.TryParse(text, out _))
        ? null : "Use a year, year and month, or a full date: 1997, 1997-07 or 1997-07-03.";

    public bool IsValid => RecordedDateError is null;

    /// <summary>The video changed outside this page while it had unsaved edits. The drafts are kept: Save overwrites the
    /// outside change and Revert loads it.</summary>
    public bool HasExternalChange => _external is not null;

    public AsyncCommand SaveCommand { get; }
    public RelayCommand RevertCommand { get; }

    /// <summary>Reads the MKV (its thumbnails are not in the library's record) and enables editing.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            ApplyBaseline(await _metadata.ReadAsync(_video, cancellationToken));
            CanEdit = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { _report("Details could not be read, so editing is off: " + exception.Message); }
    }

    /// <summary>Reconciles with the library's current record of this video. Our own save arrives here too and changes
    /// nothing. Another change is shown at once when there are no unsaved edits; otherwise it waits behind
    /// <see cref="HasExternalChange"/>.</summary>
    public void OnRecordChanged(VideoRecord? record)
    {
        if (record?.ToMetadata() is not { } text) return;
        var content = text with { Thumbnails = record.Thumbnails.Select(thumbnail => new VideoThumbnail(thumbnail.Id, thumbnail.Position, "")).ToArray() };
        if (SameContent(content, _baseline) || Matches(content)) return;
        if (IsDirty)
        {
            _external = record;
            OnPropertyChanged(nameof(HasExternalChange));
            UpdateCommands();
        }
        else Rebase(record);
    }

    /// <summary>Builds and checks the metadata to save without touching playback or the file.</summary>
    /// <exception cref="ArgumentException">A field is invalid; the message says how to fix it.</exception>
    public VideoMetadata BuildMetadata()
    {
        if (RecordedDateError is { } error) throw new ArgumentException("Recorded date: " + error);
        var recordedAt = _baseline.RecordedAt;
        var recordedAtText = _baseline.RecordedAtText;
        if (RecordedAtText != RecordedDate.FormatForEditing(_baseline))
        {
            recordedAtText = RecordedAtText.Trim();
            recordedAt = RecordedDate.TryParse(recordedAtText, out var parsed) ? parsed : null;
        }
        return new VideoMetadata(Title, Description, [.. Tags.Items], recordedAt, Chapters.ToChapters(), Frames.ToThumbnails())
        {
            RecordedAtText = recordedAtText,
        };
    }

    /// <summary>Deletes captures the MKV never received; call when the page closes.</summary>
    public void PruneCaptures() => Frames.PruneCaptures(_baseline.Thumbnails);

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        CommitOpenEdits();
        var draft = BuildMetadata();
        await _whileSaving(() => _metadata.SaveAsync(_video, draft, cancellationToken));
        // The save verified that the file now reads back as exactly this. Edits typed while it ran stay as drafts.
        var stored = MetadataText.AsStored(draft);
        _external = null;
        OnPropertyChanged(nameof(HasExternalChange));
        if (Matches(draft)) ApplyBaseline(stored);
        else
        {
            _baseline = stored;
            Changed();
        }
        _report("Changes saved inside the video file.");
    }

    private void Revert()
    {
        if (_external is { } record)
        {
            _external = null;
            OnPropertyChanged(nameof(HasExternalChange));
            Rebase(record);
        }
        else ApplyBaseline(_baseline);
    }

    /// <summary>Adopts <paramref name="record"/> as the baseline. Its thumbnails are only references, so when they differ
    /// from the shown frames the file is read again for their images.</summary>
    private void Rebase(VideoRecord record)
    {
        var text = record.ToMetadata()!;
        if (record.Thumbnails.Select(thumbnail => thumbnail.Id).SequenceEqual(_baseline.Thumbnails.Select(thumbnail => thumbnail.Id)))
            ApplyBaseline(text with { Thumbnails = _baseline.Thumbnails });
        else _ = ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        try
        {
            var read = await _metadata.ReadAsync(_video, _lifetime);
            if (!IsDirty) ApplyBaseline(read);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { _report("The video changed, but its new details could not be read: " + exception.Message); }
    }

    /// <summary>Makes <paramref name="metadata"/> the baseline and shows it. Rows are reconciled by chapter UID and
    /// thumbnail id, so unchanged rows keep their instances (and their controls, focus and decoded images).</summary>
    private void ApplyBaseline(VideoMetadata metadata)
    {
        _baseline = metadata;
        Title = metadata.Title;
        Description = metadata.Description;
        RecordedAtText = RecordedDate.FormatForEditing(metadata);
        Tags.ApplyBaseline(metadata.Tags);
        Chapters.ApplyBaseline(metadata.Chapters);
        Frames.ApplyBaseline(metadata.Thumbnails);
        Changed();
    }

    /// <summary>Whether the drafts describe <paramref name="metadata"/>.</summary>
    private bool Matches(VideoMetadata metadata) =>
        Title == metadata.Title && Description == metadata.Description && Tags.Matches(metadata.Tags) &&
        RecordedAtText == RecordedDate.FormatForEditing(metadata) && Chapters.Matches(metadata.Chapters) && Frames.Matches(metadata.Thumbnails);

    private static bool SameContent(VideoMetadata left, VideoMetadata right) =>
        left.Title == right.Title && left.Description == right.Description &&
        left.Tags.SequenceEqual(right.Tags, StringComparer.Ordinal) &&
        RecordedDate.FormatForEditing(left) == RecordedDate.FormatForEditing(right) &&
        left.Chapters.Count == right.Chapters.Count &&
        left.Chapters.All(chapter => right.Chapters.FirstOrDefault(other => other.Uid == chapter.Uid) is { } match &&
            match.Position == chapter.Position && match.Title == chapter.Title && match.Description == chapter.Description &&
            match.Tags.SequenceEqual(chapter.Tags, StringComparer.Ordinal)) &&
        left.Thumbnails.Select(thumbnail => thumbnail.Id).SequenceEqual(right.Thumbnails.Select(thumbnail => thumbnail.Id));

    private void Changed()
    {
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(RecordedDateError));
        OnPropertyChanged(nameof(IsValid));
        UpdateCommands();
    }

    private void UpdateCommands()
    {
        SaveCommand.RaiseCanExecuteChanged();
        RevertCommand.RaiseCanExecuteChanged();
    }
}
