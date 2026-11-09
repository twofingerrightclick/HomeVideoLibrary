using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace HomeVideoLibrary.Infrastructure.Editing;

/// <summary>Makes renames and moves inside a directory durable. On POSIX systems a rename lives in the directory's
/// own data, which flushing the file does not write; on Windows NTFS journals the metadata change itself.</summary>
internal static class DirectorySync
{
    private const int ReadOnly = 0;
    private const int FullFileSync = 51; // macOS F_FULLFSYNC: also flushes the drive's own write cache.

    /// <summary>Flushes <paramref name="directory"/>'s entries to disk. Failures are traced: the caller's data is
    /// already written, only its durability across a power loss is at stake.</summary>
    public static void Flush(string directory)
    {
        if (OperatingSystem.IsWindows()) return;
        var path = Encoding.UTF8.GetBytes(directory + "\0");
        var descriptor = Open(path, ReadOnly);
        if (descriptor < 0)
        {
            Trace.TraceWarning($"Could not open {directory} to flush it (errno {Marshal.GetLastPInvokeError()}).");
            return;
        }
        try
        {
            var result = OperatingSystem.IsMacOS() ? Control(descriptor, FullFileSync, 0) : Sync(descriptor);
            if (result != 0 && OperatingSystem.IsMacOS()) result = Sync(descriptor);
            if (result != 0) Trace.TraceWarning($"Could not flush {directory} (errno {Marshal.GetLastPInvokeError()}).");
        }
        finally { Close(descriptor); }
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open(byte[] path, int flags);

    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int Sync(int descriptor);

    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int Control(int descriptor, int command, int argument);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int descriptor);
}
