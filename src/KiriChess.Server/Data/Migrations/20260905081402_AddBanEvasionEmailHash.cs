using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KiriChess.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBanEvasionEmailHash : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BanEvasionEmailHash",
                table: "AspNetUsers",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUsers_BanEvasionEmailHash",
                table: "AspNetUsers",
                column: "BanEvasionEmailHash",
                filter: "\"BanEvasionEmailHash\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AspNetUsers_BanEvasionEmailHash",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "BanEvasionEmailHash",
                table: "AspNetUsers");
        }
    }
}
