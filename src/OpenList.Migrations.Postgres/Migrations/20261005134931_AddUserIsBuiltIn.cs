using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenList.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddUserIsBuiltIn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsBuiltIn",
                table: "x_users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Backfill: the column defaults to false, so an upgraded database would lose
            // the built-in admin's delete protection. Mark the earliest admin row — that
            // is the same account the old SeedAdminAsync reconciled on every startup.
            // NOT EXISTS keeps an already-marked database untouched and never sweeps in
            // later hand-created admins.
            migrationBuilder.Sql(
                """
                UPDATE x_users
                SET "IsBuiltIn" = TRUE
                WHERE "Role" = 2
                  AND "Id" = (SELECT MIN("Id") FROM x_users WHERE "Role" = 2)
                  AND NOT EXISTS (SELECT 1 FROM x_users WHERE "IsBuiltIn" = TRUE);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsBuiltIn",
                table: "x_users");
        }
    }
}
