using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KiriChess.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class UniqueRatingHistoryPerGameUser : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RatingHistories_GameId",
                table: "RatingHistories");

            migrationBuilder.CreateIndex(
                name: "IX_RatingHistories_GameId_UserId",
                table: "RatingHistories",
                columns: new[] { "GameId", "UserId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RatingHistories_GameId_UserId",
                table: "RatingHistories");

            migrationBuilder.CreateIndex(
                name: "IX_RatingHistories_GameId",
                table: "RatingHistories",
                column: "GameId");
        }
    }
}
