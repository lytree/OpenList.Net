using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenList.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddMediaFollowsWantsAndDownloads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TrashDirectory",
                table: "x_media_libraries",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "x_media_downloads",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WantId = table.Column<long>(type: "INTEGER", nullable: false),
                    ItemId = table.Column<long>(type: "INTEGER", nullable: true),
                    LibraryId = table.Column<long>(type: "INTEGER", nullable: false),
                    Tool = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    TaskId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Url = table.Column<string>(type: "TEXT", nullable: false),
                    TargetDirectory = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Progress = table.Column<double>(type: "REAL", nullable: false),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    IngestedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_x_media_downloads", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "x_media_follow_entries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    FollowId = table.Column<long>(type: "INTEGER", nullable: false),
                    PersonId = table.Column<long>(type: "INTEGER", nullable: false),
                    MediaType = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    ExternalId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    ReleaseDate = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    PosterPath = table.Column<string>(type: "TEXT", nullable: true),
                    Role = table.Column<string>(type: "TEXT", nullable: true),
                    FirstSeenAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_x_media_follow_entries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "x_media_follows",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PersonId = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastCheckedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_x_media_follows", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "x_media_wants",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ExternalId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    MediaType = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Year = table.Column<int>(type: "INTEGER", nullable: true),
                    PosterPath = table.Column<string>(type: "TEXT", nullable: true),
                    Overview = table.Column<string>(type: "TEXT", nullable: true),
                    ReleaseDate = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ItemId = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_x_media_wants", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_x_media_downloads_ItemId",
                table: "x_media_downloads",
                column: "ItemId");

            migrationBuilder.CreateIndex(
                name: "IX_x_media_downloads_Status",
                table: "x_media_downloads",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_x_media_downloads_WantId",
                table: "x_media_downloads",
                column: "WantId");

            migrationBuilder.CreateIndex(
                name: "IX_x_media_follow_entries_FirstSeenAt",
                table: "x_media_follow_entries",
                column: "FirstSeenAt");

            migrationBuilder.CreateIndex(
                name: "IX_x_media_follow_entries_FollowId",
                table: "x_media_follow_entries",
                column: "FollowId");

            migrationBuilder.CreateIndex(
                name: "IX_x_media_follow_entries_PersonId_ExternalId",
                table: "x_media_follow_entries",
                columns: new[] { "PersonId", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_x_media_follows_PersonId",
                table: "x_media_follows",
                column: "PersonId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_x_media_wants_ItemId",
                table: "x_media_wants",
                column: "ItemId");

            migrationBuilder.CreateIndex(
                name: "IX_x_media_wants_MediaType_ExternalId",
                table: "x_media_wants",
                columns: new[] { "MediaType", "ExternalId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "x_media_downloads");

            migrationBuilder.DropTable(
                name: "x_media_follow_entries");

            migrationBuilder.DropTable(
                name: "x_media_follows");

            migrationBuilder.DropTable(
                name: "x_media_wants");

            migrationBuilder.DropColumn(
                name: "TrashDirectory",
                table: "x_media_libraries");
        }
    }
}
