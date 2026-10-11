using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KiriChess.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddHistoryAndRetentionIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AspNetUsers_DeletedAt",
                table: "AspNetUsers");

            migrationBuilder.CreateIndex(
                name: "IX_TwoFactorChallenges_ExpiresAt",
                table: "TwoFactorChallenges",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_RefreshTokens_ExpiresAt",
                table: "RefreshTokens",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_Games_BlackPlayerId_EndedAt",
                table: "Games",
                columns: new[] { "BlackPlayerId", "EndedAt" },
                filter: "\"EndedAt\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Games_WhitePlayerId_EndedAt",
                table: "Games",
                columns: new[] { "WhitePlayerId", "EndedAt" },
                filter: "\"EndedAt\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ExternalLoginCodes_ExpiresAt",
                table: "ExternalLoginCodes",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_Challenges_ExpiresAt",
                table: "Challenges",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_ApiTokens_ExpiresAt",
                table: "ApiTokens",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_ApiTokens_RevokedAt",
                table: "ApiTokens",
                column: "RevokedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TwoFactorChallenges_ExpiresAt",
                table: "TwoFactorChallenges");

            migrationBuilder.DropIndex(
                name: "IX_RefreshTokens_ExpiresAt",
                table: "RefreshTokens");

            migrationBuilder.DropIndex(
                name: "IX_Games_BlackPlayerId_EndedAt",
                table: "Games");

            migrationBuilder.DropIndex(
                name: "IX_Games_WhitePlayerId_EndedAt",
                table: "Games");

            migrationBuilder.DropIndex(
                name: "IX_ExternalLoginCodes_ExpiresAt",
                table: "ExternalLoginCodes");

            migrationBuilder.DropIndex(
                name: "IX_Challenges_ExpiresAt",
                table: "Challenges");

            migrationBuilder.DropIndex(
                name: "IX_ApiTokens_ExpiresAt",
                table: "ApiTokens");

            migrationBuilder.DropIndex(
                name: "IX_ApiTokens_RevokedAt",
                table: "ApiTokens");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUsers_DeletedAt",
                table: "AspNetUsers",
                column: "DeletedAt");
        }
    }
}
