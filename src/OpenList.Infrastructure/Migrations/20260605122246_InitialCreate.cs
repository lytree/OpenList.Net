using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenList.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Metas",
                columns: table => new
                {
                    Id = table.Column<uint>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Path = table.Column<string>(type: "TEXT", nullable: false),
                    ReadUsers = table.Column<string>(type: "TEXT", nullable: false),
                    ReadUsersSub = table.Column<bool>(type: "INTEGER", nullable: false),
                    WriteUsers = table.Column<string>(type: "TEXT", nullable: false),
                    WriteUsersSub = table.Column<bool>(type: "INTEGER", nullable: false),
                    Password = table.Column<string>(type: "TEXT", nullable: true),
                    PSub = table.Column<bool>(type: "INTEGER", nullable: false),
                    Write = table.Column<bool>(type: "INTEGER", nullable: false),
                    WSub = table.Column<bool>(type: "INTEGER", nullable: false),
                    Hide = table.Column<string>(type: "TEXT", nullable: true),
                    HSub = table.Column<bool>(type: "INTEGER", nullable: false),
                    Readme = table.Column<string>(type: "TEXT", nullable: true),
                    RSub = table.Column<bool>(type: "INTEGER", nullable: false),
                    Header = table.Column<string>(type: "TEXT", nullable: true),
                    HeaderSub = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Metas", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Settings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: true),
                    Help = table.Column<string>(type: "TEXT", nullable: true),
                    Type = table.Column<string>(type: "TEXT", nullable: true),
                    Options = table.Column<string>(type: "TEXT", nullable: true),
                    Group = table.Column<int>(type: "INTEGER", nullable: false),
                    Flag = table.Column<int>(type: "INTEGER", nullable: false),
                    Index = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Settings", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "Sharings",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    FilesRaw = table.Column<string>(type: "text", nullable: false),
                    Expires = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Pwd = table.Column<string>(type: "TEXT", nullable: true),
                    Accessed = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxAccessed = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatorId = table.Column<uint>(type: "INTEGER", nullable: false),
                    Disabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Remark = table.Column<string>(type: "TEXT", nullable: true),
                    Readme = table.Column<string>(type: "text", nullable: true),
                    Header = table.Column<string>(type: "text", nullable: true),
                    OrderBy = table.Column<string>(type: "TEXT", nullable: true),
                    OrderDirection = table.Column<string>(type: "TEXT", nullable: true),
                    ExtractFolder = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sharings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Storages",
                columns: table => new
                {
                    Id = table.Column<uint>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MountPath = table.Column<string>(type: "TEXT", nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false),
                    Driver = table.Column<string>(type: "TEXT", nullable: false),
                    CacheExpiration = table.Column<int>(type: "INTEGER", nullable: false),
                    CustomCachePolicies = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: false, defaultValue: ""),
                    Addition = table.Column<string>(type: "text", nullable: false),
                    Remark = table.Column<string>(type: "TEXT", nullable: true),
                    Modified = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Disabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    DisableIndex = table.Column<bool>(type: "INTEGER", nullable: false),
                    EnableSign = table.Column<bool>(type: "INTEGER", nullable: false),
                    OrderBy = table.Column<string>(type: "TEXT", nullable: true),
                    OrderDirection = table.Column<string>(type: "TEXT", nullable: true),
                    ExtractFolder = table.Column<string>(type: "TEXT", nullable: true),
                    WebProxy = table.Column<bool>(type: "INTEGER", nullable: false),
                    WebdavPolicy = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "native_proxy"),
                    ProxyRange = table.Column<bool>(type: "INTEGER", nullable: false),
                    DownProxyURL = table.Column<string>(type: "TEXT", nullable: true),
                    DisableProxySign = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Storages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<uint>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Username = table.Column<string>(type: "TEXT", nullable: false),
                    pwd_hash = table.Column<string>(type: "TEXT", nullable: false),
                    pwd_ts = table.Column<long>(type: "INTEGER", nullable: false),
                    Salt = table.Column<string>(type: "TEXT", nullable: false),
                    BasePath = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "/"),
                    Role = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    Disabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Permission = table.Column<int>(type: "INTEGER", nullable: false),
                    OtpSecret = table.Column<string>(type: "TEXT", nullable: true),
                    SsoID = table.Column<string>(type: "TEXT", nullable: true),
                    Authn = table.Column<string>(type: "TEXT", nullable: true),
                    AllowLdap = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SshKeys",
                columns: table => new
                {
                    Id = table.Column<uint>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    PublicKey = table.Column<string>(type: "TEXT", nullable: false),
                    Fingerprint = table.Column<string>(type: "TEXT", nullable: false),
                    UserId = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SshKeys", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SshKeys_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Metas_Path",
                table: "Metas",
                column: "Path",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SshKeys_UserId",
                table: "SshKeys",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Storages_MountPath",
                table: "Storages",
                column: "MountPath",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_Username",
                table: "Users",
                column: "Username",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Metas");

            migrationBuilder.DropTable(
                name: "Settings");

            migrationBuilder.DropTable(
                name: "Sharings");

            migrationBuilder.DropTable(
                name: "SshKeys");

            migrationBuilder.DropTable(
                name: "Storages");

            migrationBuilder.DropTable(
                name: "Users");
        }
    }
}
