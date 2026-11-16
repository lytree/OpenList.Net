using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenList.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class RemoveMultiUserColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BasePath",
                table: "x_users");

            migrationBuilder.DropColumn(
                name: "IsBuiltIn",
                table: "x_users");

            migrationBuilder.DropColumn(
                name: "Permission",
                table: "x_users");

            migrationBuilder.DropColumn(
                name: "Role",
                table: "x_users");

            migrationBuilder.DropColumn(
                name: "CreatorRole",
                table: "x_tasks");

            // 必须用原生 SQL 而不是 migrationBuilder.DropColumn：SQLite 的 DropColumn 走「整表重建」，
            // 重建语句按**模型快照**列全部列，而 x_metas 的 WebdavRead/Write/Management 三列
            // 从来没有任何迁移创建过（早期是 MigrateSchemaAsync 运行期 ALTER TABLE 补的），
            // 于是在全新库上重建会报 `no such column: WebdavManagement`，整个迁移半途失败。
            // SQLite ≥ 3.35 支持直接 DROP COLUMN，不需要重建。
            migrationBuilder.Sql("ALTER TABLE \"x_metas\" DROP COLUMN \"ReadUsersJson\";");
            migrationBuilder.Sql("ALTER TABLE \"x_metas\" DROP COLUMN \"ReadUsersSub\";");
            migrationBuilder.Sql("ALTER TABLE \"x_metas\" DROP COLUMN \"WSub\";");
            migrationBuilder.Sql("ALTER TABLE \"x_metas\" DROP COLUMN \"Write\";");
            migrationBuilder.Sql("ALTER TABLE \"x_metas\" DROP COLUMN \"WriteUsersJson\";");
            migrationBuilder.Sql("ALTER TABLE \"x_metas\" DROP COLUMN \"WriteUsersSub\";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BasePath",
                table: "x_users",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsBuiltIn",
                table: "x_users",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "Permission",
                table: "x_users",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Role",
                table: "x_users",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "CreatorRole",
                table: "x_tasks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ReadUsersJson",
                table: "x_metas",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "ReadUsersSub",
                table: "x_metas",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "WSub",
                table: "x_metas",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "Write",
                table: "x_metas",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "WriteUsersJson",
                table: "x_metas",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "WriteUsersSub",
                table: "x_metas",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }
    }
}
