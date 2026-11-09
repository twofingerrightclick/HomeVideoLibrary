using System.Diagnostics;
using HomeVideoLibrary.Core;
using HomeVideoLibrary.Infrastructure.Editing;

namespace HomeVideoLibrary.Infrastructure;

/// <summary>Moves finished MKVs into <c>Videos/</c> and registers them so that no failure or crash loses a video the
/// catalog knows about or deletes one it might know about.
/// <list type="number">
/// <item>Each file is flushed and moved in as <c>Videos/.{id:N}.pending.mkv</c>; the directory is flushed.</item>
/// <item>The catalog insert commits the batch.</item>
/// <item>Each file is renamed to its catalog path; the directory is flushed.</item>
/// </list>
/// A pending file is only ever deleted when the catalog positively answers that its row does not exist during the
/// failed publication that made it. <see cref="RecoverAsync"/> finishes pending files whose rows exist and moves the
/// others to <c>.cache/orphaned/</c>, since a lost commit (power loss) also leaves a file without a row.</summary>
public static class LibraryPublisher
{
    private const string PendingSuffix = ".pending.mkv";
    private static readonly TimeSpan[] RenameRetries =
        [TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)];

    public static string OrphanedDirectory(LibraryPaths paths) => Path.Combine(paths.CacheDirectory, "orphaned");

    /// <summary>Publishes all <paramref name="items"/> in one catalog transaction, or none of them. Each staged file must
    /// be on the library's volume.</summary>
    /// <exception cref="IOException">The rows are committed but a file could not be given its final name; it stays
    /// pending and <see cref="RecoverAsync"/> finishes it when the library is next opened.</exception>
    public static async Task PublishAsync(LibraryCatalog catalog, IReadOnlyList<(LibraryVideo Video, string Staged)> items,
        CancellationToken cancellationToken)
    {
        var pending = new List<(LibraryVideo Video, string Path)>();
        try
        {
            foreach (var (video, staged) in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await using (var stream = new FileStream(staged, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    stream.Flush(flushToDisk: true);
                var path = PendingPath(catalog.Paths, video.Id);
                File.Move(staged, path, overwrite: false);
                pending.Add((video, path));
            }
            DirectorySync.Flush(catalog.Paths.VideosDirectory);
            await catalog.AddBatchAsync(items.Select(item => item.Video).ToArray(), cancellationToken);
        }
        catch (Exception failure)
        {
            if (!await RollBackAsync(catalog, pending, failure)) throw;
        }
        var unfinished = new List<string>();
        foreach (var (video, path) in pending)
            if (!await CompleteAsync(catalog, path, video.RelativePath)) unfinished.Add(video.DisplayName);
        DirectorySync.Flush(catalog.Paths.VideosDirectory);
        if (unfinished.Count > 0)
            throw new IOException($"Saved {string.Join(", ", unfinished)}, but the file could not be put in place yet. " +
                "It will be finished when the library is next opened.");
    }

    /// <summary>Finishes publications a crash interrupted after the catalog commit and quarantines pending files whose
    /// rows do not exist; files whose state the catalog cannot answer stay pending. Running it again changes nothing.
    /// Requires the library lock, so no publication of this run can be in flight.</summary>
    /// <returns>How many files were moved to <see cref="OrphanedDirectory"/>.</returns>
    public static async Task<int> RecoverAsync(LibraryCatalog catalog, LibraryLock owner, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (!Directory.Exists(catalog.Paths.VideosDirectory)) return 0;
        var quarantined = 0;
        foreach (var path in Directory.GetFiles(catalog.Paths.VideosDirectory, ".*" + PendingSuffix))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(path);
            if (!Guid.TryParseExact(name[1..^PendingSuffix.Length], "N", out var id)) continue;
            bool committed;
            try { committed = await catalog.ContainsVideoAsync(id, cancellationToken); }
            catch (Exception exception) when (exception is Microsoft.Data.Sqlite.SqliteException or IOException)
            {
                Trace.TraceWarning($"Could not check whether {name} was committed; leaving it pending. {exception.Message}");
                continue;
            }
            if (committed) await CompleteAsync(catalog, path, LibraryPaths.VideoRelativePath(id));
            else if (Quarantine(catalog.Paths, path)) quarantined++;
        }
        DirectorySync.Flush(catalog.Paths.VideosDirectory);
        return quarantined;
    }

    /// <summary>Handles a failed publication. Returns true when the batch turns out to be committed (the failure came
    /// after the commit), so publishing continues; otherwise deletes the pending files the catalog confirms it does not
    /// reference and returns false. When the catalog cannot answer, the files stay pending for recovery.</summary>
    private static async Task<bool> RollBackAsync(LibraryCatalog catalog, IReadOnlyList<(LibraryVideo Video, string Path)> pending, Exception failure)
    {
        if (pending.Count == 0) return false;
        var committed = new List<bool>();
        try
        {
            foreach (var (video, _) in pending) committed.Add(await catalog.ContainsVideoAsync(video.Id, CancellationToken.None));
        }
        catch (Exception exception) when (exception is Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            Trace.TraceError($"Publication failed ({failure.Message}) and its catalog state is unknown; leaving the files pending. {exception.Message}");
            return false;
        }
        if (committed.All(row => row))
        {
            Trace.TraceWarning($"Publication reported a failure after its catalog commit; finishing it. {failure}");
            return true;
        }
        // Rows that exist (impossible for one transaction, but never assumed) stay pending for recovery to finish.
        foreach (var ((_, path), row) in pending.Zip(committed))
            if (!row) ScratchFiles.TryDelete(path);
        return false;
    }

    private static string PendingPath(LibraryPaths paths, Guid id) => Path.Combine(paths.VideosDirectory, $".{id:N}{PendingSuffix}");

    /// <summary>Renames a committed video to its catalog path, retrying while another process (an indexer or virus
    /// scanner) briefly holds the new file.</summary>
    private static async Task<bool> CompleteAsync(LibraryCatalog catalog, string pending, string relativePath)
    {
        var destination = catalog.GetVideoPath(relativePath);
        foreach (var delay in RenameRetries.Prepend(TimeSpan.Zero))
        {
            await Task.Delay(delay, CancellationToken.None);
            try
            {
                File.Move(pending, destination, overwrite: false);
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Trace.TraceWarning($"Could not rename {pending} to {destination} yet: {exception.Message}");
            }
        }
        return false;
    }

    private static bool Quarantine(LibraryPaths paths, string pending)
    {
        var directory = OrphanedDirectory(paths);
        try
        {
            Directory.CreateDirectory(directory);
            File.Move(pending, Path.Combine(directory, Path.GetFileName(pending).TrimStart('.').Replace(PendingSuffix, ".mkv", StringComparison.Ordinal)),
                overwrite: false);
            DirectorySync.Flush(directory);
            Trace.TraceWarning($"{pending} has no catalog row; moved it to {directory}.");
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"Could not move {pending} to {directory}; leaving it pending. {exception.Message}");
            return false;
        }
    }
}
