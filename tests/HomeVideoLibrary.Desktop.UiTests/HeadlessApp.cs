using Avalonia;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using HomeVideoLibrary.Desktop.UiTests;

[assembly: AvaloniaTestApplication(typeof(HeadlessApp))]

namespace HomeVideoLibrary.Desktop.UiTests;

/// <summary>Headless Skia with the app's own style stack (Fluent, the VCR theme, app styles).</summary>
public sealed class HeadlessApp : Application
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<HeadlessApp>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });

    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new FluentTheme());
        foreach (var source in new[] { "avares://HomeVideoLibrary.UI/VcrTheme.axaml", "avares://HomeVideoLibrary/Styles/AppStyles.axaml" })
            Styles.Add(new StyleInclude(new Uri(source)) { Source = new Uri(source) });
    }
}
