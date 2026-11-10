using System.Diagnostics;
using HomeVideoLibrary.Infrastructure;

namespace HomeVideoLibrary.Desktop.ViewModels;

/// <summary>Runs a library's one-time <see cref="IdentityBackfill"/> as render-lane jobs of <see cref="IdentityBackfill.SliceSize"/>
/// videos each: it rewrites MKV metadata in place, which must not overlap a trim, split or merge reading the same file,
/// and slicing lets jobs the user queues meanwhile run between slices instead of after the whole pass. Each slice is queued
/// when the previous one has finished. Videos open on a page are retried once after the rest; still open then, they wait for
/// the next library open, as do videos that could not be checked.</summary>
public static class BackfillJob
{
    public const string Title = "Write titles and dates into older videos";

    /// <summary>Runs the pass to its end, or until a slice is cancelled or fails, or <paramref name="canContinue"/> is
    /// false after a slice (the library is being switched or closed). Each slice records its own progress. Runs on the UI
    /// thread, like the queue; never throws.</summary>
    /// <param name="isBusy">Whether a video is open on a page; called from the slice's thread.</param>
    public static async Task RunAsync(JobQueue jobs, IdentityBackfill backfill, Func<Guid, bool> isBusy, Func<bool> canContinue)
    {
        try
        {
            if (!backfill.IsDue) return;
            var plan = await backfill.PlanAsync();
            var slices = new Queue<IReadOnlyList<Guid>>(plan.Chunk(IdentityBackfill.SliceSize));
            var deferred = new List<Guid>();
            var retrying = false;
            var done = 0;
            while (slices.TryDequeue(out var slice))
            {
                BackfillSlice? result = null;
                var title = retrying ? $"{Title} (videos that were open)" : $"{Title} ({done + 1}–{done + slice.Count} of {plan.Count})";
                var job = jobs.Enqueue(title, JobLane.Render, async (progress, cancellationToken) =>
                {
                    result = await backfill.RunAsync(slice, isBusy, new JobProgressAdapter(progress, slice.Count), cancellationToken);
                    return JobOutcome.Done(Summarize(result));
                });
                await job.Completion;
                if (job.State != JobState.Succeeded || result is null || !canContinue()) return;
                done += slice.Count;
                deferred.AddRange(result.Deferred);
                if (slices.Count > 0 || retrying || deferred.Count == 0) continue;
                retrying = true;
                foreach (var retry in deferred.Chunk(IdentityBackfill.SliceSize)) slices.Enqueue(retry);
            }
        }
        catch (Exception exception) { Trace.TraceWarning($"The title and date backfill stopped: {exception}"); }
    }

    private static string Summarize(BackfillSlice slice)
    {
        var summary = slice.Updated == 0 ? $"Checked {slice.Checked}; all carry their title and date." : $"Wrote titles and dates into {slice.Updated} of {slice.Checked}.";
        if (slice.Deferred.Count > 0) summary += $" {slice.Deferred.Count} open on a page will be checked later.";
        if (slice.Skipped.Count > 0) summary += $" {slice.Skipped.Count} skipped: {slice.Skipped[0]}";
        return summary;
    }

    /// <summary>Forwards reports straight to the job's sink, which marshals them once and in order.</summary>
    private sealed class JobProgressAdapter(IProgress<JobProgress> job, int total) : IProgress<(int Done, string Label)>
    {
        public void Report((int Done, string Label) value) =>
            job.Report(new JobProgress($"Checking “{value.Label}”", (double)value.Done / total));
    }
}
