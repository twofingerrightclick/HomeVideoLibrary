using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using HomeVideoLibrary.Desktop.ViewModels.VideoPage;

namespace HomeVideoLibrary.Desktop.Views.VideoPage;

/// <summary>A transparent window kept over the visible part of the page's video, for what must draw over the native
/// surface: the fullscreen controls, and the pointer target for idle-hiding and double-click. It is owned by the page's
/// window: kept above it (an AppKit child window on macOS, so it follows it into fullscreen) and placed by
/// <see cref="Place"/>.</summary>
public sealed partial class VideoOverlayWindow : Window
{
    public VideoOverlayWindow()
    {
        InitializeComponent();
        Surface.PointerMoved += (_, _) => ViewModel?.Fullscreen.Wake();
        Surface.DoubleTapped += (_, _) => ViewModel?.Fullscreen.ToggleCommand.Execute(null);
        FullscreenControls.PointerEntered += (_, _) => ViewModel?.Fullscreen.Hold(true);
        FullscreenControls.PointerExited += (_, _) => ViewModel?.Fullscreen.Hold(false);
        // Over the video, keys come here (this window can become the key window); the ones it does not handle go on to
        // the page's window, where the page's and the shell's shortcuts listen.
        AddHandler(KeyDownEvent, ForwardKey, RoutingStrategies.Bubble);
        AddHandler(KeyUpEvent, ForwardKey, RoutingStrategies.Bubble);
    }

    private VideoPageViewModel? ViewModel => DataContext as VideoPageViewModel;

    /// <summary>Covers <paramref name="video"/> where it is visible on its screen (<see cref="VideoOverlayLayout.Frame"/>),
    /// shown over <paramref name="owner"/>; hides when none of the video is visible.</summary>
    public void Place(Window owner, Control video)
    {
        if (!owner.IsVisible || !video.IsEffectivelyVisible || TopLevel.GetTopLevel(video) != owner)
        {
            Hide();
            return;
        }
        var scaling = owner.DesktopScaling;
        var bounds = new PixelRect(video.PointToScreen(default), PixelSize.FromSize(video.Bounds.Size, scaling));
        var screen = owner.Screens.ScreenFromWindow(owner) ?? owner.Screens.ScreenFromPoint(bounds.Center);
        var frame = screen is null ? bounds : VideoOverlayLayout.Frame(bounds, screen.Bounds, screen.WorkingArea);
        if (frame.Width == 0 || frame.Height == 0)
        {
            Hide();
            return;
        }
        if (Position != frame.Position) Position = frame.Position;
        Width = frame.Width / scaling;
        Height = frame.Height / scaling;
        if (IsVisible) return;
        Show(owner);
        // AppKit drops a child window's link when it is ordered out, so tie it again on every show.
        MacChildWindow.Attach(owner, this);
    }

    private void ForwardKey(object? sender, KeyEventArgs args)
    {
        if (args.Handled || Owner is not Window owner) return;
        var forwarded = new KeyEventArgs
        {
            RoutedEvent = args.RoutedEvent, Key = args.Key, KeyModifiers = args.KeyModifiers, PhysicalKey = args.PhysicalKey,
            KeySymbol = args.KeySymbol, KeyDeviceType = args.KeyDeviceType,
        };
        owner.RaiseEvent(forwarded);
        args.Handled = forwarded.Handled;
    }
}
