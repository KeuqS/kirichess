using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KiriChess.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddChallenges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Challenges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Token = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ChallengerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Pool = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Rated = table.Column<bool>(type: "boolean", nullable: false),
                    Colour = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AcceptedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AcceptedById = table.Column<Guid>(type: "uuid", nullable: true),
                    GameId = table.Column<Guid>(type: "uuid", nullable: true),
                    CancelledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Challenges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Challenges_AspNetUsers_AcceptedById",
                        column: x => x.AcceptedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Challenges_AspNetUsers_ChallengerId",
                        column: x => x.ChallengerId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Challenges_AcceptedById",
                table: "Challenges",
                column: "AcceptedById");

            migrationBuilder.CreateIndex(
                name: "IX_Challenges_ChallengerId_AcceptedAt_CancelledAt",
                table: "Challenges",
                columns: new[] { "ChallengerId", "AcceptedAt", "CancelledAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Challenges_Token",
                table: "Challenges",
                column: "Token",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Challenges");
        }
    }
}
