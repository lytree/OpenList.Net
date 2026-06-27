using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenList.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class Init : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "x_authn_credentials",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<long>(type: "INTEGER", nullable: false),
                    CredentialId = table.Column<string>(type: "TEXT", nullable: false),
                    UserHandle = table.Column<string>(type: "TEXT", nullable: false),
                    PublicKey = table.Column<string>(type: "TEXT", nullable: false),
                    SignCount = table.Column<uint>(type: "INTEGER", nullable: false),
                    Aaguid = table.Column<string>(type: "TEXT", nullable: false),
                    Transports = table.Column<string>(type: "TEXT", nullable: false),
                    Discoverable = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_x_authn_credentials", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "x_messages",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<long>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Content = table.Column<string>(type: "TEXT", nullable: false),
                    Read = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_x_messages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "x_metas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Path = table.Column<string>(type: "TEXT", nullable: false),
                    Password = table.Column<string>(type: "TEXT", nullable: true),
                    PSub = table.Column<bool>(type: "INTEGER", nullable: false),
                    ReadUsersSub = table.Column<bool>(type: "INTEGER", nullable: false),
                    WriteUsersSub = table.Column<bool>(type: "INTEGER", nullable: false),
                    Write = table.Column<bool>(type: "INTEGER", nullable: false),
                    WSub = table.Column<bool>(type: "INTEGER", nullable: false),
                    Hide = table.Column<string>(type: "TEXT", nullable: true),
                    HideSub = table.Column<bool>(type: "INTEGER", nullable: false),
                    Readme = table.Column<string>(type: "TEXT", nullable: true),
                    RSub = table.Column<bool>(type: "INTEGER", nullable: false),
                    Header = table.Column<string>(type: "TEXT", nullable: true),
                    HeaderSub = table.Column<bool>(type: "INTEGER", nullable: false),
                    ReadUsersJson = table.Column<string>(type: "TEXT", nullable: false),
                    WriteUsersJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_x_metas", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "x_search_index",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Parent = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    IsDir = table.Column<bool>(type: "INTEGER", nullable: false),
                    Size = table.Column<long>(type: "INTEGER", nullable: false),
                    Path = table.Column<string>(type: "TEXT", nullable: false),
                    Type = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_x_search_index", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "x_settings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: false),
                    Help = table.Column<string>(type: "TEXT", nullable: true),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    Flag = table.Column<int>(type: "INTEGER", nullable: false),
                    Options = table.Column<string>(type: "TEXT", nullable: true),
                    Group = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_x_settings", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "x_sharings",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ShareCode = table.Column<string>(type: "TEXT", nullable: false),
                    Path = table.Column<string>(type: "TEXT", nullable: false),
                    FilesJson = table.Column<string>(type: "TEXT", nullable: false),
                    Password = table.Column<string>(type: "TEXT", nullable: true),
                    Expiration = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    MaxAccessed = table.Column<int>(type: "INTEGER", nullable: false),
                    Accessed = table.Column<int>(type: "INTEGER", nullable: false),
                    Disabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Remark = table.Column<string>(type: "TEXT", nullable: true),
                    Readme = table.Column<string>(type: "TEXT", nullable: true),
                    Header = table.Column<string>(type: "TEXT", nullable: true),
                    OrderBy = table.Column<string>(type: "TEXT", nullable: true),
                    OrderDirection = table.Column<string>(type: "TEXT", nullable: true),
                    ExtractFolder = table.Column<string>(type: "TEXT", nullable: true),
                    OwnerId = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_x_sharings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "x_sshkeys",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<long>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    PublicKey = table.Column<string>(type: "TEXT", nullable: false),
                    Fingerprint = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastUsedTime = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_x_sshkeys", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "x_storages",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MountPath = table.Column<string>(type: "TEXT", nullable: false),
                    Driver = table.Column<string>(type: "TEXT", nullable: false),
                    Addition = table.Column<string>(type: "TEXT", nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false),
                    Disabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Remark = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    OrderBy = table.Column<string>(type: "TEXT", nullable: true),
                    OrderDirection = table.Column<string>(type: "TEXT", nullable: true),
                    ExtractFolder = table.Column<string>(type: "TEXT", nullable: true),
                    WebProxy = table.Column<bool>(type: "INTEGER", nullable: false),
                    WebdavPolicy = table.Column<string>(type: "TEXT", nullable: true),
                    EnableSign = table.Column<bool>(type: "INTEGER", nullable: false),
                    Modified = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_x_storages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "x_users",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Username = table.Column<string>(type: "TEXT", nullable: false),
                    Password = table.Column<string>(type: "TEXT", nullable: false),
                    PasswordHash = table.Column<string>(type: "TEXT", nullable: false),
                    Role = table.Column<int>(type: "INTEGER", nullable: false),
                    Disabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Permission = table.Column<int>(type: "INTEGER", nullable: false),
                    BasePath = table.Column<string>(type: "TEXT", nullable: true),
                    TwoFactorEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    TwoFactorSecret = table.Column<string>(type: "TEXT", nullable: true),
                    SsoID = table.Column<string>(type: "TEXT", nullable: true),
                    AllowLdap = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_x_users", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_x_authn_credentials_UserId",
                table: "x_authn_credentials",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_x_authn_credentials_UserId_CredentialId",
                table: "x_authn_credentials",
                columns: new[] { "UserId", "CredentialId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_x_messages_UserId",
                table: "x_messages",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_x_metas_Path",
                table: "x_metas",
                column: "Path",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_x_search_index_Name",
                table: "x_search_index",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_x_search_index_Parent",
                table: "x_search_index",
                column: "Parent");

            migrationBuilder.CreateIndex(
                name: "IX_x_search_index_Path",
                table: "x_search_index",
                column: "Path",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_x_sshkeys_UserId",
                table: "x_sshkeys",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_x_users_Username",
                table: "x_users",
                column: "Username",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "x_authn_credentials");

            migrationBuilder.DropTable(
                name: "x_messages");

            migrationBuilder.DropTable(
                name: "x_metas");

            migrationBuilder.DropTable(
                name: "x_search_index");

            migrationBuilder.DropTable(
                name: "x_settings");

            migrationBuilder.DropTable(
                name: "x_sharings");

            migrationBuilder.DropTable(
                name: "x_sshkeys");

            migrationBuilder.DropTable(
                name: "x_storages");

            migrationBuilder.DropTable(
                name: "x_users");
        }
    }
}
