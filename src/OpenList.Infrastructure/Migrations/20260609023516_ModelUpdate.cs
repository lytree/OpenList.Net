using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenList.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ModelUpdate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "AddedTime",
                table: "SshKeys",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<DateTime>(
                name: "LastUsedTime",
                table: "SshKeys",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.CreateTable(
                name: "Messages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<uint>(type: "INTEGER", nullable: false),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Content = table.Column<string>(type: "TEXT", nullable: false),
                    Read = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Messages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SearchIndices",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Parent = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    IsDir = table.Column<bool>(type: "INTEGER", nullable: false),
                    Size = table.Column<long>(type: "INTEGER", nullable: false),
                    Modified = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SearchIndices", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TaskRecords",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    Progress = table.Column<double>(type: "REAL", nullable: false),
                    Error = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    PersistData = table.Column<string>(type: "text", nullable: true),
                    CreatorId = table.Column<uint>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskRecords", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TokenBlacklist",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Jti = table.Column<string>(type: "TEXT", nullable: false),
                    UserId = table.Column<uint>(type: "INTEGER", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TokenBlacklist", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WebAuthnCredentials",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<uint>(type: "INTEGER", nullable: false),
                    CredentialId = table.Column<string>(type: "TEXT", nullable: false),
                    PublicKey = table.Column<string>(type: "text", nullable: false),
                    UserHandle = table.Column<string>(type: "TEXT", nullable: false),
                    SignCount = table.Column<int>(type: "INTEGER", nullable: false),
                    Transports = table.Column<string>(type: "text", nullable: false),
                    AaGuid = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    RegisteredAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastUsedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebAuthnCredentials", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Messages_Read",
                table: "Messages",
                column: "Read");

            migrationBuilder.CreateIndex(
                name: "IX_Messages_UserId",
                table: "Messages",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_SearchIndices_Name",
                table: "SearchIndices",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_SearchIndices_Parent",
                table: "SearchIndices",
                column: "Parent");

            migrationBuilder.CreateIndex(
                name: "IX_TaskRecords_CreatedAt",
                table: "TaskRecords",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_TaskRecords_State",
                table: "TaskRecords",
                column: "State");

            migrationBuilder.CreateIndex(
                name: "IX_TaskRecords_Type",
                table: "TaskRecords",
                column: "Type");

            migrationBuilder.CreateIndex(
                name: "IX_TokenBlacklist_ExpiresAt",
                table: "TokenBlacklist",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_TokenBlacklist_Jti",
                table: "TokenBlacklist",
                column: "Jti");

            migrationBuilder.CreateIndex(
                name: "IX_WebAuthnCredentials_CredentialId",
                table: "WebAuthnCredentials",
                column: "CredentialId");

            migrationBuilder.CreateIndex(
                name: "IX_WebAuthnCredentials_UserId",
                table: "WebAuthnCredentials",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Messages");

            migrationBuilder.DropTable(
                name: "SearchIndices");

            migrationBuilder.DropTable(
                name: "TaskRecords");

            migrationBuilder.DropTable(
                name: "TokenBlacklist");

            migrationBuilder.DropTable(
                name: "WebAuthnCredentials");

            migrationBuilder.DropColumn(
                name: "AddedTime",
                table: "SshKeys");

            migrationBuilder.DropColumn(
                name: "LastUsedTime",
                table: "SshKeys");
        }
    }
}
