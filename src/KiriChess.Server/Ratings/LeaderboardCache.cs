using KiriChess.Server.Infrastructure.Redis;
using StackExchange.Redis;

namespace KiriChess.Server.Ratings;

/// <summary>
/// Redis-backed leaderboard cache (Phase 8.4): two ZSETs per pool, because the endpoint's
/// <c>includeProvisional</c> flag cannot be expressed as a filter over a single sorted set, a ZSET
/// scores by rating and knows nothing about rating deviation. <c>{p}lb:{pool}</c> holds every rated
/// player; <c>{p}lb:{pool}:established</c> holds only those <see cref="EstablishedBoard"/> admits:
/// RD at or under <see cref="RatingConstants.ProvisionalRdThreshold"/>, and a rated game in this pool
/// inside <see cref="RatingConstants.LeaderboardActiveWindow"/>.
///
/// <b>This is a derived cache, never the source of truth.</b> Roadmap line 150 said of Phase 4's
/// leaderboards, "Postgres index first; Redis sorted sets only when traffic demands", traffic still
/// does not demand it, nothing is deployed yet, so the Postgres
/// <c>OrderByDescending(Value).Take(n)</c> query in <see cref="RatingEndpoints"/> stays, permanently,
/// as the fallback and the source of truth. Every method here degrades to "the cache cannot answer"
/// on ANY Redis failure rather than throwing, see <see cref="TopAsync"/>, and the caller falls back
/// to Postgres. If Redis is empty, cold, stale, flushed, or unreachable, the endpoint answers from
/// Postgres with byte-identical JSON and nobody notices; a design where a flushed Redis loses a
/// ranking would be the wrong answer, no matter how much faster it is.
///
/// <b>Warmth is per pool, not global.</b> <c>{p}lb:{pool}:built</c> is written by
/// <see cref="RebuildAsync"/> only after that pool's two ZSETs have actually been replaced, and is
/// deleted before the rebuild starts, so a pool that has never been rebuilt (or whose rebuild failed
/// partway) reads as COLD and falls back to Postgres instead of answering with an empty ranking.
/// The single global marker this replaced could not express either state: it was written before the
/// work it certified, and it certified every pool including the <c>bot:</c> namespace the warm-up
/// never touched.
/// </summary>
public sealed class LeaderboardCache
{
    private readonly IRedisConnection _connection;
    private readonly ILogger<LeaderboardCache> _logger;

    public LeaderboardCache(IRedisConnection connection, ILogger<LeaderboardCache> logger)
    {
        _connection = connection;
        _logger = logger;
    }

    /// <summary>
    /// Write-through for one player's rating in one pool. No-op when Redis is disabled.
    /// <paramref name="lastPlayedAt"/> is the row's last finished rated game, not
    /// <c>Rating.UpdatedAt</c>: the inactivity sweep advances that column without a game, and using
    /// it here would put a departed player back on the established board every time their deviation
    /// inflated.
    /// </summary>
    public async Task UpsertAsync(
        string pool, Guid userId, double rating, double ratingDeviation, DateTimeOffset lastPlayedAt,
        CancellationToken ct = default)
    {
        if (!_connection.Enabled) return;
        try
        {
            var db = _connection.Db;
            var member = (RedisValue)userId.ToString();

            // ZADD into the "everyone" set always...
            await db.SortedSetAddAsync(_connection.Key($"lb:{pool}"), member, rating).WaitAsync(ct);

            // ...and into "established" only while EstablishedBoard admits them, otherwise ZREM.
            // The ZREM branch is not hypothetical. Two ways off, and neither requires the player to
            // finish another game: RD rises with inactivity, and a player whose last game is older
            // than LeaderboardActiveWindow leaves even while their RD is still settled. The sweep
            // calls this with the unchanged LastPlayedAt so the second case actually removes them.
            if (EstablishedBoard.Contains(ratingDeviation, lastPlayedAt, DateTimeOffset.UtcNow))
                await db.SortedSetAddAsync(_connection.Key($"lb:{pool}:established"), member, rating).WaitAsync(ct);
            else
                await db.SortedSetRemoveAsync(_connection.Key($"lb:{pool}:established"), member).WaitAsync(ct);
        }
        catch (Exception ex)
        {
            // Postgres already has the rating (this is called after SaveChangesAsync succeeded), so
            // a failure here is at worst a stale cache entry until the next warm-up, never a reason
            // to fail a request that already finished and was saved.
            //
            // Catch EVERYTHING, not just the two outage types. The caller (GameRegistry's
            // PersistIfOverAsync) is on the move path: an escaping exception fails the player's final
            // move after Postgres has already committed the game and both ratings, and skips
            // GameDistribution.OnGameEndedAsync so the ownership lease dangles. RedisServerException
            // (the OOM / READONLY / MISCONF family a managed Redis returns under memory pressure or
            // during a failover) is not a connection or timeout error, so the narrow filter used to
            // let exactly that case through.
            _logger.LogWarning(ex,
                "Failed to write the leaderboard cache for pool {Pool}, user {UserId}.", pool, userId);
        }
    }

