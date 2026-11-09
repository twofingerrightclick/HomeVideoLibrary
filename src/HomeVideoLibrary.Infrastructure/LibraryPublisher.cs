using System.Diagnostics;
using HomeVideoLibrary.Core;
using HomeVideoLibrary.Infrastructure.Editing;

namespace HomeVideoLibrary.Infrastructure;

/// <summary>Moves finished MKVs into <c>Videos/</c> and registers them so that a crash at any point leaves either
/// nothing or a complete video, never an MKV the catalog does not know about. Files are first moved in under a pending
/// name (<c>Videos/.{id:N}.pending.mkv</c>), the catalog insert is the commit, and only then do they get their final
/// names. <see cref="RecoverAsync"/> finishes or discards pending files that a crash left behind.</summary>
public static class LibraryPublisher
{
    private const string PendingSuffix = ".pending.mkv";

    /// <summary>Publishes all <paramref name="items"/> in one catalog transaction, or none of them. Each staged file must
    /// be on the library's volume and is flushed to disk before it is moved.</summary>
    public static async Task PublishAsync(LibraryCatalog catalog, IReadOnlyList<(LibraryVideo Video, string Staged)> items,
        CancellationToken cancellationToken)
    {
        var pending = new List<string>();
        var committed = false;
        try
        {
            foreach (var (video, staged) in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await using (var stream = new FileStream(staged, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    stream.Flush(flushToDisk: true);
                var path = PendingPath(catalog.Paths, video.Id);
                File.Move(staged, path, overwrite: false);
                pending.Add(path);
            }
            await catalog.AddBatchAsync(items.Select(item => item.Video).ToArray(), cancellationToken);
            committed = true;
        }
        finally
        {
            if (!committed) foreach (var path in pending) ScratchFiles.TryDelete(path);
        }
        foreach (var (video, _) in items) Complete(catalog, video.Id, video.RelativePath);
    }

    /// <summary>Finishes publications a crash interrupted after the catalog commit and deletes those it interrupted
    /// before it. Requires the library lock, so no publication of this run can be in flight.</summary>
    public static async Task RecoverAsync(LibraryCatalog catalog, LibraryLock owner, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (!Directory.Exists(catalog.Paths.VideosDirectory)) return;
        var pending = Directory.GetFiles(catalog.Paths.VideosDirectory, ".*" + PendingSuffix);
        if (pending.Length == 0) return;
        var rows = (await catalog.ListAsync(cancellationToken)).ToDictionary(video => video.Id);
        foreach (var path in pending)
        {
            var name = Path.GetFileName(path);
            if (!Guid.TryParseExact(name[1..^PendingSuffix.Length], "N", out var id)) continue;
            if (rows.TryGetValue(id, out var video)) Complete(catalog, id, video.RelativePath);
            else ScratchFiles.TryDelete(path);
        }
    }

    private static string PendingPath(LibraryPaths paths, Guid id) => Path.Combine(paths.VideosDirectory, $".{id:N}{PendingSuffix}");

    /// <summary>Renames a committed video to its catalog path. A failure is traced and retried by the next recovery.</summary>
    private static void Complete(LibraryCatalog catalog, Guid id, string relativePath)
    {
        var pending = PendingPath(catalog.Paths, id);
        try { File.Move(pending, catalog.GetVideoPath(relativePath), overwrite: false); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Trace.TraceError($"Could not finish publishing {pending}; it will be retried when the library is next opened. {exception.Message}");
        }
    }
}
