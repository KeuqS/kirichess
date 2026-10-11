using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KiriChess.Server.Data.Migrations
{
    /// <summary>
    /// Data-only migration: the six pool keys become neutral ids.
    ///
    /// The keys used to spell the pool names (<c>flagfall</c>, <c>zeitnot</c>, <c>sundial</c>), which
    /// meant a rebrand either left a stale word sitting in four tables or forced this backfill. They
    /// are now named after the economy they run (<c>steal1</c>, <c>bounty1</c>, …), which no rename
    /// can falsify, so this should be the last time these values move. The display names live in
    /// <c>config/kirichess.rules.json</c> and reach the UI as labels; nothing downstream reads a
    /// pool's name off its key. See <c>PoolLabelDriftTests</c>, which pins this exact set.
    ///
    /// There is no schema change here. These four columns are denormalised grouping strings with no
    /// foreign key pointing at them, which is the only reason a rename is a backfill rather than a
    /// referential-integrity problem.
    ///
    /// The <c>bot:</c> pass covers the parallel bot rating namespace (<c>RatingConstants.BotPools</c>,
    /// Phase 9f.5), whose keys are built by prefixing the official key. An UPDATE that matches no rows
    /// is a no-op, so running the bot pass against tables that never hold a bot pool costs nothing.
    /// </summary>
    public partial class RenamePoolKeysToNeutralIds : Migration
    {
        /// <summary>Old key to new key, in <c>config/kirichess.rules.json</c> declaration order.</summary>
        private static readonly (string Old, string New)[] Renames =
        [
            ("zeitnot", "bounty1"),
            ("ratchet", "burn1"),
            ("pendulum", "steal2"),
            ("flagfall", "steal1"),
            ("mainspring", "burn2"),
            ("sundial", "steal3"),
        ];

        /// <summary>Every table/column pair that stores a pool key.</summary>
        private static readonly (string Table, string Column)[] Targets =
        [
            ("Games", "RatingPool"),
            ("Ratings", "Pool"),
            ("RatingHistories", "Pool"),
            ("Challenges", "Pool"),
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
            => Apply(migrationBuilder, forward: true);

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
            => Apply(migrationBuilder, forward: false);

        private static void Apply(MigrationBuilder migrationBuilder, bool forward)
        {
            foreach (var (oldKey, newKey) in Renames)
            {
                var from = forward ? oldKey : newKey;
                var to = forward ? newKey : oldKey;

                foreach (var (table, column) in Targets)
                {
                    migrationBuilder.Sql(
                        $"""UPDATE "{table}" SET "{column}" = '{to}' WHERE "{column}" = '{from}';""");
                    migrationBuilder.Sql(
                        $"""UPDATE "{table}" SET "{column}" = 'bot:{to}' WHERE "{column}" = 'bot:{from}';""");
                }
            }
        }
    }
}
