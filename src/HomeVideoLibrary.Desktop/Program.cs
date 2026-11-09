using Avalonia;

namespace HomeVideoLibrary.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        AppErrors.InstallProcessHandlers();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .LogToTrace();
}
