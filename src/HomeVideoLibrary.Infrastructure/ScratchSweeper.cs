using System.Diagnostics;

namespace HomeVideoLibrary.Infrastructure;

/// <summary>Removes temporary files that an earlier, interrupted run left behind.</summary>
public static class ScratchSweeper
{
    /// <summary>Deletes scratch and preview entries, plus the staging names older versions wrote into <c>Videos/</c>, that
    /// were last written before <paramref name="owner"/> was acquired. The lock guarantees no other instance is using
    /// them, and the time bound keeps this instance's own work safe. Library MKVs (<c>Videos/*.mkv</c> without a leading
    /// dot) and metadata recovery snapshots never match. Failures are traced, not thrown.</summary>
    public static Task SweepAsync(LibraryLock owner, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var paths = owner.Paths;
        var before = owner.AcquiredAt.UtcDateTime;
        return Task.Run(() =>
        {
            SweepChildren(paths.ScratchDirectory, "*", before, cancellationToken);
            SweepChildren(paths.PreviewDirectory, "*", before, cancellationToken);
            SweepChildren(paths.CacheDirectory, "metadata-*", before, cancellationToken);
            SweepChildren(paths.VideosDirectory, ".*.import.mkv", before, cancellationToken);
            SweepChildren(paths.VideosDirectory, ".*.metadata.mkv", before, cancellationToken);
            SweepChildren(paths.VideosDirectory, ".edit-*", before, cancellationToken);
        }, cancellationToken);
    }

    private static void SweepChildren(string directory, string pattern, DateTime beforeUtc, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory)) return;
        foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos(pattern))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (LatestWriteUtc(entry) >= beforeUtc) continue;
                if (entry is DirectoryInfo folder) folder.Delete(recursive: true);
                else entry.Delete();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Trace.TraceWarning($"Could not remove stale temporary entry {entry.FullName}: {exception.Message}");
            }
        }
    }

    /// <summary>A directory counts as fresh while anything inside it is being written.</summary>
    private static DateTime LatestWriteUtc(FileSystemInfo entry) => entry is DirectoryInfo folder
        ? folder.EnumerateFileSystemInfos("*", SearchOption.AllDirectories).Select(child => child.LastWriteTimeUtc)
            .Append(folder.LastWriteTimeUtc).Max()
        : entry.LastWriteTimeUtc;
}
