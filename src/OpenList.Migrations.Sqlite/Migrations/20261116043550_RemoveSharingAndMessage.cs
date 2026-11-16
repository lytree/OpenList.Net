using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenList.Migrations.Sqlite.Migrations
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
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Content = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Read = table.Column<bool>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    UserId = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_x_messages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "x_sharings",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Accessed = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Disabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Expiration = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ExtractFolder = table.Column<string>(type: "TEXT", nullable: true),
                    FilesJson = table.Column<string>(type: "TEXT", nullable: false),
                    Header = table.Column<string>(type: "TEXT", nullable: true),
                    MaxAccessed = table.Column<int>(type: "INTEGER", nullable: false),
                    OrderBy = table.Column<string>(type: "TEXT", nullable: true),
                    OrderDirection = table.Column<string>(type: "TEXT", nullable: true),
                    OwnerId = table.Column<long>(type: "INTEGER", nullable: false),
                    Password = table.Column<string>(type: "TEXT", nullable: true),
                    Path = table.Column<string>(type: "TEXT", nullable: false),
                    Readme = table.Column<string>(type: "TEXT", nullable: true),
                    Remark = table.Column<string>(type: "TEXT", nullable: true),
                    ShareCode = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
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
