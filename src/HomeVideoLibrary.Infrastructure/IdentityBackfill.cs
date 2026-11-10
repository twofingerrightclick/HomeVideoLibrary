using System.Diagnostics;
using HomeVideoLibrary.Core;

namespace HomeVideoLibrary.Infrastructure;

public interface IIdentityWriter
{
    /// <summary>See <see cref="MatroskaMetadataService.FillMissingIdentityAsync"/>; false when nothing was missing.</summary>
    Task<bool> FillMissingIdentityAsync(LibraryVideo video, CancellationToken cancellationToken = default);
}

/// <param name="Updated">Videos whose file was written.</param>
/// <param name="Deferred">Videos that were busy (open on a page) and should be checked again later.</param>
/// <param name="Skipped">Videos that could not be checked (their file could not be read, or the write failed), with the
/// reason. Each is tried again on a later run, up to <see cref="IdentityBackfill.MaxAttempts"/> times.</param>
public sealed record BackfillSlice(int Checked, int Updated, IReadOnlyList<Guid> Deferred, IReadOnlyList<string> Skipped);

/// <summary>The one-time pass that writes TITLE and DATE_RECORDED into videos no import of this app wrote them into, so
/// a video file that leaves the library (for the Trash) still says what it is. It covers the videos the library had when
/// it first ran, in slices so other work can go between them, and checks each at most once: a video is handled once its
/// file was written, found complete or found gone, and a value the user clears afterwards is never filled in again. Until
/// every video is handled or given up on, each library open continues with the rest.</summary>
/// <remarks>Progress lives in <c>.cache</c>, like <c>publish-recovered</c>: a per-library fact that needs no catalog
/// schema. The <see cref="StampPath"/> file marks the pass done; before that, <see cref="PendingPath"/> lists the videos
/// still to check with their failed attempts.</remarks>
public sealed class IdentityBackfill(LibraryModel model, IIdentityWriter writer)
{
    public const int SliceSize = 25;

    /// <summary>Checks of one video that may fail before the pass gives up on it.</summary>
    public const int MaxAttempts = 3;

    private Dictionary<Guid, int>? _pending;

    public static string StampPath(LibraryPaths paths) => Path.Combine(paths.CacheDirectory, "identity-backfill.done");

    public static string PendingPath(LibraryPaths paths) => Path.Combine(paths.CacheDirectory, "identity-backfill.pending");

    /// <summary>Whether the library has videos and has not completed the pass.</summary>
    public bool IsDue => !model.Snapshot.Videos.IsEmpty && !File.Exists(StampPath(model.Catalog.Paths));

    /// <summary>The videos still to check, newest first; records the pass as done when none are left.</summary>
    public async Task<IReadOnlyList<Guid>> PlanAsync()
    {
        var pending = Pending();
        var snapshot = model.Snapshot;
        foreach (var gone in pending.Keys.Where(id => snapshot.Find(id) is null).ToArray()) pending.Remove(gone);
        if (pending.Count == 0) await SaveAsync();
        return snapshot.Chronological.Select(record => record.Id).Where(pending.ContainsKey).ToArray();
    }

    /// <summary>Checks <paramref name="videoIds"/>, publishing each video it wrote, and records which are handled. A video
    /// that has left the library, or whose file the system says is gone, is handled with nothing to write; one
    /// <paramref name="isBusy"/> claims is deferred, so the pass never writes under an open page's save. Per-video failures
    /// are collected, not thrown.</summary>
    /// <param name="isBusy">Called from this method's thread.</param>
    /// <param name="progress">Videos of this slice checked so far and the one being checked.</param>
    public async Task<BackfillSlice> RunAsync(IReadOnlyList<Guid> videoIds, Func<Guid, bool> isBusy,
        IProgress<(int Done, string Label)>? progress = null, CancellationToken cancellationToken = default)
    {
        var pending = Pending();
        var deferred = new List<Guid>();
        var skipped = new List<string>();
        var updated = 0;
        void Skip(Guid id, string reason)
        {
            skipped.Add(reason);
            if (!pending.TryGetValue(id, out var attempts)) return;
            if (attempts + 1 < MaxAttempts) pending[id] = attempts + 1;
            else
            {
                pending.Remove(id);
                Trace.TraceWarning($"Gave up writing the title and date into video {id}: {reason}");
            }
        }
        try
        {
            for (var index = 0; index < videoIds.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (model.Snapshot.Find(videoIds[index]) is not { } record)
                {
                    pending.Remove(videoIds[index]);
                    continue;
                }
                progress?.Report((index, record.Label));
                if (isBusy(record.Id))
                {
                    deferred.Add(record.Id);
                    continue;
                }
                switch (LibraryFiles.Probe(model.GetVideoPath(record)))
                {
                    case FilePresence.Absent:
                        pending.Remove(record.Id);
                        continue;
                    case FilePresence.Unknown:
                        Skip(record.Id, $"{record.Label}: its file cannot be read.");
                        continue;
                }
                try
                {
                    var wrote = await writer.FillMissingIdentityAsync(record.ToLibraryVideo(), cancellationToken);
                    pending.Remove(record.Id);
                    if (!wrote) continue;
                    updated++;
                    await model.UpsertFromFileAsync(record.ToLibraryVideo(), CancellationToken.None);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    Trace.TraceWarning($"Could not write the title and date into {record.RelativePath}: {exception}");
                    Skip(record.Id, $"{record.Label}: {exception.Message}");
                }
            }
        }
        finally { await SaveAsync(); }
        return new BackfillSlice(videoIds.Count, updated, deferred, skipped);
    }

    /// <summary>The videos not yet handled, with their failed attempts; on the first run, every video in the library.</summary>
    private Dictionary<Guid, int> Pending()
    {
        if (_pending is not null) return _pending;
        var path = PendingPath(model.Catalog.Paths);
        if (!File.Exists(path)) return _pending = model.Snapshot.Chronological.ToDictionary(record => record.Id, _ => 0);
        _pending = [];
        foreach (var line in File.ReadLines(path))
            if (line.Split(' ') is [var id, var attempts] && Guid.TryParse(id, out var videoId) && int.TryParse(attempts, out var count))
                _pending[videoId] = count;
        return _pending;
    }

    /// <summary>Writes the pending list, replacing the old one whole so a crash leaves one or the other; once nothing is
    /// pending, writes the stamp instead.</summary>
    private async Task SaveAsync()
    {
        var paths = model.Catalog.Paths;
        Directory.CreateDirectory(paths.CacheDirectory);
        var pending = Pending();
        if (pending.Count == 0)
        {
            await File.WriteAllTextAsync(StampPath(paths), DateTimeOffset.UtcNow.ToString("O"), CancellationToken.None);
            File.Delete(PendingPath(paths));
            return;
        }
        var temporary = PendingPath(paths) + ".tmp";
        await File.WriteAllLinesAsync(temporary, pending.Select(entry => $"{entry.Key:N} {entry.Value}"), CancellationToken.None);
        File.Move(temporary, PendingPath(paths), overwrite: true);
    }
}
