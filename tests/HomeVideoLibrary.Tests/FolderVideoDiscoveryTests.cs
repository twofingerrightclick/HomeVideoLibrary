using HomeVideoLibrary.Infrastructure;
using Xunit;

namespace HomeVideoLibrary.Tests;

public sealed class FolderVideoDiscoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hvl-discovery-" + Guid.NewGuid().ToString("N"));

    private string FileAt(string relative)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "untouched original");
        return path;
    }

    [Fact]
    public async Task RecursesAtEveryDepthAndExcludesTheEntireLibraryButNotSimilarlyNamedFolders()
    {
        FileAt("source/one.MP4");
        FileAt("source/a/b/c/two.mov");
        FileAt("source/a/notes.txt");
        FileAt("source/library/Videos/owned.mkv");
        FileAt("source/library/.cache/scratch/staged.mp4");
        FileAt("source/library-copy/three.avi");

        var result = await new FolderVideoDiscovery().ScanAsync(Path.Combine(_root, "source"), Path.Combine(_root, "source/library"));

        Assert.True(result.Completed);
        Assert.Equal(["one.MP4", "three.avi", "two.mov"], result.Candidates.Select(Path.GetFileName).Order());
        Assert.Equal(2, result.Skipped);
        Assert.Equal(0, result.Failed);
        Assert.All(result.Candidates, path => Assert.Equal("untouched original", File.ReadAllText(path)));
    }

    [Fact]
    public async Task DoesNotFollowFileLinksDirectoryLoopsOrALinkedSelectedRoot()
    {
        var source = FileAt("source/one.mp4");
        var library = Directory.CreateDirectory(Path.Combine(_root, "library")).FullName;
        var folder = Path.GetDirectoryName(source)!;
        Directory.CreateSymbolicLink(Path.Combine(folder, "loop"), folder);
        File.CreateSymbolicLink(Path.Combine(folder, "linked.mp4"), source);
        var alias = Path.Combine(_root, "alias");
        Directory.CreateSymbolicLink(alias, folder);

        var result = await new FolderVideoDiscovery().ScanAsync(folder, library);
        Assert.Single(result.Candidates);
        Assert.Equal(2, result.Skipped);
        var linkedRoot = await new FolderVideoDiscovery().ScanAsync(alias, library);
        Assert.Empty(linkedRoot.Candidates);
        Assert.Equal(1, linkedRoot.Skipped);
        Assert.Contains("linked folders", Assert.Single(linkedRoot.Issues));
    }

    [Fact]
    public async Task ExcludesLibraryOpenedThroughALinkedAncestorAndSelectionInsideItsSubtree()
    {
        FileAt("real/library/Videos/owned.mkv");
        FileAt("real/other.mp4");
        Directory.CreateSymbolicLink(Path.Combine(_root, "alias"), Path.Combine(_root, "real"));
        var library = Path.Combine(_root, "alias/library");
        var result = await new FolderVideoDiscovery().ScanAsync(Path.Combine(_root, "real"), library);
        Assert.Equal("other.mp4", Path.GetFileName(Assert.Single(result.Candidates)));
        var inside = await new FolderVideoDiscovery().ScanAsync(Path.Combine(_root, "real/library/Videos"), library);
        Assert.Empty(inside.Candidates);
        Assert.Equal(1, inside.Skipped);
    }

    [Fact]
    public async Task ReportsAnInaccessibleChildAndContinuesWithAccessibleSiblings()
    {
        FileAt("source/denied/hidden.mp4");
        FileAt("source/accessible/good.mp4");
        var library = Directory.CreateDirectory(Path.Combine(_root, "library")).FullName;
        // Deterministic on administrator accounts too, while exercising real traversal around the failed directory.
        var discovery = new FolderVideoDiscovery(path => Path.GetFileName(path) == "denied"
            ? throw new UnauthorizedAccessException("Permission denied for this folder") : Directory.EnumerateFileSystemEntries(path));
        var result = await discovery.ScanAsync(Path.Combine(_root, "source"), library);
        Assert.True(result.Completed);
        Assert.Equal("good.mp4", Path.GetFileName(Assert.Single(result.Candidates)));
        Assert.Equal(1, result.Failed);
        Assert.Contains("denied", Assert.Single(result.Issues));
    }

    [Fact]
    public async Task CancellationStopsDiscoveryAndRetainsPartialCounts()
    {
        var source = FileAt("source/one.mp4");
        FileAt("source/two.mp4");
        var library = Directory.CreateDirectory(Path.Combine(_root, "library")).FullName;
        using var cancellation = new CancellationTokenSource();
        IEnumerable<string> Entries(string path)
        {
            yield return Path.Combine(path, "one.mp4");
            cancellation.Cancel();
            yield return Path.Combine(path, "two.mp4");
        }
        var result = await new FolderVideoDiscovery(Entries).ScanAsync(Path.GetDirectoryName(source)!, library, cancellationToken: cancellation.Token);
        Assert.False(result.Completed);
        Assert.Single(result.Candidates);
    }

    [Fact]
    public async Task RechecksParentsReplacedByLinksAfterScanning()
    {
        FileAt("source/child/one.mp4");
        FileAt("library/one.mp4");
        var library = Path.Combine(_root, "library");
        var result = await new FolderVideoDiscovery().ScanAsync(Path.Combine(_root, "source"), library);
        var candidate = Assert.Single(result.Candidates);
        Assert.True(FolderVideoDiscovery.CanImport(candidate, library));
        Directory.Delete(Path.Combine(_root, "source/child"), recursive: true);
        Directory.CreateSymbolicLink(Path.Combine(_root, "source/child"), library);
        Assert.False(FolderVideoDiscovery.CanImport(candidate, library));
    }

    [Fact]
    public async Task CaseDistinctFilesAndSubfoldersSurviveOnCaseSensitiveFilesystems()
    {
        var first = FileAt("source/Trip.mp4");
        // The capability belongs to this volume, not the OS. Linux and case-sensitive APFS exercise both branches.
        var sensitive = !File.Exists(Path.Combine(_root, "source/trip.mp4"));
        if (sensitive) FileAt("source/trip.mp4");
        FileAt("source/Trip/one.mp4");
        if (sensitive) FileAt("source/trip/two.mp4");
        FileAt("source/Library/owned.mp4");
        if (sensitive) FileAt("source/library/independent.mp4");
        var library = Path.Combine(_root, "source/Library");

        var result = await new FolderVideoDiscovery().ScanAsync(Path.GetDirectoryName(first)!, library);

        Assert.Equal(sensitive ? 5 : 2, result.Candidates.Count);
        Assert.DoesNotContain(result.Candidates, path => Path.GetFileName(path) == "owned.mp4");
        Assert.All(result.Candidates, path => Assert.True(FolderVideoDiscovery.CanImport(path, library)));
        if (sensitive)
        {
            Assert.Contains(result.Candidates, path => Path.GetFileName(path) == "trip.mp4");
            Assert.Contains(result.Candidates, path => Path.GetFileName(path) == "two.mp4");
            Assert.Contains(result.Candidates, path => Path.GetFileName(path) == "independent.mp4");
        }
    }

    [Fact]
    public async Task LibraryWithAnAlternateCaseSpellingStillExcludesItsPhysicalDirectory()
    {
        FileAt("source/Library/Videos/owned.mp4");
        FileAt("source/other.mp4");
        var library = Path.Combine(_root, "source/Library");
        var alternate = Path.Combine(_root, "source/library");
        if (Directory.Exists(alternate)) library = alternate; // Exercise case aliases where supported by the volume.

        var result = await new FolderVideoDiscovery().ScanAsync(Path.Combine(_root, "source"), library);

        Assert.Equal("other.mp4", Path.GetFileName(Assert.Single(result.Candidates)));
        var inside = await new FolderVideoDiscovery().ScanAsync(Path.Combine(library, "Videos"), library);
        Assert.Empty(inside.Candidates);
        Assert.Equal(1, inside.Skipped);
    }

    [Fact]
    public async Task LibraryAtTheFilesystemRootExcludesAllDescendants()
    {
        var source = FileAt("source/nested/one.mp4");
        var library = Path.GetPathRoot(source)!;

        var result = await new FolderVideoDiscovery().ScanAsync(Path.Combine(_root, "source"), library);

        Assert.True(result.Completed);
        Assert.Empty(result.Candidates);
        Assert.Equal(1, result.Skipped);
        Assert.Equal(0, result.Failed);
        // Use the physical spelling so the check exercises the root boundary, rather than rejecting /var's link.
        var physicalSource = OperatingSystem.IsWindows() ? source : FreedesktopTrash.RealPath(source)!;
        Assert.False(FolderVideoDiscovery.CanImport(physicalSource, library));
    }

    public void Dispose() => TestLibrary.DeleteDirectory(_root);
}
