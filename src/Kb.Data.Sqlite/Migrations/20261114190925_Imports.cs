using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kb.Data.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class Imports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ImportBatches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    TargetSpaceId = table.Column<Guid>(type: "TEXT", nullable: true),
                    TargetParentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CompletedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Grouped = table.Column<bool>(type: "INTEGER", nullable: false),
                    Error = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImportBatches", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ImportFiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    BatchId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ItemId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ParentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Path = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    Size = table.Column<long>(type: "INTEGER", nullable: false),
                    Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Mime = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Note = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Text = table.Column<string>(type: "TEXT", nullable: true),
                    Embedded = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImportFiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ImportItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    BatchId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    ProposalJson = table.Column<string>(type: "TEXT", nullable: true),
                    Error = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    PageId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImportItems", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ImportBatches_UserId_Grouped",
                table: "ImportBatches",
                columns: new[] { "UserId", "Grouped" });

            migrationBuilder.CreateIndex(
                name: "IX_ImportFiles_BatchId",
                table: "ImportFiles",
                column: "BatchId");

            migrationBuilder.CreateIndex(
                name: "IX_ImportFiles_ItemId",
                table: "ImportFiles",
                column: "ItemId");

            migrationBuilder.CreateIndex(
                name: "IX_ImportItems_Status",
                table: "ImportItems",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_ImportItems_UserId_Status",
                table: "ImportItems",
                columns: new[] { "UserId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ImportBatches");

            migrationBuilder.DropTable(
                name: "ImportFiles");

            migrationBuilder.DropTable(
                name: "ImportItems");
        }
    }
}
