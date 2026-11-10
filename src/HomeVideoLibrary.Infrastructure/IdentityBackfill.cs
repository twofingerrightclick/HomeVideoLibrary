using System.Diagnostics;
using HomeVideoLibrary.Core;

namespace HomeVideoLibrary.Infrastructure;

public interface IIdentityWriter
{
    /// <summary>See <see cref="MatroskaMetadataService.FillMissingIdentityAsync"/>; false when nothing was missing.</summary>
    Task<bool> FillMissingIdentityAsync(LibraryVideo video, CancellationToken cancellationToken = default);
}

/// <param name="Failures">One line per video that could not be updated: its label and why.</param>
public sealed record BackfillResult(int Checked, int Updated, IReadOnlyList<string> Failures);

/// <summary>The one-time pass that writes TITLE, DATE_RECORDED and the original file name into videos imported before
/// imports wrote them into the MKV, so a video file that leaves the library (for the Trash) still says what it is. Every
/// video is checked once; only files missing something are written. The pass is recorded in the catalog when it runs to
/// the end, so it never runs again for this library; a cancelled pass starts over next time, skipping done files
/// quickly.</summary>
public sealed class IdentityBackfill(LibraryModel model, IIdentityWriter writer)
{
    public const string TaskName = "identity-backfill";

    /// <summary>Whether this library has videos and has not finished the pass yet.</summary>
    public async Task<bool> IsDueAsync(CancellationToken cancellationToken = default) =>
        !model.Snapshot.Videos.IsEmpty && !await model.Catalog.HasCompletedAsync(TaskName, cancellationToken);

    /// <summary>Checks every video whose file is there, publishing each one it wrote. Per-video failures are collected,
    /// not thrown.</summary>
    /// <param name="progress">Videos checked so far, of how many, and the one being checked.</param>
    public async Task<BackfillResult> RunAsync(IProgress<(int Done, int Total, string Label)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var videos = model.Snapshot.Chronological;
        var failures = new List<string>();
        var updated = 0;
        for (var index = 0; index < videos.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var record = videos[index];
            progress?.Report((index, videos.Count, record.Label));
            if (model.Snapshot.Find(record.Id) is null || !File.Exists(model.GetVideoPath(record))) continue;
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
                failures.Add($"{record.Label}: {exception.Message}");
            }
        }
        await model.Catalog.RecordCompletedAsync(TaskName, CancellationToken.None);
        return new BackfillResult(videos.Count, updated, failures);
    }
}
