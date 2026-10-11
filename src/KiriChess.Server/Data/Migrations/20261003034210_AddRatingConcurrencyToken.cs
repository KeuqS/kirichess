using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KiriChess.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRatingConcurrencyToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // C-7-6. xmin already exists on every PostgreSQL row. Npgsql skips AddColumn for
            // that system column, so applying this records the model change and alters no table.
            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                table: "Ratings",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "xmin",
                table: "Ratings");
        }
    }
}
