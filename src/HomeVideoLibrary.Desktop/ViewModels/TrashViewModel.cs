using HomeVideoLibrary.Infrastructure;

namespace HomeVideoLibrary.Desktop.ViewModels;

/// <summary>A queued Move to Trash. <see cref="Completion"/> covers the job and what follows it (asking to delete
/// permanently what had no Trash, the status line, focus) and never faults.</summary>
public sealed record TrashRequest(JobViewModel Job, Task Completion);

/// <summary>Moving videos to the Trash, and removing videos whose files are missing from the library. A request is
/// confirmed, then a page showing one of the videos is left through its leave prompt, then the files go in one background
/// job of the render lane. Trims, splits and merges read their source files in that lane, so a video is never trashed
/// under a running edit, and an edit queued after a trash finds its source gone rather than half read. Until its job
/// ends, a video is held: it cannot be opened or trashed again.</summary>
public sealed class TrashViewModel : ViewModelBase
{
    private const int MaxDetails = 40;
    private const string UndoHint = "To undo, restore from the Trash and import again; albums are not kept.";
    private readonly Func<LibraryModel?> _model;
    private readonly Func<LibraryModel, VideoRemover> _createRemover;
    private readonly ShelfViewModel _shelf;
    private readonly JobQueue _jobs;
    private readonly IInteraction _interaction;
    private readonly Func<IReadOnlyCollection<Guid>, Task<bool>> _leave;
    private readonly Func<bool> _canStart;
    private readonly Action<string> _status;
    private readonly Action _viewChanged;
    private readonly HashSet<Guid> _held = [];

    /// <param name="model">The open library's model, or null.</param>
    /// <param name="leave">Leaves the page when it shows one of these videos (asking about unsaved changes); false
    /// when the page stays.</param>
    /// <param name="canStart">Whether new work may start (the library is not being switched or closed).</param>
    /// <param name="viewChanged">Shows the model's current snapshot, so the shelf no longer has the removed tapes when
    /// focus moves on.</param>
    public TrashViewModel(Func<LibraryModel?> model, Func<LibraryModel, VideoRemover> createRemover, ShelfViewModel shelf, JobQueue jobs,
        IInteraction interaction, Func<IReadOnlyCollection<Guid>, Task<bool>> leave, Func<bool> canStart, Action<string> status, Action viewChanged)
    {
        _model = model; _createRemover = createRemover; _shelf = shelf; _jobs = jobs; _interaction = interaction; _leave = leave;
        _canStart = canStart; _status = status; _viewChanged = viewChanged;
        MoveSelectedToTrashCommand = new AsyncCommand(_ => MoveToTrashAsync([.. _shelf.SelectedIds]),
            () => _canStart() && Trashable(_shelf.SelectedIds).Count > 0, context: "Move to Trash");
        RemoveMissingCommand = new AsyncCommand(_ => RemoveMissingAsync([.. _shelf.SelectedIds]),
            () => _canStart() && HasMissingSelected, context: "Remove from library");
        _shelf.SelectionChanged += UpdateCommands;
    }

    /// <summary>Moves the selected videos to the Trash; missing and held ones are left out.</summary>
    public AsyncCommand MoveSelectedToTrashCommand { get; }

    /// <summary>Removes the selected videos whose files are missing from the library (rows only).</summary>
    public AsyncCommand RemoveMissingCommand { get; }

    public bool HasMissingSelected => Selected(_shelf.SelectedIds).Any(record => record.IsMissing);

    /// <summary>Whether <paramref name="videoId"/> waits for or is in a trash job.</summary>
    public bool Holds(Guid videoId) => _held.Contains(videoId);

