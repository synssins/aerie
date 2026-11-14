using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kb.Data.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class UserCodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CodesExempt",
                table: "AspNetUsers",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "CodesRequired",
                table: "AspNetUsers",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CodesExempt",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "CodesRequired",
                table: "AspNetUsers");
        }
    }
}
