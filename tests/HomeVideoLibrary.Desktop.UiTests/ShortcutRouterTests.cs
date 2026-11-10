using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using HomeVideoLibrary.Desktop.ViewModels;
using HomeVideoLibrary.Desktop.Views;
using HomeVideoLibrary.UI.Audio;
using Xunit;

namespace HomeVideoLibrary.Desktop.UiTests;

public sealed class ShortcutRouterTests
{
    [AvaloniaFact]
    public void A_letter_shortcut_types_into_a_text_box_instead_of_running()
    {
        var marks = 0;
        var field = new TextBox();
        var key = new Button { Content = "MARK" };
        var window = new Window { Content = new StackPanel { Children = { field, key } } };
        new ShortcutRouter(KeyModifiers.Control).Map(Key.M, KeyModifiers.None, () => new RelayCommand(() => marks++)).Attach(window);
        window.Show();

        field.Focus();
        window.KeyPressQwerty(PhysicalKey.M, RawInputModifiers.None);
        window.KeyTextInput("m");
        window.KeyReleaseQwerty(PhysicalKey.M, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("m", field.Text);
        Assert.Equal(0, marks);

        key.Focus();
        window.KeyPressQwerty(PhysicalKey.M, RawInputModifiers.None);
        Assert.Equal(1, marks);
    }

    [AvaloniaFact]
    public void Command_chords_run_from_text_entry()
    {
        var saves = 0;
        var field = new TextBox();
        var window = new Window { Content = field };
        new ShortcutRouter(KeyModifiers.Control).MapCommand(Key.S, () => new RelayCommand(() => saves++)).Attach(window);
        window.Show();

        field.Focus();
        window.KeyPressQwerty(PhysicalKey.S, RawInputModifiers.Control);

        Assert.Equal(1, saves);
    }

    [AvaloniaFact]
    public void Key_repeat_of_a_one_shot_shortcut_is_swallowed_and_a_new_press_runs_again()
    {
        var (window, clock) = Show(out var key);
        var marks = 0;
        Router(clock).Map(Key.M, KeyModifiers.None, () => new RelayCommand(() => marks++)).Attach(window);
        key.Focus();

        window.KeyPressQwerty(PhysicalKey.M, RawInputModifiers.None);
        clock.Advance(TimeSpan.FromMilliseconds(500));
        window.KeyPressQwerty(PhysicalKey.M, RawInputModifiers.None);
        clock.Advance(TimeSpan.FromMilliseconds(30));
        window.KeyPressQwerty(PhysicalKey.M, RawInputModifiers.None);
        Assert.Equal(1, marks);

        window.KeyReleaseQwerty(PhysicalKey.M, RawInputModifiers.None);
        window.KeyPressQwerty(PhysicalKey.M, RawInputModifiers.None);
        Assert.Equal(2, marks);
    }

    [AvaloniaFact]
    public void A_repeating_shortcut_runs_on_each_repeat()
    {
        var (window, clock) = Show(out var key);
        var seeks = 0;
        Router(clock).Map(Key.Right, KeyModifiers.None, () => new RelayCommand(() => seeks++), repeats: true).Attach(window);
        key.Focus();

        for (var press = 0; press < 3; press++)
        {
            window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
            clock.Advance(TimeSpan.FromMilliseconds(30));
        }
        Assert.Equal(3, seeks);
    }

    [AvaloniaFact]
    public void Focus_moving_between_controls_does_not_forget_a_held_key()
    {
        var (window, clock) = Show(out var key);
        var other = new Button { Content = "OTHER" };
        ((StackPanel)window.Content!).Children.Add(other);
        var marks = 0;
        Router(clock).Map(Key.M, KeyModifiers.None, () => new RelayCommand(() => marks++)).Attach(window);
        key.Focus();

        window.KeyPressQwerty(PhysicalKey.M, RawInputModifiers.None);
        other.Focus();
        clock.Advance(TimeSpan.FromMilliseconds(30));
        window.KeyPressQwerty(PhysicalKey.M, RawInputModifiers.None);
        Assert.Equal(1, marks);
    }

    [AvaloniaFact]
    public void Pressing_again_after_a_lost_KeyUp_runs_again()
    {
        var (window, clock) = Show(out var key);
        var searches = 0;
        Router(clock).MapCommand(Key.F, () => new RelayCommand(() => searches++)).Attach(window);
        key.Focus();

        // macOS sends no KeyUp for F released while Command stays down.
        window.KeyPressQwerty(PhysicalKey.F, RawInputModifiers.Control);
        clock.Advance(ShortcutRouter.RepeatWindow + TimeSpan.FromMilliseconds(200));
        window.KeyPressQwerty(PhysicalKey.F, RawInputModifiers.Control);
        Assert.Equal(2, searches);
    }

    [AvaloniaFact]
    public void Repeat_of_a_shortcut_that_cannot_run_falls_through()
    {
        var clock = new ManualClock();
        var router = Router(clock).Map(Key.M, KeyModifiers.None, () => new RelayCommand(() => { }, () => false));
        var press = () => new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.M, KeyModifiers = KeyModifiers.None };

        var first = press();
        router.Handle(first);
        clock.Advance(TimeSpan.FromMilliseconds(30));
        var repeat = press();
        router.Handle(repeat);

        Assert.False(first.Handled);
        Assert.False(repeat.Handled);
    }

    private static ShortcutRouter Router(ManualClock clock) => new(KeyModifiers.Control, new SilentSounds(), clock);

    private static (Window Window, ManualClock Clock) Show(out Button key)
    {
        key = new Button { Content = "KEY" };
        var window = new Window { Content = new StackPanel { Children = { key } } };
        window.Show();
        return (window, new ManualClock());
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(TimeSpan by) => _ticks += by.Ticks;
    }

    private sealed class SilentSounds : IClickSoundService
    {
        public bool IsEnabled { get; set; }
        public float Volume { get; set; }
        public void Play(ClickSoundKind kind) { }
    }
}
