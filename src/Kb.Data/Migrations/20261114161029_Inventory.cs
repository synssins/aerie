using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kb.Data.Migrations
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
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OwnerType",
                table: "Pages",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReviewMonths",
                table: "Pages",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "InvAccessRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Edit = table.Column<bool>(type: "bit", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    TargetId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InvAccessRules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InvApplications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    NameKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Vendor = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    OwnerType = table.Column<int>(type: "int", nullable: true),
                    OwnerId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InvApplications", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InvHistory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ApplicationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Server = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ServerKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    From = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    To = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    At = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    By = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InvHistory", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InvInstalls",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ApplicationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Server = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ServerKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Version = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ChangedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ChangedBy = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InvInstalls", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PageAppLinks",
                columns: table => new
                {
                    PageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ApplicationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ServersJson = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PageAppLinks", x => new { x.PageId, x.ApplicationId });
                });

            migrationBuilder.InsertData(
                table: "InvAccessRules",
                columns: new[] { "Id", "Edit", "Kind", "TargetId" },
                values: new object[] { new Guid("00000000-0000-0000-0000-0000000000a1"), false, 0, null });

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
