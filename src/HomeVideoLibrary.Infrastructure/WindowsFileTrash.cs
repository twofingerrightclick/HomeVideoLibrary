using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace HomeVideoLibrary.Infrastructure;

/// <summary>The Recycle Bin through the shell's <c>IFileOperation</c>, the API Explorer uses. Windows deletes a file
/// outright when its Recycle Bin cannot take it, so that case is ruled out before the call where it can be predicted (not a
/// local fixed drive; the drive's bin deletes immediately or is smaller than the file), and detected afterwards where it
/// cannot: the operation's progress sink reports whether a recycled item was created, and a file deleted outright (after
/// Windows' own warning, which the user accepted) is <see cref="TrashResult.Deleted"/>, never Trashed.
/// Microsoft.VisualBasic's <c>FileSystem.DeleteFile</c> and <c>SHFileOperation</c> were rejected: neither reports whether a
/// file was recycled or deleted.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsFileTrash(Func<nint>? ownerWindow = null) : IFileTrash
{
    private const uint AllowUndo = 0x40, NoConfirmation = 0x10, Silent = 0x4, NoErrorUi = 0x400, WantNukeWarning = 0x4000,
        RecycleOnDelete = 0x80000; // FOF_* and FOFX_RECYCLEONDELETE
    private const string BitBucket = @"Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket";

    public TrashResult MoveToTrash(string path)
    {
        var full = Path.GetFullPath(path);
        FileTrash.RequireFile(full);
        // A junction or symlink can put the file on another drive than its path says; the drive that holds it decides.
        if (WindowsTrashRules.VolumeRoot(FinalPath(full)) is not { } root || GetDriveType(root) != DriveFixed) return TrashResult.Unavailable;
        if (!WindowsTrashRules.BinAccepts(new FileInfo(full).Length, ReadPolicy(root))) return TrashResult.Unavailable;
        var owner = ownerWindow?.Invoke() ?? 0;
        // Shell operations and the warning dialog they may show need a single-threaded apartment with a message loop.
        var outcome = DedicatedThread.Run(ApartmentState.STA, () => Recycle(full, owner));
        return WindowsTrashRules.Map(outcome.Result, outcome.ItemResult, outcome.Aborted, outcome.Recycled, Path.GetFileName(full));
    }

    private static (int Result, int ItemResult, bool Aborted, bool? Recycled) Recycle(string path, nint owner)
    {
        var operation = (IFileOperation)Activator.CreateInstance(Type.GetTypeFromCLSID(FileOperationClass, throwOnError: true)!)!;
        try
        {
            var result = CreateItem(path, 0, typeof(IShellItem).GUID, out var item);
            if (result < 0) return (result, 0, false, null);
            var sink = new DeleteSink();
            operation.SetOperationFlags(AllowUndo | NoConfirmation | Silent | NoErrorUi | WantNukeWarning | RecycleOnDelete);
            if (owner != 0) operation.SetOwnerWindow(owner);
            operation.DeleteItem(item, sink);
            result = operation.PerformOperations();
            operation.GetAnyOperationsAborted(out var aborted);
            return (result, sink.ItemResult, aborted, sink.Recycled);
        }
        finally { Marshal.ReleaseComObject(operation); }
    }

    /// <summary>The path of the file itself, with junctions and symlinks resolved, as <c>\\?\C:\...</c> or
    /// <c>\\?\UNC\server\share\...</c>.</summary>
    private static string FinalPath(string path)
    {
        using var handle = CreateFile(path, 0, FileShare.ReadWrite | FileShare.Delete, 0, FileMode.Open, BackupSemantics, 0);
        if (handle.IsInvalid) throw new IOException($"Could not open {Path.GetFileName(path)}.", Marshal.GetHRForLastWin32Error());
        var buffer = new char[1024];
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0);
        if (length == 0 || length >= buffer.Length) throw new IOException($"Could not resolve {Path.GetFileName(path)}.", Marshal.GetHRForLastWin32Error());
        return new string(buffer, 0, (int)length);
    }

    /// <summary>The drive's Recycle Bin settings from Explorer's per-volume keys (falling back to the older global value);
    /// unknown when Windows has not written them, which means the defaults: recycle, with a capacity Windows manages.</summary>
    private static RecycleBinPolicy ReadPolicy(string root)
    {
        var volume = new char[64];
        var key = GetVolumeNameForVolumeMountPoint(root, volume, (uint)volume.Length) && new string(volume).TrimEnd('\0') is var name &&
            name.IndexOf('{') is var open and >= 0 && name.IndexOf('}') is var close && close > open
            ? Registry.CurrentUser.OpenSubKey($@"{BitBucket}\Volume\{name[open..(close + 1)]}") : null;
        using (key)
        using (var global = Registry.CurrentUser.OpenSubKey(BitBucket))
        {
            var nuke = key?.GetValue("NukeOnDelete") as int? ?? global?.GetValue("NukeOnDelete") as int?;
            var capacity = key?.GetValue("MaxCapacity") as int?;
            return new RecycleBinPolicy(nuke == 1, capacity is > 0 ? capacity.Value * 1024L * 1024 : null);
        }
    }

    private static readonly Guid FileOperationClass = new("3ad05575-8857-4850-9277-11b85bdb8e09");
    private const uint DriveFixed = 3, BackupSemantics = 0x02000000;

    [DllImport("kernel32.dll", EntryPoint = "GetDriveTypeW", CharSet = CharSet.Unicode)]
    private static extern uint GetDriveType(string root);

    [DllImport("kernel32.dll", EntryPoint = "GetVolumeNameForVolumeMountPointW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPoint(string mountPoint, char[] volumeName, uint length);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, FileShare share, nint security, FileMode mode, uint flags, nint template);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, char[] path, uint length, uint flags);

    [DllImport("shell32.dll", EntryPoint = "SHCreateItemFromParsingName", CharSet = CharSet.Unicode)]
    private static extern int CreateItem(string path, nint bindContext, in Guid interfaceId, out IShellItem item);

    /// <summary>Records what happened to the deleted item: PostDeleteItem names the recycled item, or none when the file
    /// was deleted outright; or it reports why the item failed, which PerformOperations need not.</summary>
    [ComVisible(true)]
    private sealed class DeleteSink : IFileOperationProgressSink
    {
        public bool? Recycled { get; private set; }

        /// <summary>The item's failure; 0 when it did not fail or was never reached.</summary>
        public int ItemResult { get; private set; }

        public int PostDeleteItem(uint flags, IShellItem item, int result, IShellItem? newlyCreated)
        {
            if (result >= 0) Recycled = newlyCreated is not null;
            else ItemResult = result;
            return 0;
        }

        public int StartOperations() => 0;
        public int FinishOperations(int result) => 0;
        public int PreRenameItem(uint flags, IShellItem item, string? newName) => 0;
        public int PostRenameItem(uint flags, IShellItem item, string? newName, int result, IShellItem? created) => 0;
        public int PreMoveItem(uint flags, IShellItem item, IShellItem destination, string? newName) => 0;
        public int PostMoveItem(uint flags, IShellItem item, IShellItem destination, string? newName, int result, IShellItem? created) => 0;
        public int PreCopyItem(uint flags, IShellItem item, IShellItem destination, string? newName) => 0;
        public int PostCopyItem(uint flags, IShellItem item, IShellItem destination, string? newName, int result, IShellItem? created) => 0;
        public int PreDeleteItem(uint flags, IShellItem item) => 0;
        public int PreNewItem(uint flags, IShellItem destination, string? newName) => 0;
        public int PostNewItem(uint flags, IShellItem destination, string? newName, string? template, uint attributes, int result,
            IShellItem? created) => 0;
        public int UpdateProgress(uint total, uint done) => 0;
        public int ResetTimer() => 0;
        public int PauseTimer() => 0;
        public int ResumeTimer() => 0;
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellItem
    {
        void BindToHandler(nint bindContext, in Guid handler, in Guid interfaceId, out nint result);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint form, out nint name);
        void GetAttributes(uint mask, out uint attributes);
        void Compare(IShellItem other, uint hint, out int order);
    }

    // Methods in vtable order; only the ones called have useful signatures.
    [ComImport, Guid("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        uint Advise(IFileOperationProgressSink sink);
        void Unadvise(uint cookie);
        void SetOperationFlags(uint flags);
        void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
        void SetProgressDialog(nint dialog);
        void SetProperties(nint properties);
        void SetOwnerWindow(nint owner);
        void ApplyPropertiesToItem(IShellItem item);
        void ApplyPropertiesToItems(nint items);
        void RenameItem(IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string newName, IFileOperationProgressSink? sink);
        void RenameItems(nint items, [MarshalAs(UnmanagedType.LPWStr)] string newName);
        void MoveItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? newName, IFileOperationProgressSink? sink);
        void MoveItems(nint items, IShellItem destination);
        void CopyItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? newName, IFileOperationProgressSink? sink);
        void CopyItems(nint items, IShellItem destination);
        void DeleteItem(IShellItem item, IFileOperationProgressSink? sink);
        void DeleteItems(nint items);
        void NewItem(IShellItem destination, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name,
            [MarshalAs(UnmanagedType.LPWStr)] string? template, IFileOperationProgressSink? sink);
        [PreserveSig] int PerformOperations();
        void GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }

    [ComImport, Guid("04b0f1a7-9490-44bc-96e1-4296a31252e2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IFileOperationProgressSink
    {
        [PreserveSig] int StartOperations();
        [PreserveSig] int FinishOperations(int result);
        [PreserveSig] int PreRenameItem(uint flags, IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string? newName);
        [PreserveSig] int PostRenameItem(uint flags, IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string? newName, int result, IShellItem? created);
        [PreserveSig] int PreMoveItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? newName);
        [PreserveSig] int PostMoveItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? newName,
            int result, IShellItem? created);
        [PreserveSig] int PreCopyItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? newName);
        [PreserveSig] int PostCopyItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? newName,
            int result, IShellItem? created);
        [PreserveSig] int PreDeleteItem(uint flags, IShellItem item);
        [PreserveSig] int PostDeleteItem(uint flags, IShellItem item, int result, IShellItem? newlyCreated);
        [PreserveSig] int PreNewItem(uint flags, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? newName);
        [PreserveSig] int PostNewItem(uint flags, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? newName,
            [MarshalAs(UnmanagedType.LPWStr)] string? template, uint attributes, int result, IShellItem? created);
        [PreserveSig] int UpdateProgress(uint total, uint done);
        [PreserveSig] int ResetTimer();
        [PreserveSig] int PauseTimer();
        [PreserveSig] int ResumeTimer();
    }
}

