using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KiriChess.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddApiPlayDisclosure : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "BlackViaApi",
                table: "Games",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "WhiteViaApi",
                table: "Games",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "ChallengerViaApi",
                table: "Challenges",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BlackViaApi",
                table: "Games");

            migrationBuilder.DropColumn(
                name: "WhiteViaApi",
                table: "Games");

            migrationBuilder.DropColumn(
                name: "ChallengerViaApi",
                table: "Challenges");
        }
    }
}
