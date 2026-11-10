namespace HomeVideoLibrary.Infrastructure;

public enum TrashResult
{
    /// <summary>The file is in the Trash (the Recycle Bin on Windows), from where the user can restore it.</summary>
    Trashed,
    /// <summary>The file's drive has no Trash it can move to without copying (a network share, a removable drive, another
    /// device than the user's Trash). The file is untouched.</summary>
    Unavailable,
    /// <summary>Another program has the file open. The file is untouched; trying again later may work.</summary>
    Locked,
    /// <summary>The system deleted the file outright instead of keeping it: Windows does when the Recycle Bin cannot take a
    /// file it did not foresee, after its own warning, which the user accepted. The file cannot be restored.</summary>
    Deleted,
}

/// <summary>Moves files to the operating system's Trash, where the user can restore them. Never copies a file and
/// never deletes one instead: when the Trash cannot take a file, the answer is <see cref="TrashResult.Unavailable"/>.</summary>
public interface IFileTrash
{
    /// <exception cref="FileNotFoundException">There is no file at <paramref name="path"/>.</exception>
    /// <exception cref="IOException">The system refused the move for another reason (for example permissions).</exception>
    TrashResult MoveToTrash(string path);
}

public static class FileTrash
{
    /// <summary>The Trash of the platform the app runs on: Finder's on macOS, the Recycle Bin on Windows and the
    /// freedesktop.org Trash elsewhere.</summary>
    /// <param name="ownerWindow">The native handle of the window that owns any dialog the system shows (Windows); 0 or
    /// null for none.</param>
    public static IFileTrash ForCurrentPlatform(Func<nint>? ownerWindow = null) =>
        OperatingSystem.IsMacOS() ? new MacFileTrash()
        : OperatingSystem.IsWindows() ? new WindowsFileTrash(ownerWindow)
        : new FreedesktopTrash();

    internal static void RequireFile(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("The video file is not there any more.", path);
    }
}
