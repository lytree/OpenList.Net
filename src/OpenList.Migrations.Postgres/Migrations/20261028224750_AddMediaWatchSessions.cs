using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace OpenList.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddMediaWatchSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 生成器顺带带上了 4 个 x_metas.Webdav* 补列（模型与上一版快照之间的历史漂移）。
            // 它们必须**手工去掉**：旧库早已由 DbInitializer.MigrateSchemaAsync 补过这些列，
            // 再执行一次 ADD COLUMN 会报 "column already exists" 并让整个迁移失败，
            // 而 DbInitializer 会吞掉异常退回 EnsureCreated —— 新库照常，旧库则静默缺表。
            // 全新库上这几列仍由 MigrateSchemaAsync 在建完表之后补，两边的结果一致。
            migrationBuilder.CreateTable(
                name: "x_media_watch_sessions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    ItemId = table.Column<long>(type: "bigint", nullable: false),
                    FileId = table.Column<long>(type: "bigint", nullable: true),
                    Token = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EndedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    StartPositionSeconds = table.Column<long>(type: "bigint", nullable: false),
                    LastPositionSeconds = table.Column<long>(type: "bigint", nullable: false),
                    MaxPositionSeconds = table.Column<long>(type: "bigint", nullable: false),
                    WatchedSeconds = table.Column<long>(type: "bigint", nullable: false),
                    Completed = table.Column<bool>(type: "boolean", nullable: false)
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