    public void UpdateCommands()
    {
        OnPropertyChanged(nameof(HasMissingSelected));
        MoveSelectedToTrashCommand.RaiseCanExecuteChanged();
        RemoveMissingCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Asks, leaves a page showing one of the videos and queues the job. Returns null when nothing was queued
    /// (declined, nothing trashable, or the page stayed).</summary>
    public async Task<TrashRequest?> MoveToTrashAsync(IReadOnlyCollection<Guid> videoIds)
    {
        if (_model() is not { } model || !_canStart()) return null;
        var videos = Trashable(videoIds);
        if (videos.Count == 0) return null;
        if (!await _interaction.ConfirmAsync(new Confirmation("MOVE TO TRASH",
                $"Move {Count(videos.Count)} to Trash? {Size(model, videos)} moves to the Trash. {UndoHint}",
                Details(model, videos), "MOVE TO TRASH")))
            return null;
        var ids = videos.Select(video => video.Id).ToArray();
        if (!await _leave(ids))
        {
            _status("Nothing was moved to the Trash; the open video stayed open.");
            return null;
        }
        var next = _shelf.TapeAfter(ids);
        _held.UnionWith(ids);
        UpdateCommands();
        var remover = _createRemover(model);
        var title = videos.Count == 1 ? $"Move “{videos[0].Label}” to Trash" : $"Move {videos.Count} videos to Trash";
        var batch = Enqueue(title, videos, remover.MoveToTrashAsync, "Moving", Moved);
        return new TrashRequest(batch.Job, FinishAsync(model, remover, videos, batch, next));
    }

    /// <summary>Removes videos whose files are missing from the library after asking; the files are not touched.</summary>
    public async Task RemoveMissingAsync(IReadOnlyCollection<Guid> videoIds)
    {
        if (_model() is not { } model || !_canStart()) return;
        var missing = Selected(videoIds).Where(record => record.IsMissing && !_held.Contains(record.Id)).ToArray();
        if (missing.Length == 0) return;
        var them = missing.Length == 1 ? "it" : "them";
        if (!await _interaction.ConfirmAsync(new Confirmation("REMOVE FROM LIBRARY",
                $"Remove {Count(missing.Length)} from the library? The {(missing.Length == 1 ? "file is" : "files are")} already gone; " +
                $"this forgets {them} and {(missing.Length == 1 ? "its" : "their")} album memberships.",
                Details(model, missing), "REMOVE")))
            return;
        var ids = missing.Select(record => record.Id).ToArray();
        var results = await Task.Run(() => _createRemover(model).RemoveMissingAsync(ids));
        _status(Summarize(results, count => $"Removed {Count(count)} from the library."));
    }

    /// <summary>After the job: the videos whose drive has no Trash are offered for permanent deletion (a second, explicit
    /// question about those only), then the status line reports the result and focus moves to the next tape.</summary>
    private async Task FinishAsync(LibraryModel model, VideoRemover remover, IReadOnlyList<VideoRecord> videos, Batch trash, Guid? next)
    {
        try
        {
            await trash.Job.Completion;
            var report = new List<string> { Summarize(trash.Results, Moved) };
            var stranded = videos.Where(video => trash.Results.Any(result => result.VideoId == video.Id && result.Result == RemovalResult.Unavailable))
                .ToArray();
            if (stranded.Length > 0 && ReferenceEquals(_model(), model) && await _interaction.ConfirmAsync(new Confirmation("DELETE PERMANENTLY",
                    $"{Count(stranded.Length)} cannot go to the Trash because {(stranded.Length == 1 ? "its" : "their")} drive has none. " +
                    $"Delete {(stranded.Length == 1 ? "it" : "them")} permanently? {Size(model, stranded)} is deleted and cannot be restored.",
                    Details(model, stranded), "DELETE PERMANENTLY")))
            {
                var deletion = Enqueue(stranded.Length == 1 ? $"Delete “{stranded[0].Label}”" : $"Delete {stranded.Length} videos", stranded,
                    remover.DeletePermanentlyAsync, "Deleting", Deleted);
                await deletion.Job.Completion;
                report.Add(Summarize(deletion.Results, Deleted));
            }
            if (trash.Results.Any(result => result.Result == RemovalResult.Trashed)) report.Add(UndoHint);
            if (string.Join(" ", report.Where(part => part.Length > 0)) is { Length: > 0 } message) _status(message);
            if (next is not { } id || !ReferenceEquals(_model(), model)) return;
            _viewChanged();
            _shelf.RequestFocus(id);
        }
        catch (Exception exception) { AppErrors.Report(exception, "Move to Trash"); }
        finally
        {
            _held.ExceptWith(videos.Select(video => video.Id));
            UpdateCommands();
        }
    }

    /// <summary>Queues one job removing <paramref name="videos"/> one by one. It fails only when nothing left the library
    /// and something went wrong; a drive without a Trash is no failure, since the follow-up asks about it.</summary>
    private Batch Enqueue(string title, IReadOnlyList<VideoRecord> videos, Func<Guid, CancellationToken, Task<VideoRemoval>> remove, string stage,
        Func<int, string> done)
    {
        var results = new List<VideoRemoval>();
        var job = _jobs.Enqueue(title, JobLane.Render, async (progress, cancellationToken) =>
        {
            for (var index = 0; index < videos.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress.Report(new JobProgress($"{stage} “{videos[index].Label}”", (double)index / videos.Count));
                results.Add(await remove(videos[index].Id, cancellationToken));
            }
            var summary = Summarize(results, done);
            if (!results.Any(result => result.IsRemoved) && results.Any(result => result.Result != RemovalResult.Unavailable))
                throw new IOException(summary);
            return JobOutcome.Done(summary);
        });
        return new Batch(job, results);
    }

    /// <param name="Results">Filled by the job; read once it has ended.</param>
    private sealed record Batch(JobViewModel Job, IReadOnlyList<VideoRemoval> Results);

    private IReadOnlyList<VideoRecord> Trashable(IEnumerable<Guid> videoIds) =>
        Selected(videoIds).Where(record => !record.IsMissing && !_held.Contains(record.Id)).ToArray();

    /// <summary>The library's records for <paramref name="videoIds"/>, newest first.</summary>
    private IEnumerable<VideoRecord> Selected(IEnumerable<Guid> videoIds)
    {
        if (_model() is not { } model) return [];
        var ids = videoIds as IReadOnlySet<Guid> ?? videoIds.ToHashSet();
        return ids.Count == 0 ? [] : model.Snapshot.Chronological.Where(record => ids.Contains(record.Id));
    }

    /// <summary>One line per video: its label, size and albums.</summary>
    private static IReadOnlyList<string> Details(LibraryModel model, IReadOnlyList<VideoRecord> videos)
    {
        var snapshot = model.Snapshot;
        var lines = videos.Take(MaxDetails).Select(video =>
        {
            var albums = snapshot.AlbumsContaining(video.Id).Select(album => album.Name).ToArray();
            return $"{video.Label} · {Gigabytes(Bytes(model, video))}" + (albums.Length > 0 ? " · in " + string.Join(", ", albums) : "");
        }).ToList();
        if (videos.Count > MaxDetails) lines.Add($"…and {videos.Count - MaxDetails} more");
        return lines;
    }

    private static string Size(LibraryModel model, IEnumerable<VideoRecord> videos) => Gigabytes(videos.Sum(video => Bytes(model, video)));

    private static long Bytes(LibraryModel model, VideoRecord video) =>
        (video.Stamp ?? FileStamp.TryRead(model.GetVideoPath(video)))?.Length ?? 0;

    private static string Gigabytes(long bytes) => bytes switch
    {
        0 => "0 GB",
        < 10_000_000 => "under 0.01 GB",
        _ => $"{bytes / 1e9:0.##} GB",
    };

    /// <summary>One sentence per outcome: what was removed, then each kind of failure with the videos it hit.</summary>
    private static string Summarize(IReadOnlyCollection<VideoRemoval> results, Func<int, string> done)
    {
        var parts = new List<string>();
        var removed = results.Count(result => result.IsRemoved);
        if (removed > 0) parts.Add(done(removed));
        foreach (var group in results.Where(result => !result.IsRemoved).GroupBy(result => result.Result))
        {
            var labels = group.Select(result => result.Label).ToArray();
            var one = labels.Length == 1;
            parts.Add(group.Key switch
            {
                RemovalResult.Locked => $"{Names(labels)} {(one ? "is" : "are")} open in another program; try again later.",
                RemovalResult.Unavailable => $"{Names(labels)} cannot go to the Trash on {(one ? "its" : "their")} drive.",
                RemovalResult.Missing => $"{Names(labels)} {(one ? "has" : "have")} no file; use Remove from library.",
                _ => string.Join(" ", group.Select(result => $"“{result.Label}”: {result.Error}")),
            });
        }
        return string.Join(" ", parts);
    }

    private static string Names(IReadOnlyList<string> labels) =>
        string.Join(", ", labels.Take(3).Select(label => $"“{label}”")) + (labels.Count > 3 ? $" and {labels.Count - 3} more" : "");

    private static string Moved(int count) => $"Moved {Count(count)} to the Trash.";
    private static string Deleted(int count) => $"Deleted {Count(count)} permanently.";

    private static string Count(int count) => count == 1 ? "1 video" : $"{count} videos";
}
