using System.Diagnostics;
using HomeVideoLibrary.Infrastructure;

namespace HomeVideoLibrary.Desktop.ViewModels;

/// <summary>Runs a library's one-time <see cref="IdentityBackfill"/> as a background job in the render lane: it rewrites
/// MKV metadata in place, which must not overlap a trim, split or merge reading the same file.</summary>
public static class BackfillJob
{
    public const string Title = "Write titles and dates into older videos";

    /// <summary>Queues the backfill when the library still needs it. Never throws: when the catalog cannot say, the
    /// backfill waits for the next open.</summary>
    /// <returns>The queued job, or null.</returns>
    public static async Task<JobViewModel?> QueueIfDueAsync(JobQueue jobs, IdentityBackfill backfill)
    {
        try
        {
            if (!await Task.Run(() => backfill.IsDueAsync())) return null;
        }
        catch (Exception exception)
        {
            Trace.TraceWarning($"Could not tell whether older videos need their titles and dates: {exception}");
            return null;
        }
        return jobs.Enqueue(Title, JobLane.Render, async (progress, cancellationToken) =>
        {
            var result = await backfill.RunAsync(new JobProgressAdapter(progress), cancellationToken);
            var summary = result.Updated == 0
                ? $"All {result.Checked} videos already carry their title and date."
                : $"Wrote titles and dates into {result.Updated} of {result.Checked} videos.";
            if (result.Failures.Count > 0) summary += $" {result.Failures.Count} could not be updated: {result.Failures[0]}";
            return JobOutcome.Done(summary);
        });
    }

    /// <summary>Forwards reports straight to the job's sink, which marshals them once and in order.</summary>
    private sealed class JobProgressAdapter(IProgress<JobProgress> job) : IProgress<(int Done, int Total, string Label)>
    {
        public void Report((int Done, int Total, string Label) value) =>
            job.Report(new JobProgress($"Checking “{value.Label}”", value.Total == 0 ? null : (double)value.Done / value.Total));
    }
}
