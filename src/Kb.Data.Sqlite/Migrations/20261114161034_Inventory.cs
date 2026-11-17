using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kb.Data.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class Inventory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OwnerId",
                table: "Pages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OwnerType",
                table: "Pages",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReviewMonths",
                table: "Pages",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "InvAccessRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Edit = table.Column<bool>(type: "INTEGER", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    TargetId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InvAccessRules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InvApplications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    NameKey = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Vendor = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    OwnerType = table.Column<int>(type: "INTEGER", nullable: true),
                    OwnerId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InvApplications", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InvHistory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ApplicationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Server = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ServerKey = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    From = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    To = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    At = table.Column<long>(type: "INTEGER", nullable: false),
                    By = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InvHistory", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InvInstalls",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ApplicationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Server = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ServerKey = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Version = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ChangedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ChangedBy = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InvInstalls", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PageAppLinks",
                columns: table => new
                {
                    PageId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ApplicationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ServersJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PageAppLinks", x => new { x.PageId, x.ApplicationId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_InvApplications_NameKey",
                table: "InvApplications",
                column: "NameKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InvHistory_ApplicationId_ServerKey",
                table: "InvHistory",
                columns: new[] { "ApplicationId", "ServerKey" });

            migrationBuilder.CreateIndex(
                name: "IX_InvInstalls_ApplicationId_ServerKey",
                table: "InvInstalls",
                columns: new[] { "ApplicationId", "ServerKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InvInstalls_ServerKey",
                table: "InvInstalls",
                column: "ServerKey");

            migrationBuilder.CreateIndex(
                name: "IX_PageAppLinks_ApplicationId",
                table: "PageAppLinks",
                column: "ApplicationId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InvAccessRules");

            migrationBuilder.DropTable(
                name: "InvApplications");

            migrationBuilder.DropTable(
                name: "InvHistory");

            migrationBuilder.DropTable(
                name: "InvInstalls");

            migrationBuilder.DropTable(
                name: "PageAppLinks");

            migrationBuilder.DropColumn(
                name: "OwnerId",
                table: "Pages");

            migrationBuilder.DropColumn(
                name: "OwnerType",
                table: "Pages");

            migrationBuilder.DropColumn(
                name: "ReviewMonths",
                table: "Pages");
        }
    }
}
