using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kb.Data.Sqlite.Migrations
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
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "CommentsEnabled",
                table: "Pages",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "RatingsEnabled",
                table: "Pages",
                type: "INTEGER",
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
