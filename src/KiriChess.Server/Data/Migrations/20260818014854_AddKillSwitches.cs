using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KiriChess.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddKillSwitches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "KillSwitches",
                columns: table => new
                {
                    Name = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KillSwitches", x => x.Name);
                });

            // KillSwitchService's fallback ("a row that has never been seen reads as enabled") makes
            // this seed a belt-and-suspenders row rather than a requirement, but the admin GET/PUT
            // surface (KillSwitchEndpoints) wants all four to already exist rather than materialising
            // one on the first PUT.
            var seededAt = new DateTimeOffset(2026, 8, 18, 0, 0, 0, TimeSpan.Zero);
            migrationBuilder.InsertData(
                table: "KillSwitches",
                columns: new[] { "Name", "Enabled", "UpdatedAt", "UpdatedByUserId" },
                values: new object[,]
                {
                    { "Registration", true, seededAt, null },
                    { "Spectating", true, seededAt, null },
                    { "SeekList", true, seededAt, null },
                    { "Pairing", true, seededAt, null },
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "KillSwitches");
        }
    }
}
