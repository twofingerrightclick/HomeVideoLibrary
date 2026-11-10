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
/// <param name="Skipped">Videos that could not be checked: their file is missing or unreadable, or the write failed,
/// with the reason.</param>
public sealed record BackfillSlice(int Checked, int Updated, IReadOnlyList<Guid> Deferred, IReadOnlyList<string> Skipped);

/// <summary>The one-time pass that writes TITLE and DATE_RECORDED into videos no import of this app wrote them into, so
/// a video file that leaves the library (for the Trash) still says what it is. It runs in slices so other work can go
/// between them, and is recorded as done (in a stamp file) only once every video was checked without a skip; until then
/// each library open runs it again, finding nothing to write in the files already done.</summary>
public sealed class IdentityBackfill(LibraryModel model, IIdentityWriter writer)
{
    public const int SliceSize = 25;

    /// <summary>Like <c>publish-recovered</c>: a per-library fact that needs no catalog schema. Losing <c>.cache</c> only
    /// makes the pass run again.</summary>
    public static string StampPath(LibraryPaths paths) => Path.Combine(paths.CacheDirectory, "identity-backfill.done");

    /// <summary>Whether the library has videos and has not completed the pass.</summary>
    public bool IsDue => !model.Snapshot.Videos.IsEmpty && !File.Exists(StampPath(model.Catalog.Paths));

    /// <summary>Every video to check, newest first.</summary>
    public IReadOnlyList<Guid> Plan() => model.Snapshot.Chronological.Select(record => record.Id).ToArray();

    /// <summary>Checks <paramref name="videoIds"/>, publishing each video it wrote. A video that has left the library is
    /// passed over; one <paramref name="isBusy"/> claims is deferred, so the pass never writes under an open page's save.
    /// Per-video failures are collected, not thrown.</summary>
    /// <param name="isBusy">Called from this method's thread.</param>
    /// <param name="progress">Videos of this slice checked so far and the one being checked.</param>
    public async Task<BackfillSlice> RunAsync(IReadOnlyList<Guid> videoIds, Func<Guid, bool> isBusy,
        IProgress<(int Done, string Label)>? progress = null, CancellationToken cancellationToken = default)
    {
        var deferred = new List<Guid>();
        var skipped = new List<string>();
        var updated = 0;
        for (var index = 0; index < videoIds.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (model.Snapshot.Find(videoIds[index]) is not { } record) continue;
            progress?.Report((index, record.Label));
            if (isBusy(record.Id))
            {
                deferred.Add(record.Id);
                continue;
            }
            if (LibraryFiles.Probe(model.GetVideoPath(record)) is not FilePresence.Present)
            {
                skipped.Add($"{record.Label}: its file is missing or cannot be read.");
                continue;
            }
            try
            {
                if (!await writer.FillMissingIdentityAsync(record.ToLibraryVideo(), cancellationToken)) continue;
                updated++;
                await model.UpsertFromFileAsync(record.ToLibraryVideo(), CancellationToken.None);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                Trace.TraceWarning($"Could not write the title and date into {record.RelativePath}: {exception}");
                skipped.Add($"{record.Label}: {exception.Message}");
            }
        }
        return new BackfillSlice(videoIds.Count, updated, deferred, skipped);
    }

    /// <summary>Records the pass as done, so it never runs again for this library.</summary>
    public async Task RecordCompletedAsync()
    {
        Directory.CreateDirectory(model.Catalog.Paths.CacheDirectory);
        await File.WriteAllTextAsync(StampPath(model.Catalog.Paths), DateTimeOffset.UtcNow.ToString("O"), CancellationToken.None);
    }
}
