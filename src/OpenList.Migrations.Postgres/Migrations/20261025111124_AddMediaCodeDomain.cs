using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace OpenList.Migrations.Postgres.Migrations
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
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "SeriesId",
                table: "x_media_items",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "StudioId",
                table: "x_media_items",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "x_media_series",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_x_media_series", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "x_media_studios",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
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
