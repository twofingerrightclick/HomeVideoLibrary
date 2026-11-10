using HomeVideoLibrary.Infrastructure;
using Xunit;
using static HomeVideoLibrary.Tests.TestLibrary;

namespace HomeVideoLibrary.Tests;

/// <summary>Every file these tests trash is one they just created in a temporary directory.</summary>
public sealed class FileTrashTests : IDisposable
{
    private const uint Uid = 4242;
    // Symlink-free (on macOS the temp directory is under /var, a symlink), as the Trash resolves paths before using them.
    private readonly string _root = Path.Combine(ResolvedTemp(), "hvl-trash-" + Guid.NewGuid().ToString("N"));

    private static string ResolvedTemp() =>
        OperatingSystem.IsWindows() ? Path.GetTempPath() : FreedesktopTrash.RealPath(Path.GetTempPath()) ?? Path.GetTempPath();

    /// <summary>Uses a disk image mounted inside the test directory, so the file lands in that volume's own
    /// <c>.Trashes</c> and the user's Trash is never touched; the image is detached afterwards.</summary>
    [Fact]
    public async Task TheMacTrashMovesAFileIntoItsVolumesOwnTrash()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var image = Path.Combine(_root, "volume.dmg");
        var mount = Directory.CreateDirectory(Path.Combine(_root, "volume")).FullName;
        await HdiutilAsync("create", "-size", "8m", "-fs", "APFS", "-volname", "HVLTrashTest", image);
        await HdiutilAsync("attach", "-nobrowse", "-noautoopen", "-mountpoint", mount, image);
        try
        {
            var file = CreateFile(Path.Combine(mount, "Videos", "tape.mkv"));

            Assert.Equal(TrashResult.Trashed, new MacFileTrash().MoveToTrash(file, out var location));

            Assert.False(File.Exists(file));
            Assert.NotNull(location);
            // The location is the real path (/private/var/... for /var/...).
            Assert.Contains($"/{Path.GetFileName(_root)}/volume/.Trashes/", location);
            Assert.True(File.Exists(location));
        }
        finally { await HdiutilAsync("detach", "-force", mount); }
    }

    private static async Task HdiutilAsync(params string[] arguments)
    {
        var result = await new MediaProcess().RunAsync("/usr/bin/hdiutil", arguments, TimeSpan.FromMinutes(1));
        Assert.True(result.ExitCode == 0, $"hdiutil {arguments[0]}: {result.StandardError}{result.StandardOutput}");
    }

    [Fact]
    public void AFileThatIsNotThereIsReportedNotTrashed()
    {
        Directory.CreateDirectory(_root);
        Assert.Throws<FileNotFoundException>(() => FileTrash.ForCurrentPlatform().MoveToTrash(Path.Combine(_root, "absent.mkv")));
    }

    [Fact]
    public void TheFreedesktopHomeTrashRecordsWhereTheFileCameFromAndNumbersTakenNames()
    {
        if (OperatingSystem.IsWindows()) return;
        var home = Path.Combine(_root, "home", "Trash");
        var trash = new FreedesktopTrash(home, _ => "/", Uid);
        var first = CreateFile(Path.Combine(_root, "Videos", "tape 1.mkv"));

        Assert.Equal(TrashResult.Trashed, trash.MoveToTrash(first));
        var second = CreateFile(first);
        Assert.Equal(TrashResult.Trashed, trash.MoveToTrash(second));

        Assert.False(File.Exists(first));
        Assert.Equal(["tape 1.2.mkv", "tape 1.mkv"], Names(Path.Combine(home, "files")));
        Assert.Equal(["tape 1.2.mkv.trashinfo", "tape 1.mkv.trashinfo"], Names(Path.Combine(home, "info")));
        var info = File.ReadAllLines(Path.Combine(home, "info", "tape 1.mkv.trashinfo"));
        Assert.Equal("[Trash Info]", info[0]);
        Assert.Equal("Path=" + Path.GetFullPath(first).Replace(" ", "%20"), info[1]);
        Assert.Matches(@"^DeletionDate=\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d$", info[2]);
    }

    [Fact]
    public void AFileOnAnotherDeviceGoesToThatDevicesOwnTrashWithARelativePath()
    {
        if (OperatingSystem.IsWindows()) return;
        var device = Path.Combine(_root, "external");
        var home = Path.Combine(_root, "home", "Trash");
        var trash = new FreedesktopTrash(home, path => Path.GetFullPath(path).StartsWith(device, StringComparison.Ordinal) ? device : "/", Uid);
        var file = CreateFile(Path.Combine(device, "Library", "Videos", "a%b.mkv"));

        Assert.Equal(TrashResult.Trashed, trash.MoveToTrash(file));

        Assert.False(Directory.Exists(home));
        var own = Path.Combine(device, $".Trash-{Uid}");
        Assert.True(File.Exists(Path.Combine(own, "files", "a%b.mkv")));
        Assert.Contains("Path=Library/Videos/a%25b.mkv", File.ReadAllLines(Path.Combine(own, "info", "a%b.mkv.trashinfo")));
    }

    [Fact]
    public void AnAdministratorsStickyTrashIsUsedAndASymlinkedOneIsNot()
    {
        if (OperatingSystem.IsWindows()) return;
        var sticky = Path.Combine(_root, "sticky");
        var shared = Directory.CreateDirectory(Path.Combine(sticky, ".Trash"));
        File.SetUnixFileMode(shared.FullName, (UnixFileMode)0b1_111_111_111);
        var linked = Path.Combine(_root, "linked");
        Directory.CreateDirectory(linked);
        Directory.CreateSymbolicLink(Path.Combine(linked, ".Trash"), shared.FullName);
        var trash = new FreedesktopTrash(Path.Combine(_root, "home", "Trash"),
            path => Path.GetFullPath(path).StartsWith(sticky, StringComparison.Ordinal) ? sticky
                : Path.GetFullPath(path).StartsWith(linked, StringComparison.Ordinal) ? linked : "/", Uid);

        Assert.Equal(TrashResult.Trashed, trash.MoveToTrash(CreateFile(Path.Combine(sticky, "one.mkv"))));
        Assert.Equal(TrashResult.Trashed, trash.MoveToTrash(CreateFile(Path.Combine(linked, "two.mkv"))));

        Assert.True(File.Exists(Path.Combine(shared.FullName, Uid.ToString(), "files", "one.mkv")));
        Assert.True(File.Exists(Path.Combine(linked, $".Trash-{Uid}", "files", "two.mkv")));
    }

    [Fact]
    public void AFileWhoseDeviceIsUnknownStaysWhereItIs()
    {
        if (OperatingSystem.IsWindows()) return;
        var file = CreateFile(Path.Combine(_root, "Videos", "kept.mkv"));
        var trash = new FreedesktopTrash(Path.Combine(_root, "home", "Trash"), path => path.EndsWith("Videos", StringComparison.Ordinal) ? null : "/", Uid);

        Assert.Equal(TrashResult.Unavailable, trash.MoveToTrash(file));
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void AFileReachedThroughASymlinkIsTrashedOnTheDeviceItIsReallyOn()
    {
        if (OperatingSystem.IsWindows()) return;
        var device = Path.Combine(_root, "external");
        var file = CreateFile(Path.Combine(device, "Library", "Videos", "a.mkv"));
        var link = Path.Combine(_root, "link");
        Directory.CreateSymbolicLink(link, Path.Combine(device, "Library"));
        var trash = new FreedesktopTrash(Path.Combine(_root, "home", "Trash"), path => path.StartsWith(device, StringComparison.Ordinal) ? device : "/", Uid);

        Assert.Equal(TrashResult.Trashed, trash.MoveToTrash(Path.Combine(link, "Videos", "a.mkv")));

        Assert.False(File.Exists(file));
        Assert.Contains("Path=Library/Videos/a.mkv", File.ReadAllLines(Path.Combine(device, $".Trash-{Uid}", "info", "a.mkv.trashinfo")));
    }

    [Fact]
    public void MountPointsAreDecodedAndTheLongestOneHoldingTheFileWins()
    {
        if (OperatingSystem.IsWindows()) return;
        string[] mountInfo =
        [
            "22 1 8:1 / / rw,relatime shared:1 - ext4 /dev/sda1 rw",
            @"30 22 8:17 / /media/My\040Drive rw,nosuid - exfat /dev/sdb1 rw",
            "31 22 8:1 /srv/videos /home/me/Videos rw,relatime shared:1 - ext4 /dev/sda1 rw",
        ];

        Assert.Equal("/media/My Drive", FreedesktopTrash.MountPointIn("/media/My Drive/Library/Videos", mountInfo));
        Assert.Equal("/home/me/Videos", FreedesktopTrash.MountPointIn("/home/me/Videos/Library", mountInfo));
        Assert.Equal("/", FreedesktopTrash.MountPointIn("/home/me/Videos2", mountInfo));
        Assert.Equal("a b\\c", FreedesktopTrash.Unescape(@"a\040b\134c"));
    }

    [Fact]
    public void AMoveTheKernelRefusesAcrossDevicesLeavesTheFileAndNoRecord()
    {
        if (OperatingSystem.IsWindows()) return;
        var home = Path.Combine(_root, "home", "Trash");
        var file = CreateFile(Path.Combine(_root, "Videos", "a.mkv"));
        var trash = new FreedesktopTrash(home, _ => "/", Uid, rename: (_, _) => 18);

        Assert.Equal(TrashResult.Unavailable, trash.MoveToTrash(file));

        Assert.True(File.Exists(file));
        Assert.Empty(Directory.GetFiles(Path.Combine(home, "info")));
    }

    [Fact]
    public void AnUnusableSharedTrashFallsBackToTheUsersOwn()
    {
        if (OperatingSystem.IsWindows()) return;
        var device = Path.Combine(_root, "device");
        var shared = Directory.CreateDirectory(Path.Combine(device, ".Trash"));
        File.SetUnixFileMode(shared.FullName, (UnixFileMode)0b1_111_111_111);
        File.WriteAllText(Path.Combine(shared.FullName, Uid.ToString()), "not a directory");
        var trash = new FreedesktopTrash(Path.Combine(_root, "home", "Trash"), path => path.StartsWith(device, StringComparison.Ordinal) ? device : "/", Uid);

        Assert.Equal(TrashResult.Trashed, trash.MoveToTrash(CreateFile(Path.Combine(device, "a.mkv"))));

        Assert.True(File.Exists(Path.Combine(device, $".Trash-{Uid}", "files", "a.mkv")));
    }

    [Fact]
    public void NonAsciiNamesKeepTheirNameAndArePercentEncodedInTheRecord()
    {
        if (OperatingSystem.IsWindows()) return;
        var home = Path.Combine(_root, "home", "Trash");
        var name = "Grandma’s tape ✓.mkv";
        var file = CreateFile(Path.Combine(_root, "Vidéos", name));

        Assert.Equal(TrashResult.Trashed, new FreedesktopTrash(home, _ => "/", Uid).MoveToTrash(file));

        Assert.True(File.Exists(Path.Combine(home, "files", name)));
        var recorded = File.ReadAllLines(Path.Combine(home, "info", name + ".trashinfo"))[1];
        Assert.EndsWith("/Vid%C3%A9os/Grandma%E2%80%99s%20tape%20%E2%9C%93.mkv", recorded);
        Assert.All(recorded, character => Assert.True(character < 128));
    }

    [Theory]
    [InlineData(@"\\?\C:\Library\Videos\a.mkv", @"C:\")]
    [InlineData(@"D:\Videos\a.mkv", @"D:\")]
    [InlineData(@"\\?\Volume{0b2a7c39-1111-2222-3333-444455556666}\Videos\a.mkv", @"\\?\Volume{0b2a7c39-1111-2222-3333-444455556666}\")]
    [InlineData(@"\\?\UNC\nas\share\Videos\a.mkv", null)]
    [InlineData(@"\\nas\share\Videos\a.mkv", null)]
    public void AWindowsFileIsJudgedByTheDriveItIsReallyOn(string finalPath, string? root) =>
        Assert.Equal(root, WindowsTrashRules.VolumeRoot(finalPath));

    [Theory]
    [InlineData(false, null, 5_000_000_000L, true)]
    [InlineData(true, null, 1L, false)]
    [InlineData(false, 4_000_000_000L, 5_000_000_000L, false)]
    [InlineData(false, 4_000_000_000L, 4_000_000_000L, true)]
    public void TheRecycleBinTakesOnlyFilesItKeeps(bool nukeOnDelete, long? capacity, long length, bool accepts) =>
        Assert.Equal(accepts, WindowsTrashRules.BinAccepts(length, new RecycleBinPolicy(nukeOnDelete, capacity)));

    [Theory]
    [InlineData(0, false, true, TrashResult.Trashed)]
    [InlineData(0, false, false, TrashResult.Deleted)]
    [InlineData(unchecked((int)0x800704C7), false, null, TrashResult.Unavailable)]
    [InlineData(0x75, false, null, TrashResult.Unavailable)]
    [InlineData(0, true, null, TrashResult.Unavailable)]
    [InlineData(unchecked((int)0x80070020), false, null, TrashResult.Locked)]
    [InlineData(unchecked((int)0x80270031), false, null, TrashResult.Locked)]
    public void WindowsOutcomesMapToWhatHappenedToTheFile(int result, bool aborted, bool? recycled, TrashResult expected) =>
        Assert.Equal(expected, WindowsTrashRules.Map(result, aborted, recycled, "a.mkv"));

    [Fact]
    public void AFileDeletedOutrightIsNeverReportedAsTrashedAndOtherFailuresThrow()
    {
        Assert.NotEqual(TrashResult.Trashed, WindowsTrashRules.Map(0, aborted: true, recycled: false, "a.mkv"));
        Assert.Throws<IOException>(() => WindowsTrashRules.Map(unchecked((int)0x80070005), false, null, "a.mkv"));
    }

    private static string CreateFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "test video");
        return path;
    }

    private static string[] Names(string directory) =>
        Directory.GetFiles(directory).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()!;

    public void Dispose() => DeleteDirectory(_root);
}
