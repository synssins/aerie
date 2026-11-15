using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kb.Data.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class DataCenterSpaces : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DcLongTasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Subject = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    FinishedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Successful = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DcLongTasks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DcSpaces",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SpaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "TEXT", nullable: true),
                    PersonalOwnerId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ArchivedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DcSpaces", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DcLongTasks_UserId_StartedAt",
                table: "DcLongTasks",
                columns: new[] { "UserId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_DcSpaces_PersonalOwnerId",
                table: "DcSpaces",
                column: "PersonalOwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_DcSpaces_SpaceId",
                table: "DcSpaces",
                column: "SpaceId",
                unique: true);

            // Spaces that exist now get their compatible id here, in creation order (contracts/behaviour/common.md, Identifiers).
            migrationBuilder.Sql("INSERT INTO DcSpaces (SpaceId) SELECT Id FROM Spaces ORDER BY CreatedAt, Id;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DcLongTasks");

            migrationBuilder.DropTable(
                name: "DcSpaces");
        }
    }
}
