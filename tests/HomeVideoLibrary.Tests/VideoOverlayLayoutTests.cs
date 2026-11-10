using Avalonia;
using HomeVideoLibrary.Desktop.Views.VideoPage;
using Xunit;

namespace HomeVideoLibrary.Tests;

public sealed class VideoOverlayLayoutTests
{
    // A 16" MacBook Pro screen: a 39-point menu bar around the camera housing and a 47-point Dock.
    private static readonly PixelRect NotchScreen = new(0, 0, 2056, 1329);
    private static readonly PixelRect NotchWorkingArea = new(0, 39, 2056, 1243);
    // An external display: a 25-point menu bar, the Dock hidden.
    private static readonly PixelRect Display = new(2056, -549, 3440, 1440);
    private static readonly PixelRect DisplayWorkingArea = new(2056, -524, 3440, 1415);

    [Fact]
    public void FullscreenBelowTheCameraHousingCoversTheWholeVideo()
    {
        var video = new PixelRect(0, 39, 2056, 1290);

        Assert.Equal(video, VideoOverlayLayout.Frame(video, NotchScreen, NotchWorkingArea));
    }

    [Fact]
    public void FullscreenUnderTheMenuBarStripKeepsTheBottomEdgeOnScreen()
    {
        var video = Display;

        var frame = VideoOverlayLayout.Frame(video, Display, DisplayWorkingArea);

        Assert.Equal(DisplayWorkingArea.Y, frame.Y);
        Assert.Equal(Display.Bottom, frame.Bottom);
        Assert.Equal(Display.X, frame.X);
        Assert.Equal(Display.Width, frame.Width);
    }

    [Fact]
    public void AWindowedVideoIsCoveredExactly()
    {
        var video = new PixelRect(308, 191, 1094, 679);

        Assert.Equal(video, VideoOverlayLayout.Frame(video, NotchScreen, NotchWorkingArea));
    }

    [Fact]
    public void TheDockDoesNotRaiseTheBottomEdge()
    {
        var video = new PixelRect(0, 39, 2056, 1290);

        Assert.Equal(NotchScreen.Bottom, VideoOverlayLayout.Frame(video, NotchScreen, NotchWorkingArea).Bottom);
    }

    [Fact]
    public void AVideoPartlyOffTheScreenIsCoveredWhereItShows()
    {
        var video = new PixelRect(1800, 900, 600, 600);

        Assert.Equal(new PixelRect(1800, 900, 256, 429), VideoOverlayLayout.Frame(video, NotchScreen, NotchWorkingArea));
    }

    [Fact]
    public void AVideoOffTheScreenGetsNoFrame()
    {
        var video = new PixelRect(-900, 100, 600, 400);

        Assert.Equal(default, VideoOverlayLayout.Frame(video, NotchScreen, NotchWorkingArea));
    }
}
