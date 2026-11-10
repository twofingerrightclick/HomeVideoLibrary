using System.Collections.Concurrent;
using HomeVideoLibrary.Core;

namespace HomeVideoLibrary.Desktop.ViewModels.Editing;

/// <summary>What a page needs to start clip edits: the editing service, the queue its render jobs run in, and how
/// their outputs reach the library.</summary>
/// <param name="Publish">Publishes committed outputs to the library (see <see cref="ClipEditJob"/>).</param>
public sealed record ClipEdits(IClipEditingService Service, JobQueue Jobs, Func<IReadOnlyList<LibraryVideo>, Task> Publish);

/// <summary>A clip edit as a background render job: the edit's stages become job progress, its warnings join the
/// final summary and its outputs are published to the library.</summary>
public static class ClipEditJob
{
    /// <param name="sources">The videos the edit reads; they are held (not saved over) while the job is active.</param>
    /// <param name="edit">Runs the edit on the thread pool; captures its inputs before it is queued.</param>
    /// <remarks>The outputs are published even when the job was cancelled after the service committed, because the new
    /// files already exist; a publishing failure becomes a warning.</remarks>
    public static JobViewModel Enqueue(ClipEdits edits, string title, IReadOnlyCollection<Guid> sources,
        Func<IClipEditingService, IProgress<ClipEditProgress>, CancellationToken, Task<IReadOnlyList<LibraryVideo>>> edit) =>
        edits.Jobs.Enqueue(title, JobLane.Render,
            (progress, cancellationToken) => RunAsync((stages, token) => edit(edits.Service, stages, token), edits.Publish, progress, cancellationToken),
            sources);

    private static async Task<JobOutcome> RunAsync(Func<IProgress<ClipEditProgress>, CancellationToken, Task<IReadOnlyList<LibraryVideo>>> edit,
        Func<IReadOnlyList<LibraryVideo>, Task> outputsCreated, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        var warnings = new ConcurrentQueue<string>();
        var stages = new RelayProgress(update =>
        {
            if (update.Warning is { } warning) warnings.Enqueue(warning);
            else progress.Report(new JobProgress(update.Stage, update.Fraction));
        });
        var outputs = await edit(stages, cancellationToken);
        try { await outputsCreated(outputs); }
        catch (Exception exception) { warnings.Enqueue("Search refresh needed: " + exception.Message); }
        var saved = outputs.Count == 1 ? "Saved 1 new video." : $"Saved {outputs.Count} new videos.";
        return new JobOutcome(string.Join(" ", [saved + " Originals unchanged.", .. warnings.Distinct()]), outputs.Select(video => video.Id).ToArray());
    }

    private sealed class RelayProgress(Action<ClipEditProgress> report) : IProgress<ClipEditProgress>
    {
        public void Report(ClipEditProgress value) => report(value);
    }
}
