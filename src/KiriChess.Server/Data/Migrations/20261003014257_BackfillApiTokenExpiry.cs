using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KiriChess.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class BackfillApiTokenExpiry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // S-1-4. The column existed and was never written, so every row was immortal. New mints
            // write ExpiresAt (default 90 days, ApiTokenDefaults.DefaultExpiresInDays). This gives
            // the rows already issued the same life, measured from when they were created.
            migrationBuilder.Sql(
                """
                UPDATE "ApiTokens"
                SET "ExpiresAt" = "CreatedAt" + INTERVAL '90 days'
                WHERE "ExpiresAt" IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Only the rows this Up could have written: an expiry that is exactly CreatedAt + 90 days.
            // A token minted afterwards with an explicit ExpiresInDays other than 90 is left alone.
            migrationBuilder.Sql(
                """
                UPDATE "ApiTokens"
                SET "ExpiresAt" = NULL
                WHERE "ExpiresAt" = "CreatedAt" + INTERVAL '90 days';
                """);
        }
    }
}
