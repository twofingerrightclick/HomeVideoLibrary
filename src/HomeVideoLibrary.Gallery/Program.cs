using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Threading;

namespace HomeVideoLibrary.Gallery;

internal static class Program
{
    private const double Scale = 2;

    /// <summary>
    /// Runs the gallery window, or with <c>--screenshot &lt;dir&gt; [section-key...]</c> renders each
    /// section headlessly (2x unless it is an <see cref="IScreenshotScript"/>) to <c>&lt;dir&gt;/&lt;key&gt;.png</c> and exits.
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--screenshot")
        {
            return Screenshot(args[1], args[2..]);
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .LogToTrace();

    private static int Screenshot(string directory, string[] keys)
    {
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();

        Directory.CreateDirectory(directory);
        var sections = GallerySection.All.Where(s => keys.Length == 0 || keys.Contains(s.Key)).ToList();
        foreach (var section in sections)
        {
            var content = section.Create();

            // Overlay popups are positioned in unscaled window space, so scripted sections that open
            // them render at 1x; everything else is magnified for pixel-level review.
            var scale = content is IScreenshotScript ? 1 : Scale;
            var window = new Window
            {
                Width = 1180 * scale,
                Height = 600,
                Background = (IBrush?)Application.Current!.FindResource("Vcr.Brush.Chassis"),
                Content = new LayoutTransformControl
                {
                    LayoutTransform = new ScaleTransform(scale, scale),
                    Child = new Border { Padding = new Thickness(28), Child = content },
                },
            };
            window.Show();
            Pump();

            // The headless screen clamps SizeToContent to 1080px, so size the window explicitly.
            var root = (Control)window.Content!;
            root.Measure(new Size(window.Width, double.PositiveInfinity));
            window.Height = Math.Ceiling(root.DesiredSize.Height);
            for (var i = 0; i < 12; i++)
            {
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Thread.Sleep(15);
            }

            if (content is IScreenshotScript script)
            {
                script.Prepare(window);
                for (var i = 0; i < 12; i++)
                {
                    Dispatcher.UIThread.RunJobs();
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    Thread.Sleep(15);
                }
            }

            var path = Path.Combine(directory, section.Key + ".png");
            window.CaptureRenderedFrame()?.Save(path);
            Console.WriteLine(path);
            window.Close();
        }

        return 0;
    }

    private static void Pump()
    {
        for (var i = 0; i < 4; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }
}

/// <summary>A section that needs simulated input (hover, typing) before its screenshot is taken.</summary>
public interface IScreenshotScript
{
    void Prepare(Window window);
}
