using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HomeVideoLibrary.UI.Controls;
using Xunit;
using static HomeVideoLibrary.UI.Tests.Headless;

namespace HomeVideoLibrary.UI.Tests;

public sealed class ButtonPressFeedbackTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cap_reaches_full_depth_then_animates_back_to_the_correct_resting_position(bool latch)
    {
        var (window, button) = Host(Create(latch ? "latch" : "plastic"));
        try
        {
            var cap = Cap(button);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            window.MouseDown(At(button, 0.5), MouseButton.Left);
            window.MouseUp(At(button, 0.5), MouseButton.Left);
            await Task.Delay(50);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Assert.Equal(2, cap.RenderTransform!.Value.M32, precision: 2);

            for (var i = 0; i < 30 && button.Classes.Contains("vcr-pressed"); i++)
                await Task.Delay(5);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(30);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            var rest = latch ? 1 : 0;
            Assert.InRange(cap.RenderTransform!.Value.M32, rest + 0.01, 1.99);
            await Task.Delay(160);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Assert.Equal(rest, cap.RenderTransform!.Value.M32, precision: 2);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData("stock")]
    [InlineData("plastic")]
    [InlineData("rubber")]
    [InlineData("latch")]
    [InlineData("toggle")]
    public async Task Quick_click_executes_once_and_remains_visible_after_release(string kind)
    {
        var command = new RecordingCommand();
        var button = Create(kind);
        button.Command = command;
        var (window, _) = Host(button);
        try
        {
            var cap = Cap(button);
            Assert.True(ButtonPressFeedback.GetIsEnabled(button));
            window.MouseDown(At(button, 0.5), MouseButton.Left);
            window.MouseUp(At(button, 0.5), MouseButton.Left);

            Assert.False(button.IsPressed);
            Assert.Single(command.Calls);
            Assert.Contains("vcr-pressed", button.Classes);
            Assert.Equal(TimeSpan.FromMilliseconds(kind == "rubber" ? 45 : 30), Assert.IsType<TransformOperationsTransition>(cap.Transitions![0]).Duration);

            await Task.Delay(180);
            Dispatcher.UIThread.RunJobs();

            Assert.DoesNotContain("vcr-pressed", button.Classes);
            Assert.Equal(TimeSpan.FromMilliseconds(kind == "rubber" ? 160 : 130), Assert.IsType<TransformOperationsTransition>(cap.Transitions![0]).Duration);
            Assert.Single(command.Calls);
            if (button is ToggleButton toggle) Assert.True(toggle.IsChecked);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Holding_stays_down_and_rapid_clicks_do_not_queue_commands_or_stale_releases()
    {
        var command = new RecordingCommand();
        var (window, button) = Host(new PlasticButton { Content = "PLAY", Command = command });
        try
        {
            window.MouseDown(At(button, 0.5), MouseButton.Left);
            await Task.Delay(150);
            Assert.True(button.IsPressed);
            Assert.Contains("vcr-pressed", button.Classes);
            Assert.Empty(command.Calls);
            window.MouseUp(At(button, 0.5), MouseButton.Left);
            Assert.DoesNotContain("vcr-pressed", button.Classes);

            window.MouseDown(At(button, 0.5), MouseButton.Left);
            window.MouseUp(At(button, 0.5), MouseButton.Left);
            window.MouseDown(At(button, 0.5), MouseButton.Left);
            await Task.Delay(150);
            Assert.Contains("vcr-pressed", button.Classes);
            window.MouseUp(At(button, 0.5), MouseButton.Left);
            Assert.DoesNotContain("vcr-pressed", button.Classes);
            Assert.Equal(3, command.Calls.Count);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(PhysicalKey.Enter)]
    [InlineData(PhysicalKey.Space)]
    public async Task Keyboard_activation_has_feedback_and_one_command(PhysicalKey key)
    {
        var command = new RecordingCommand();
        var (window, button) = Host(new PlasticButton { Content = "PLAY", Command = command });
        try
        {
            button.Focus();
            window.KeyPressQwerty(key, RawInputModifiers.None);
            window.KeyReleaseQwerty(key, RawInputModifiers.None);
            Assert.Single(command.Calls);
            Assert.Contains("vcr-pressed", button.Classes);
            await Task.Delay(180);
            Assert.DoesNotContain("vcr-pressed", button.Classes);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Release_outside_cancels_and_disabling_or_detaching_clears_feedback()
    {
        var command = new RecordingCommand();
        var (window, button) = Host(new PlasticButton { Content = "PLAY", Width = 100, Height = 40, Command = command });
        try
        {
            window.MouseDown(At(button, 0.5), MouseButton.Left);
            window.MouseMove(new Avalonia.Point(1, 1));
            window.MouseUp(new Avalonia.Point(1, 1), MouseButton.Left);
            Assert.Empty(command.Calls);
            Assert.DoesNotContain("vcr-pressed", button.Classes);

            window.MouseDown(At(button, 0.5), MouseButton.Left);
            button.IsEnabled = false;
            Assert.DoesNotContain("vcr-pressed", button.Classes);
            window.MouseUp(At(button, 0.5), MouseButton.Left);
            button.IsEnabled = true;
            window.MouseDown(At(button, 0.5), MouseButton.Left);
            window.Content = null;
            Assert.DoesNotContain("vcr-pressed", button.Classes);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Reduced_motion_is_inherited_and_uses_immediate_feedback()
    {
        var (window, button) = Host(new RubberButton { Content = "MARK" });
        try
        {
            window.Classes.Add("reduced-motion");
            Assert.True(ButtonPressFeedback.GetReducedMotion(button));
            window.MouseDown(At(button, 0.5), MouseButton.Left);
            Assert.Contains("vcr-pressed", button.Classes);
            Assert.Null(Cap(button).Transitions);
            window.MouseUp(At(button, 0.5), MouseButton.Left);
            Assert.DoesNotContain("vcr-pressed", button.Classes);
            window.Classes.Remove("reduced-motion");
            Assert.NotNull(Cap(button).Transitions);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Shared_reduced_motion_preference_reaches_existing_and_new_dialog_windows()
    {
        var resources = Avalonia.Application.Current!.Resources;
        var hadValue = resources.TryGetValue("Vcr.ReducedMotion", out var previous);
        var (window, button) = Host(new PlasticButton { Content = "SAVE" });
        Window? dialog = null;
        try
        {
            resources["Vcr.ReducedMotion"] = true;
            var promptButton = new PlasticButton { Content = "OK" };
            (dialog, _) = Host(promptButton);

            foreach (var control in new[] { button, promptButton })
            {
                Assert.True(ButtonPressFeedback.GetReducedMotion(control));
                Assert.Null(Cap(control).Transitions);
            }
            Assert.DoesNotContain("reduced-motion", dialog.Classes);

            resources["Vcr.ReducedMotion"] = false;
            foreach (var control in new[] { button, promptButton })
            {
                Assert.False(ButtonPressFeedback.GetReducedMotion(control));
                Assert.NotNull(Cap(control).Transitions);
            }
        }
        finally
        {
            dialog?.Close();
            window.Close();
            if (hadValue) resources["Vcr.ReducedMotion"] = previous;
            else resources.Remove("Vcr.ReducedMotion");
        }
    }

    [AvaloniaFact]
    public async Task Latch_returns_to_the_latest_checked_state_in_both_directions()
    {
        var (window, button) = Host(new LatchButton { Content = "LOOP", IsChecked = true });
        try
        {
            var cap = Cap(button);
            window.MouseDown(At(button, 0.5), MouseButton.Left);
            window.MouseUp(At(button, 0.5), MouseButton.Left);
            Assert.False(button.IsChecked);
            await Task.Delay(150);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            // The model can change a mode while the release is still animating.
            button.IsChecked = true;
            await Task.Delay(180);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Assert.Equal(1, cap.RenderTransform!.Value.M32, precision: 2);

            window.MouseDown(At(button, 0.5), MouseButton.Left);
            window.MouseUp(At(button, 0.5), MouseButton.Left);
            await Task.Delay(300);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Assert.False(button.IsChecked);
            Assert.Equal(0, cap.RenderTransform!.Value.M32, precision: 2);
        }
        finally { window.Close(); }
    }

    private static Button Create(string kind) => kind switch
    {
        "plastic" => new PlasticButton { Content = "PLAY" },
        "rubber" => new RubberButton { Content = "MARK" },
        "latch" => new LatchButton { Content = "LOOP" },
        "toggle" => new ToggleButton { Content = "LOOP" },
        _ => new Button { Content = "PLAY" },
    };

    private static Border Cap(Button button) => button.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PART_Cap");
}
