using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KiriChess.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddModeration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ModeratorNote",
                table: "Reports",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReviewedAt",
                table: "Reports",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ReviewedByUserId",
                table: "Reports",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "UserSanctions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ModeratorNote = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    IssuedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    IssuedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LiftedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LiftedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    LiftReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ReportId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserSanctions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserSanctions_AspNetUsers_IssuedByUserId",
                        column: x => x.IssuedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UserSanctions_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UserSanctions_Reports_ReportId",
                        column: x => x.ReportId,
                        principalTable: "Reports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserSanctions_IssuedAt",
                table: "UserSanctions",
                column: "IssuedAt");

            migrationBuilder.CreateIndex(
                name: "IX_UserSanctions_IssuedByUserId",
                table: "UserSanctions",
                column: "IssuedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_UserSanctions_ReportId",
                table: "UserSanctions",
                column: "ReportId");

            migrationBuilder.CreateIndex(
                name: "IX_UserSanctions_UserId_LiftedAt_ExpiresAt",
                table: "UserSanctions",
                columns: new[] { "UserId", "LiftedAt", "ExpiresAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UserSanctions");

            migrationBuilder.DropColumn(
                name: "ModeratorNote",
                table: "Reports");

            migrationBuilder.DropColumn(
                name: "ReviewedAt",
                table: "Reports");

            migrationBuilder.DropColumn(
                name: "ReviewedByUserId",
                table: "Reports");
        }
    }
}
