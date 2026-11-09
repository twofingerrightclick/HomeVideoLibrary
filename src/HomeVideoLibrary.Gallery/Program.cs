using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace HomeVideoLibrary.Gallery;

internal static class Program
{
    private const double Scale = 2;

    /// <summary>
    /// Runs the gallery window, or with <c>--screenshot &lt;dir&gt; [section-key...]</c> renders each
    /// section headlessly at 2x to <c>&lt;dir&gt;/&lt;key&gt;.png</c> and exits.
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
            var window = new Window
            {
                Width = 1180,
                SizeToContent = SizeToContent.Height,
                Background = (IBrush?)Application.Current!.FindResource("Vcr.Brush.Chassis"),
                Content = new Border { Padding = new Thickness(28), Child = content },
            };
            window.Show();
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
            var pixelSize = new PixelSize((int)(window.Bounds.Width * Scale), (int)(window.Bounds.Height * Scale));
            using (var bitmap = new RenderTargetBitmap(pixelSize, new Vector(96 * Scale, 96 * Scale)))
            {
                bitmap.Render(window);
                bitmap.Save(path);
            }

            Console.WriteLine(path);
            window.Close();
        }

        return 0;
    }
}

/// <summary>A section that needs simulated input (hover, typing) before its screenshot is taken.</summary>
public interface IScreenshotScript
{
    void Prepare(Window window);
}
