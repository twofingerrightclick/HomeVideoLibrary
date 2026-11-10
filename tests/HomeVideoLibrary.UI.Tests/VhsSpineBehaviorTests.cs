using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
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

    [AvaloniaTheory]
    [InlineData(800, 300)]
    [InlineData(300, 800)]
    [InlineData(1200, 60)]
    public void The_spine_keeps_real_VHS_proportions_in_any_space(double width, double height)
    {
        var spine = new VhsSpine { Label = "Christmas 1991", HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        var window = new Window { Width = width, Height = height, Content = spine };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Equal(VhsSpine.AspectRatio, spine.Bounds.Width / spine.Bounds.Height, precision: 2);
        Assert.True(spine.Bounds.Width <= width + 0.5 && spine.Bounds.Height <= height + 0.5);
    }

    [AvaloniaFact]
    public void With_the_open_transition_a_click_still_opens_the_tape_exactly_once()
    {
        var open = new RecordingCommand();
        var (window, spine) = Host(new VhsSpine { Command = open, OpenTransition = true, Width = 600 });

        window.MouseDown(At(spine, 0.5), MouseButton.Left);
        window.MouseUp(At(spine, 0.5), MouseButton.Left);
        for (var frame = 0; frame < 30; frame++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(16);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }

        Assert.Single(open.Calls);
    }

    [AvaloniaFact]
    public void Without_the_open_transition_a_click_opens_at_once()
    {
        var open = new RecordingCommand();
        var (window, spine) = Host(new VhsSpine { Command = open, Width = 600 });

        window.MouseDown(At(spine, 0.5), MouseButton.Left);
        window.MouseUp(At(spine, 0.5), MouseButton.Left);

        Assert.Single(open.Calls);
    }
}
