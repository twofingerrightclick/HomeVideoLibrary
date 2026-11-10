using HomeVideoLibrary.Core;

namespace HomeVideoLibrary.Infrastructure;

/// <summary>Recognizes library folders and picks where a new library goes.</summary>
public static class LibraryFolder
{
    public const string DefaultName = "HomeVideoLibrary";

    /// <summary>Files the OS writes into folders a user merely looked at; they do not make a folder non-empty.</summary>
    private static readonly HashSet<string> OsLitter = new(StringComparer.OrdinalIgnoreCase) { ".DS_Store", "Thumbs.db", "desktop.ini" };

    /// <summary>Whether <paramref name="folder"/> holds a library, that is, its catalog database.</summary>
    public static bool IsLibrary(string folder) => File.Exists(new LibraryPaths(folder).DatabasePath);

    /// <summary>The folder to open for a library created in <paramref name="chosen"/>. That is <paramref name="chosen"/>
    /// itself when it already is a library or is named <see cref="DefaultName"/>, so a library never nests in another;
    /// otherwise the first of <c>HomeVideoLibrary</c>, <c>HomeVideoLibrary 2</c>, … in it that is a library, empty or
    /// absent, so a library never mixes into an unrelated folder.</summary>
    public static string ForNewLibrary(string chosen)
    {
        chosen = Path.TrimEndingDirectorySeparator(Path.GetFullPath(chosen));
        if (IsLibrary(chosen) || string.Equals(Path.GetFileName(chosen), DefaultName, StringComparison.OrdinalIgnoreCase)) return chosen;
        for (var number = 1; ; number++)
        {
            var candidate = Path.Combine(chosen, number == 1 ? DefaultName : $"{DefaultName} {number}");
            if (File.Exists(candidate)) continue;
            if (!Directory.Exists(candidate) || IsLibrary(candidate) || IsEmpty(candidate)) return candidate;
        }
    }

    private static bool IsEmpty(string folder) =>
        Directory.EnumerateFileSystemEntries(folder).All(entry => OsLitter.Contains(Path.GetFileName(entry)));
}
