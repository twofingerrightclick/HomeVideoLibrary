using System.Diagnostics;

namespace HomeVideoLibrary.Infrastructure.Editing;

/// <summary>Cleanup that never masks the error being cleaned up after.</summary>
internal static class ScratchFiles
{
    public static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"Could not delete temporary file {path}: {exception.Message}");
        }
    }

    public static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"Could not delete temporary directory {path}: {exception.Message}");
        }
    }

    /// <summary>Throws when the volume holding <paramref name="directory"/> has less than <paramref name="bytes"/> free.</summary>
    public static void EnsureFreeSpace(string directory, long bytes)
    {
        long available;
        try { available = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(directory))!).AvailableFreeSpace; }
        catch (Exception exception) when (exception is IOException or ArgumentException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"Could not check free space for {directory}: {exception.Message}");
            return;
        }
        if (available < bytes)
            throw new IOException($"Not enough free disk space: {FormatSize(bytes)} needed, {FormatSize(available)} available.");
    }

    private static string FormatSize(long bytes) => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB" : $"{bytes / (double)(1L << 20):0} MB";
}