    /// <summary>
    /// Top <paramref name="take"/> user ids, highest rating first, or null when the cache cannot
    /// answer, disabled, unreachable, or not warmed. Null means "ask Postgres", and is deliberately
    /// distinct from an empty list, which means "this pool genuinely has no ranked players".
    /// </summary>
    public async Task<IReadOnlyList<Guid>?> TopAsync(string pool, int take, bool includeProvisional, CancellationToken ct = default)
    {
        if (!_connection.Enabled) return null;
        try
        {
            // An unwarmed cache answering "no players" would render an empty leaderboard on a site
            // that has them, which is worse than the slower Postgres query it would have replaced.
            // The check is per pool: a `bot:` pool that no warm-up has ever rebuilt must read as
            // cold, not as a real ranking that happens to be empty.
            if (!await IsWarmAsync(pool, ct)) return null;

            var key = _connection.Key(includeProvisional ? $"lb:{pool}" : $"lb:{pool}:established");
            var members = await _connection.Db.SortedSetRangeByRankAsync(key, 0, take - 1, Order.Descending).WaitAsync(ct);
            return members.Select(m => Guid.Parse((string)m!)).ToArray();
        }
        catch (Exception ex)
        {
            // Catch EVERYTHING: null means "ask Postgres", which is a correct answer to every
            // possible failure here, including a RedisServerException and the Guid.Parse above,
            // which the two-type filter did not cover at all. A derived cache must never 500 a
            // read-only leaderboard.
            _logger.LogWarning(ex, "Failed to read the leaderboard cache for pool {Pool}; falling back to Postgres.", pool);
            return null;
        }
    }

