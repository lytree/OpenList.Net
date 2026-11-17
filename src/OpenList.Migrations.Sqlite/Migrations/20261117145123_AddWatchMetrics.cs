using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenList.Migrations.Sqlite.Migrations
{
    /// <summary>
    /// 观看历史的度量口径对齐 Peach（`asset` 上的 play_count / play_seconds /
    /// max_reached / leave_ratio / seek_count 五列）。
    ///
    /// <para>
    /// Peach 把这些直接写在单用户的作品行上；本库的媒体状态按用户隔离，所以拆成两层：
    /// </para>
    /// <list type="bullet">
    /// <item>会话表（<c>x_media_watch_sessions</c>）记这一段的事实：片长、拖动次数、
    /// 这一段到过的最远比例、收尾时的离开比例。</item>
    /// <item>状态表（<c>x_user_media_states</c>）记这一部对这个用户累计成什么样：
    /// 播放次数、累计观看秒数、最远比例、最近离开比例、累计拖动。</item>
    /// </list>
    /// <para>
    /// 比例一律存 0–1 的 REAL，不存百分数：阈值（0.6 / 0.25）直接和它们比，
    /// 换算只发生在展示层。片长缺失时这些列留空或 0，不拿别的长度凑一个分母。
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
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "LeaveRatio",
                table: "x_user_media_states",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "MaxReached",
                table: "x_user_media_states",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<int>(
                name: "PlayCount",
                table: "x_user_media_states",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "PlaySeconds",
                table: "x_user_media_states",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "SeekCount",
                table: "x_user_media_states",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "DurationSeconds",
                table: "x_media_watch_sessions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "LeaveRatio",
                table: "x_media_watch_sessions",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "MaxReached",
                table: "x_media_watch_sessions",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<int>(
                name: "SeekCount",
                table: "x_media_watch_sessions",
                type: "INTEGER",
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
