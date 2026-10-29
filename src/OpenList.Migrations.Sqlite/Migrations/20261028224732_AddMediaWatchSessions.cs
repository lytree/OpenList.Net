using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenList.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddMediaWatchSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 生成器顺带带上了 4 个 x_metas.Webdav* 补列（模型与上一版快照之间的历史漂移）。
            // 它们必须**手工去掉**：旧库早已由 DbInitializer.MigrateSchemaAsync 补过这些列，
            // 再 ADD COLUMN 一次会报 "duplicate column name" 让整个迁移失败，而
            // DbInitializer 会吞掉异常退回 EnsureCreated —— 新库照常，旧库静默缺表。
            // 全新库上这几列仍由 MigrateSchemaAsync 在建完表之后补，两边结果一致。
            migrationBuilder.CreateTable(
                name: "x_media_watch_sessions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<long>(type: "INTEGER", nullable: false),
                    ItemId = table.Column<long>(type: "INTEGER", nullable: false),
                    FileId = table.Column<long>(type: "INTEGER", nullable: true),
                    Token = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    EndedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    StartPositionSeconds = table.Column<long>(type: "INTEGER", nullable: false),
                    LastPositionSeconds = table.Column<long>(type: "INTEGER", nullable: false),
                    MaxPositionSeconds = table.Column<long>(type: "INTEGER", nullable: false),
                    WatchedSeconds = table.Column<long>(type: "INTEGER", nullable: false),
                    Completed = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_x_media_watch_sessions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_x_media_watch_sessions_ItemId",
                table: "x_media_watch_sessions",
                column: "ItemId");

            migrationBuilder.CreateIndex(
                name: "IX_x_media_watch_sessions_UserId_StartedAt",
                table: "x_media_watch_sessions",
                columns: new[] { "UserId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_x_media_watch_sessions_UserId_Token",
                table: "x_media_watch_sessions",
                columns: new[] { "UserId", "Token" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "x_media_watch_sessions");
        }
    }
}
