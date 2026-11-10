using System.Diagnostics;
using System.Text;
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
/// file was written, found complete or found gone, and a value the user clears afterwards is never filled in again. A
/// user's save that clears a title or recorded date also handles its video (see <see cref="SavedAsync"/>). Until every
/// video is handled or given up on, each library open continues with the rest.</summary>
/// <remarks>Progress lives in <c>.cache</c>, like <c>publish-recovered</c>: a per-library fact that needs no catalog
/// schema. The <see cref="StampPath"/> file marks the pass done; before that, <see cref="PendingPath"/> lists the videos
/// still to check with their failed attempts.</remarks>
public sealed class IdentityBackfill(LibraryModel model, IIdentityWriter writer)
{
    public const int SliceSize = 25;

    /// <summary>Checks of one video that may fail before the pass gives up on it.</summary>
    public const int MaxAttempts = 3;

    /// <summary>Guards <see cref="_pending"/>: a slice runs on the pool while <see cref="SavedAsync"/> may come from a
    /// page's save.</summary>
    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _saving = new(1, 1);
    private Dictionary<Guid, int>? _pending;

    public static string StampPath(LibraryPaths paths) => Path.Combine(paths.CacheDirectory, "identity-backfill.done");

    public static string PendingPath(LibraryPaths paths) => Path.Combine(paths.CacheDirectory, "identity-backfill.pending");

    /// <summary>Whether the library has videos and has not completed the pass.</summary>
    public bool IsDue => !model.Snapshot.Videos.IsEmpty && !File.Exists(StampPath(model.Catalog.Paths));

    /// <summary>The videos still to check, newest first; records the pass as done when none are left.</summary>
    public async Task<IReadOnlyList<Guid>> PlanAsync()
    {
        var snapshot = model.Snapshot;
        IReadOnlyList<Guid> plan;
        lock (_lock)
        {
            var pending = Pending();
            foreach (var gone in pending.Keys.Where(id => snapshot.Find(id) is null).ToArray()) pending.Remove(gone);
            plan = snapshot.Chronological.Select(record => record.Id).Where(pending.ContainsKey).ToArray();
        }
        if (plan.Count == 0) await SaveAsync();
        return plan;
    }

    /// <summary>Takes a user's save of a video into account. When it cleared the title or recorded date the video had,
    /// the video is handled: filling either in later would undo that edit. Any other save leaves the video to the pass,
    /// which adds only what is still missing. Safe to call while a slice runs.</summary>
    /// <param name="before">The video as it was before the save.</param>
    public async Task SavedAsync(VideoRecord before, VideoMetadata saved)
    {
        if (!IsDue) return;
        var stored = MetadataText.AsStored(saved);
        var cleared = before.Title.Length > 0 && stored.Title.Length == 0 ||
            (before.EmbeddedRecordedAt is not null || before.RecordedAtText.Length > 0) && stored.RecordedAtText.Length == 0;
        if (!cleared) return;
        lock (_lock)
            if (!Pending().Remove(before.Id)) return;
        await SaveAsync();
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
        var deferred = new List<Guid>();
        var skipped = new List<string>();
        var updated = 0;
        void Skip(Guid id, string reason)
        {
            skipped.Add(reason);
            lock (_lock)
            {
                var pending = Pending();
                if (!pending.TryGetValue(id, out var attempts)) return;
                if (attempts + 1 < MaxAttempts) pending[id] = attempts + 1;
                else
                {
                    pending.Remove(id);
                    Trace.TraceWarning($"Gave up writing the title and date into video {id}: {reason}");
                }
            }
        }
        void Handled(Guid id)
        {
            lock (_lock) Pending().Remove(id);
        }
        try
        {
            for (var index = 0; index < videoIds.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (model.Snapshot.Find(videoIds[index]) is not { } record)
                {
                    Handled(videoIds[index]);
                    continue;
                }
                lock (_lock)
                    if (!Pending().ContainsKey(record.Id)) continue;
                progress?.Report((index, record.Label));
                if (isBusy(record.Id))
                {
                    deferred.Add(record.Id);
                    continue;
                }
                switch (LibraryFiles.Probe(model.GetVideoPath(record)))
                {
                    case FilePresence.Absent:
                        Handled(record.Id);
                        continue;
                    case FilePresence.Unknown:
                        Skip(record.Id, $"{record.Label}: its file cannot be read.");
                        continue;
                }
                try
                {
                    var wrote = await writer.FillMissingIdentityAsync(record.ToLibraryVideo(), cancellationToken);
                    Handled(record.Id);
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

    /// <summary>The videos not yet handled, with their failed attempts; on the first run, or when the saved list is
    /// damaged, every video in the library. Call under <see cref="_lock"/>.</summary>
    private Dictionary<Guid, int> Pending()
    {
        if (_pending is not null) return _pending;
        var path = PendingPath(model.Catalog.Paths);
        if (File.Exists(path))
        {
            if (Read(path) is { } saved) return _pending = saved;
            Trace.TraceWarning($"{path} is damaged; every video is checked again.");
        }
        return _pending = model.Snapshot.Chronological.ToDictionary(record => record.Id, _ => 0);
    }

    /// <summary>The saved pending list, or null when it is damaged. It is never written empty (the stamp replaces it), so
    /// an empty file, like a line that does not parse, is a write a crash cut short.</summary>
    private static Dictionary<Guid, int>? Read(string path)
    {
        var pending = new Dictionary<Guid, int>();
        foreach (var line in File.ReadLines(path))
        {
            if (line.Split(' ') is not [var id, var attempts] || !Guid.TryParse(id, out var videoId) || !int.TryParse(attempts, out var count))
                return null;
            pending[videoId] = count;
        }
        return pending.Count > 0 ? pending : null;
    }

    /// <summary>Writes the pending list, replacing the old one whole so a crash leaves one or the other: the new list is
    /// on disk before it takes the old one's name. Once nothing is pending, writes the stamp instead.</summary>
    private async Task SaveAsync()
    {
        var paths = model.Catalog.Paths;
        await _saving.WaitAsync();
        try
        {
            Directory.CreateDirectory(paths.CacheDirectory);
            string lines;
            lock (_lock) lines = string.Concat(Pending().Select(entry => $"{entry.Key:N} {entry.Value}\n"));
            if (lines.Length == 0)
            {
                await File.WriteAllTextAsync(StampPath(paths), DateTimeOffset.UtcNow.ToString("O"), CancellationToken.None);
                File.Delete(PendingPath(paths));
                return;
            }
            var temporary = PendingPath(paths) + ".tmp";
            await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes(lines));
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, PendingPath(paths), overwrite: true);
        }
        finally { _saving.Release(); }
    }
}
