using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace HomeVideoLibrary.Infrastructure;

/// <summary>The freedesktop.org Trash (version 1.0), used by GNOME, KDE and most Linux file managers. A file goes to the
/// home Trash when it is on the same device, otherwise to its own device's <c>$topdir/.Trash/$uid</c> or
/// <c>$topdir/.Trash-$uid</c>. Each move first reserves its name by creating <c>info/NAME.trashinfo</c> exclusively,
/// then renames the file with rename(2), which never copies: a file on a device without a usable Trash is
/// <see cref="TrashResult.Unavailable"/>.</summary>
[UnsupportedOSPlatform("windows")]
internal sealed class FreedesktopTrash : IFileTrash
{
    private const int CrossDevice = 18, Busy = 16; // EXDEV, EBUSY
    private const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private readonly string _homeTrash;
    private readonly Func<string, string?> _mountPointOf;
    private readonly uint _uid;

    public FreedesktopTrash() : this(HomeTrash(), MountPointOf, GetUserId()) { }

    /// <param name="mountPointOf">The mount point of the device holding an existing path; null when unknown.</param>
    internal FreedesktopTrash(string homeTrash, Func<string, string?> mountPointOf, uint uid)
    {
        _homeTrash = Path.GetFullPath(homeTrash); _mountPointOf = mountPointOf; _uid = uid;
    }

    public TrashResult MoveToTrash(string path)
    {
        var full = Path.GetFullPath(path);
        FileTrash.RequireFile(full);
        var device = _mountPointOf(Path.GetDirectoryName(full)!);
        if (device == _mountPointOf(NearestExisting(_homeTrash)))
            return Move(full, _homeTrash, relativeTo: null);
        if (device is null || TopDirectoryTrash(device) is not { } trash) return TrashResult.Unavailable;
        return Move(full, trash, relativeTo: device);
    }

    /// <summary>The device's own Trash: <c>$topdir/.Trash/$uid</c> when the administrator provided a sticky,
    /// non-symlink <c>.Trash</c>, otherwise <c>$topdir/.Trash-$uid</c>. Null when neither can be used.</summary>
    private string? TopDirectoryTrash(string topDirectory)
    {
        var shared = new DirectoryInfo(Path.Combine(topDirectory, ".Trash"));
        if (shared.Exists && shared.LinkTarget is null && shared.UnixFileMode.HasFlag(UnixFileMode.StickyBit))
        {
            var mine = Path.Combine(shared.FullName, _uid.ToString(CultureInfo.InvariantCulture));
            if (IsUsable(mine)) return mine;
        }
        var own = Path.Combine(topDirectory, ".Trash-" + _uid.ToString(CultureInfo.InvariantCulture));
        return IsUsable(own) ? own : null;
    }

