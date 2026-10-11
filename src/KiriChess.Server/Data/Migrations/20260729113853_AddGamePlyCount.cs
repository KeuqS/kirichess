using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KiriChess.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddGamePlyCount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PlyCount",
                table: "Games",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Backfill: existing rows would otherwise report a ply count of 0 and drag every
            // historical average to zero. "Moves" is a jsonb array, so Postgres can count it in
            // place - no round trip through the app.
            migrationBuilder.Sql(@"UPDATE ""Games"" SET ""PlyCount"" = jsonb_array_length(""Moves"");");

            migrationBuilder.CreateIndex(
                name: "IX_Games_RatingPool_EndedAt",
                table: "Games",
                columns: new[] { "RatingPool", "EndedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Games_RatingPool_EndedAt",
                table: "Games");

            migrationBuilder.DropColumn(
                name: "PlyCount",
                table: "Games");
        }
    }
}
