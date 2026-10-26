using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenList.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddMediaCodeDomain : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Code",
                table: "x_media_items",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "SeriesId",
                table: "x_media_items",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "StudioId",
                table: "x_media_items",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "x_media_series",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_x_media_series", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "x_media_studios",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_x_media_studios", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_x_media_items_Code",
                table: "x_media_items",
                column: "Code");

            migrationBuilder.CreateIndex(
                name: "IX_x_media_items_SeriesId",
                table: "x_media_items",
                column: "SeriesId");

            migrationBuilder.CreateIndex(
                name: "IX_x_media_items_StudioId",
                table: "x_media_items",
                column: "StudioId");

            migrationBuilder.CreateIndex(
                name: "IX_x_media_series_Name",
                table: "x_media_series",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_x_media_studios_Name",
                table: "x_media_studios",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "x_media_series");

            migrationBuilder.DropTable(
                name: "x_media_studios");

            migrationBuilder.DropIndex(
                name: "IX_x_media_items_Code",
                table: "x_media_items");

            migrationBuilder.DropIndex(
                name: "IX_x_media_items_SeriesId",
                table: "x_media_items");

            migrationBuilder.DropIndex(
                name: "IX_x_media_items_StudioId",
                table: "x_media_items");

            migrationBuilder.DropColumn(
                name: "Code",
                table: "x_media_items");

            migrationBuilder.DropColumn(
                name: "SeriesId",
                table: "x_media_items");

            migrationBuilder.DropColumn(
                name: "StudioId",
                table: "x_media_items");
        }
    }
}
