using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace HomeVideoLibrary.Infrastructure.Editing;

/// <summary>Renames that are on disk when they return. A rename lives in directory data: on POSIX systems flushing the
/// file does not write it, so the directory is flushed (with F_FULLFSYNC on macOS, which also empties the drive's
/// cache); on Windows the move is made with MOVEFILE_WRITE_THROUGH. Where the platform call is unavailable the move
/// still happens and the gap is traced.</summary>
internal static class DurableFile
{
    private const int ReadOnly = 0;
    private const int CloseOnExecMac = 0x1000000, CloseOnExecLinux = 0x80000;
    private const int FullFileSync = 51;
    private const int MoveFileWriteThrough = 0x8;
    private static bool _unavailable;

    /// <summary>Moves <paramref name="source"/> to <paramref name="destination"/> (never replacing it) and makes the
    /// change durable.</summary>
    public static void Move(string source, string destination)
    {
        if (OperatingSystem.IsWindows() && !_unavailable)
        {
            try
            {
                if (!Windows.MoveFileEx(source, destination, MoveFileWriteThrough))
                    throw new IOException($"Could not move {source} to {destination}.", Marshal.GetHRForLastWin32Error());
                return;
            }
            catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
            {
                Unavailable(exception);
            }
        }
        File.Move(source, destination, overwrite: false);
        FlushDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        var sourceDirectory = Path.GetDirectoryName(Path.GetFullPath(source))!;
        if (!string.Equals(sourceDirectory, Path.GetDirectoryName(Path.GetFullPath(destination)), StringComparison.Ordinal))
            FlushDirectory(sourceDirectory);
    }

    /// <summary>Flushes <paramref name="directory"/>'s entries on POSIX systems. Failures are traced: the data is already
    /// written, only its durability across a power loss is at stake.</summary>
    public static void FlushDirectory(string directory)
    {
        if (OperatingSystem.IsWindows() || _unavailable) return;
        try
        {
            var path = Encoding.UTF8.GetBytes(directory + "\0");
            var macOS = OperatingSystem.IsMacOS();
            var descriptor = macOS ? Mac.Open(path, ReadOnly | CloseOnExecMac) : Linux.Open(path, ReadOnly | CloseOnExecLinux);
            if (descriptor < 0)
            {
                Trace.TraceWarning($"Could not open {directory} to flush it (errno {Marshal.GetLastPInvokeError()}).");
                return;
            }
            try
            {
                var result = macOS ? Mac.Control(descriptor, FullFileSync, 0) : Linux.Sync(descriptor);
                if (result != 0 && macOS) result = Mac.Sync(descriptor); // F_FULLFSYNC is refused by some file systems.
                if (result != 0) Trace.TraceWarning($"Could not flush {directory} (errno {Marshal.GetLastPInvokeError()}).");
            }
            finally
            {
                if (macOS) Mac.Close(descriptor); else Linux.Close(descriptor);
            }
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            Unavailable(exception);
        }
    }

    private static void Unavailable(Exception exception)
    {
        _unavailable = true;
        Trace.TraceWarning($"Durable renames are unavailable on this system; renames are not flushed. {exception.Message}");
    }

    private static class Mac
    {
        [DllImport("libc", EntryPoint = "open", SetLastError = true)]
        public static extern int Open(byte[] path, int flags);

        [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
        public static extern int Sync(int descriptor);

        [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
        public static extern int Control(int descriptor, int command, int argument);

        [DllImport("libc", EntryPoint = "close", SetLastError = true)]
        public static extern int Close(int descriptor);
    }

    private static class Linux
    {
        [DllImport("libc.so.6", EntryPoint = "open", SetLastError = true)]
        public static extern int Open(byte[] path, int flags);

        [DllImport("libc.so.6", EntryPoint = "fsync", SetLastError = true)]
        public static extern int Sync(int descriptor);

        [DllImport("libc.so.6", EntryPoint = "close", SetLastError = true)]
        public static extern int Close(int descriptor);
    }

    private static class Windows
    {
        [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool MoveFileEx(string existingFileName, string newFileName, int flags);
    }
}
