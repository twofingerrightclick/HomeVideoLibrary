using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Xunit;

namespace HomeVideoLibrary.UI.Tests;

/// <summary>The primary key's amber legend must stay legible (WCAG AA, 4.5:1) on every shade of its cap in every state.</summary>
public sealed class PrimaryKeyContrastTests
{
    [AvaloniaTheory]
    [InlineData("Vcr.Brush.Plastic.Primary")]
    [InlineData("Vcr.Brush.Plastic.Primary.Hover")]
    [InlineData("Vcr.Brush.Plastic.Primary.Pressed")]
    public void The_legend_reads_at_4_5_to_1_on_every_shade_of_the_cap(string capKey)
    {
        var legend = Resource<ISolidColorBrush>("Vcr.Brush.Primary").Color;

        foreach (var stop in Resource<IGradientBrush>(capKey).GradientStops)
        {
            var ratio = Contrast(legend, stop.Color);
            Assert.True(ratio >= 4.5, $"{legend} on {stop.Color} is {ratio:0.00}:1");
        }
    }

    private static T Resource<T>(string key) =>
        Application.Current!.TryGetResource(key, ThemeVariant.Dark, out var value) && value is T resource
            ? resource
            : throw new InvalidOperationException($"{key} is not a {typeof(T).Name}.");

    private static double Contrast(Color a, Color b)
    {
        var (lighter, darker) = (Luminance(a), Luminance(b)) is var (x, y) && x > y ? (x, y) : (y, x);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double Luminance(Color color) =>
        (0.2126 * Linear(color.R)) + (0.7152 * Linear(color.G)) + (0.0722 * Linear(color.B));

    private static double Linear(byte channel)
    {
        var c = channel / 255.0;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }
}
