using System.Diagnostics;
using HomeVideoLibrary.Core;

namespace HomeVideoLibrary.Infrastructure;

/// <summary>Shelf hover strips: for each video, the frames at the middle of each twentieth, tiled 5 × 4 into one JPEG at
/// <c>.cache/strips/{videoId:N}.{mediaIdentity}.v1.jpg</c>. The key is <see cref="VideoRecord.MediaIdentity"/>, so a
/// metadata save keeps a strip and new media needs a new one. Slot times follow from the record's duration, so there is no
/// manifest. Strips are a rebuildable cache; nothing is written into the MKV.</summary>
public sealed class HoverStripStore(LibraryPaths paths, IMatroskaFileEditor frames)
{
    public const int Slots = 20, Columns = 5, Rows = Slots / Columns;
    private const int ConcurrentCaptures = 2;
    private const string FormatVersion = "v1";

    /// <summary>The middle of slot <paramref name="slot"/>'s twentieth, so the first and last frames miss the black leader
    /// and tail.</summary>
    public static TimeSpan SlotTime(int slot, TimeSpan duration)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(slot);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(slot, Slots);
        return TimeSpan.FromTicks(duration.Ticks * (2 * slot + 1) / (2 * Slots));
    }

    /// <summary>The pixels of slot <paramref name="slot"/>'s frame within a strip of the given size.</summary>
    public static (int X, int Y, int Width, int Height) Tile(int slot, int stripWidth, int stripHeight)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(slot);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(slot, Slots);
        var (width, height) = (stripWidth / Columns, stripHeight / Rows);
        return (slot % Columns * width, slot / Columns * height, width, height);
    }

    /// <summary>Whether a strip can exist for <paramref name="record"/>: its media identity and duration are known.</summary>
    public static bool CanBuild(VideoRecord record) => FileName(record) is not null;

    /// <summary>The strip for <paramref name="record"/>'s media, or null when there is none yet.</summary>
    public string? TryGet(VideoRecord record) => PathFor(record) is { } path && File.Exists(path) ? path : null;

    /// <summary>Returns the strip for <paramref name="record"/>'s media, building it first when needed. Returns null, keeping
    /// nothing, when the file is not the one the record describes or changes during the build: a newer record follows.
    /// Throws when the frames cannot be captured.</summary>
    /// <exception cref="ArgumentException"><see cref="CanBuild"/> is false.</exception>
    public async Task<string?> GetOrBuildAsync(VideoRecord record, string mkvPath, CancellationToken cancellationToken)
    {
        var target = PathFor(record) ?? throw new ArgumentException("The video's media identity or duration is unknown.", nameof(record));
        if (File.Exists(target)) return target;
        var stamp = FileStamp.TryRead(mkvPath);
        if (stamp is null || stamp != record.Stamp) return null;
        var scratch = Directory.CreateDirectory(Path.Combine(paths.ScratchDirectory, "strip-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var images = Enumerable.Range(0, Slots).Select(slot => Path.Combine(scratch, $"{slot:00}.bmp")).ToArray();
            await Parallel.ForEachAsync(Enumerable.Range(0, Slots),
                new ParallelOptions { MaxDegreeOfParallelism = ConcurrentCaptures, CancellationToken = cancellationToken },
                async (slot, token) => await frames.CaptureKeyframeAsync(mkvPath, SlotTime(slot, record.Duration), images[slot],
                    PreviewCache.FrameWidth, token));
            var strip = Path.Combine(scratch, "strip.jpg");
            await frames.TileAsync(images, strip, Columns, cancellationToken);
            if (FileStamp.TryRead(mkvPath) != stamp) return null;
            Directory.CreateDirectory(paths.StripDirectory);
            File.Move(strip, target, overwrite: true);
            foreach (var older in Directory.EnumerateFiles(paths.StripDirectory, $"{record.Id:N}.*").Where(path => path != target))
                TryDelete(older);
            return target;
        }
        finally { TryDelete(scratch); }
    }

    /// <summary>Deletes strips of videos not in <paramref name="live"/> and strips in another format. Failures are traced,
    /// not thrown.</summary>
    public void Prune(IReadOnlySet<Guid> live)
    {
        if (!Directory.Exists(paths.StripDirectory)) return;
        foreach (var path in Directory.EnumerateFiles(paths.StripDirectory))
        {
            var parts = Path.GetFileName(path).Split('.');
            if (parts is not [var id, _, FormatVersion, "jpg"] || !Guid.TryParseExact(id, "N", out var videoId) || !live.Contains(videoId))
                TryDelete(path);
        }
    }

    private string? PathFor(VideoRecord record) => FileName(record) is { } name ? Path.Combine(paths.StripDirectory, name) : null;

    private static string? FileName(VideoRecord record) =>
        record.MediaIdentity is { Length: > 0 } identity && identity.All(char.IsAsciiHexDigit) && record.Duration > TimeSpan.Zero
            ? $"{record.Id:N}.{identity}.{FormatVersion}.jpg" : null;

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"Could not remove hover strip file {path}: {exception.Message}");
        }
    }
}
