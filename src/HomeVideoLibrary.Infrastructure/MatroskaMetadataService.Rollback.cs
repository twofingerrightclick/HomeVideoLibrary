using System.Diagnostics;
using System.Text.Json;
using HomeVideoLibrary.Core;

namespace HomeVideoLibrary.Infrastructure;

public sealed partial class MatroskaMetadataService
{
    private static readonly JsonSerializerOptions SnapshotJson = new() { WriteIndented = true };

    /// <summary>Where a save keeps its snapshot of the metadata it is about to replace. Each snapshot lives here from just
    /// before the write until the write is verified or rolled back, so one that remains belongs to a save that crashed or
    /// could not be rolled back; its <c>snapshot.json</c> describes how to restore it. Nothing deletes these automatically.</summary>
    public static string RecoveryDirectory(LibraryPaths paths) => Path.Combine(paths.CacheDirectory, "recovery");

    /// <summary>Writes the plan in place. Any failure once mkvpropedit may have changed the file restores the snapshot
    /// and verifies the restore; if that fails too, the snapshot stays on disk and the error names it.</summary>
    private async Task WriteWithRollbackAsync(string path, MetadataState original, VideoMetadata metadata, WritePlan plan, string scratch,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = await CaptureSnapshotAsync(path, original, plan, cancellationToken);
        var before = FileStamp.Of(path);
        try
        {
            // From here the file may change, so cancellation is ignored: stopping mkvpropedit half way is what corrupts.
            await RunAsync(tools.MkvPropEdit, [path, .. plan.Arguments], CancellationToken.None, WriteTimeout);
            if (AfterWriteForTesting is { } hook) await hook(path);
            await VerifyAsync(path, original, metadata, plan, scratch, CancellationToken.None);
        }
        catch (Exception exception) when (exception is MediaToolStartException || FileStamp.Of(path) == before)
        {
            DeleteSnapshot(snapshot);
            throw;
        }
        catch (Exception exception)
        {
            Trace.TraceError($"Metadata write to {path} failed; restoring the snapshot in {snapshot.Directory}. {exception}");
            await RestoreAsync(path, original, snapshot, scratch, exception);
            throw;
        }
        DeleteSnapshot(snapshot);
    }

    /// <summary>Saves everything the plan can change: the tag and chapter XML, the segment title and, when the plan rewrites
    /// attachments, every owned attachment present before (possibly none, which restore then reproduces).</summary>
    private async Task<Snapshot> CaptureSnapshotAsync(string path, MetadataState original, WritePlan plan, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(RecoveryDirectory(catalog.Paths),
            $"{Path.GetFileNameWithoutExtension(path)}-{DateTime.UtcNow:yyyyMMdd'T'HHmmss'Z'}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var tags = CopyInto(original.TagsPath, directory, "tags.xml");
            var chapters = CopyInto(original.ChaptersPath, directory, "chapters.xml");
            var attachments = new List<SavedAttachment>();
            if (plan.TouchesAttachments)
            {
                var owned = OwnedAttachments(original);
                attachments.AddRange(owned.Select((attachment, index) =>
                    new SavedAttachment(attachment.Name, attachment.ContentType, $"attachment-{index}-{attachment.Name}")));
                if (owned.Length > 0)
                    await RunAsync(tools.MkvExtract, [path, "attachments",
                        .. owned.Select((attachment, index) => $"{attachment.Id}:{Path.Combine(directory, attachments[index].File)}")], cancellationToken);
            }
            var snapshot = new Snapshot(directory, path, tags, chapters, original.Container.Title, plan.TouchesAttachments, attachments);
            await File.WriteAllTextAsync(Path.Combine(directory, "snapshot.json"), JsonSerializer.Serialize(snapshot, SnapshotJson), cancellationToken);
            return snapshot;
        }
        catch
        {
            DeleteDirectory(directory);
            throw;
        }
    }

    private async Task RestoreAsync(string path, MetadataState original, Snapshot snapshot, string scratch, Exception writeError)
    {
        try
        {
            var emptyTags = Path.Combine(snapshot.Directory, "empty-tags.xml");
            await File.WriteAllTextAsync(emptyTags, "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<Tags />");
            List<string> arguments = [path, "--tags", "all:" + (snapshot.TagsFile is { } tags ? Path.Combine(snapshot.Directory, tags) : emptyTags),
                "--chapters", snapshot.ChaptersFile is { } chapters ? Path.Combine(snapshot.Directory, chapters) : ""];
            arguments.AddRange(string.IsNullOrEmpty(snapshot.Title)
                ? ["--edit", "info", "--delete", "title"] : ["--edit", "info", "--set", "title=" + snapshot.Title]);
            if (snapshot.AttachmentsTouched)
            {
                var current = await IdentifyAsync(path, CancellationToken.None);
                foreach (var attachment in current.Attachments.Where(IsOwned))
                    arguments.AddRange(["--delete-attachment", attachment.DeleteSelector]);
                foreach (var attachment in snapshot.Attachments)
                    arguments.AddRange(["--attachment-name", attachment.Name, "--attachment-mime-type",
                        attachment.ContentType.Length > 0 ? attachment.ContentType : "application/octet-stream",
                        "--add-attachment", Path.Combine(snapshot.Directory, attachment.File)]);
            }
            await RunAsync(tools.MkvPropEdit, arguments, CancellationToken.None, WriteTimeout);
            await VerifyAsync(path, original, original.Written,
                new WritePlan([], original.Manifest, TouchesAttachments: false, new Dictionary<string, string>()), scratch, CancellationToken.None);
        }
        catch (Exception rollbackError)
        {
            Trace.TraceError($"Restoring the metadata of {path} failed; the snapshot stays in {snapshot.Directory}. {rollbackError}");
            throw new InvalidDataException(
                $"Saving failed and the previous details could not be restored. A copy of them was kept in {snapshot.Directory}.",
                new AggregateException(writeError, rollbackError));
        }
        DeleteSnapshot(snapshot);
    }

    private static string? CopyInto(string? source, string directory, string name)
    {
        if (source is null) return null;
        File.Copy(source, Path.Combine(directory, name));
        return name;
    }

    private static void DeleteSnapshot(Snapshot snapshot) => DeleteDirectory(snapshot.Directory);

    private static void DeleteDirectory(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"Could not delete {path}: {exception.Message}");
        }
    }

    private readonly record struct FileStamp(long Length, DateTime LastWriteUtc)
    {
        public static FileStamp Of(string path)
        {
            var info = new FileInfo(path);
            return new FileStamp(info.Length, info.LastWriteTimeUtc);
        }
    }

    /// <param name="File">Path relative to the snapshot directory.</param>
    private sealed record SavedAttachment(string Name, string ContentType, string File);

    /// <param name="TagsFile">Relative to <see cref="Directory"/>; null when the video had no tags.</param>
    /// <param name="ChaptersFile">Relative to <see cref="Directory"/>; null when the video had no chapters.</param>
    /// <param name="AttachmentsTouched">Whether the write replaced the owned attachments, in which case restoring makes them
    /// exactly <see cref="Attachments"/> again.</param>
    private sealed record Snapshot(string Directory, string VideoPath, string? TagsFile, string? ChaptersFile, string? Title,
        bool AttachmentsTouched, IReadOnlyList<SavedAttachment> Attachments);
}
