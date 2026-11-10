using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace HomeVideoLibrary.Desktop.Views.VideoPage;

/// <summary>Keeps a window that draws over part of another window (the video overlay) with it on macOS, including in
/// fullscreen.
/// Avalonia shows an owned window as a separate NSWindow ordered above its owner, not as an AppKit child window. Such a
/// window stays on the Space it was shown on, so when its owner enters fullscreen (its own Space) it is left behind and
/// whatever it draws vanishes. An AppKit child window moves with its parent between Spaces and stays ordered above it.
/// AppKit drops the relationship when the child is ordered out, so attach again every time the child is shown. Other
/// platforms have no Spaces, and their owned windows already stay above a fullscreen owner, so this does nothing there.</summary>
internal static class MacChildWindow
{
    private const long NSWindowAbove = 1;

    /// <summary>Makes <paramref name="child"/> an AppKit child window of <paramref name="parent"/>. Both must be shown.</summary>
    public static void Attach(Window parent, Window child)
    {
        if (!OperatingSystem.IsMacOS() || NativeWindow(parent) is not { } parentWindow || NativeWindow(child) is not { } childWindow) return;
        Send(parentWindow, Selector("addChildWindow:ordered:"), childWindow, NSWindowAbove);
    }

    private static IntPtr? NativeWindow(Window window) =>
        window.TryGetPlatformHandle() is { HandleDescriptor: "NSWindow", Handle: var handle } && handle != IntPtr.Zero ? handle : null;

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "sel_registerName", CharSet = CharSet.Ansi)]
    private static extern IntPtr Selector(string name);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void Send(IntPtr receiver, IntPtr selector, IntPtr window, long ordering);
}