    /// <summary>
    /// Rebuilds both ZSETs for one pool from the given rows (the boot warm-up, <see cref="LeaderboardWarmup"/>).
    /// Writes into a <c>:building</c> key and RENAMEs it over the live one, RENAME is atomic in
    /// Redis, so a concurrent reader sees the old ranking right up until the swap and never an empty
    /// or half-populated one.
    ///
    /// This method also owns the pool's warm marker, and that ordering is the whole point: the
    /// marker is DELETED first and SET last, so the only state in which a reader is told "this pool's
    /// cache can be trusted" is one where the ZSETs behind it have already been replaced. A rebuild
    /// that throws halfway leaves the pool cold and every read falling back to Postgres, rather than
    /// serving an unbuilt ZSET's empty list as a real ranking.
    ///
    /// Returns true when the pool is now warm. False means Redis is disabled or the rebuild failed;
    /// either way the caller must not treat the pool as cached.
    /// </summary>
    public async Task<bool> RebuildAsync(string pool, IReadOnlyCollection<LeaderboardCacheRow> rows, CancellationToken ct = default)
    {
        if (!_connection.Enabled) return false;
        try
        {
            var db = _connection.Db;

            // Cold for the duration of the rebuild. Readers fall back to Postgres, which is the same
            // fully working leaderboard they would have got had this instance never booted.
            await db.KeyDeleteAsync(WarmKey(pool)).WaitAsync(ct);
            var buildKey = _connection.Key($"lb:{pool}:building");
            var buildEstablishedKey = _connection.Key($"lb:{pool}:established:building");
            var liveKey = _connection.Key($"lb:{pool}");
            var liveEstablishedKey = _connection.Key($"lb:{pool}:established");

            // Clear a previous rebuild's leftovers (e.g. a crash between ZADD and RENAME) so they
            // can never be mistaken for this rebuild's data.
            await db.KeyDeleteAsync(new RedisKey[] { buildKey, buildEstablishedKey }).WaitAsync(ct);

            if (rows.Count > 0)
            {
                var all = rows.Select(r => new SortedSetEntry(r.UserId.ToString(), r.Rating)).ToArray();
                await db.SortedSetAddAsync(buildKey, all).WaitAsync(ct);

                var asOf = DateTimeOffset.UtcNow;
                var established = rows.Where(r => EstablishedBoard.Contains(r.RatingDeviation, r.LastPlayedAt, asOf))
                    .Select(r => new SortedSetEntry(r.UserId.ToString(), r.Rating)).ToArray();
                if (established.Length > 0)
                    await db.SortedSetAddAsync(buildEstablishedKey, established).WaitAsync(ct);
            }

            // RENAME requires the source key to exist; an empty pool has nothing above to ZADD, so
            // fall back to deleting the live key directly, an empty ranking is a real state here,
            // not an error, and must not leave a stale live key behind forever.
            await ReplaceAsync(db, buildKey, liveKey, ct);
            await ReplaceAsync(db, buildEstablishedKey, liveEstablishedKey, ct);

            // Last, and only now. The TTL is the same one-hour self-heal the single global marker
            // carried: a marker that never expires would let a long-lived process serve a ranking
            // that drifted from Postgres forever. It only heals rather than simply switching the
            // cache off because LeaderboardWarmup re-runs on a timer shorter than this TTL
            // (C-1-3); before that it did not, and every pool went cold an hour after boot.
            // The value is the instant of the rebuild, purely so an operator reading the key during
            // an incident can see how stale the ranking behind it is. Nothing reads it.
            await db.StringSetAsync(WarmKey(pool), DateTimeOffset.UtcNow.ToString("O"), WarmTtl).WaitAsync(ct);
            return true;
        }
        catch (Exception ex)
        {
            // Catch EVERYTHING, same reasoning as UpsertAsync and TopAsync. The marker was deleted
            // before any of the work above, and is set only on the success path, so there is nothing
            // to undo here: a failed rebuild has already left the pool cold.
            _logger.LogWarning(ex, "Failed to rebuild the leaderboard cache for pool {Pool}; Postgres remains the fallback.", pool);
            return false;
        }
    }

    private static async Task ReplaceAsync(IDatabase db, RedisKey buildKey, RedisKey liveKey, CancellationToken ct)
    {
        if (await db.KeyExistsAsync(buildKey).WaitAsync(ct))
            await db.KeyRenameAsync(buildKey, liveKey).WaitAsync(ct);
        else
            await db.KeyDeleteAsync(liveKey).WaitAsync(ct);
    }

    /// <summary>
    /// True once <see cref="RebuildAsync"/> has successfully rebuilt THIS pool
    /// (<c>{p}lb:{pool}:built</c>). A pool nobody warmed is cold, whatever any other pool's state is.
    /// </summary>
    public async Task<bool> IsWarmAsync(string pool, CancellationToken ct = default)
    {
        if (!_connection.Enabled) return false;
        try
        {
            return await _connection.Db.KeyExistsAsync(WarmKey(pool)).WaitAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check the warm marker for pool {Pool}; treating the cache as not warmed.", pool);
            return false;
        }
    }

    /// <summary>The per-pool warm marker's TTL, and therefore how long a rebuilt pool stays trusted.</summary>
    public static readonly TimeSpan WarmTtl = TimeSpan.FromHours(1);

    private RedisKey WarmKey(string pool) => _connection.Key($"lb:{pool}:built");
}

/// <summary>One rating row, as <see cref="LeaderboardCache.RebuildAsync"/> needs it.</summary>
public readonly record struct LeaderboardCacheRow(
    Guid UserId, double Rating, double RatingDeviation, DateTimeOffset LastPlayedAt);
