using Avalonia.Controls;
using Avalonia.Threading;
using HomeVideoLibrary.Gallery.Demo;
using HomeVideoLibrary.UI.Logic;

namespace HomeVideoLibrary.Gallery.Sections;

public partial class ChaptersSection : UserControl
{
    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(250);

    private static readonly TagSuggestionProvider Library = TagSuggestions.FromSnapshot(
        [new("Christmas", 42), new("Grandma", 37), new("snow day", 10), new("sledding", 6), new("birthday", 31)]);

    private readonly DispatcherTimer _player;

    public ChaptersSection()
    {
        InitializeComponent();
        var videoA = CreateVideoA();
        var videoB = CreateVideoB();
        PageHost.Content = videoA;
        ShowA.Click += (_, _) => PageHost.Content = videoA;
        ShowB.Click += (_, _) => PageHost.Content = videoB;

        // Stands in for the player: it is the only writer of Position, ticking the page that is shown.
        _player = new DispatcherTimer { Interval = Step };
        _player.Tick += (_, _) =>
        {
            if (PageHost.Content is VideoPageViewModel page)
            {
                var next = page.Chapters.Position + Step;
                page.Chapters.Position = next >= page.Chapters.Duration ? TimeSpan.Zero : next;
            }
        };
        PlayLatch.IsCheckedChanged += (_, _) => _player.IsEnabled = PlayLatch.IsChecked == true;
        DetachedFromVisualTree += (_, _) => _player.Stop();
    }

    public static VideoPageViewModel CreateVideoA() => new(
        "Christmas '91",
        new ChaptersPanelViewModel(TimeSpan.FromMinutes(42),
        [
            (TimeSpan.Zero, "Opening presents"),
            (TimeSpan.FromSeconds(252), "Grandma arrives"),
            (TimeSpan.FromSeconds(545), "Sledding on Miller Hill"),
            (TimeSpan.FromSeconds(1064), "Hot cocoa"),
            (TimeSpan.FromSeconds(1390), "Snowball fight in the dark"),
            (TimeSpan.FromSeconds(2010), "Goodnight"),
        ])
        {
            Position = TimeSpan.FromSeconds(700),
        },
        new TagEditorViewModel(["Christmas", "Grandma"], Library));

    public static VideoPageViewModel CreateVideoB() => new(
        "Lake Tahoe, summer 1994",
        new ChaptersPanelViewModel(TimeSpan.FromMinutes(25),
        [
            (TimeSpan.Zero, "Driving up"),
            (TimeSpan.FromMinutes(6), "First swim"),
            (TimeSpan.FromMinutes(17), "Campfire"),
        ])
        {
            Position = TimeSpan.FromMinutes(3),
        },
        new TagEditorViewModel(["summer 1994"], Library));
}
