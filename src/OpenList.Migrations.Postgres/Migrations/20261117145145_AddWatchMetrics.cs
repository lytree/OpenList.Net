using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenList.Migrations.Postgres.Migrations
{
    /// <summary>
    /// 观看历史的度量口径对齐 Peach（`asset` 上的 play_count / play_seconds /
    /// max_reached / leave_ratio / seek_count 五列）。
    ///
    /// <para>
    /// Peach 把这些直接写在单用户的作品行上；本库的媒体状态按用户隔离，所以拆成两层：
    /// 会话表记这一段的事实（片长、拖动次数、这段的最远比例、收尾时的离开比例），
    /// 状态表记这一部对这个用户累计成什么样（播放次数、累计观看秒数、最远/离开比例、累计拖动）。
    /// 与 Sqlite 的 <c>AddWatchMetrics</c> 同一次改动、同一份口径。
    /// </para>
    /// </summary>
    public partial class AddWatchMetrics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "DurationSeconds",
                table: "x_user_media_states",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "LeaveRatio",
                table: "x_user_media_states",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "MaxReached",
                table: "x_user_media_states",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<int>(
                name: "PlayCount",
                table: "x_user_media_states",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "PlaySeconds",
                table: "x_user_media_states",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "SeekCount",
                table: "x_user_media_states",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "DurationSeconds",
                table: "x_media_watch_sessions",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "LeaveRatio",
                table: "x_media_watch_sessions",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "MaxReached",
                table: "x_media_watch_sessions",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<int>(
                name: "SeekCount",
                table: "x_media_watch_sessions",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DurationSeconds",
                table: "x_user_media_states");

            migrationBuilder.DropColumn(
                name: "LeaveRatio",
                table: "x_user_media_states");

            migrationBuilder.DropColumn(
                name: "MaxReached",
                table: "x_user_media_states");

            migrationBuilder.DropColumn(
                name: "PlayCount",
                table: "x_user_media_states");

            migrationBuilder.DropColumn(
                name: "PlaySeconds",
                table: "x_user_media_states");

            migrationBuilder.DropColumn(
                name: "SeekCount",
                table: "x_user_media_states");

            migrationBuilder.DropColumn(
                name: "DurationSeconds",
                table: "x_media_watch_sessions");

            migrationBuilder.DropColumn(
                name: "LeaveRatio",
                table: "x_media_watch_sessions");

            migrationBuilder.DropColumn(
                name: "MaxReached",
                table: "x_media_watch_sessions");

            migrationBuilder.DropColumn(
                name: "SeekCount",
                table: "x_media_watch_sessions");
        }
    }
}
