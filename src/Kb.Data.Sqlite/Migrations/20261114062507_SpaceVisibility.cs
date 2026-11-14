using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kb.Data.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class SpaceVisibility : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "IsPublic",
                table: "Spaces",
                newName: "Visibility");

            // IsPublic 1 became Visibility 1 (SignedIn). It stays so only where the old server-wide switch was on; a sensitive space stays Private.
            migrationBuilder.Sql("UPDATE \"Spaces\" SET \"Visibility\" = 0 WHERE \"IsSensitive\" = 1 OR NOT EXISTS (SELECT 1 FROM \"RuntimeSettings\" WHERE \"Key\" = 'PublicSpaceEnabled' AND \"Value\" = 'true')");
            migrationBuilder.Sql("DELETE FROM \"RuntimeSettings\" WHERE \"Key\" = 'PublicSpaceEnabled'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "Visibility",
                table: "Spaces",
                newName: "IsPublic");
        }
    }
}
