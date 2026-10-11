using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KiriChess.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddChallengeCustomConfig : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CustomConfig",
                table: "Challenges",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CustomConfig",
                table: "Challenges");
        }
    }
}
