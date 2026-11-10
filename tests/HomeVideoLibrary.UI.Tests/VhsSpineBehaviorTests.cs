using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.VisualTree;
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
        window.MouseDown(At(spine, 0.5), MouseButton.Left);
        window.MouseUp(At(spine, 0.5), MouseButton.Left);
        PumpFrames(until: () => open.Calls.Count > 0);
        PumpFrames(until: () => false, timeout: TimeSpan.FromMilliseconds(100));

        Assert.Single(open.Calls);
    }

    [AvaloniaFact]
    public void The_open_transition_grows_and_fades_the_tape_then_opens_it_and_leaves_it_at_rest()
    {
        var open = new RecordingCommand();
        var (window, spine) = Host(new VhsSpine { Command = open, OpenTransition = true, Width = 600 });
        var face = spine.GetVisualDescendants().OfType<Viewbox>().Single();
        double fadedTo = 1, grewTo = 1;

        window.MouseDown(At(spine, 0.5), MouseButton.Left);
        window.MouseUp(At(spine, 0.5), MouseButton.Left);
        PumpFrames(until: () =>
        {
            fadedTo = Math.Min(fadedTo, face.Opacity);
            grewTo = Math.Max(grewTo, Scale(face));
            return open.Calls.Count > 0;
        });

        Assert.Single(open.Calls);
        Assert.True(fadedTo < 0.9, $"faded only to {fadedTo}");
        Assert.True(grewTo > 1.01, $"grew only to {grewTo}");
        Assert.Equal(1, face.Opacity);
        Assert.Equal(1, Scale(face), precision: 3);

        static double Scale(Visual visual) => visual.RenderTransform?.Value.M11 ?? 1;
    }

    // Animations run on the wall clock, so frames are pumped in real time.
    private static void PumpFrames(Func<bool> until, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (!until() && DateTime.UtcNow < deadline)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
    }

    // 704 px wide is the full shelf stack: about 1.57x the design face, so the 24 px floor is about 15.3 design units.
    [AvaloniaTheory]
    [InlineData("Christmas 1991", 1, false)]
    [InlineData("Road trip to Grandma's house for Thanksgiving", 1, true)]
    [InlineData("Grandpa's seventieth birthday party at the lake house with the whole family", 2, true)]
    public void The_label_shrinks_then_wraps_but_stays_at_least_24_px_on_screen(string title, int lines, bool shrunk)
    {
        var (spine, label) = HostSpine(title, width: 704);
        var scale = spine.Bounds.Height / VhsSpine.DesignHeight;

        Assert.Equal(lines, label.TextLayout.TextLines.Count);
        Assert.Equal(shrunk, label.FontSize < 27);
        Assert.True(label.FontSize * scale >= VhsSpine.MinLabelFontSize - 0.01);
        Assert.False(label.TextLayout.TextLines[^1].HasCollapsed);
    }

    [AvaloniaFact]
    public void A_short_label_keeps_the_design_size()
    {
        var (_, label) = HostSpine("Christmas 1991", width: 704);

        Assert.Equal(27, label.FontSize);
    }

    [AvaloniaFact]
    public void A_label_too_long_for_two_lines_ends_in_an_ellipsis_at_the_floor()
    {
        var (spine, label) = HostSpine(string.Join(' ', Enumerable.Repeat("Summer camp at Big Sur", 6)), width: 704);

        Assert.Equal(VhsSpine.MinLabelFontSize, label.FontSize * spine.Bounds.Height / VhsSpine.DesignHeight, precision: 2);
        Assert.Equal(2, label.TextLayout.TextLines.Count);
        Assert.True(label.TextLayout.TextLines[^1].HasCollapsed);
    }

    [AvaloniaFact]
    public void A_new_label_is_refitted()
    {
        var (spine, label) = HostSpine(string.Join(' ', Enumerable.Repeat("Summer camp at Big Sur", 6)), width: 704);

        spine.Label = "Christmas 1991";
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Equal(27, label.FontSize);
        Assert.Single(label.TextLayout.TextLines);
    }

    private static (VhsSpine Spine, TextBlock Label) HostSpine(string title, double width)
    {
        var spine = new VhsSpine { Label = title, Width = width, HorizontalAlignment = HorizontalAlignment.Left };
        var window = new Window { Width = width + 40, Height = 300, Content = spine };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var label = spine.GetVisualDescendants().OfType<TextBlock>().Single(child => child.Name == "PART_Label");
        return (spine, label);
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
