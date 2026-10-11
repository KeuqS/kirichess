using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KiriChess.Server.Data.Migrations
{
    /// <inheritdoc />
    // Phase 8a.2. Four new nullable ApplicationUser columns for erasure (DeletedAt) and consent
    // (TermsAcceptedAt, TermsVersion, AgeConfirmedAt) - no backfill, since NULL is the meaningful
    // value for every one of them (README scope decision 7: existing accounts predate the policy).
    //
    // The NormalizedDisplayName unique index is dropped and recreated PARTIAL
    // (filter: "DeletedAt" IS NULL) rather than altered in place, because EF Core has no AlterIndex.
    // Without the filter, every tombstoned account sharing the DisplayName "Deleted account" would
    // collide on the second deletion - see KiriChessDbContext's comment on this index and README
    // ground truth 3.
    public partial class AddAccountErasureAndConsent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AspNetUsers_NormalizedDisplayName",
                table: "AspNetUsers");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AgeConfirmedAt",
                table: "AspNetUsers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeletedAt",
                table: "AspNetUsers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "TermsAcceptedAt",
                table: "AspNetUsers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TermsVersion",
                table: "AspNetUsers",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUsers_DeletedAt",
                table: "AspNetUsers",
                column: "DeletedAt");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUsers_NormalizedDisplayName",
                table: "AspNetUsers",
                column: "NormalizedDisplayName",
                unique: true,
                filter: "\"DeletedAt\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AspNetUsers_DeletedAt",
                table: "AspNetUsers");

            migrationBuilder.DropIndex(
                name: "IX_AspNetUsers_NormalizedDisplayName",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "AgeConfirmedAt",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "TermsAcceptedAt",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "TermsVersion",
                table: "AspNetUsers");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUsers_NormalizedDisplayName",
                table: "AspNetUsers",
                column: "NormalizedDisplayName",
                unique: true);
        }
    }
}
