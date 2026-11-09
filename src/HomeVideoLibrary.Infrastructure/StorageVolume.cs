namespace HomeVideoLibrary.Infrastructure;

/// <summary>Where a library lives, for choices that depend on the filesystem.</summary>
internal static class StorageVolume
{
    /// <summary>True when <paramref name="path"/> is on a network filesystem (SMB, NFS, AFP, WebDAV…): a UNC path on
    /// Windows, or a mount the OS reports as a network drive.</summary>
    public static bool IsNetwork(string path) => IsNetwork(path, Drives());

    internal static bool IsNetwork(string path, IEnumerable<(string Root, DriveType Type)> drives)
    {
        var full = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows() && full.StartsWith(@"\\", StringComparison.Ordinal) &&
            (!full.StartsWith(@"\\?\", StringComparison.Ordinal) || full.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)))
            return true;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var mount = drives.Where(drive => Contains(drive.Root, full, comparison)).OrderByDescending(drive => drive.Root.Length).FirstOrDefault();
        return mount.Type == DriveType.Network;
    }

    private static bool Contains(string root, string path, StringComparison comparison)
    {
        var trimmed = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed.Length == 0 || path.Equals(trimmed, comparison) ||
            path.StartsWith(trimmed + Path.DirectorySeparatorChar, comparison);
    }

    private static IEnumerable<(string Root, DriveType Type)> Drives()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            DriveType type;
            try { type = drive.DriveType; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { continue; }
            yield return (drive.RootDirectory.FullName, type);
        }
    }
}
