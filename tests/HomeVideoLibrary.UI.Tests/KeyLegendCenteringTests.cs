using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HomeVideoLibrary.UI.Controls;
using Xunit;

namespace HomeVideoLibrary.UI.Tests;

/// <summary>
/// Off-center button text was a reported defect in the app, so these render real keys and measure
/// where the legend's ink lands on the cap face.
/// </summary>
public sealed class KeyLegendCenteringTests
{
    private const double Scale = 4;
    private const double ToleranceDip = 0.75;

    [AvaloniaTheory]
    [InlineData("plastic", "EJECT")]
    [InlineData("plastic", "OK")]
    [InlineData("plastic", "00:12")]
    [InlineData("plastic transport", "STOP")]
    [InlineData("plastic toolbar", "EDIT")]
    [InlineData("plastic primary", "SAVE")]
    [InlineData("rubber", "MENU")]
    [InlineData("rubber round", "8")]
    [InlineData("latch", "LOOP")]
    [InlineData("stock", "IMPORT")]
    public void Legend_ink_is_centered_on_the_key_cap(string kind, string legend)
    {
        var parts = kind.Split(' ');
        Button button = parts[0] switch
        {
            "plastic" => new PlasticButton(),
            "rubber" => new RubberButton(),
            "latch" => new LatchButton(),
            _ => new Button(),
        };
        button.Content = legend;
        button.Classes.AddRange(parts.Skip(1));

        var (ink, face) = Render(button);

        Assert.InRange(ink.Center.Y - face.Center.Y, -ToleranceDip, ToleranceDip);
        Assert.InRange(ink.Center.X - face.Center.X, -ToleranceDip, ToleranceDip);
    }

    private static (Rect Ink, Rect Face) Render(Button button)
    {
        button.HorizontalAlignment = HorizontalAlignment.Left;
        button.VerticalAlignment = VerticalAlignment.Top;
        button.MinWidth = 90;
        var window = new Window
        {
            Width = 600,
            Height = 300,
            Background = Brushes.Black,
            Content = new LayoutTransformControl
            {
                LayoutTransform = new ScaleTransform(Scale, Scale),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Child = new Border { Padding = new Thickness(6), Child = button },
            },
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        var cap = button.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PART_Cap");
        var origin = cap.TranslatePoint(default, window) ?? throw new InvalidOperationException("Cap is not in the window.");
        var face = new Rect(origin, cap.Bounds.Size * Scale);

        using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Nothing was rendered.");
        var ink = InkBounds(frame, face.Deflate(3 * Scale)) ?? throw new InvalidOperationException("No legend ink found.");
        window.Close();
        return (Shrink(ink), Shrink(face));
    }

    private static Rect Shrink(Rect pixels) =>
        new(pixels.X / Scale, pixels.Y / Scale, pixels.Width / Scale, pixels.Height / Scale);

    // Legends are light on a dark cap; anything brighter than the brightest cap shading counts as ink.
    private static Rect? InkBounds(WriteableBitmap frame, Rect search)
    {
        using var buffer = frame.Lock();
        var bytes = new byte[buffer.RowBytes * buffer.Size.Height];
        Marshal.Copy(buffer.Address, bytes, 0, bytes.Length);
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (var y = (int)search.Top; y < (int)search.Bottom; y++)
        {
            for (var x = (int)search.Left; x < (int)search.Right; x++)
            {
                var i = (y * buffer.RowBytes) + (x * 4);
                var luminance = (0.0722 * bytes[i]) + (0.7152 * bytes[i + 1]) + (0.2126 * bytes[i + 2]);
                if (luminance < 140)
                {
                    continue;
                }

                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }
        }

        return maxX < 0 ? null : new Rect(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }
}
