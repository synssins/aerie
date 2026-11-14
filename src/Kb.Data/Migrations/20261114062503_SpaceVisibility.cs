using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kb.Data.Migrations
{
    /// <inheritdoc />
    public partial class SpaceVisibility : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Visibility",
                table: "Spaces",
                type: "int",
                nullable: false,
                defaultValue: 0);

            // A public space stays readable by everyone signed in only where the old server-wide switch was on. A sensitive one stays Private.
            migrationBuilder.Sql("UPDATE [Spaces] SET [Visibility] = 1 WHERE [IsPublic] = 1 AND [IsSensitive] = 0 AND EXISTS (SELECT 1 FROM [RuntimeSettings] WHERE [Key] = N'PublicSpaceEnabled' AND [Value] = N'true')");
            migrationBuilder.Sql("DELETE FROM [RuntimeSettings] WHERE [Key] = N'PublicSpaceEnabled'");

            migrationBuilder.DropColumn(
                name: "IsPublic",
                table: "Spaces");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Visibility",
                table: "Spaces");

            migrationBuilder.AddColumn<bool>(
                name: "IsPublic",
                table: "Spaces",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }
    }
}
