using System.Collections.Concurrent;

namespace HomeVideoLibrary.Infrastructure;

public sealed record MediaTools(string Ffmpeg, string Ffprobe)
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);
    // Only successes are cached: a failure may be a slow first launch or a tool installed while the app runs.
    private static readonly ConcurrentDictionary<string, bool> KnownAvailable = new();

    public string MkvExtract { get; init; } = Find("mkvextract");
    public string MkvMerge { get; init; } = Find("mkvmerge");
    public string MkvPropEdit { get; init; } = Find("mkvpropedit");
    public static MediaTools Discover() => new(Find("ffmpeg"), Find("ffprobe"));

    /// <summary>File names of the tools that do not answer a version query within 10 s. A tool that answered once is not
    /// probed again in this process; one that failed is probed again on the next call. Cancellation stops the wait,
    /// not the probes.</summary>
    public async Task<IReadOnlyList<string>> FindUnavailableAsync(CancellationToken cancellationToken = default)
    {
        (string Path, string VersionArgument)[] tools =
            [(Ffmpeg, "-version"), (Ffprobe, "-version"), (MkvMerge, "--version"), (MkvExtract, "--version"), (MkvPropEdit, "--version")];
        var available = await Task.WhenAll(tools.Select(tool => IsAvailableAsync(tool.Path, tool.VersionArgument))).WaitAsync(cancellationToken);
        return tools.Where((_, index) => !available[index]).Select(tool => Path.GetFileName(tool.Path)).ToArray();
    }

    private static async Task<bool> IsAvailableAsync(string executable, string versionArgument)
    {
        if (KnownAvailable.ContainsKey(executable)) return true;
        var available = await ProbeAsync(executable, versionArgument);
        if (available) KnownAvailable.TryAdd(executable, true);
        return available;
    }

    private static async Task<bool> ProbeAsync(string executable, string versionArgument)
    {
        try { return (await new MediaProcess().RunAsync(executable, [versionArgument], ProbeTimeout)).ExitCode == 0; }
        catch (Exception exception) when (exception is InvalidOperationException or TimeoutException or IOException)
        {
            System.Diagnostics.Trace.TraceWarning($"{executable} is unavailable: {exception.Message}");
            return false;
        }
    }

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
