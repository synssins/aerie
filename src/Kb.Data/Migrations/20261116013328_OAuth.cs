using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kb.Data.Migrations
{
    /// <inheritdoc />
    public partial class OAuth : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OAuthClients",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClientId = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    SecretHash = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Type = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    RedirectUrisJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Scopes = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    RequirePkce = table.Column<bool>(type: "bit", nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SecretRotatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OAuthClients", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OAuthAuthorizeRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClientId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RedirectUri = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    Scopes = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    State = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CodeChallenge = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Decision = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: true),
                    DecidedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OAuthAuthorizeRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OAuthAuthorizeRequests_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_OAuthAuthorizeRequests_OAuthClients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "OAuthClients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OAuthGrants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClientId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Scopes = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastUsedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OAuthGrants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OAuthGrants_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_OAuthGrants_OAuthClients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "OAuthClients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OAuthCodes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Hash = table.Column<byte[]>(type: "varbinary(32)", maxLength: 32, nullable: false),
                    GrantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RedirectUri = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    Scopes = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    CodeChallenge = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UsedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    FamilyId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OAuthCodes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OAuthCodes_OAuthGrants_GrantId",
                        column: x => x.GrantId,
                        principalTable: "OAuthGrants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OAuthTokenFamilies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GrantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Scopes = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastUsedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RevokeReason = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OAuthTokenFamilies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OAuthTokenFamilies_OAuthGrants_GrantId",
                        column: x => x.GrantId,
                        principalTable: "OAuthGrants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OAuthAccessTokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Hash = table.Column<byte[]>(type: "varbinary(32)", maxLength: 32, nullable: false),
                    Prefix = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    FamilyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Scopes = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OAuthAccessTokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OAuthAccessTokens_OAuthTokenFamilies_FamilyId",
                        column: x => x.FamilyId,
                        principalTable: "OAuthTokenFamilies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OAuthRefreshTokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Hash = table.Column<byte[]>(type: "varbinary(32)", maxLength: 32, nullable: false),
                    Prefix = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    FamilyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AccessTokenId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RotatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OAuthRefreshTokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OAuthRefreshTokens_OAuthTokenFamilies_FamilyId",
                        column: x => x.FamilyId,
                        principalTable: "OAuthTokenFamilies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OAuthAccessTokens_FamilyId",
                table: "OAuthAccessTokens",
                column: "FamilyId");

            migrationBuilder.CreateIndex(
                name: "IX_OAuthAccessTokens_Hash",
                table: "OAuthAccessTokens",
                column: "Hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OAuthAuthorizeRequests_ClientId",
                table: "OAuthAuthorizeRequests",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_OAuthAuthorizeRequests_ExpiresAt",
                table: "OAuthAuthorizeRequests",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_OAuthAuthorizeRequests_UserId",
                table: "OAuthAuthorizeRequests",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_OAuthClients_ClientId",
                table: "OAuthClients",
                column: "ClientId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OAuthCodes_GrantId",
                table: "OAuthCodes",
                column: "GrantId");

            migrationBuilder.CreateIndex(
                name: "IX_OAuthCodes_Hash",
                table: "OAuthCodes",
                column: "Hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OAuthGrants_ClientId",
                table: "OAuthGrants",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_OAuthGrants_UserId_ClientId",
                table: "OAuthGrants",
                columns: new[] { "UserId", "ClientId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OAuthRefreshTokens_FamilyId",
                table: "OAuthRefreshTokens",
                column: "FamilyId");

            migrationBuilder.CreateIndex(
                name: "IX_OAuthRefreshTokens_Hash",
                table: "OAuthRefreshTokens",
                column: "Hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OAuthTokenFamilies_GrantId",
                table: "OAuthTokenFamilies",
                column: "GrantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OAuthAccessTokens");

            migrationBuilder.DropTable(
                name: "OAuthAuthorizeRequests");

            migrationBuilder.DropTable(
                name: "OAuthCodes");

            migrationBuilder.DropTable(
                name: "OAuthRefreshTokens");

            migrationBuilder.DropTable(
                name: "OAuthTokenFamilies");

            migrationBuilder.DropTable(
                name: "OAuthGrants");

            migrationBuilder.DropTable(
                name: "OAuthClients");
        }
    }
}
