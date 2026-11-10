using Avalonia;

namespace HomeVideoLibrary.Desktop.Views.VideoPage;

/// <summary>Where the window drawn over the video goes. Coordinates are the platform's screen units (the ones
/// <c>Window.Position</c> and <c>Screen.Bounds</c> use).</summary>
public static class VideoOverlayLayout
{
    /// <summary>The overlay's frame: the video's on-screen bounds, cut to the part of its screen a window is really shown
    /// in. That excludes the top inset the working area reserves for the menu bar and a camera housing (notch), where macOS
    /// either hides the window's top or moves the window down, pushing its bottom (and the controls anchored there) off
    /// the screen. The bottom inset (the Dock) is not excluded: a window never covers the Dock, and fullscreen hides it, so
    /// the video reaches the screen's bottom edge and so do the controls.</summary>
    /// <returns>An empty rectangle when no part of the video is on the screen.</returns>
    public static PixelRect Frame(PixelRect video, PixelRect screen, PixelRect workingArea)
    {
        var top = Math.Max(screen.Y, workingArea.Y);
        var visible = new PixelRect(screen.X, top, screen.Width, Math.Max(0, screen.Bottom - top));
        var frame = video.Intersect(visible);
        return frame.Width > 0 && frame.Height > 0 ? frame : default;
    }
}
