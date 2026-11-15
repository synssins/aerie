using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kb.Data.Migrations
{
    /// <inheritdoc />
    public partial class DataCenterContent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DcContents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AerieId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DcContents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DcLabels",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Prefix = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DcLabels", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DcVersions",
                columns: table => new
                {
                    PageVersionId = table.Column<long>(type: "bigint", nullable: false),
                    PageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Number = table.Column<int>(type: "int", nullable: false),
                    Removed = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DcVersions", x => x.PageVersionId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DcContents_AerieId",
                table: "DcContents",
                column: "AerieId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DcLabels_Prefix_Name",
                table: "DcLabels",
                columns: new[] { "Prefix", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DcVersions_PageId_Number",
                table: "DcVersions",
                columns: new[] { "PageId", "Number" },
                unique: true);

            // Content and versions that exist now get their compatible ids and numbers here, in creation order (common.md, Identifiers).
            migrationBuilder.Sql("INSERT INTO DcContents (AerieId, Kind) SELECT Id, Kind FROM (SELECT Id, 'page' AS Kind, CreatedAt FROM Pages UNION ALL SELECT Id, 'attachment' AS Kind, CreatedAt FROM Attachments) x ORDER BY CreatedAt, Id;");
            migrationBuilder.Sql("INSERT INTO DcVersions (PageVersionId, PageId, Number, Removed) SELECT Id, PageId, ROW_NUMBER() OVER (PARTITION BY PageId ORDER BY Major, Minor, Id), 0 FROM PageVersions;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DcContents");

            migrationBuilder.DropTable(
                name: "DcLabels");

            migrationBuilder.DropTable(
                name: "DcVersions");
        }
    }
}
