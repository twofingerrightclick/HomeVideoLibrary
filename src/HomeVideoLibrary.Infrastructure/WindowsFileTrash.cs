using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace HomeVideoLibrary.Infrastructure;

/// <summary>The Recycle Bin through <c>SHFileOperationW</c> with undo. Only fixed drives have one: on removable, network
/// and other drives Windows would delete the file outright, so those are <see cref="TrashResult.Unavailable"/> without
/// calling it. Microsoft.VisualBasic's <c>FileSystem.DeleteFile</c> is not used because it deletes outright in exactly
/// those cases.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsFileTrash : IFileTrash
{
    private const uint Delete = 3; // FO_DELETE
    private const ushort Silent = 0x4, NoConfirmation = 0x10, AllowUndo = 0x40, NoErrorUi = 0x400, WantNukeWarning = 0x4000;
    private const int SharingViolation = 32, LockViolation = 33;

    public TrashResult MoveToTrash(string path)
    {
        var full = Path.GetFullPath(path);
        FileTrash.RequireFile(full);
        if (full.StartsWith(@"\\", StringComparison.Ordinal) || Path.GetPathRoot(full) is not { Length: > 0 } root ||
            new DriveInfo(root).DriveType != DriveType.Fixed)
            return TrashResult.Unavailable;
        var operation = new FileOperation
        {
            Function = Delete,
            // The list of paths ends with an empty one; the marshaller adds the final terminator.
            From = full + "\0",
            // WantNukeWarning: should the Recycle Bin refuse the file anyway (too large for it), Windows asks before
            // deleting instead of deleting silently.
            Flags = AllowUndo | NoConfirmation | Silent | NoErrorUi | WantNukeWarning,
        };
        var result = FileOperationW(ref operation);
        if (result is SharingViolation or LockViolation) return TrashResult.Locked;
        if (result != 0) throw new IOException($"Windows could not move {Path.GetFileName(full)} to the Recycle Bin (error 0x{result:X}).");
        // The user declined the warning that the file would be deleted rather than recycled.
        if (operation.AnyOperationsAborted) return TrashResult.Unavailable;
        if (File.Exists(full)) throw new IOException($"Windows reported {Path.GetFileName(full)} as recycled, but it is still there.");
        return TrashResult.Trashed;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FileOperation
    {
        public nint Window;
        public uint Function;
        public string From;
        public string? To;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)] public bool AnyOperationsAborted;
        public nint NameMappings;
        public string? ProgressTitle;
    }

    [DllImport("shell32.dll", EntryPoint = "SHFileOperationW", CharSet = CharSet.Unicode)]
    private static extern int FileOperationW(ref FileOperation operation);
}
