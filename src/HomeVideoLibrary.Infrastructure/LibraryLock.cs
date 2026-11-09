using System.Diagnostics;
using System.Text;
using HomeVideoLibrary.Core;

namespace HomeVideoLibrary.Infrastructure;

/// <summary>Exclusive ownership of a library by one app instance, held for as long as the library is open. It is an OS file
/// lock on <c>.cache/library.lock</c>, so it is released when the process exits or crashes and never needs cleanup.
/// Holding it is what makes temporary files from earlier runs safe to delete (<see cref="ScratchSweeper"/>).</summary>
public sealed class LibraryLock : IDisposable
{
    private readonly FileStream _file;

    private LibraryLock(LibraryPaths paths, FileStream file)
    {
        Paths = paths;
        _file = file;
        AcquiredAt = DateTimeOffset.UtcNow;
    }

    public LibraryPaths Paths { get; }

    /// <summary>When this instance took ownership. Anything in the library's temporary folders written earlier belongs
    /// to a previous owner, since no other instance can have worked on the library since.</summary>
    public DateTimeOffset AcquiredAt { get; }

    /// <summary>Takes the lock, or returns null when another running instance holds it. Creates <c>.cache</c> if needed.</summary>
    /// <exception cref="UnauthorizedAccessException">The library folder is not writable.</exception>
    public static LibraryLock? TryAcquire(LibraryPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        Directory.CreateDirectory(paths.CacheDirectory);
        var path = Path.Combine(paths.CacheDirectory, "library.lock");
        FileStream file;
        try { file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException exception)
        {
            Trace.TraceInformation($"{paths.RootDirectory} is open in another instance: {exception.Message}");
            return null;
        }
        // Diagnostic only: names the owner for someone inspecting the file.
        file.SetLength(0);
        file.Write(Encoding.UTF8.GetBytes($"pid {Environment.ProcessId} since {DateTimeOffset.UtcNow:O}\n"));
        file.Flush();
        return new LibraryLock(paths, file);
    }

    public void Dispose() => _file.Dispose();
}
