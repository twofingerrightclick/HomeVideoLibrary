using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using HomeVideoLibrary.Desktop.ViewModels;
using HomeVideoLibrary.Desktop.Views;
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
}
