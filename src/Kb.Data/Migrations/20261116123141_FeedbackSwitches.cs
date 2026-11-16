using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kb.Data.Migrations
{
    /// <inheritdoc />
    public partial class FeedbackSwitches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "RatingsEnabled",
                table: "Spaces",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "CommentsEnabled",
                table: "Pages",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "RatingsEnabled",
                table: "Pages",
                type: "bit",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RatingsEnabled",
                table: "Spaces");

            migrationBuilder.DropColumn(
                name: "CommentsEnabled",
                table: "Pages");

            migrationBuilder.DropColumn(
                name: "RatingsEnabled",
                table: "Pages");
        }
    }
}
