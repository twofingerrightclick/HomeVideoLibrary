using HomeVideoLibrary.Core;
using HomeVideoLibrary.Infrastructure;
using Xunit;

namespace HomeVideoLibrary.Tests;

public sealed class ScratchSweeperTests : IDisposable
{
    private readonly LibraryPaths _paths = new(Path.Combine(Path.GetTempPath(), "hvl-sweeper-" + Guid.NewGuid().ToString("N")));

    [Fact]
    public async Task RemovesStaleTemporaryEntriesAndKeepsFreshOnesLibraryVideosAndRecoverySnapshots()
    {
        using var owner = LibraryLock.TryAcquire(_paths)!;
        var processStart = owner.AcquiredAt.UtcDateTime;
        var stale = new[]
        {
            Path.Combine(_paths.ScratchDirectory, "import-1.mkv"),
            Path.Combine(_paths.ScratchDirectory, "edit-2", "part.mkv"),
            Path.Combine(_paths.PreviewDirectory, "video", "3.jpg"),
            Path.Combine(_paths.CacheDirectory, "metadata-4", "tags.xml"),
            Path.Combine(_paths.VideosDirectory, ".5.import.mkv"),
            Path.Combine(_paths.VideosDirectory, ".6.metadata.mkv"),
            Path.Combine(_paths.VideosDirectory, ".edit-7", "out.mkv"),
        };
        var library = Path.Combine(_paths.VideosDirectory, "0123456789abcdef0123456789abcdef.mkv");
        var thumbnail = Path.Combine(_paths.ThumbnailCacheDirectory, "video", "chosen.jpg");
        var recovery = Path.Combine(MatroskaMetadataService.RecoveryDirectory(_paths), "video-snapshot", "snapshot.json");
        foreach (var file in stale.Append(library).Append(thumbnail).Append(recovery)) Touch(file, processStart.AddHours(-1));
        foreach (var directory in new[] { "edit-2", "metadata-4", ".edit-7", "video" }
                     .SelectMany(name => new[] { _paths.ScratchDirectory, _paths.CacheDirectory, _paths.VideosDirectory, _paths.PreviewDirectory }
                         .Select(root => Path.Combine(root, name))).Where(Directory.Exists))
            Directory.SetLastWriteTimeUtc(directory, processStart.AddHours(-1));
        var fresh = Path.Combine(_paths.ScratchDirectory, "import-fresh.mkv");
        Touch(fresh, processStart.AddMinutes(1));

        await ScratchSweeper.SweepAsync(owner);

        Assert.All(stale, path => Assert.False(File.Exists(path), path));
        Assert.False(Directory.Exists(Path.Combine(_paths.ScratchDirectory, "edit-2")));
        Assert.False(Directory.Exists(Path.Combine(_paths.VideosDirectory, ".edit-7")));
        Assert.True(File.Exists(fresh));
        Assert.True(File.Exists(library));
        Assert.True(File.Exists(thumbnail));
        Assert.True(File.Exists(recovery));
    }

    [Fact]
    public async Task ToleratesAMissingLibraryLayout()
    {
        using var owner = LibraryLock.TryAcquire(_paths)!;
        await ScratchSweeper.SweepAsync(owner);
    }

    [Fact]
    public void OnlyOneOwnerHoldsALibraryAtATime()
    {
        using (var first = LibraryLock.TryAcquire(_paths))
        {
            Assert.NotNull(first);
            Assert.Null(LibraryLock.TryAcquire(_paths));
        }
        using var second = LibraryLock.TryAcquire(_paths);
        Assert.NotNull(second);
    }

    private static void Touch(string path, DateTime lastWriteUtc)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
    }

    public void Dispose()
    {
        if (Directory.Exists(_paths.RootDirectory)) Directory.Delete(_paths.RootDirectory, recursive: true);
    }
}
