namespace HomeVideoLibrary.Infrastructure;

public sealed record MediaTools(string Ffmpeg, string Ffprobe)
{
    public string MkvExtract { get; init; } = Find("mkvextract");
    public string MkvMerge { get; init; } = Find("mkvmerge");
    public string MkvPropEdit { get; init; } = Find("mkvpropedit");
    public static MediaTools Discover() => new(Find("ffmpeg"), Find("ffprobe"));

    // TODO: probe each tool's version with a timeout and cache the result (chunk 2 of the overhaul).
    public Task<IReadOnlyList<string>> FindUnavailableAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>([]);

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
