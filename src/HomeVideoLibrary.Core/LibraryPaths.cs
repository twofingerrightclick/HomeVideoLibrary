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

    /// <summary>Temporary files; on the same volume as <see cref="VideosDirectory"/> so publishing moves are atomic.</summary>
    public string ScratchDirectory => Path.Combine(CacheDirectory, "scratch");
    public string ThumbnailCacheDirectory => Path.Combine(CacheDirectory, "thumbnails");
    public string PreviewDirectory => Path.Combine(CacheDirectory, "preview");
    /// <summary>Shelf hover strips. Unlike <see cref="PreviewDirectory"/>, kept across launches.</summary>
    public string StripDirectory => Path.Combine(CacheDirectory, "strips");

    public static string VideoRelativePath(Guid id) => $"Videos/{id:N}.mkv";

    /// <summary>Where a video waits between being moved into the library and being registered; see LibraryPublisher.</summary>
    public string PendingVideoPath(Guid id) => Path.Combine(VideosDirectory, $".{id:N}.pending.mkv");
}
