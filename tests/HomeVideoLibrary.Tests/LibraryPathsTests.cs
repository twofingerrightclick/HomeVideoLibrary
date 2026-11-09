using HomeVideoLibrary.Core;
using Xunit;

namespace HomeVideoLibrary.Tests;

public sealed class LibraryPathsTests
{
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void RejectsAnEmptyLibraryRoot(string root)
    {
        Assert.Throws<ArgumentException>(() => new LibraryPaths(root));
    }

    [Fact]
    public void ResolvesAllLocationsInsideTheSelectedRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "home-video-library-test");
        var paths = new LibraryPaths(root);

        Assert.Equal(Path.GetFullPath(root), paths.RootDirectory);
        Assert.Equal(Path.Combine(root, "library.sqlite3"), paths.DatabasePath);
        Assert.Equal(Path.Combine(root, "Videos"), paths.VideosDirectory);
        Assert.Equal(Path.Combine(root, ".cache"), paths.CacheDirectory);
    }
}
