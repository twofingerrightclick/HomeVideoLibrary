using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;

namespace HomeVideoLibrary.Infrastructure;

public sealed partial record MediaTools(string Ffmpeg, string Ffprobe)
{
    /// <summary>ffmpeg 6.0 has the per-stream frame timing and encoder time base options decoding checks rely on
    /// (<c>-fps_mode</c>, <c>-enc_time_base demux</c>).</summary>
    public static readonly Version MinimumFfmpeg = new(6, 0);

    /// <summary>MKVToolNix 50 has everything the edits use: append with generated chapters, v2 timestamp files and
    /// <c>--ui-language en_US</c>.</summary>
    public static readonly Version MinimumMkvToolNix = new(50, 0);

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);
    // Only successes are cached: a failure may be a slow first launch or a tool installed while the app runs.
    private static readonly ConcurrentDictionary<string, bool> KnownAvailable = new();

    public string MkvExtract { get; init; } = Find("mkvextract");
    public string MkvMerge { get; init; } = Find("mkvmerge");
    public string MkvPropEdit { get; init; } = Find("mkvpropedit");
    public static MediaTools Discover() => new(Find("ffmpeg"), Find("ffprobe"));

    /// <summary>The tools that do not answer a version query within 10 s, by file name, or that are older than the
    /// minimum, named with the version required (for example "ffmpeg 6.0 or newer"). A tool that passed once is not
    /// probed again in this process; one that failed is probed again on the next call. Cancellation stops the wait,
    /// not the probes.</summary>
    public async Task<IReadOnlyList<string>> FindUnavailableAsync(CancellationToken cancellationToken = default)
    {
        (string Path, string VersionArgument, Version Minimum)[] tools =
        [
            (Ffmpeg, "-version", MinimumFfmpeg), (Ffprobe, "-version", MinimumFfmpeg), (MkvMerge, "--version", MinimumMkvToolNix),
            (MkvExtract, "--version", MinimumMkvToolNix), (MkvPropEdit, "--version", MinimumMkvToolNix),
        ];
        var problems = await Task.WhenAll(tools.Select(tool => CheckAsync(tool.Path, tool.VersionArgument, tool.Minimum))).WaitAsync(cancellationToken);
        return problems.OfType<string>().ToArray();
    }

    /// <summary>The version a tool's version banner states ("ffmpeg version 9.0.2", "ffmpeg version n6.1",
    /// "mkvmerge v103.0"), or null for builds that do not state one, such as ffmpeg development snapshots.</summary>
    internal static Version? ParseVersion(string banner) =>
        VersionPattern().Match(banner) is { Success: true } match
            ? new Version(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture))
            : null;

    private static async Task<string?> CheckAsync(string executable, string versionArgument, Version minimum)
    {
        if (KnownAvailable.ContainsKey(executable)) return null;
        var name = Path.GetFileName(executable);
        MediaProcessResult result;
        try { result = await new MediaProcess().RunAsync(executable, [versionArgument], ProbeTimeout); }
        catch (Exception exception) when (exception is InvalidOperationException or TimeoutException or IOException)
        {
            System.Diagnostics.Trace.TraceWarning($"{executable} is unavailable: {exception.Message}");
            return name;
        }
        if (result.ExitCode != 0) return name;
        if (ParseVersion(result.StandardOutput) is { } version && version < minimum)
        {
            System.Diagnostics.Trace.TraceWarning($"{executable} is version {version}; {minimum} or newer is required.");
            return $"{name} {minimum} or newer";
        }
        KnownAvailable.TryAdd(executable, true);
        return null;
    }

    [GeneratedRegex(@"(?:version n?|\bv)(\d+)\.(\d+)")]
    private static partial Regex VersionPattern();

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
