using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kb.Data.Migrations
{
    /// <inheritdoc />
    public partial class SpaceDelete : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeletedAt",
                table: "Spaces",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DeletedBy",
                table: "Spaces",
                type: "uniqueidentifier",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "Spaces");

            migrationBuilder.DropColumn(
                name: "DeletedBy",
                table: "Spaces");
        }
    }
}
