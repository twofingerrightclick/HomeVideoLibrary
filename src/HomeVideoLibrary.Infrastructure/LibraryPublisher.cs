using System.Diagnostics;
using System.Globalization;
using HomeVideoLibrary.Core;
using HomeVideoLibrary.Infrastructure.Editing;
using Microsoft.Data.Sqlite;

namespace HomeVideoLibrary.Infrastructure;

/// <param name="Unfinished">Videos that are registered and playable but still under their pending name, because the
/// final rename was refused; recovery finishes them.</param>
public sealed record PublishResult(IReadOnlyList<Guid> Unfinished)
{
    public const string UnfinishedNotice = "Saved; the file keeps a temporary name until the library is refreshed or reopened.";
}

/// <param name="Finished">Committed videos given their final names.</param>
/// <param name="Quarantined">Pending files without a catalog row, moved to <see cref="LibraryPublisher.OrphanedDirectory"/>.</param>
/// <param name="Stuck">Pending files that could not be finished or moved, left in place for the next recovery.</param>
/// <param name="Unregistered">Video files under their final names with no catalog row, written since the last
/// recovery: what a lost commit would leave. Reported only; library membership belongs to the catalog.</param>
public sealed record PublishRecovery(int Finished, int Quarantined, int Stuck, IReadOnlyList<string> Unregistered)
{
    public static PublishRecovery None { get; } = new(0, 0, 0, []);
}