    /// <summary>Whether <paramref name="trash"/> is (or can be created as) a real directory; a symlink is refused.</summary>
    private static bool IsUsable(string trash)
    {
        var directory = new DirectoryInfo(trash);
        if (directory.Exists) return directory.LinkTarget is null;
        if (File.Exists(trash)) return false;
        try
        {
            Directory.CreateDirectory(trash, Private);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <param name="relativeTo">The top directory the trashinfo path is relative to (so the device can be mounted
    /// elsewhere), or null for an absolute path.</param>
    private static TrashResult Move(string file, string trash, string? relativeTo)
    {
        var files = Directory.CreateDirectory(Path.Combine(trash, "files"), Private).FullName;
        var info = Directory.CreateDirectory(Path.Combine(trash, "info"), Private).FullName;
        var original = relativeTo is null ? file : Path.GetRelativePath(relativeTo, file);
        var (name, record) = Reserve(info, files, file, original);
        if (Rename(file, Path.Combine(files, name)) is not { } errno) return TrashResult.Trashed;
        File.Delete(record);
        return errno switch
        {
            CrossDevice => TrashResult.Unavailable,
            Busy => TrashResult.Locked,
            _ => throw new IOException($"Could not move {Path.GetFileName(file)} to the Trash (errno {errno})."),
        };
    }

    /// <summary>Claims a name in the Trash by creating its <c>.trashinfo</c> with O_EXCL; a taken name gets a number
    /// ("clip.2.mkv").</summary>
    private static (string Name, string Record) Reserve(string info, string files, string file, string original)
    {
        var stem = Path.GetFileNameWithoutExtension(file);
        var extension = Path.GetExtension(file);
        for (var attempt = 1; ; attempt++)
        {
            var name = attempt == 1 ? stem + extension : $"{stem}.{attempt}{extension}";
            var record = Path.Combine(info, name + ".trashinfo");
            if (File.Exists(Path.Combine(files, name))) continue;
            FileStream stream;
            try { stream = new FileStream(record, FileMode.CreateNew, FileAccess.Write, FileShare.None); }
            catch (IOException) when (File.Exists(record)) { continue; }
            try
            {
                using (stream)
                {
                    stream.Write(Encoding.UTF8.GetBytes(TrashInfo(original, DateTime.Now)));
                    stream.Flush(flushToDisk: true);
                }
                return (name, record);
            }
            catch
            {
                File.Delete(record);
                throw;
            }
        }
    }

    /// <summary>The <c>.trashinfo</c> content: the original path with each segment percent-encoded, and the local
    /// deletion time.</summary>
    internal static string TrashInfo(string original, DateTime deleted) =>
        "[Trash Info]\nPath=" + string.Join('/', original.Split('/').Select(Uri.EscapeDataString)) +
        "\nDeletionDate=" + deleted.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + "\n";

    private static string NearestExisting(string path)
    {
        var current = path;
        while (!Directory.Exists(current) && Path.GetDirectoryName(current) is { } parent) current = parent;
        return current;
    }

    private static string HomeTrash()
    {
        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrEmpty(dataHome) || !Path.IsPathRooted(dataHome))
            dataHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        return Path.Combine(dataHome, "Trash");
    }

    /// <summary>The longest mount point in <c>/proc/self/mountinfo</c> containing the real path of <paramref name="path"/>.</summary>
    private static string? MountPointOf(string path)
    {
        string resolved;
        string[] lines;
        try
        {
            resolved = RealPath(path) ?? path;
            lines = File.ReadAllLines("/proc/self/mountinfo");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DllNotFoundException) { return null; }
        return lines.Select(line => line.Split(' ')).Where(fields => fields.Length > 4).Select(fields => Unescape(fields[4]))
            .Where(mount => mount == "/" || resolved == mount || resolved.StartsWith(mount + "/", StringComparison.Ordinal))
            .MaxBy(mount => mount.Length);
    }

    /// <summary>Decodes mountinfo's octal escapes (<c>\040</c> for a space).</summary>
    private static string Unescape(string field)
    {
        var builder = new StringBuilder(field.Length);
        for (var index = 0; index < field.Length; index++)
        {
            if (field[index] == '\\' && index + 3 < field.Length && field.AsSpan(index + 1, 3).IndexOfAnyExceptInRange('0', '7') < 0)
            {
                builder.Append((char)Convert.ToInt32(field.Substring(index + 1, 3), 8));
                index += 3;
            }
            else builder.Append(field[index]);
        }
        return builder.ToString();
    }

    private static string? RealPath(string path)
    {
        var resolved = RealPathNative(path, 0);
        if (resolved == 0) return null;
        try { return Marshal.PtrToStringUTF8(resolved); }
        finally { Free(resolved); }
    }

    /// <summary>rename(2); returns null on success, else errno.</summary>
    private static int? Rename(string source, string destination) =>
        RenameNative(source, destination) == 0 ? null : Marshal.GetLastPInvokeError();

    [DllImport("libc", EntryPoint = "rename", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern int RenameNative(string source, string destination);

    [DllImport("libc", EntryPoint = "realpath", CharSet = CharSet.Ansi)]
    private static extern nint RealPathNative(string path, nint resolved);

    [DllImport("libc", EntryPoint = "free")]
    private static extern void Free(nint pointer);

    [DllImport("libc", EntryPoint = "getuid")]
    private static extern uint GetUserId();
}
