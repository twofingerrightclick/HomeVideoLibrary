using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using HomeVideoLibrary.UI.Controls;
using Xunit;
using static HomeVideoLibrary.UI.Tests.Headless;

namespace HomeVideoLibrary.UI.Tests;

public sealed class VhsSpineBehaviorTests
{
    [AvaloniaFact]
    public void A_double_click_opens_the_tape_once()
    {
        var open = new RecordingCommand();
        var (window, spine) = Host(new VhsSpine { Label = "Disneyland 1989", Command = open });

        window.MouseDown(At(spine, 0.5), MouseButton.Left);
        window.MouseUp(At(spine, 0.5), MouseButton.Left);
        window.MouseDown(At(spine, 0.5), MouseButton.Left);
        window.MouseUp(At(spine, 0.5), MouseButton.Left);

        Assert.Single(open.Calls);
    }

    [AvaloniaFact]
    public void A_modifier_click_is_left_to_the_host_for_selection()
    {
        var open = new RecordingCommand();
        var (window, spine) = Host(new VhsSpine { Command = open });

        window.MouseDown(At(spine, 0.5), MouseButton.Left, RawInputModifiers.Control);
        window.MouseUp(At(spine, 0.5), MouseButton.Left, RawInputModifiers.Control);

        Assert.Empty(open.Calls);
    }

    [AvaloniaFact]
    public void Enter_on_the_leading_check_box_does_not_open_the_tape()
    {
        var open = new RecordingCommand();
        var box = new CheckBox();
        var (window, _) = Host(new VhsSpine { Command = open, LeadingContent = box });
        box.Focus();

        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);

        Assert.Empty(open.Calls);
    }
}
