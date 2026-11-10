using System.Collections.Concurrent;
using HomeVideoLibrary.Core;

namespace HomeVideoLibrary.Desktop.ViewModels.Editing;

/// <summary>A clip edit as a background render job: the edit's stages become job progress, its warnings join the
/// final summary and its outputs are published to the library.</summary>
public static class ClipEditJob
{
    /// <param name="edit">Runs the edit on the thread pool; captures its inputs before it is queued.</param>
    /// <param name="outputsCreated">Publishes committed outputs to the library. It runs even when the job was cancelled
    /// after the service committed, because the new files already exist; a failure becomes a warning.</param>
    public static JobViewModel Enqueue(JobQueue jobs, string title,
        Func<IProgress<ClipEditProgress>, CancellationToken, Task<IReadOnlyList<LibraryVideo>>> edit,
        Func<IReadOnlyList<LibraryVideo>, Task> outputsCreated) =>
        jobs.Enqueue(title, JobLane.Render, (progress, cancellationToken) => RunAsync(edit, outputsCreated, progress, cancellationToken));

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
