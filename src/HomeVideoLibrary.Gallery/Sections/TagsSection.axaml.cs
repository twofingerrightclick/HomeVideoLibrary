using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HomeVideoLibrary.UI.Controls;
using HomeVideoLibrary.UI.Logic;

namespace HomeVideoLibrary.Gallery.Sections;

public partial class TagsSection : UserControl, IScreenshotScript
{
    private static readonly TagFrequency[] LibraryTags =
    [
        new("Christmas", 42), new("Grandma", 37), new("birthday", 31), new("beach", 28),
        new("Thanksgiving", 22), new("Grandpa", 21), new("summer 1994", 19), new("lake house", 17),
        new("first steps", 6), new("graduation", 9), new("Grand Canyon", 4), new("Uncle Greg", 12),
        new("county fairground", 8), new("Halloween", 15), new("school play", 7), new("road trip", 11),
        new("Disneyland", 5), new("baseball", 13), new("dance recital", 6), new("snow day", 10),
        new("New Year's Eve", 8), new("wedding", 9), new("newborn", 4), new("camping", 12),
        new("swimming pool", 7), new("Easter", 14), new("backyard", 16), new("piano", 3),
        new("Fourth of July", 11), new("hunting", 2), new("grandma", 3),
    ];

    public TagsSection()
    {
        InitializeComponent();

        PopulatedInput.Tags = new ObservableCollection<string>
        {
            "Christmas", "Grandma", "summer 1994", "lake house", "first steps", "Uncle Greg",
        };
        DisabledInput.Tags = new ObservableCollection<string> { "birthday", "backyard" };
        LiveInput.Tags = new ObservableCollection<string> { "Christmas", "summer 1994" };

        foreach (var input in new[] { EmptyInput, PopulatedInput, DisabledInput, LiveInput })
        {
            input.SuggestionsSource = LibraryTags;
        }
    }

    /// <summary>Drives the live input through its keyboard and pointer contract; throws on any deviation.</summary>
    public void Prepare(Window window)
    {
        var input = LiveInput;
        var tags = input.Tags!;
        var baseline = tags.ToList();
        var entry = input.GetVisualDescendants().OfType<TextBox>().Single();
        entry.Focus();
        Pump();

        window.KeyTextInput("beach,");
        Pump();
        Expect(tags.Last() == "beach" && string.IsNullOrEmpty(input.Text), "comma commits the typed text");

        Press(window, PhysicalKey.Backspace);
        Expect(tags.SequenceEqual(baseline), "Backspace on an empty entry removes the last chip");

        window.KeyTextInput("CHRISTMAS");
        Press(window, PhysicalKey.Enter);
        Expect(tags.SequenceEqual(baseline) && string.IsNullOrEmpty(input.Text), "duplicate entry is ignored");

        window.KeyTextInput("new year's   EVE");
        Press(window, PhysicalKey.Enter);
        Expect(tags.Last() == "New Year's Eve", "typed text takes the library casing");
        Press(window, PhysicalKey.Backspace);

        window.KeyTextInput("gr");
        Pump();
        Expect(input.IsDropDownOpen && input.Suggestions[0].Tag == "Grandma", "typing opens ranked suggestions");
        Expect(input.HighlightedIndex == -1, "no row is highlighted until the user moves");

        Press(window, PhysicalKey.Escape);
        Expect(!input.IsDropDownOpen && input.Text == "gr", "Esc closes the list and keeps the text");

        Press(window, PhysicalKey.ArrowDown);
        Expect(input.IsDropDownOpen && input.HighlightedIndex == 0, "Down reopens and highlights the first row");
        Press(window, PhysicalKey.ArrowUp);
        var last = input.Suggestions[^1].Tag;
        Expect(input.HighlightedIndex == input.Suggestions.Count - 1, "Up wraps to the last row");
        Press(window, PhysicalKey.Tab);
        Expect(tags.Last() == last && entry.IsFocused, "Tab accepts the highlight and keeps focus");
        Press(window, PhysicalKey.Backspace);

        window.KeyTextInput("gr");
        Pump();
        var row = window.GetVisualDescendants().OfType<TagSuggestionItem>().ElementAt(1);
        var clicked = row.Text;
        Click(window, row);
        Expect(tags.Last() == clicked && entry.IsFocused && string.IsNullOrEmpty(input.Text), "clicking a suggestion commits it");
        Press(window, PhysicalKey.Backspace);

        window.KeyTextInput("temp,");
        Pump();
        var chip = input.GetVisualDescendants().OfType<TagChip>().Last();
        Click(window, chip.GetVisualDescendants().OfType<Button>().Single());
        Expect(tags.SequenceEqual(baseline) && entry.IsFocused, "the chip's X removes it");

        if (window.Clipboard is { } clipboard)
        {
            Await(clipboard.SetTextAsync("lake, Dock ,  summer   1994, pier"));
            entry.Paste();
            for (var i = 0; i < 20 && tags.Count == baseline.Count; i++)
            {
                Pump();
            }

            Expect(tags.Skip(baseline.Count).SequenceEqual(["lake", "Dock", "pier"]), "paste commits every comma-separated tag");
            for (var i = 0; i < 3; i++)
            {
                Press(window, PhysicalKey.Backspace);
            }
        }

        Expect(tags.SequenceEqual(baseline), "smoke check restores the specimen");

        window.KeyTextInput("gr");
        Press(window, PhysicalKey.ArrowDown);
        Press(window, PhysicalKey.ArrowDown);
    }

    private static void Press(Window window, PhysicalKey key)
    {
        window.KeyPressQwerty(key, RawInputModifiers.None);
        window.KeyReleaseQwerty(key, RawInputModifiers.None);
        Pump();
    }

    private static void Click(Window window, Control target)
    {
        var center = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window)
            ?? throw new InvalidOperationException($"{target.GetType().Name} is not in the window");
        window.MouseMove(center);
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
        Pump();
    }

    private static void Await(Task task)
    {
        for (var i = 0; i < 20 && !task.IsCompleted; i++)
        {
            Pump();
        }

        task.GetAwaiter().GetResult();
    }

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    }

    private static void Expect(bool condition, string behavior)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"TagChipInput smoke check failed: {behavior}.");
        }
    }
}
