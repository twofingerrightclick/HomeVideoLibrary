using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HomeVideoLibrary.UI.Controls;
using HomeVideoLibrary.UI.Logic;
using Xunit;

namespace HomeVideoLibrary.UI.Tests;

/// <summary>Interaction contracts the app relies on, driven through headless input.</summary>
public sealed class ControlBehaviorTests
{
    private static readonly TimeSpan Length = TimeSpan.FromMinutes(40);

    [AvaloniaFact]
    public void Tag_entry_commits_pending_text_when_focus_leaves()
    {
        var other = new Button { Content = "elsewhere" };
        var input = new TagChipInput();
        var (window, _) = Host(new StackPanel { Children = { input, other } });
        var entry = Entry(input);
        entry.Focus();
        window.KeyTextInput("pending");

        other.Focus();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(["pending"], input.Tags!);
        Assert.True(string.IsNullOrEmpty(input.Text));
    }

    [AvaloniaFact]
    public void Tag_entry_text_does_not_carry_over_to_another_tag_list()
    {
        var input = new TagChipInput { Tags = new ObservableCollection<string>(["a"]) };
        Host(input);
        input.Text = "typed for video A";

        input.Tags = new ObservableCollection<string>(["b"]);

        Assert.True(string.IsNullOrEmpty(input.Text));
    }

    [AvaloniaFact]
    public void Tag_backspace_removes_a_chip_only_on_a_fresh_unmodified_press()
    {
        var input = new TagChipInput { Tags = new ObservableCollection<string>(["one", "two"]) };
        var (window, _) = Host(input);
        Entry(input).Focus();

        window.KeyPressQwerty(PhysicalKey.Backspace, RawInputModifiers.Control);
        window.KeyReleaseQwerty(PhysicalKey.Backspace, RawInputModifiers.Control);
        Assert.Equal(2, input.Tags!.Count);

        window.KeyPressQwerty(PhysicalKey.Backspace, RawInputModifiers.None);
        window.KeyPressQwerty(PhysicalKey.Backspace, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Backspace, RawInputModifiers.None);
        Assert.Equal(["one"], input.Tags!);
    }

    [AvaloniaFact]
    public void Tag_input_subscribes_to_the_library_index_only_while_shown()
    {
        var index = new CountingCollection();
        var input = new TagChipInput { SuggestionsSource = index };
        var (window, _) = Host(input);
        Assert.Equal(1, index.Subscribers);

        window.Content = null;

        Assert.Equal(0, index.Subscribers);
    }

    [AvaloniaFact]
    public void Tag_suggestions_updated_from_a_worker_thread_are_applied_on_the_UI_thread()
    {
        var index = new ObservableCollection<TagFrequency> { new("Grandma", 3) };
        var input = new TagChipInput { SuggestionsSource = index };
        var (window, _) = Host(input);
        Entry(input).Focus();
        window.KeyTextInput("gr");

        Task.Run(() => index.Add(new TagFrequency("graduation", 2))).Wait();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(["Grandma", "graduation"], input.Suggestions.Select(s => s.Tag));
    }

    [AvaloniaFact]
    public void Chapter_row_enters_edit_mode_when_a_placeholder_is_promoted_after_IsEditing_was_set()
    {
        var row = new ChapterListItem { IsPlaceholder = true, Start = TimeSpan.FromMinutes(3) };
        Host(row);

        row.IsEditing = true;
        Assert.DoesNotContain(":editing", row.Classes);

        row.IsPlaceholder = false;

        Assert.Contains(":editing", row.Classes);
    }

    private static (Window Window, T Control) Host<T>(T control)
        where T : Control
    {
        var window = new Window { Width = 800, Height = 200, Content = control };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, control);
    }

    private static Point At(Control control, double fraction) =>
        control.TranslatePoint(new Point(control.Bounds.Width * fraction, control.Bounds.Height / 2), TopLevel.GetTopLevel(control)!)!.Value;

    private static TextBox Entry(TagChipInput input) =>
        input.GetVisualDescendants().OfType<TextBox>().Single();

    private sealed class CountingCollection : IEnumerable<TagFrequency>, INotifyCollectionChanged
    {
        private NotifyCollectionChangedEventHandler? _changed;

        public event NotifyCollectionChangedEventHandler? CollectionChanged
        {
            add
            {
                _changed += value;
                Subscribers++;
            }

            remove
            {
                _changed -= value;
                Subscribers--;
            }
        }

        public int Subscribers { get; private set; }

        public IEnumerator<TagFrequency> GetEnumerator() => Enumerable.Empty<TagFrequency>().GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
