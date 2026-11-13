using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HomeVideoLibrary.Desktop.Views.VideoPage;
using HomeVideoLibrary.UI.Controls;
using Xunit;
using static HomeVideoLibrary.Desktop.UiTests.VideoPageFixture;

namespace HomeVideoLibrary.Desktop.UiTests;

public sealed class PlaybackSpeedViewTests
{
    [AvaloniaFact]
    public void DropupSliderAndPresetsControlTheSameSpeed()
    {
        var (window, host) = Window();
        var page = Page();
        Show(host, page);
        var deck = Assert.Single(host.GetVisualDescendants().OfType<TransportDeck>());
        var key = deck.FindControl<PlasticButton>("SpeedButton")!;
        Assert.Equal("1×", key.Content);
        var flyout = Assert.IsType<Flyout>(key.Flyout);
        Assert.Equal(PlacementMode.TopEdgeAlignedLeft, flyout.Placement);
        flyout.ShowAt(key);
        Dispatcher.UIThread.RunJobs();
        var content = Assert.IsType<StackPanel>(flyout.Content);
        var slider = Assert.Single(content.GetVisualDescendants().OfType<Slider>());
        Assert.Equal(0.25, slider.Minimum);
        Assert.Equal(2, slider.Maximum);
        slider.Value = 1.35;
        Assert.Equal(1.35, page.Player.Speed);
        Assert.Equal("1.35×", key.Content);
        var presets = content.GetVisualDescendants().OfType<PlasticButton>().ToArray();
        Assert.Equal(8, presets.Length);
        Assert.Equal("0.25×", presets[0].Content);
        Assert.Equal("2×", presets[^1].Content);
        presets[1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(0.5, page.Player.Speed);
        Assert.False(flyout.IsOpen);
        flyout.ShowAt(key);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0.5, slider.Value);
        if (Environment.GetEnvironmentVariable("HVL_SPEED_SCREENSHOTS") is { } directory)
        {
            Directory.CreateDirectory(directory);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            using var frame = window.CaptureRenderedFrame();
            frame!.Save(Path.Combine(directory, "speed-deck.png"));
            using var popup = TopLevel.GetTopLevel(content)!.CaptureRenderedFrame();
            popup!.Save(Path.Combine(directory, "speed-dropup.png"));
        }
        flyout.Hide();
        window.Close();
    }
}
