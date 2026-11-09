using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Input;
using HomeVideoLibrary.UI.Controls;
using HomeVideoLibrary.UI.Logic;

namespace HomeVideoLibrary.Gallery.Sections;

public partial class ChaptersSection : UserControl
{
    private static readonly TimeSpan Duration = TimeSpan.FromMinutes(42);
    private readonly ObservableCollection<DemoChapter> _chapters =
    [
        new(TimeSpan.Zero, "Opening presents"),
        new(TimeSpan.FromSeconds(252), "Grandma arrives"),
        new(TimeSpan.FromSeconds(545), "Sledding on Miller Hill"),
        new(TimeSpan.FromSeconds(1064), "Hot cocoa"),
        new(TimeSpan.FromSeconds(1390), "Snowball fight in the dark"),
        new(TimeSpan.FromSeconds(2010), "Goodnight"),
    ];

    private DemoChapter? _editing;

    public ChaptersSection()
    {
        InitializeComponent();
        LiveScrub.Duration = Duration;
        LiveScrub.Position = TimeSpan.FromSeconds(700);
        LiveScrub.BufferedPosition = Duration;
        LiveScrub.ChapterSegments = _chapters;
        LiveScrub.PropertyChanged += (_, e) =>
        {
            if (e.Property == ScrubBar.PositionProperty)
            {
                Rebuild();
            }
        };
        AddChapter.Click += (_, _) => AddAtPlayhead();
        AddChapter.PointerEntered += (_, _) => LiveScrub.PreviewMarkerPosition = LiveScrub.Position;
        AddChapter.PointerExited += (_, _) => LiveScrub.PreviewMarkerPosition = null;
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.M && e.Source is not TextBox)
            {
                e.Handled = true;
                AddAtPlayhead();
            }
        };
        Rebuild();
    }

    private void AddAtPlayhead()
    {
        var playhead = LiveScrub.Position;
        var starts = _chapters.Select(c => c.Start).ToList();
        if (ChapterPlacement.HasChapterAt(starts, playhead, TimeSpan.FromSeconds(1)))
        {
            return;
        }

        var chapter = new DemoChapter(playhead, string.Empty);
        _chapters.Insert(ChapterPlacement.PlaceholderIndex(starts, playhead), chapter);
        _editing = chapter;
        Rebuild();
    }

    private void Rebuild()
    {
        var playhead = LiveScrub.Position;
        var starts = _chapters.Select(c => c.Start).ToList();
        var placeholderIndex = ChapterPlacement.PlaceholderIndex(starts, playhead);
        var canAdd = !ChapterPlacement.HasChapterAt(starts, playhead, TimeSpan.FromSeconds(1));
        AddChapter.IsEnabled = canAdd;
        LiveStatus.Text = $"{ScrubTimeline.FormatTimestamp(playhead)} — new chapter would be row {placeholderIndex + 1}";

        LiveRows.Children.Clear();
        for (var i = 0; i <= _chapters.Count; i++)
        {
            if (i == placeholderIndex && canAdd)
            {
                LiveRows.Children.Add(new ChapterListItem
                {
                    Start = playhead,
                    IsPlaceholder = true,
                    TitleWatermark = $"New chapter at {ScrubTimeline.FormatTimestamp(playhead)}",
                });
            }

            if (i == _chapters.Count)
            {
                break;
            }

            var chapter = _chapters[i];
            var next = i + 1 < _chapters.Count ? _chapters[i + 1].Start : Duration;
            var row = new ChapterListItem
            {
                Start = chapter.Start,
                Title = chapter.Title,
                IsCurrent = playhead >= chapter.Start && playhead < next,
                IsEditing = ReferenceEquals(chapter, _editing),
            };
            row.JumpRequested += (_, _) => LiveScrub.Position = chapter.Start;
            row.TitleCommitted += (_, e) => chapter.Title = e.NewTitle;
            row.PropertyChanged += (_, e) =>
            {
                if (e.Property == ChapterListItem.IsEditingProperty && !row.IsEditing && ReferenceEquals(_editing, chapter))
                {
                    _editing = null;
                }
            };
            LiveRows.Children.Add(row);
        }
    }

    private sealed class DemoChapter(TimeSpan start, string title) : IChapterMark, INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        public TimeSpan Start { get; } = start;

        public string Title
        {
            get => title;
            set
            {
                title = value;
                OnPropertyChanged();
            }
        }

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
