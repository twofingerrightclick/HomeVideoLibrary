namespace HomeVideoLibrary.Core;

/// <summary>The on-disk layout of a user-selected library.</summary>
public sealed class LibraryPaths
{
    public LibraryPaths(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        RootDirectory = Path.GetFullPath(rootDirectory);
    }

    public string RootDirectory { get; }
    public string DatabasePath => Path.Combine(RootDirectory, "library.sqlite3");
    public string VideosDirectory => Path.Combine(RootDirectory, "Videos");
    public string CacheDirectory => Path.Combine(RootDirectory, ".cache");
}
