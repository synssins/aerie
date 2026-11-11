using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kb.Data.Migrations
{
    /// <inheritdoc />
    public partial class SpaceAndUserSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AgingAudience",
                table: "Spaces",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "AgingDays",
                table: "Spaces",
                type: "int",
                nullable: false,
                defaultValue: 180);

            migrationBuilder.AddColumn<bool>(
                name: "CommentsEnabled",
                table: "Spaces",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "RecycleDays",
                table: "Spaces",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SettingsJson",
                table: "AspNetUsers",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "{}");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AgingAudience",
                table: "Spaces");

            migrationBuilder.DropColumn(
                name: "AgingDays",
                table: "Spaces");

            migrationBuilder.DropColumn(
                name: "CommentsEnabled",
                table: "Spaces");

            migrationBuilder.DropColumn(
                name: "RecycleDays",
                table: "Spaces");

            migrationBuilder.DropColumn(
                name: "SettingsJson",
                table: "AspNetUsers");
        }
    }
}
