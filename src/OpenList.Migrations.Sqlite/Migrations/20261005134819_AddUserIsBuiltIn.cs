using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenList.Migrations.Sqlite.Migrations
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
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // Backfill: the column defaults to 0, so an upgraded database would lose the
            // built-in admin's delete protection. Mark the earliest admin row — that is
            // the same account the old SeedAdminAsync reconciled on every startup.
            // Guarded by NOT EXISTS so an already-marked database is left untouched and
            // later hand-created admins are never swept in.
            // SQLite has no UPDATE...FROM, hence the NOT EXISTS subquery form.
            migrationBuilder.Sql(
                """
                UPDATE x_users
                SET IsBuiltIn = 1
                WHERE Role = 2
                  AND Id = (SELECT MIN(Id) FROM x_users WHERE Role = 2)
                  AND NOT EXISTS (SELECT 1 FROM x_users WHERE IsBuiltIn = 1);
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
