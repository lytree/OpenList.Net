using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace OpenList.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class RemoveSharingAndMessage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "x_messages");

            migrationBuilder.DropTable(
                name: "x_sharings");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "x_messages",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Content = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Read = table.Column<bool>(type: "boolean", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    UserId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_x_messages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "x_sharings",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Accessed = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Disabled = table.Column<bool>(type: "boolean", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Expiration = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ExtractFolder = table.Column<string>(type: "text", nullable: true),
                    FilesJson = table.Column<string>(type: "text", nullable: false),
                    Header = table.Column<string>(type: "text", nullable: true),
                    MaxAccessed = table.Column<int>(type: "integer", nullable: false),
                    OrderBy = table.Column<string>(type: "text", nullable: true),
                    OrderDirection = table.Column<string>(type: "text", nullable: true),
                    OwnerId = table.Column<long>(type: "bigint", nullable: false),
                    Password = table.Column<string>(type: "text", nullable: true),
                    Path = table.Column<string>(type: "text", nullable: false),
                    Readme = table.Column<string>(type: "text", nullable: true),
                    Remark = table.Column<string>(type: "text", nullable: true),
                    ShareCode = table.Column<string>(type: "text", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_x_sharings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_x_messages_UserId",
                table: "x_messages",
                column: "UserId");
        }
    }
}
