using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace HomeVideoLibrary.Infrastructure;

/// <summary>Owns the catalog schema. Runtime repositories deliberately retain their raw SQL and tolerant reads.</summary>
public sealed class LibraryDbContext(DbContextOptions<LibraryDbContext> options) : DbContext(options)
{
    internal static DbContextOptions<LibraryDbContext> Options(string databasePath) =>
        new DbContextOptionsBuilder<LibraryDbContext>().UseSqlite(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath, Pooling = false, ForeignKeys = true,
        }.ToString()).Options;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var library = modelBuilder.Entity<LibraryInfoRow>();
        library.ToTable("library_info");
        library.HasKey(row => row.Id);
        library.Property(row => row.Id).HasColumnName("id").ValueGeneratedNever();
        library.Property(row => row.Name).HasColumnName("name");

        var deletions = modelBuilder.Entity<DeletionHistoryRow>();
        deletions.ToTable("deletion_history");
        deletions.HasKey(row => row.Fingerprint);
        deletions.Property(row => row.Fingerprint).HasColumnName("fingerprint");
        deletions.Property(row => row.Title).HasColumnName("title");
        deletions.Property(row => row.RemovedAt).HasColumnName("removed_at");
        deletions.Property(row => row.Kind).HasColumnName("kind");

        var videos = modelBuilder.Entity<VideoRow>();
        videos.ToTable("videos");
        videos.HasKey(row => row.Id);
        videos.Property(row => row.Id).HasColumnName("id");
        videos.Property(row => row.RelativePath).HasColumnName("relative_path");
        videos.Property(row => row.DisplayName).HasColumnName("display_name");
        videos.Property(row => row.DurationTicks).HasColumnName("duration_ticks");
        videos.Property(row => row.ImportedAt).HasColumnName("imported_at");
        videos.Property(row => row.RecordedAt).HasColumnName("recorded_at");
        videos.Property(row => row.SourceFingerprint).HasColumnName("source_fingerprint");
        videos.HasIndex(row => row.RelativePath).IsUnique().HasDatabaseName("videos_relative_path");
        videos.HasIndex(row => row.SourceFingerprint).IsUnique().HasDatabaseName("videos_source_fingerprint_key")
            .HasFilter("source_fingerprint IS NOT NULL");

        var albums = modelBuilder.Entity<AlbumRow>();
        albums.ToTable("albums");
        albums.HasKey(row => row.Id);
        albums.Property(row => row.Id).HasColumnName("id");
        albums.Property(row => row.Name).HasColumnName("name");
        // AlbumNames.Key supplies Unicode identity; SQLite must compare the stored key byte for byte.
        albums.Property(row => row.NameKey).HasColumnName("name_key").UseCollation("BINARY");
        albums.HasIndex(row => row.NameKey).IsUnique().HasDatabaseName("albums_name_key");

        var memberships = modelBuilder.Entity<AlbumVideoRow>();
        memberships.ToTable("album_videos");
        memberships.HasKey(row => new { row.AlbumId, row.VideoId });
        memberships.Property(row => row.AlbumId).HasColumnName("album_id");
        memberships.Property(row => row.VideoId).HasColumnName("video_id");
        memberships.HasOne<AlbumRow>().WithMany().HasForeignKey(row => row.AlbumId).OnDelete(DeleteBehavior.Cascade);
        memberships.HasOne<VideoRow>().WithMany().HasForeignKey(row => row.VideoId).OnDelete(DeleteBehavior.Cascade);
        memberships.HasIndex(row => row.VideoId).HasDatabaseName("album_videos_video");

        var cache = modelBuilder.Entity<VideoCacheRow>();
        cache.ToTable("video_cache");
        cache.HasKey(row => row.VideoId);
        cache.Property(row => row.VideoId).HasColumnName("video_id");
        cache.Property(row => row.RecordJson).HasColumnName("record_json");
        cache.Property(row => row.FileLength).HasColumnName("file_length");
        cache.Property(row => row.ModifiedTicks).HasColumnName("modified_ticks");
        cache.HasOne<VideoRow>().WithOne().HasForeignKey<VideoCacheRow>(row => row.VideoId).OnDelete(DeleteBehavior.Cascade);
    }
}

// Keep text IDs and timestamps identical to the existing SQL storage; no Guid/DateTime conversion conventions.
internal sealed class LibraryInfoRow
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

internal sealed class VideoRow
{
    public string Id { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public long DurationTicks { get; set; }
    public string ImportedAt { get; set; } = "";
    public string RecordedAt { get; set; } = "";
    public string? SourceFingerprint { get; set; }
}

internal sealed class AlbumRow
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string NameKey { get; set; } = "";
}

internal sealed class AlbumVideoRow
{
    public string AlbumId { get; set; } = "";
    public string VideoId { get; set; } = "";
}

internal sealed class VideoCacheRow
{
    public string VideoId { get; set; } = "";
    public string RecordJson { get; set; } = "";
    public long FileLength { get; set; }
    public long ModifiedTicks { get; set; }
}

/// <summary>Requires an explicit absolute database path; tooling never defaults to a user's library.</summary>
public sealed class LibraryDbContextFactory : IDesignTimeDbContextFactory<LibraryDbContext>
{
    public LibraryDbContext CreateDbContext(string[] args)
    {
        if (args.Length != 2 || args[0] != "--database-path" || string.IsNullOrWhiteSpace(args[1]) ||
            !Path.IsPathFullyQualified(args[1]))
            throw new ArgumentException("Supply --database-path followed by an absolute scratch database path.");
        return new LibraryDbContext(LibraryDbContext.Options(Path.GetFullPath(args[1])));
    }
}

internal sealed class DeletionHistoryRow
{
    public string Fingerprint { get; set; } = "";
    public string Title { get; set; } = "";
    public string RemovedAt { get; set; } = "";
    public int Kind { get; set; }
}
