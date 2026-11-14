using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kb.Data.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class SpaceReviewMonths : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ReviewMonths",
                table: "Spaces",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReviewMonths",
                table: "Spaces");
        }
    }
}
