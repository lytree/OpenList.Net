using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenList.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddMediaSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "x_media_files",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ItemId = table.Column<long>(type: "INTEGER", nullable: false),
                    Path = table.Column<string>(type: "TEXT", nullable: false),
                    Size = table.Column<long>(type: "INTEGER", nullable: false),
                    DurationSeconds = table.Column<int>(type: "INTEGER", nullable: true),
                    Resolution = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_x_media_files", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "x_media_item_persons",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ItemId = table.Column<long>(type: "INTEGER", nullable: false),
                    PersonId = table.Column<long>(type: "INTEGER", nullable: false),
                    Role = table.Column<string>(type: "TEXT", nullable: false),
                    Character = table.Column<string>(type: "TEXT", nullable: true),
                    Order = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_x_media_item_persons", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "x_media_items",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    LibraryId = table.Column<long>(type: "INTEGER", nullable: false),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    OriginalTitle = table.Column<string>(type: "TEXT", nullable: true),
                    Year = table.Column<int>(type: "INTEGER", nullable: true),
                    TmdbId = table.Column<string>(type: "TEXT", nullable: true),
                    Overview = table.Column<string>(type: "TEXT", nullable: true),
                    PosterPath = table.Column<string>(type: "TEXT", nullable: true),
                    BackdropPath = table.Column<string>(type: "TEXT", nullable: true),
                    Rating = table.Column<double>(type: "REAL", nullable: true),
                    GenresJson = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    DirectoryPath = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_x_media_items", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "x_media_libraries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Path = table.Column<string>(type: "TEXT", nullable: false),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    AutoScan = table.Column<bool>(type: "INTEGER", nullable: false),
                    MetadataLanguage = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedBy = table.Column<long>(type: "INTEGER", nullable: false),
                    LastScanAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_x_media_libraries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "x_media_persons",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    TmdbId = table.Column<string>(type: "TEXT", nullable: true),
                    ProfilePath = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_x_media_persons", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "x_media_scan_logs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    LibraryId = table.Column<long>(type: "INTEGER", nullable: false),
                    TaskId = table.Column<string>(type: "TEXT", nullable: true),
                    Added = table.Column<int>(type: "INTEGER", nullable: false),
                    Updated = table.Column<int>(type: "INTEGER", nullable: false),
                    Removed = table.Column<int>(type: "INTEGER", nullable: false),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    StartedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    FinishedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_x_media_scan_logs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "x_user_media_states",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<long>(type: "INTEGER", nullable: false),
                    ItemId = table.Column<long>(type: "INTEGER", nullable: false),
                    Favorite = table.Column<bool>(type: "INTEGER", nullable: false),
                    Watched = table.Column<bool>(type: "INTEGER", nullable: false),
                    UserRating = table.Column<int>(type: "INTEGER", nullable: true),
                    ProgressSeconds = table.Column<long>(type: "INTEGER", nullable: false),
                    LastPlayedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_x_user_media_states", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_x_media_files_ItemId",
                table: "x_media_files",
                column: "ItemId");

            migrationBuilder.CreateIndex(
                name: "IX_x_media_files_Path",
                table: "x_media_files",
                column: "Path",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_x_media_item_persons_ItemId",
                table: "x_media_item_persons",
                column: "ItemId");

            migrationBuilder.CreateIndex(
                name: "IX_x_media_item_persons_ItemId_PersonId_Role",
                table: "x_media_item_persons",
                columns: new[] { "ItemId", "PersonId", "Role" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_x_media_item_persons_PersonId",
                table: "x_media_item_persons",
                column: "PersonId");

            migrationBuilder.CreateIndex(
                name: "IX_x_media_items_LibraryId",
                table: "x_media_items",
                column: "LibraryId");

            migrationBuilder.CreateIndex(
                name: "IX_x_media_items_LibraryId_DirectoryPath",
                table: "x_media_items",
                columns: new[] { "LibraryId", "DirectoryPath" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_x_media_items_TmdbId",
                table: "x_media_items",
                column: "TmdbId");

            migrationBuilder.CreateIndex(
                name: "IX_x_media_libraries_Path",
                table: "x_media_libraries",
                column: "Path",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_x_media_persons_Name",
                table: "x_media_persons",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_x_media_persons_Name_TmdbId",
                table: "x_media_persons",
                columns: new[] { "Name", "TmdbId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_x_media_scan_logs_LibraryId",
                table: "x_media_scan_logs",
                column: "LibraryId");

            migrationBuilder.CreateIndex(
                name: "IX_x_user_media_states_UserId_ItemId",
                table: "x_user_media_states",
                columns: new[] { "UserId", "ItemId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "x_media_files");

            migrationBuilder.DropTable(
                name: "x_media_item_persons");

            migrationBuilder.DropTable(
                name: "x_media_items");

            migrationBuilder.DropTable(
                name: "x_media_libraries");

            migrationBuilder.DropTable(
                name: "x_media_persons");

            migrationBuilder.DropTable(
                name: "x_media_scan_logs");

            migrationBuilder.DropTable(
                name: "x_user_media_states");
        }
    }
}
