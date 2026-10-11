using KiriChess.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace KiriChess.Server.Ratings;

/// <summary>
/// Applies Glicko-2's between-period RD inflation to every <c>Rating</c> row that has missed at
/// least one rating period. Separate from <see cref="RatingInactivitySweeper"/> (the timer) for the
/// reason every hosted service in this codebase splits that way: the timer is untestable and the
/// work is not, so the work takes an explicit <c>now</c> and returns a result a test can assert on.
///
/// <b>This is the only writer of <c>Rating</c> outside a finished game.</b> Three constraints follow
/// from that and none of them may be relaxed casually:
///
/// - It writes <c>RatingDeviation</c> and <c>UpdatedAt</c> and nothing else. Not <c>Value</c>, not
///   <c>Volatility</c>, not <c>GamesPlayed</c>, and not <c>LastPlayedAt</c>. An idle player has
///   produced no evidence about their strength or their consistency, and has certainly not played a
///   game. <c>LastPlayedAt</c> is what the established leaderboard uses as "last played"; advancing
///   it here would keep a departed player on that board for as long as their deviation kept rising.
/// - RD is capped at <see cref="RatingConstants.DefaultRd"/> by <see cref="RatingInactivity.Inflate"/>.
/// - It is idempotent. The number of periods to apply is derived from <c>UpdatedAt</c>, and
///   <c>UpdatedAt</c> advances by exactly the periods applied, so a second run in the same period
///   finds zero periods elapsed and writes nothing. Two instances running it at the same moment both
///   read the same <c>UpdatedAt</c> and compute the same RD. <c>Rating.Version</c> (<c>xmin</c>)
///   makes the loser's save throw rather than write; that throw is caught below, and the next tick
///   sees the advanced <c>UpdatedAt</c> and skips the row. A game that finishes during the sweep
///   loses the same way, and the next tick recomputes from the rating that game wrote. No Redis
///   lock, for the same reason <c>RetentionSweep</c> documents for its deletes: it would be
///   machinery guarding nothing.
///
/// <b><c>UpdatedAt</c>'s meaning.</b> Once this runs, the column reads "the rating state is current
/// as of this instant", not "the player last played at this instant". That change is deliberate and
/// unavoidable without a new column, because reconstructing how much inflation a row has already
/// received is impossible from RD alone. Every reader was checked before choosing it: the column is
/// surfaced verbatim as <c>RatingDto.UpdatedAt</c> (GET /api/ratings/me, /api/ratings/users/{id},
/// the public profile, GET /api/v1/ratings/me) and as <c>AccountExportRatingDto.UpdatedAt</c> in the
/// GDPR export. Both are "last modified" readings and both stay accurate. Nothing in the server or
/// in ui/ filters, sorts, gates or renders on it, so no behaviour changes meaning underneath.
///
/// Rows are loaded and written per sweep rather than updated in one SQL statement: the inflation is
/// a per-row function of that row's volatility, and the one reviewed implementation of it is
/// <see cref="Glicko2"/>, in C#. Bounding work per tick is a problem for the day the Ratings table
/// is large enough to care; today the whole table is smaller than a single leaderboard page.
/// </summary>
public sealed class RatingInactivitySweep
{
    private readonly KiriChessDbContext _db;
    private readonly LeaderboardCache _cache;
    private readonly ILogger<RatingInactivitySweep> _logger;

    public RatingInactivitySweep(
        KiriChessDbContext db, LeaderboardCache cache, ILogger<RatingInactivitySweep> logger)
    {
        _db = db;
        _cache = cache;
        _logger = logger;
    }

    /// <summary>
    /// Inflates every stale row against <paramref name="now"/>. <paramref name="now"/> is a
    /// parameter, not UtcNow, so the tests can prove a period boundary without sleeping.
    /// </summary>
    public async Task<RatingInactivityResult> RunAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        var period = RatingConstants.RatingPeriod;
        var cutoff = now - period;

        // The filter and the arithmetic must agree: a row is a candidate only if at least one whole
        // period has passed, which is the same condition PeriodsElapsed returns 0 for.
        var candidates = await _db.Ratings.Where(r => r.UpdatedAt <= cutoff).ToListAsync(ct);

        var inflated = 0;
        var capped = 0;
        var writes = new List<(string Pool, Guid UserId, double Rating, double Rd, DateTimeOffset LastPlayedAt)>();

        foreach (var row in candidates)
        {
            var periods = RatingInactivity.PeriodsElapsed(row.UpdatedAt, now, period);
            if (periods <= 0) continue;

            var before = new Glicko2Rating(row.Value, row.RatingDeviation, row.Volatility);
            var after = RatingInactivity.Inflate(before, periods);

            // A row already at the cap has nothing to gain and is left completely alone, UpdatedAt
            // included: writing it would churn the column every tick forever to say nothing.
            if (after.Rd <= row.RatingDeviation) continue;

            row.RatingDeviation = after.Rd;
            // Advance by whole periods rather than to `now`, so the remainder of the current period
            // is not silently discarded and the row keeps its original phase.
            row.UpdatedAt = row.UpdatedAt + period * periods;

            inflated++;
            if (Math.Abs(after.Rd - RatingConstants.DefaultRd) < 1e-9) capped++;
            writes.Add((row.Pool, row.UserId, row.Value, row.RatingDeviation, row.LastPlayedAt));
        }

        if (inflated == 0)
            return new RatingInactivityResult(now, 0, 0);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The winner already wrote this period's RD, or a finished game wrote the row. Either
            // way this batch did not commit, so the cache must not be told that it did. The next
            // tick reads UpdatedAt again.
            _logger.LogInformation(
                "Rating inactivity sweep lost a row to a concurrent rating write; the next tick retries.");
            return new RatingInactivityResult(now, 0, 0);
        }

        // Write-through exactly as game completion does, and only after SaveChangesAsync succeeded,
        // for the same reason GameRegistry gives: a cache that reports an RD Postgres never recorded
        // is worse than a cache that is briefly stale. UpsertAsync no-ops when Redis is disabled and
        // swallows every failure, so this loop cannot fail the sweep.
        //
        // This is also what makes UpsertAsync's ZREM branch reachable without a game. Two cases,
        // and both are this loop: RD crossing the provisional threshold, and LastPlayedAt ageing
        // out of the established window while RD is still settled. LastPlayedAt is passed through
        // unchanged, which is the whole point of not writing it above.
        foreach (var (pool, userId, rating, rd, lastPlayedAt) in writes)
            await _cache.UpsertAsync(pool, userId, rating, rd, lastPlayedAt, ct);

        _logger.LogInformation(
            "Rating inactivity sweep inflated RD on {RowsInflated} row(s) ({RowsCapped} now at the {DefaultRd} cap).",
            inflated, capped, RatingConstants.DefaultRd);

        return new RatingInactivityResult(now, inflated, capped);
    }
}
