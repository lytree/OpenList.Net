using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenList.Migrations.Postgres.Migrations
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

            migrationBuilder.DropColumn(
                name: "ReadUsersJson",
                table: "x_metas");

            migrationBuilder.DropColumn(
                name: "ReadUsersSub",
                table: "x_metas");

            migrationBuilder.DropColumn(
                name: "WSub",
                table: "x_metas");

            migrationBuilder.DropColumn(
                name: "Write",
                table: "x_metas");

            migrationBuilder.DropColumn(
                name: "WriteUsersJson",
                table: "x_metas");

            migrationBuilder.DropColumn(
                name: "WriteUsersSub",
                table: "x_metas");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BasePath",
                table: "x_users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsBuiltIn",
                table: "x_users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "Permission",
                table: "x_users",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Role",
                table: "x_users",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "CreatorRole",
                table: "x_tasks",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ReadUsersJson",
                table: "x_metas",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "ReadUsersSub",
                table: "x_metas",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "WSub",
                table: "x_metas",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "Write",
                table: "x_metas",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "WriteUsersJson",
                table: "x_metas",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "WriteUsersSub",
                table: "x_metas",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }
    }
}
