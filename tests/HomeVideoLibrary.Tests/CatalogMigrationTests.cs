using HomeVideoLibrary.Core;
using HomeVideoLibrary.Infrastructure;
using HomeVideoLibrary.Desktop.ViewModels;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static HomeVideoLibrary.Tests.TestLibrary;

namespace HomeVideoLibrary.Tests;

public sealed class CatalogMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hvl-migration-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FreshAndRepeatedInitializationUseOneMigrationAndVolumeJournal(bool network)
    {
        var paths = new LibraryPaths(_root);
        var catalog = new LibraryCatalog(paths, _ => network);
        await catalog.InitializeAsync();
        Assert.Equal(network ? "delete" : "wal", await ScalarAsync(paths.DatabasePath, "PRAGMA journal_mode"));
        await catalog.InitializeAsync();
        Assert.Equal(network ? "delete" : "wal", await ScalarAsync(paths.DatabasePath, "PRAGMA journal_mode"));
        await using var context = new LibraryDbContext(LibraryDbContext.Options(paths.DatabasePath));
        var migration = Assert.Single(context.Database.GetMigrations());
        Assert.EndsWith("_InitialCreate", migration);
        Assert.Equal(migration, Assert.Single(await context.Database.GetAppliedMigrationsAsync()));
        Assert.False(context.Database.HasPendingModelChanges());
        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        Assert.Empty(Directory.GetFiles(paths.CacheDirectory, "backup-*"));
        await using var connection = await catalog.OpenAsync(default);
        foreach (var (pragma, expected) in new[] { ("foreign_keys", 1L), ("busy_timeout", 5000L), ("synchronous", 1L) })
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA " + pragma;
            Assert.Equal(expected, await command.ExecuteScalarAsync());
        }
    }

    [Fact]
    public async Task EmptyExistingDatabaseIsAllowed()
    {
        var paths = new LibraryPaths(_root);
        Directory.CreateDirectory(_root);
        await ExecuteAsync(paths, "PRAGMA application_id=0");
        await new LibraryCatalog(paths).InitializeAsync();
        Assert.Equal(1L, await ScalarAsync(paths.DatabasePath, "SELECT COUNT(*) FROM __EFMigrationsHistory"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedFirstMigrationWithEmptyBookkeepingCanBeRetried(bool historyCreated)
    {
        var paths = new LibraryPaths(_root);
        Directory.CreateDirectory(_root);
        await ExecuteAsync(paths, "CREATE TABLE __EFMigrationsLock(Id INTEGER PRIMARY KEY, Timestamp TEXT NOT NULL);" +
            (historyCreated ? "CREATE TABLE __EFMigrationsHistory(MigrationId TEXT PRIMARY KEY,ProductVersion TEXT NOT NULL);" : ""));
        var catalog = new LibraryCatalog(paths, _ => true);
        await catalog.InitializeAsync();
        await catalog.InitializeAsync();
        Assert.Equal(1L, await ScalarAsync(paths.DatabasePath, "SELECT COUNT(*) FROM __EFMigrationsHistory"));
        Assert.Equal("delete", await ScalarAsync(paths.DatabasePath, "PRAGMA journal_mode"));
    }

    [Fact]
    public async Task OccupiedMigrationLockIsRejectedWithoutWaitingOrDeletingIt()
    {
        var catalog = await CreateCatalogAsync(_root);
        await ExecuteAsync(catalog.Paths, "INSERT INTO __EFMigrationsLock VALUES(1,'2024-01-01T00:00:00Z')");
        var before = await File.ReadAllBytesAsync(catalog.Paths.DatabasePath);
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => catalog.InitializeAsync());
        Assert.Contains("unfinished EF migration lock", exception.Message);
        Assert.Equal(before, await File.ReadAllBytesAsync(catalog.Paths.DatabasePath));
    }

    [Theory]
    [InlineData(true, "delete")]
    [InlineData(false, "wal")]
    public async Task JournalPolicyChangesBeforePendingMigrationWork(bool network, string expected)
    {
        var catalog = await CreateCatalogAsync(_root);
        // EF inserts its lock before processing pending migrations. Observe the journal from a trigger there.
        await ExecuteAsync(catalog.Paths, """
            CREATE TABLE journal_observation(mode TEXT);
            CREATE TRIGGER observe_migration_lock AFTER INSERT ON __EFMigrationsLock BEGIN
                INSERT INTO journal_observation SELECT journal_mode FROM pragma_journal_mode;
            END;
            """);
        await new LibraryCatalog(catalog.Paths, _ => !network).InitializeAsync();
        await ExecuteAsync(catalog.Paths, "DELETE FROM journal_observation");
        await new LibraryCatalog(catalog.Paths, _ => network).InitializeAsync();
        Assert.Equal(expected, await ScalarAsync(catalog.Paths.DatabasePath, "SELECT mode FROM journal_observation LIMIT 1"));
        Assert.Equal(expected, await ScalarAsync(catalog.Paths.DatabasePath, "PRAGMA journal_mode"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("CREATE TABLE __EFMigrationsHistory(MigrationId TEXT PRIMARY KEY,ProductVersion TEXT NOT NULL);")]
    [InlineData("CREATE TABLE __EFMigrationsHistory(wrong TEXT);")]
    public async Task LegacyCatalogRejectionLeavesDatabaseAndMediaUnchanged(string history)
    {
        var paths = new LibraryPaths(_root);
        Directory.CreateDirectory(paths.VideosDirectory);
        await ExecuteAsync(paths, "CREATE TABLE albums(id TEXT PRIMARY KEY,name TEXT); INSERT INTO albums VALUES('kept','Family');" + history);
        var pending = paths.PendingVideoPath(Guid.NewGuid());
        await File.WriteAllTextAsync(pending, "untouched media");
        var before = await File.ReadAllBytesAsync(paths.DatabasePath);
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => new LibraryCatalog(paths).InitializeAsync());
        Assert.Contains("fresh library", exception.Message);
        Assert.Equal(before, await File.ReadAllBytesAsync(paths.DatabasePath));
        Assert.Equal("Family", await ScalarAsync(paths.DatabasePath, "SELECT name FROM albums"));
        Assert.Equal("untouched media", await File.ReadAllTextAsync(pending));
        Assert.False(Directory.Exists(paths.CacheDirectory));
    }

    [Fact]
    public async Task OpeningRejectedCatalogDoesNotRecoverPublicationsOrSweepScratch()
    {
        var paths = new LibraryPaths(_root);
        Directory.CreateDirectory(paths.VideosDirectory);
        Directory.CreateDirectory(paths.ScratchDirectory);
        await ExecuteAsync(paths, "CREATE TABLE albums(id TEXT PRIMARY KEY,name TEXT); INSERT INTO albums VALUES('kept','Family');");
        var pending = paths.PendingVideoPath(Guid.NewGuid());
        await File.WriteAllTextAsync(pending, "pending media");
        var scratch = Path.Combine(paths.ScratchDirectory, "old.mkv");
        await File.WriteAllTextAsync(scratch, "scratch media");
        var before = await File.ReadAllBytesAsync(paths.DatabasePath);
        var swept = false;
        var servicesCreated = false;
        var viewModel = new MainWindowViewModel(new MainWindowDependencies
        {
            Interaction = new FakeInteraction(),
            Settings = new LibrarySettings(Path.Combine(_root, "settings.json")),
            CreateServices = _ => { servicesCreated = true; throw new InvalidOperationException(); },
            SweepScratch = (_, _) => { swept = true; return Task.CompletedTask; },
            Post = action => action(),
        });

        await viewModel.OpenLibraryAsync(_root);

        Assert.Contains("fresh library", viewModel.Status);
        Assert.False(swept);
        Assert.False(servicesCreated);
        Assert.Equal(before, await File.ReadAllBytesAsync(paths.DatabasePath));
        Assert.Equal("pending media", await File.ReadAllTextAsync(pending));
        Assert.Equal("scratch media", await File.ReadAllTextAsync(scratch));
        Assert.False(File.Exists(Path.Combine(_root, "settings.json")));
        using var owner = LibraryLock.TryAcquire(paths);
        Assert.NotNull(owner);
    }

    [Theory]
    [InlineData("99991231235959_Future")]
    [InlineData("20000101000000_Unknown")]
    public async Task UnknownAppliedMigrationIsRejectedWithoutChanges(string migration)
    {
        var catalog = await CreateCatalogAsync(_root);
        await ExecuteAsync(catalog.Paths, $"INSERT INTO __EFMigrationsHistory VALUES('{migration}','10.0.14')");
        var before = await File.ReadAllBytesAsync(catalog.Paths.DatabasePath);
        await Assert.ThrowsAsync<InvalidDataException>(() => catalog.InitializeAsync());
        Assert.Equal(before, await File.ReadAllBytesAsync(catalog.Paths.DatabasePath));
    }

    [Fact]
    public async Task RawSqlRepositoriesRetainConstraintsCascadesAndCacheCompatibility()
    {
        var catalog = await CreateCatalogAsync(_root);
        var first = await AddVideoAsync(catalog, "First");
        var second = await AddVideoAsync(catalog, "Second");
        var albums = new AlbumRepository(catalog);
        var album = await albums.CreateAsync("École", [first.Id, second.Id]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => albums.CreateAsync("ecole"));
        await albums.AddVideosAsync(album.Id, [first.Id]);
        await catalog.WriteCacheAsync([new(first.Id, new("{}", 2, 3), TimeSpan.FromSeconds(5))]);
        await ExecuteAsync(catalog.Paths, $"UPDATE videos SET source_fingerprint='v2:sample' WHERE id='{first.Id}'");
        var duplicate = await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(catalog.Paths,
            $"UPDATE videos SET source_fingerprint='v2:sample' WHERE id='{second.Id}'"));
        Assert.True(LibraryCatalog.IsDuplicateSourceFingerprint(duplicate));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(catalog.Paths,
            $"UPDATE videos SET relative_path='{first.RelativePath}' WHERE id='{second.Id}'"));
        await using (var connection = await catalog.OpenAsync(default))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"INSERT INTO album_videos VALUES('{album.Id}','{Guid.NewGuid()}')";
            await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());
            command.CommandText = $"INSERT INTO album_videos VALUES('{album.Id}','{first.Id}')";
            await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());
        }
        var contents = await catalog.LoadContentsAsync();
        Assert.Equal(2, contents.Memberships.Count);
        Assert.Equal("{}", contents.Cache[first.Id].RecordJson);
        Assert.Equal(TimeSpan.FromSeconds(5), contents.Videos.Single(video => video.Id == first.Id).Duration);
        await catalog.DeleteVideosAsync([first.Id]);
        contents = await catalog.LoadContentsAsync();
        Assert.Empty(contents.Cache);
        Assert.Single(contents.Memberships);
        await albums.DeleteAsync(album.Id);
        Assert.Empty((await catalog.LoadContentsAsync()).Memberships);
        Assert.True(File.Exists(catalog.GetVideoPath(first.RelativePath)));
    }

    [Fact]
    public async Task CancellationBeforeInitializationTouchesNothingAndCanBeRetried()
    {
        var paths = new LibraryPaths(_root);
        var catalog = new LibraryCatalog(paths);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => catalog.InitializeAsync(cancellation.Token));
        Assert.False(Directory.Exists(_root));
        await catalog.InitializeAsync();
        var before = await File.ReadAllBytesAsync(paths.DatabasePath);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => catalog.InitializeAsync(cancellation.Token));
        Assert.Equal(before, await File.ReadAllBytesAsync(paths.DatabasePath));
        await catalog.InitializeAsync();
    }

    [Fact]
    public void DesignFactoryRequiresExplicitAbsoluteScratchPath()
    {
        var factory = new LibraryDbContextFactory();
        Assert.Throws<ArgumentException>(() => factory.CreateDbContext([]));
        Assert.Throws<ArgumentException>(() => factory.CreateDbContext(["--database-path", "relative.sqlite3"]));
        using var context = factory.CreateDbContext(["--database-path", Path.Combine(_root, "scratch.sqlite3")]);
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task UnreadableRowsAreSkippedAndReported()
    {
        var catalog = await CreateCatalogAsync(_root);
        var good = await AddVideoAsync(catalog, "good");
        await ExecuteAsync(catalog.Paths, """
            INSERT INTO videos(id,relative_path,display_name,duration_ticks,imported_at,recorded_at,source_fingerprint) VALUES ('not-a-guid', 'Videos/bad.mkv', 'Bad', 1, '2024-01-01T00:00:00Z', '2024-01-01T00:00:00Z', NULL);
            INSERT INTO videos(id,relative_path,display_name,duration_ticks,imported_at,recorded_at,source_fingerprint) VALUES ('7b0a5f8e-35a4-4c43-9d43-0d3e3b0b4d11', '../outside.mkv', 'Escapes', 1, '2024-01-01T00:00:00Z', '2024-01-01T00:00:00Z', NULL);
            """);

        Assert.Equal(good.Id, Assert.Single(await catalog.ListAsync()).Id);
        var contents = await catalog.LoadContentsAsync();
        Assert.Equal(good.Id, Assert.Single(contents.Videos).Id);
        Assert.Equal(2, contents.Issues.Count);
    }

    [Theory]
    [InlineData("smbfs", 0x1000u, true)]
    [InlineData("nfs", 0x1000u, true)]
    [InlineData("apfs", 0x1000u, false)]
    [InlineData("macfuse", 0u, true)]
    public void MacNetworkFilesystemsAreRecognized(string type, uint flags, bool network) =>
        Assert.Equal(network, StorageVolume.IsMacNetwork(type, flags));

    [Theory]
    [InlineData(0x6969L, true)]
    [InlineData(0xFF534D42L, true)]
    [InlineData(0xEF53L, false)]
    [InlineData(0x9123683EL, false)]
    public void LinuxNetworkFilesystemsAreRecognized(long magic, bool network) =>
        Assert.Equal(network, StorageVolume.IsLinuxNetwork(magic));

    [Fact]
    public async Task ProbingTheLibraryVolumeIsSafeConcurrentlyAndFallsBackToLocal()
    {
        Directory.CreateDirectory(_root);
        var answers = await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(() => StorageVolume.IsNetwork(_root))));
        Assert.All(answers, Assert.False);
        Assert.False(StorageVolume.IsNetwork(Path.Combine(_root, "does not exist")));
    }

    private static async Task ExecuteAsync(LibraryPaths paths, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={paths.DatabasePath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(string database, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={database};Pooling=False;Mode=ReadOnly");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    public void Dispose() => DeleteDirectory(_root);
}
