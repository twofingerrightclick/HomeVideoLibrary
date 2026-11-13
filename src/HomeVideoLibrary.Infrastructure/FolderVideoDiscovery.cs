using System.Diagnostics;

namespace HomeVideoLibrary.Infrastructure;

/// <summary>Candidate extensions shared by the file picker and recursive folder discovery. The importer validates media.</summary>
public static class VideoCandidates
{
    public static IReadOnlyList<string> Extensions { get; } = Array.AsReadOnly(new[]
        { ".mp4", ".m4v", ".mov", ".mkv", ".avi", ".mts", ".m2ts", ".ts", ".mpg", ".mpeg", ".vob", ".wmv", ".3gp", ".webm", ".dv", ".flv" });

    public static bool Contains(string path) => Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
}

public sealed record FolderScanProgress(int Candidates, int Skipped, int Failed);

/// <summary>Skipped counts individual non-video files, links and excluded subtrees, not their unseen contents.</summary>
public sealed class FolderScanResult
{
    public List<string> Candidates { get; } = [];
    public int Skipped { get; internal set; }
    public int Failed { get; internal set; }
    public bool Completed { get; internal set; }
    public List<string> Issues { get; } = [];
    public int OmittedIssues { get; private set; }

    public void AddIssue(string issue)
    {
        if (Issues.Count < 50) Issues.Add(issue);
        else OmittedIssues++;
    }
}

/// <summary>Walks folders on a worker, without following file or directory links or entering the active library.
/// Keeps candidate paths, traversal identity and bounded diagnostics; progress is throttled independently of directory size.</summary>
public sealed class FolderVideoDiscovery
{
    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private readonly Func<string, IEnumerable<string>> _entries;

    public FolderVideoDiscovery() : this(path => Directory.EnumerateFileSystemEntries(path)) { }
    internal FolderVideoDiscovery(Func<string, IEnumerable<string>> entries) => _entries = entries;

    public Task<FolderScanResult> ScanAsync(string folder, string libraryRoot, IProgress<FolderScanProgress>? progress = null,
        CancellationToken cancellationToken = default) => Task.Run(() => Scan(folder, libraryRoot, progress, cancellationToken));

    private FolderScanResult Scan(string folder, string libraryRoot, IProgress<FolderScanProgress>? progress, CancellationToken token)
    {
        var result = new FolderScanResult();
        var clock = Stopwatch.StartNew();
        void Report(bool force = false)
        {
            if (!force && clock.ElapsedMilliseconds < 100) return;
            progress?.Report(new(result.Candidates.Count, result.Skipped, result.Failed));
            clock.Restart();
        }
        try
        {
            token.ThrowIfCancellationRequested();
            // Resolve ancestors too: a library opened through an alias still excludes its physical directory.
            var excluded = ResolveDirectory(libraryRoot);
            var selected = Path.GetFullPath(folder);
            if ((File.GetAttributes(selected) & FileAttributes.ReparsePoint) != 0)
            {
                result.Skipped++;
                result.AddIssue($"{selected}: linked folders are skipped. Select the original folder.");
            }
            else
            {
                selected = ResolveDirectory(selected);
                var pending = new Stack<string>();
                pending.Push(selected);
                // Enumeration supplies the filesystem's actual names. Case-distinct entries must remain distinct,
                // including on case-sensitive macOS volumes (sensitivity is not a property of the operating system).
                var seen = new HashSet<string>(StringComparer.Ordinal);
                while (pending.TryPop(out var directory))
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        if (IsWithin(directory, excluded) || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                        {
                            result.Skipped++;
                            result.AddIssue($"{directory}: library folder or linked folder skipped.");
                            continue;
                        }
                        foreach (var entry in _entries(directory))
                        {
                            token.ThrowIfCancellationRequested();
                            if (!seen.Add(entry)) continue;
                            try
                            {
                                var attributes = File.GetAttributes(entry);
                                if ((attributes & FileAttributes.ReparsePoint) != 0)
                                {
                                    result.Skipped++;
                                    result.AddIssue($"{entry}: link skipped.");
                                }
                                else if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                                else if (VideoCandidates.Contains(entry)) result.Candidates.Add(entry);
                                else result.Skipped++;
                            }
                            catch (Exception error) when (IsAccessError(error)) { Failed(entry, error); }
                            Report();
                        }
                    }
                    catch (Exception error) when (IsAccessError(error)) { Failed(directory, error); }
                    Report();
                }
            }
            result.Completed = true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (IsAccessError(error)) { Failed(folder, error); result.Completed = true; }
        finally { Report(force: true); }
        return result;

        void Failed(string path, Exception error)
        {
            result.Failed++;
            result.AddIssue($"{path}: {error.Message}");
        }
    }

    /// <summary>Rechecks boundaries immediately before import, including parents replaced by links since discovery.</summary>
    public static bool CanImport(string source, string libraryRoot)
    {
        var path = Path.GetFullPath(source);
        var excluded = ResolveDirectory(libraryRoot);
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
        return !IsWithin(ResolveDirectory(Path.GetDirectoryName(path)!), excluded);
    }

    private static bool IsAccessError(Exception error) => error is IOException or UnauthorizedAccessException;

    private static bool IsWithin(string path, string root) => string.Equals(path, root, PathComparison) ||
        path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, PathComparison);

    private static string ResolveDirectory(string path, int links = 0)
    {
        // Darwin realpath returns actual filesystem spelling as well as resolving symlinks. This lets ordinal
        // comparison handle both case-sensitive and case-insensitive volumes, even with a differently cased picker path.
        if (OperatingSystem.IsMacOS())
            return Path.TrimEndingDirectorySeparator(FreedesktopTrash.RealPath(path)
                ?? throw new IOException($"Could not resolve directory: {path}"));
        if (links > 40) throw new IOException("Too many linked directories in this path.");
        var full = Path.GetFullPath(path);
        var current = Path.GetPathRoot(full)!;
        foreach (var part in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = new DirectoryInfo(Path.Combine(current, part));
            current = directory.ResolveLinkTarget(returnFinalTarget: true) is { } target
                ? ResolveDirectory(target.FullName, links + 1) : directory.FullName;
        }
        return Path.TrimEndingDirectorySeparator(current);
    }
}
