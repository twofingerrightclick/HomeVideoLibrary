using System.Collections.Concurrent;

namespace HomeVideoLibrary.Infrastructure;

public sealed record MediaTools(string Ffmpeg, string Ffprobe)
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);
    private static readonly ConcurrentDictionary<string, Lazy<Task<bool>>> Availability = new();

    public string MkvExtract { get; init; } = Find("mkvextract");
    public string MkvMerge { get; init; } = Find("mkvmerge");
    public string MkvPropEdit { get; init; } = Find("mkvpropedit");
    public static MediaTools Discover() => new(Find("ffmpeg"), Find("ffprobe"));

    /// <summary>File names of the tools that do not answer a version query within 10 s. Each executable is probed once
    /// per process; cancellation stops the wait, not the shared probe.</summary>
    public async Task<IReadOnlyList<string>> FindUnavailableAsync(CancellationToken cancellationToken = default)
    {
        (string Path, string VersionArgument)[] tools =
            [(Ffmpeg, "-version"), (Ffprobe, "-version"), (MkvMerge, "--version"), (MkvExtract, "--version"), (MkvPropEdit, "--version")];
        var probes = tools.Select(tool => Availability
            .GetOrAdd(tool.Path, _ => new Lazy<Task<bool>>(() => ProbeAsync(tool.Path, tool.VersionArgument))).Value).ToArray();
        var available = await Task.WhenAll(probes).WaitAsync(cancellationToken);
        return tools.Where((_, index) => !available[index]).Select(tool => Path.GetFileName(tool.Path)).ToArray();
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
