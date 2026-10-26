using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace OpenList.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddMediaCollections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FlagNote",
                table: "x_user_media_states",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Flagged",
                table: "x_user_media_states",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "WatchLater",
                table: "x_user_media_states",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "x_media_playlist_items",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PlaylistId = table.Column<long>(type: "bigint", nullable: false),
                    ItemId = table.Column<long>(type: "bigint", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    AddedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_x_media_playlist_items", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "x_media_playlists",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_x_media_playlists", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_x_media_playlist_items_PlaylistId",
                table: "x_media_playlist_items",
                column: "PlaylistId");

            migrationBuilder.CreateIndex(
                name: "IX_x_media_playlist_items_PlaylistId_ItemId",
                table: "x_media_playlist_items",
                columns: new[] { "PlaylistId", "ItemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_x_media_playlist_items_PlaylistId_Position",
                table: "x_media_playlist_items",
                columns: new[] { "PlaylistId", "Position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_x_media_playlists_UserId",
                table: "x_media_playlists",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "x_media_playlist_items");

            migrationBuilder.DropTable(
                name: "x_media_playlists");

            migrationBuilder.DropColumn(
                name: "FlagNote",
                table: "x_user_media_states");

            migrationBuilder.DropColumn(
                name: "Flagged",
                table: "x_user_media_states");

            migrationBuilder.DropColumn(
                name: "WatchLater",
                table: "x_user_media_states");
        }
    }
}
