using HomeVideoLibrary.Core;
using HomeVideoLibrary.UI.Logic;

namespace HomeVideoLibrary.Desktop.ViewModels.VideoPage;

/// <summary>One chapter of the open video. It is keyed by <see cref="Uid"/> and lives as long as the chapter does, so an
/// open title edit (<see cref="IsEditing"/>, <see cref="DraftTitle"/>) survives playhead updates and reordering.</summary>
public sealed class ChapterRowViewModel : ViewModelBase
{
    private readonly ChaptersPanelViewModel _panel;
    private TimeSpan _start;
    private string _title, _description;
    private string? _draftTitle;
    private bool _isEditing, _isCurrent, _isSelected;

    internal ChapterRowViewModel(ChaptersPanelViewModel panel, VideoChapter chapter, TagSuggestionProvider suggest)
    {
        _panel = panel;
        Uid = chapter.Uid;
        _start = chapter.Position;
        _title = chapter.Title;
        _description = chapter.Description;
        Tags = new TagListViewModel(chapter.Tags, suggest, () => panel.ChapterEdited(this));
        JumpCommand = new RelayCommand(() => panel.Jump(this));
        CommitTitleCommand = new RelayCommand<string>(title => panel.Rename(this, title));
        DeleteCommand = new RelayCommand(() => panel.Delete(this), () => panel.CanEdit);
        MoveToPlayheadCommand = new RelayCommand(() => panel.MoveToPlayhead(this), () => panel.CanEdit);
    }

    public ulong Uid { get; }
    public TimeSpan Start
    {
        get => _start;
        internal set { if (SetProperty(ref _start, value)) OnPropertyChanged(nameof(StartText)); }
    }

    public string StartText => Timecode.Format(Start);

    /// <summary>The committed title; edits arrive through <see cref="CommitTitleCommand"/>.</summary>
    public string Title { get => _title; internal set => SetProperty(ref _title, value); }

    /// <summary>The title being typed while <see cref="IsEditing"/>.</summary>
    public string? DraftTitle { get => _draftTitle; set => SetProperty(ref _draftTitle, value); }
    public bool IsEditing { get => _isEditing; set => SetProperty(ref _isEditing, value); }

    /// <summary>Whether the playhead is inside this chapter.</summary>
    public bool IsCurrent { get => _isCurrent; internal set => SetProperty(ref _isCurrent, value); }

    /// <summary>Whether this is the chapter whose details are shown.</summary>
    public bool IsSelected { get => _isSelected; internal set => SetProperty(ref _isSelected, value); }

    public string Description
    {
        get => _description;
        set { if (SetProperty(ref _description, value)) _panel.ChapterEdited(this); }
    }

    public TagListViewModel Tags { get; }

    public RelayCommand JumpCommand { get; }
    /// <summary>Parameter: the new title.</summary>
    public RelayCommand<string> CommitTitleCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand MoveToPlayheadCommand { get; }

    public VideoChapter ToChapter() => new(Uid, Start, Title, Description, [.. Tags.Items]);

    public bool Matches(VideoChapter chapter) => chapter.Uid == Uid && chapter.Position == Start && chapter.Title == Title &&
        chapter.Description == Description && Tags.Matches(chapter.Tags);

    /// <summary>Shows <paramref name="chapter"/> (same <see cref="Uid"/>) as the saved state, touching only fields that differ.</summary>
    internal void ApplyBaseline(VideoChapter chapter)
    {
        Start = chapter.Position;
        Title = chapter.Title;
        Description = chapter.Description;
        Tags.ApplyBaseline(chapter.Tags);
    }

    internal void UpdateCommands()
    {
        DeleteCommand.RaiseCanExecuteChanged();
        MoveToPlayheadCommand.RaiseCanExecuteChanged();
    }
}
