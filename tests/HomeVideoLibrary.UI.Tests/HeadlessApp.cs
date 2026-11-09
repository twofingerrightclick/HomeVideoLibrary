using Avalonia;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using HomeVideoLibrary.UI.Tests;

[assembly: AvaloniaTestApplication(typeof(HeadlessApp))]

namespace HomeVideoLibrary.UI.Tests;

/// <summary>Real Skia rendering with Fluent plus the VCR theme, as the app will include them.</summary>
public sealed class HeadlessApp : Application
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<HeadlessApp>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });

    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new FluentTheme());
        Styles.Add(new StyleInclude(new Uri("avares://HomeVideoLibrary.UI/"))
        {
            Source = new Uri("avares://HomeVideoLibrary.UI/VcrTheme.axaml"),
        });
    }
}
