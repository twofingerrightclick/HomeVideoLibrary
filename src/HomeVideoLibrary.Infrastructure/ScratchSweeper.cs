using System.Diagnostics;
using HomeVideoLibrary.Core;

namespace HomeVideoLibrary.Infrastructure;

/// <summary>Removes temporary files that an earlier, interrupted run left behind.</summary>
public static class ScratchSweeper
{
    /// <summary>Deletes scratch and preview entries, plus the staging names older versions wrote into <c>Videos/</c>, whose last
    /// write is before <paramref name="processStartUtc"/>, so this run's own work is never touched. Library MKVs
    /// (<c>Videos/*.mkv</c> without a leading dot) never match. Failures are traced, not thrown.</summary>
    public static Task SweepAsync(LibraryPaths paths, DateTime processStartUtc, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            SweepChildren(paths.ScratchDirectory, "*", processStartUtc, cancellationToken);
            SweepChildren(paths.PreviewDirectory, "*", processStartUtc, cancellationToken);
            SweepChildren(paths.CacheDirectory, "metadata-*", processStartUtc, cancellationToken);
            SweepChildren(paths.VideosDirectory, ".*.import.mkv", processStartUtc, cancellationToken);
            SweepChildren(paths.VideosDirectory, ".*.metadata.mkv", processStartUtc, cancellationToken);
            SweepChildren(paths.VideosDirectory, ".edit-*", processStartUtc, cancellationToken);
        }, cancellationToken);

    private static void SweepChildren(string directory, string pattern, DateTime processStartUtc, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory)) return;
        foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos(pattern))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (LatestWriteUtc(entry) >= processStartUtc) continue;
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
