using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace HomeVideoLibrary.Infrastructure;

/// <summary>Where a library lives, for choices that depend on the filesystem.</summary>
/// <remarks>Only the library's own path is probed. Enumerating every mount (<see cref="DriveInfo.GetDrives"/>) is
/// unsafe here: on macOS it throws for mounts with empty names and its native call shares a static buffer, which
/// crashed concurrent callers.</remarks>
internal static class StorageVolume
{
    /// <summary>True when <paramref name="path"/> (an existing directory) is on a network filesystem such as SMB, NFS,
    /// AFP or WebDAV. When the filesystem cannot be determined, returns false (local) and traces why.</summary>
    public static bool IsNetwork(string path)
    {
        var full = Path.GetFullPath(path);
        try
        {
            if (OperatingSystem.IsWindows()) return IsWindowsNetwork(full);
            if (OperatingSystem.IsMacOS()) return IsMacNetwork(full);
            if (OperatingSystem.IsLinux()) return IsLinuxNetwork(full);
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException
                                              or DllNotFoundException or EntryPointNotFoundException)
        {
            Trace.TraceWarning($"Could not tell whether {full} is on a network filesystem; treating it as local: {exception.Message}");
            return false;
        }
    }

    private static bool IsWindowsNetwork(string full)
    {
        if (full.StartsWith(@"\\", StringComparison.Ordinal))
            return !full.StartsWith(@"\\?\", StringComparison.Ordinal) || full.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase);
        // One drive, by its root: GetDriveType reports mapped network drives as Network.
        return Path.GetPathRoot(full) is { Length: > 0 } root && new DriveInfo(root).DriveType == DriveType.Network;
    }

    private const int StatfsBufferSize = 4096;

    private static bool IsMacNetwork(string full)
    {
        var buffer = new byte[StatfsBufferSize];
        var result = RuntimeInformation.ProcessArchitecture == Architecture.X64 ? MacStatfsX64(full, buffer) : MacStatfs(full, buffer);
        if (result != 0) throw new IOException($"statfs failed (errno {Marshal.GetLastWin32Error()}).");
        // struct statfs (64-bit inode layout): f_flags at offset 64, f_fstypename[16] at offset 72.
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(64));
        var name = buffer.AsSpan(72, 16);
        var length = name.IndexOf((byte)0);
        return IsMacNetwork(Encoding.ASCII.GetString(length < 0 ? name : name[..length]), flags);
    }

    private const uint MacMountLocal = 0x1000; // MNT_LOCAL

    /// <summary>A mount the kernel does not mark local, or a known network filesystem type, is treated as network.</summary>
    internal static bool IsMacNetwork(string fileSystemType, uint flags) =>
        (flags & MacMountLocal) == 0 || fileSystemType is "smbfs" or "nfs" or "afpfs" or "webdav" or "cifs" or "ftp";

    private static bool IsLinuxNetwork(string full)
    {
        var buffer = new byte[StatfsBufferSize];
        if (LinuxStatfs(full, buffer) != 0) throw new IOException($"statfs failed (errno {Marshal.GetLastWin32Error()}).");
        // struct statfs on 64-bit Linux starts with the filesystem magic as a native long.
        var magic = Environment.Is64BitProcess
            ? BinaryPrimitives.ReadInt64LittleEndian(buffer)
            : BinaryPrimitives.ReadInt32LittleEndian(buffer);
        return IsLinuxNetwork(magic);
    }

    /// <summary>Filesystem magic numbers from statfs(2): NFS, SMB, CIFS, SMB2, Coda, AFS, 9P, Ceph, and FUSE (sshfs and
    /// similar; a local FUSE filesystem only loses WAL, which is safe).</summary>
    internal static bool IsLinuxNetwork(long magic) => (magic & 0xFFFFFFFF) is
        0x6969 or 0x517B or 0xFF534D42 or 0xFE534D42 or 0x73757245 or 0x5346414F or 0x01021997 or 0x00C36400 or 0x65735546;

    [DllImport("libc", EntryPoint = "statfs", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern int MacStatfs(string path, [Out] byte[] buffer);

    // Intel macOS keeps the 32-bit-inode statfs under the plain name; the layout read above is the $INODE64 one.
    [DllImport("libc", EntryPoint = "statfs$INODE64", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern int MacStatfsX64(string path, [Out] byte[] buffer);

    [DllImport("libc", EntryPoint = "statfs", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern int LinuxStatfs(string path, [Out] byte[] buffer);
}
