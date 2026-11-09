namespace HomeVideoLibrary.Infrastructure;

public sealed record MediaTools(string Ffmpeg, string Ffprobe)
{
    public static MediaTools Discover() => new(Find("ffmpeg"), Find("ffprobe"));

    private static string Find(string name)
    {
        // GUI apps on macOS do not normally inherit Homebrew's PATH.
        if (OperatingSystem.IsMacOS())
        {
            foreach (var root in new[] { "/opt/homebrew/bin", "/usr/local/bin" })
            {
                var candidate = Path.Combine(root, name);
                if (File.Exists(candidate))
                    return candidate;
            }
        }
        return name;
    }
}
