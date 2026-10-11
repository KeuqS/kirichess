using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KiriChess.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDisplayNameModeration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DisplayNameChangedAt",
                table: "AspNetUsers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NormalizedDisplayName",
                table: "AspNetUsers",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            // ---- Phase 8a.4 backfill. Ordering below is load-bearing. ----
            //
            // This SQL is deliberately WEAKER than DisplayNameNormalizer.NormalizeForIdentity: Postgres
            // has no NFKD and no confusable table, so this is lower() + strip-non-alphanumeric only.
            // Consequence: a pre-existing "B0b" gets the key 'b0b' here where C# would say 'bob', so it
            // can coexist with a "Bob" that a new signup would now be refused for.
            //
            // That drift is reported, not hidden: GET /api/moderation/content/display-names returns
            // NormalizedDrift per row and a moderator renames the offenders. Do NOT try to port the
            // confusable table into this migration - a migration that is wrong in a way nobody can test
            // is worse than a weaker one that is honest about it.

            // 1. Key every existing row.
            migrationBuilder.Sql(@"
                UPDATE ""AspNetUsers""
                SET ""NormalizedDisplayName"" = regexp_replace(lower(""DisplayName""), '[^a-z0-9]', '', 'g');");

            // 2. Rows whose name has no alphanumeric substance key to ''. Give them a stable
            //    id-derived key FIRST, so step 3 does not merely suffix them into ''2, ''3.
            migrationBuilder.Sql(@"
                UPDATE ""AspNetUsers""
                SET ""NormalizedDisplayName"" = 'user' || left(replace(""Id""::text, '-', ''), 24)
                WHERE ""NormalizedDisplayName"" = '';");

            // 3. De-duplicate. Deterministic order (CreatedAt, Id): the oldest account in each
            //    colliding group keeps its current key and every later one takes a numeric suffix. The
            //    suffix goes on BOTH columns so the visible name and its key stay consistent - a
            //    silent key-only change would leave two accounts still looking identical to every
            //    other player, which is the thing the unique index exists to prevent.
            //
            //    This is wrapped in a loop rather than a single pass because a single pass is not
            //    guaranteed to reach a unique state: suffixing collides too. If a database holds two
            //    "Bob"s AND an existing "Bob2", a single pass suffixes the second "Bob" into "Bob2",
            //    which collides with the row already there - CreateIndex would then fail and abort the
            //    deploy. Each pass re-ranks and re-suffixes only the groups still colliding, and every
            //    suffix is appended to the row's CURRENT (possibly already-suffixed) key, so keys only
            //    ever lengthen - that monotonic lengthening is what guarantees the loop terminates.
            //    The 10-pass cap is a backstop for a case that should not be reachable in practice; if
            //    it is ever hit, failing loudly with RAISE EXCEPTION is the correct outcome for a
            //    migration - a hung or silently-wrong deploy is worse than one that stops and says why.
            migrationBuilder.Sql(@"
                DO $$
                DECLARE
                    pass_count integer := 0;
                    duplicates_remaining integer;
                BEGIN
                    LOOP
                        pass_count := pass_count + 1;
                        IF pass_count > 10 THEN
                            RAISE EXCEPTION 'AddDisplayNameModeration backfill: NormalizedDisplayName still has duplicate groups after 10 de-duplication passes. Each pass strictly lengthens colliding keys, so this should be structurally impossible - investigate the data rather than raising this cap.';
                        END IF;

                        WITH ranked AS (
                            SELECT ""Id"",
                                   row_number() OVER (
                                       PARTITION BY ""NormalizedDisplayName""
                                       ORDER BY ""CreatedAt"", ""Id""
                                   ) AS rn
                            FROM ""AspNetUsers""
                        )
                        UPDATE ""AspNetUsers"" u
                        SET ""DisplayName"" = left(u.""DisplayName"", 58) || '-' || r.rn::text,
                            ""NormalizedDisplayName"" = left(u.""NormalizedDisplayName"", 58) || r.rn::text
                        FROM ranked r
                        WHERE u.""Id"" = r.""Id"" AND r.rn > 1;

                        SELECT count(*) INTO duplicates_remaining
                        FROM (
                            SELECT ""NormalizedDisplayName""
                            FROM ""AspNetUsers""
                            GROUP BY ""NormalizedDisplayName""
                            HAVING count(*) > 1
                        ) dups;

                        EXIT WHEN duplicates_remaining = 0;
                    END LOOP;
                END $$;");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUsers_NormalizedDisplayName",
                table: "AspNetUsers",
                column: "NormalizedDisplayName",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AspNetUsers_NormalizedDisplayName",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "DisplayNameChangedAt",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "NormalizedDisplayName",
                table: "AspNetUsers");
        }
    }
}
