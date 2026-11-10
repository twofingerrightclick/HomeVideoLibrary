namespace HomeVideoLibrary.Infrastructure;

public enum FilePresence
{
    Present,
    /// <summary>The system said the file does not exist, in a directory it could read.</summary>
    Absent,
    /// <summary>The check itself failed (permissions, I/O, the directory or its drive gone), so nothing may be decided.</summary>
    Unknown,
}

public static class LibraryFiles
{
    /// <summary>Whether the file at <paramref name="path"/> is there. A failed check is <see cref="FilePresence.Unknown"/>,
    /// never Absent: <see cref="File.Exists"/> answers false for an unreadable file too, and acting on that would forget
    /// videos that still exist. A missing directory is Unknown as well, since an unmounted drive looks the same.</summary>
    public static FilePresence Probe(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return FilePresence.Present;
        }
        catch (FileNotFoundException) { return IsReadableDirectory(Path.GetDirectoryName(path)) ? FilePresence.Absent : FilePresence.Unknown; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException) { return FilePresence.Unknown; }
    }

    private static bool IsReadableDirectory(string? directory)
    {
        if (directory is null) return false;
        try
        {
            using var entries = Directory.EnumerateFileSystemEntries(directory).GetEnumerator();
            entries.MoveNext();
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; }
    }
}
