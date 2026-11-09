namespace HomeVideoLibrary.Infrastructure;

public static class MediaErrors
{
    private const int MaxLength = 300;

    /// <summary>A short user-facing summary of tool stderr: the last <paramref name="lines"/> non-empty lines,
    /// at most 300 characters. Log the full stderr separately.</summary>
    public static string Summarize(string stderr, int lines = 2)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lines);
        var tail = (stderr ?? "").Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .TakeLast(lines);
        var summary = string.Join(" ", tail);
        return summary.Length <= MaxLength ? summary : "…" + summary[^(MaxLength - 1)..];
    }
}
