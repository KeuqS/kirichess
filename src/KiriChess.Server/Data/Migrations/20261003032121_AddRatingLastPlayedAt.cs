using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KiriChess.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRatingLastPlayedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastPlayedAt",
                table: "Ratings",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");

            // C-7-1. The column is "last finished rated game", and UpdatedAt is not: the inactivity
            // sweep advances UpdatedAt without a game. History is the real clock. A row with no
            // history (a seed, nothing else writes a Rating without one) falls back to UpdatedAt,
            // which for those rows still is the insert.
            migrationBuilder.Sql(
                """
                UPDATE "Ratings" AS r
                SET "LastPlayedAt" = COALESCE(
                    (
                        SELECT MAX(h."CreatedAt")
                        FROM "RatingHistories" AS h
                        WHERE h."UserId" = r."UserId" AND h."Pool" = r."Pool"
                    ),
                    r."UpdatedAt");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastPlayedAt",
                table: "Ratings");
        }
    }
}
