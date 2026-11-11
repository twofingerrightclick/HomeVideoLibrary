using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HomeVideoLibrary.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "albums",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    name_key = table.Column<string>(type: "TEXT", nullable: false, collation: "BINARY")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_albums", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "videos",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    relative_path = table.Column<string>(type: "TEXT", nullable: false),
                    display_name = table.Column<string>(type: "TEXT", nullable: false),
                    duration_ticks = table.Column<long>(type: "INTEGER", nullable: false),
                    imported_at = table.Column<string>(type: "TEXT", nullable: false),
                    recorded_at = table.Column<string>(type: "TEXT", nullable: false),
                    source_fingerprint = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_videos", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "album_videos",
                columns: table => new
                {
                    album_id = table.Column<string>(type: "TEXT", nullable: false),
                    video_id = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_album_videos", x => new { x.album_id, x.video_id });
                    table.ForeignKey(
                        name: "FK_album_videos_albums_album_id",
                        column: x => x.album_id,
                        principalTable: "albums",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_album_videos_videos_video_id",
                        column: x => x.video_id,
                        principalTable: "videos",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "video_cache",
                columns: table => new
                {
                    video_id = table.Column<string>(type: "TEXT", nullable: false),
                    record_json = table.Column<string>(type: "TEXT", nullable: false),
                    file_length = table.Column<long>(type: "INTEGER", nullable: false),
                    modified_ticks = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_video_cache", x => x.video_id);
                    table.ForeignKey(
                        name: "FK_video_cache_videos_video_id",
                        column: x => x.video_id,
                        principalTable: "videos",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "album_videos_video",
                table: "album_videos",
                column: "video_id");

            migrationBuilder.CreateIndex(
                name: "albums_name_key",
                table: "albums",
                column: "name_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "videos_relative_path",
                table: "videos",
                column: "relative_path",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "videos_source_fingerprint_key",
                table: "videos",
                column: "source_fingerprint",
                unique: true,
                filter: "source_fingerprint IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "album_videos");

            migrationBuilder.DropTable(
                name: "video_cache");

            migrationBuilder.DropTable(
                name: "albums");

            migrationBuilder.DropTable(
                name: "videos");
        }
    }
}
