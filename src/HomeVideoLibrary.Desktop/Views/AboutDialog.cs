using Avalonia.Controls;

namespace HomeVideoLibrary.Desktop.Views;

internal static class AboutDialog
{
    public static Task ShowAsync(Window owner) => new PromptWindow<bool>(
        $"About {AppInfo.Name}",
        $"{AppInfo.Name}\nVersion {AppInfo.Version}\nBuild {AppInfo.Build}",
        true, [new("OK", true, "primary", IsDefault: true, IsCancel: true)])
        .AskAsync(owner);
}
