using System.Globalization;

namespace HomeVideoLibrary.Core;

public static class Timecode
{
    // TODO: accept m:ss[.f] and h:mm:ss[.f] input (chunk 2 of the overhaul).
    public static bool TryParse(string? text, out TimeSpan value)
    {
        value = TimeSpan.Zero;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ||
            !double.IsFinite(seconds) || seconds < 0 || seconds >= TimeSpan.MaxValue.TotalSeconds)
            return false;
        value = TimeSpan.FromSeconds(seconds);
        return true;
    }

    /// <summary>Formats as <c>h:mm:ss.fff</c>.</summary>
    public static string Format(TimeSpan value) =>
        $"{(long)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}.{value.Milliseconds:000}";
}