/// <summary>Moves finished MKVs into <c>Videos/</c> and registers them, following the temp-file, flush, rename pattern
/// (as in Maildir delivery) with the catalog row as the commit record:
/// <list type="number">
/// <item>Each file is flushed and durably moved in under its pending name (<see cref="LibraryPaths.PendingVideoPath"/>),
/// which records the intent.</item>
/// <item>The catalog insert commits the batch, durably (<see cref="LibraryCatalog.AddBatchDurablyAsync"/>).</item>
/// <item>Each file is durably renamed to its catalog path.</item>
/// </list>
/// Because the commit is on disk before any final rename, a crash or power loss leaves either a pending file without a
/// row (quarantined, never deleted) or a row whose file is pending (finished, and readable meanwhile through
/// <see cref="LibraryCatalog.GetVideoPath"/>). A pending file is deleted only when, during the publication that made it,
/// the catalog positively answers that its row does not exist.</summary>
public static class LibraryPublisher
{
    private static readonly TimeSpan[] RenameRetries =
        [TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)];

    public static string OrphanedDirectory(LibraryPaths paths) => Path.Combine(paths.CacheDirectory, "orphaned");

    private static string RecoveryStamp(LibraryPaths paths) => Path.Combine(paths.CacheDirectory, "publish-recovered");

    /// <summary>Publishes all <paramref name="items"/> in one catalog transaction, or none of them. Each staged file must
    /// be on the library's volume. A final rename that keeps failing does not undo the publication: the result lists
    /// those videos, which are registered and readable under their pending names.</summary>
    public static async Task<PublishResult> PublishAsync(LibraryCatalog catalog, IReadOnlyList<(LibraryVideo Video, string Staged)> items,
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
                var path = catalog.Paths.PendingVideoPath(video.Id);
                DurableFile.Move(staged, path);
                pending.Add((video, path));
            }
            await catalog.AddBatchDurablyAsync(items.Select(item => item.Video).ToArray(), cancellationToken);
        }
        catch (Exception failure)
        {
            if (!await RollBackAsync(catalog, pending, failure)) throw;
        }
        var unfinished = new List<Guid>();
        foreach (var (video, path) in pending)
            if (!await TryFinishAsync(path, catalog.GetFinalVideoPath(video.RelativePath), retry: true))
                unfinished.Add(video.Id);
        return new PublishResult(unfinished);
    }

    /// <summary>Finishes committed publications whose final rename has not happened yet. Safe while other publications
    /// run: it never touches a pending file without a row.</summary>
    /// <returns>How many were finished.</returns>
    public static async Task<int> FinishCommittedAsync(LibraryCatalog catalog, CancellationToken cancellationToken = default) =>
        (await ReconcileAsync(catalog, quarantine: false, cancellationToken)).Finished;

    /// <summary>Recovery for library open, which must run before scratch is swept: finishes committed publications
    /// (including a committed video whose move out of scratch did not survive a power loss, found by the id in its
    /// staged name), quarantines pending files without a row (a lost commit looks the same as an abandoned one, so
    /// nothing is deleted), and reports final-named files written since the last recovery that have no row. Running it
    /// again changes nothing. Requires the library lock, so no publication of this run can be in flight.</summary>
    public static async Task<PublishRecovery> RecoverAsync(LibraryCatalog catalog, LibraryLock owner, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var recovery = await ReconcileAsync(catalog, quarantine: true, cancellationToken);
        var adopted = await FinishFromScratchAsync(catalog, cancellationToken);
        return recovery with { Finished = recovery.Finished + adopted, Unregistered = await FindUnregisteredAsync(catalog, cancellationToken) };
    }

    /// <summary>The name a staged file must have before <see cref="PublishAsync"/>, so recovery can tie it to its row.</summary>
    public static string StagedName(Guid id) => $"{id:N}.mkv";

    /// <summary>Moves staged files whose rows are committed but which are neither final nor pending into place.</summary>
    private static async Task<int> FinishFromScratchAsync(LibraryCatalog catalog, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(catalog.Paths.ScratchDirectory)) return 0;
        var finished = 0;
        foreach (var staged in Directory.EnumerateFiles(catalog.Paths.ScratchDirectory, "*.mkv", SearchOption.AllDirectories))
        {
            var name = Path.GetFileNameWithoutExtension(staged);
            if (!Guid.TryParseExact(name.StartsWith("import-", StringComparison.Ordinal) ? name[7..] : name, "N", out var id)) continue;
            try
            {
                if (await catalog.GetRelativePathAsync(id, cancellationToken) is not { } relativePath) continue;
                var final = catalog.GetFinalVideoPath(relativePath);
                if (File.Exists(final) || File.Exists(catalog.Paths.PendingVideoPath(id))) continue;
                DurableFile.Move(staged, final);
                Trace.TraceWarning($"Recovered committed video {relativePath} from {staged}.");
                finished++;
            }
            catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException or ArgumentException)
            {
                Trace.TraceWarning($"Could not recover {staged}: {exception.Message}");
            }
        }
        return finished;
    }

    private static async Task<PublishRecovery> ReconcileAsync(LibraryCatalog catalog, bool quarantine, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(catalog.Paths.VideosDirectory)) return PublishRecovery.None;
        int finished = 0, quarantined = 0, stuck = 0;
        foreach (var path in Directory.GetFiles(catalog.Paths.VideosDirectory, ".*.pending.mkv"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(path);
            if (!Guid.TryParseExact(name[1..name.IndexOf('.', 1)], "N", out var id) || path != catalog.Paths.PendingVideoPath(id)) continue;
            string? destination;
            try
            {
                destination = await catalog.GetRelativePathAsync(id, cancellationToken) is { } relativePath
                    ? catalog.GetFinalVideoPath(relativePath) : null;
            }
            catch (Exception exception) when (exception is SqliteException or IOException or ArgumentException)
            {
                Trace.TraceWarning($"Could not tell where {name} belongs; leaving it pending. {exception.Message}");
                stuck++;
                continue;
            }
            if (destination is not null)
            {
                if (await TryFinishAsync(path, destination, retry: false)) finished++;
                else stuck++;
            }
            else if (quarantine)
            {
                if (Quarantine(catalog.Paths, path, id)) quarantined++;
                else stuck++;
            }
        }
        return new PublishRecovery(finished, quarantined, stuck, []);
    }

    /// <summary>Final-named videos with no row, written after the previous recovery (the first run only records the
    /// time). Advances the recovery time stamp.</summary>
    private static async Task<IReadOnlyList<string>> FindUnregisteredAsync(LibraryCatalog catalog, CancellationToken cancellationToken)
    {
        var stamp = RecoveryStamp(catalog.Paths);
        var now = DateTime.UtcNow;
        DateTime? since = File.Exists(stamp) && long.TryParse(await File.ReadAllTextAsync(stamp, cancellationToken), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var ticks) ? new DateTime(ticks, DateTimeKind.Utc) : null;
        IReadOnlyList<string> unregistered = [];
        if (since is { } previous)
        {
            var registered = await catalog.ListRelativePathsAsync(cancellationToken);
            unregistered = new DirectoryInfo(catalog.Paths.VideosDirectory).EnumerateFiles("*.mkv")
                .Where(file => !file.Name.StartsWith('.') && file.LastWriteTimeUtc >= previous && !registered.Contains("Videos/" + file.Name))
                .Select(file => file.Name).Order(StringComparer.Ordinal).ToArray();
        }
        Directory.CreateDirectory(catalog.Paths.CacheDirectory);
        await File.WriteAllTextAsync(stamp, now.Ticks.ToString(CultureInfo.InvariantCulture), cancellationToken);
        foreach (var name in unregistered) Trace.TraceWarning($"Videos/{name} is not registered in the catalog.");
        return unregistered;
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
        catch (Exception exception) when (exception is SqliteException or IOException)
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

    /// <summary>Renames a committed video to its catalog path. Another finisher may win the race, which counts as
    /// finished; only a destination occupied while the pending file still exists is stuck, and it is not retried.
    /// With <paramref name="retry"/>, sharing and access errors (an indexer or virus scanner holding the new file) are
    /// retried for a few seconds, as only the publication that just made the file should wait.</summary>
    private static async Task<bool> TryFinishAsync(string pending, string destination, bool retry)
    {
        foreach (var delay in retry ? RenameRetries.Prepend(TimeSpan.Zero) : [TimeSpan.Zero])
        {
            await Task.Delay(delay, CancellationToken.None);
            if (!File.Exists(pending)) return File.Exists(destination);
            if (File.Exists(destination))
            {
                if (!File.Exists(pending)) return true;
                Trace.TraceError($"Cannot finish {pending}: {destination} already exists.");
                return false;
            }
            try
            {
                DurableFile.Move(pending, destination);
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (!File.Exists(pending)) return File.Exists(destination);
                Trace.TraceWarning($"Could not rename {pending} to {destination} yet: {exception.Message}");
            }
        }
        return false;
    }

    private static bool Quarantine(LibraryPaths paths, string pending, Guid id)
    {
        var directory = OrphanedDirectory(paths);
        var target = Path.Combine(directory, $"{id:N}-{DateTime.UtcNow:yyyyMMddHHmmssfff}.mkv");
        try
        {
            Directory.CreateDirectory(directory);
            DurableFile.Move(pending, target);
            Trace.TraceWarning($"{pending} has no catalog row; moved it to {target}.");
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"Could not move {pending} to {directory}; leaving it pending. {exception.Message}");
            return false;
        }
    }
}
