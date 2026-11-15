using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kb.Data.Migrations
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
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    FinishedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Successful = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DcLongTasks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DcSpaces",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SpaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PersonalOwnerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ArchivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
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
