using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Kb.Data.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class CustomRoles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "RoleId",
                table: "SpaceRoleAssignments",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateTable(
                name: "SpaceRoles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Permissions = table.Column<int>(type: "INTEGER", nullable: false),
                    BuiltIn = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpaceRoles", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "SpaceRoles",
                columns: new[] { "Id", "BuiltIn", "Name", "Permissions" },
                values: new object[,]
                {
                    { new Guid("00000000-0000-0000-0000-000000000001"), 1, "Reader", 1 },
                    { new Guid("00000000-0000-0000-0000-000000000002"), 2, "Contributor", 7 },
                    { new Guid("00000000-0000-0000-0000-000000000003"), 3, "Supervisor", 255 },
                    { new Guid("00000000-0000-0000-0000-000000000004"), 4, "Owner", 1023 }
                });

            migrationBuilder.CreateIndex(
                name: "IX_SpaceRoleAssignments_RoleId",
                table: "SpaceRoleAssignments",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_SpaceRoles_Name",
                table: "SpaceRoles",
                column: "Name",
                unique: true);

            // Every assignment moves to the built-in role of its level (1 Reader .. 4 Owner), whose ids are fixed.
            migrationBuilder.Sql("UPDATE \"SpaceRoleAssignments\" SET \"RoleId\" = '00000000-0000-0000-0000-00000000000' || \"Role\" WHERE \"Role\" BETWEEN 1 AND 4");

            migrationBuilder.DropColumn(
                name: "Role",
                table: "SpaceRoleAssignments");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SpaceRoles");

            migrationBuilder.DropIndex(
                name: "IX_SpaceRoleAssignments_RoleId",
                table: "SpaceRoleAssignments");

            migrationBuilder.DropColumn(
                name: "RoleId",
                table: "SpaceRoleAssignments");

            migrationBuilder.AddColumn<int>(
                name: "Role",
                table: "SpaceRoleAssignments",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }
    }
}