/// <param name="NukeOnDelete">The drive's bin is set to delete files immediately.</param>
/// <param name="MaxCapacity">The bin's size in bytes, when set.</param>
internal readonly record struct RecycleBinPolicy(bool NukeOnDelete, long? MaxCapacity);

/// <summary>The platform-independent decisions of <see cref="WindowsFileTrash"/>, so they can be tested anywhere.</summary>
internal static class WindowsTrashRules
{
    private const int Cancelled = 0x4C7, OperationCancelled = 0x75; // ERROR_CANCELLED, DE_OPCANCELLED
    private const int SharingViolation = 0x20, LockViolation = 0x21;
    // COPYENGINE_E_* from the Windows SDK's sherrors.h.
    private const int CopyEngineUserCancelled = unchecked((int)0x80270000),
        CopyEngineSharingViolationSource = unchecked((int)0x80270027), CopyEngineSharingViolationDestination = unchecked((int)0x80270028);

    /// <summary>The root to ask about the drive of a final path (<c>\\?\C:\...</c>, <c>\\?\Volume{...}\...</c>), or null
    /// for a network path (<c>\\?\UNC\...</c>, <c>\\server\...</c>), which never has a Recycle Bin.</summary>
    public static string? VolumeRoot(string finalPath)
    {
        var path = finalPath.StartsWith(@"\\?\", StringComparison.Ordinal) ? finalPath[4..] : finalPath;
        if (path.StartsWith(@"UNC\", StringComparison.OrdinalIgnoreCase) || path.StartsWith(@"\\", StringComparison.Ordinal)) return null;
        if (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':') return path[..2] + @"\";
        if (path.StartsWith("Volume{", StringComparison.OrdinalIgnoreCase) && path.IndexOf('\\') is var end and > 0) return @"\\?\" + path[..(end + 1)];
        return null;
    }

    /// <summary>Whether the bin would keep a file of <paramref name="length"/> bytes rather than delete it outright.</summary>
    public static bool BinAccepts(long length, RecycleBinPolicy policy) =>
        !policy.NukeOnDelete && (policy.MaxCapacity is not { } capacity || length <= capacity);

    /// <param name="result">From PerformOperations, which can succeed while the item failed.</param>
    /// <param name="itemResult">From the progress sink: the item's failure, or 0.</param>
    /// <param name="aborted">From GetAnyOperationsAborted.</param>
    /// <param name="recycled">From the progress sink: whether a recycled item was created; null when no item was deleted.</param>
    /// <exception cref="IOException">The operation failed for another reason.</exception>
    public static TrashResult Map(int result, int itemResult, bool aborted, bool? recycled, string name)
    {
        if (result >= 0 && recycled is { } created) return created ? TrashResult.Trashed : TrashResult.Deleted;
        if (IsLocked(result) || IsLocked(itemResult)) return TrashResult.Locked;
        // The user declined Windows' warning that the file would be deleted rather than recycled.
        if (IsCancel(result) || IsCancel(itemResult) || aborted && result >= 0 && itemResult >= 0) return TrashResult.Unavailable;
        var failure = result < 0 ? result : itemResult < 0 ? itemResult : unchecked((int)0x80004005);
        throw new IOException($"Windows could not move {name} to the Recycle Bin (0x{failure:X8}).", failure);
    }

    private static bool IsLocked(int result) => result is CopyEngineSharingViolationSource or CopyEngineSharingViolationDestination ||
        IsWin32(result) && (result & 0xFFFF) is SharingViolation or LockViolation;

    private static bool IsCancel(int result) => result is OperationCancelled or CopyEngineUserCancelled ||
        IsWin32(result) && (result & 0xFFFF) is Cancelled or OperationCancelled;

    private static bool IsWin32(int result) => (result & 0x1FFF0000) == 0x00070000;
}
