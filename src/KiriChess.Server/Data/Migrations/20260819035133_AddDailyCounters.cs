using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KiriChess.Server.Data.Migrations
{
    /// <inheritdoc />
    // AD.6 / 11a.3. One table, DailyCounters: a counter per (UTC date, allowlisted key, allowlisted
    // source), no surrogate id and no other column - holds no cookie, visitor id, session id, IP,
    // user agent, referrer or account id, which is what keeps it outside GDPR entirely. Date maps to
    // "date" (DateOnly), not "timestamp", because a counter is bucketed by calendar day, never an
    // instant.
    public partial class AddDailyCounters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DailyCounters",
                columns: table => new
                {
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    CounterKey = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Count = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DailyCounters", x => new { x.Date, x.CounterKey, x.Source });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DailyCounters");
        }
    }
}
