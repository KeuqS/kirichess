using KiriChess.Server.Data;
using KiriChess.Server.Infrastructure.Redis;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

namespace KiriChess.Server.Ratings;

/// <summary>
/// Fills the leaderboard sorted sets at boot and keeps them certified afterwards (Phase 8.4).
/// Registered only when Redis is enabled (see <c>Program.cs</c>), a disabled deploy pays no timer,
/// no Redis writes, nothing, exactly like <see cref="InstanceHeartbeat"/>. Until the first pass
/// finishes (or if it never runs at all), <see cref="LeaderboardCache.TopAsync"/> stays unwarmed and
/// every leaderboard request keeps answering from Postgres, which is a fully working leaderboard on
/// its own.
/// <para>
/// C-1-3: it repeats, and that is not an optimisation. <see cref="LeaderboardCache"/>'s per-pool
/// warm marker carries a one-hour TTL so a long-lived process cannot serve a ranking that has
/// drifted from Postgres forever. Nothing rewrote that marker, so the whole cache went cold exactly
/// one hour after boot and stayed cold for the life of the process: the ZSETs kept being written on
/// every rating change and were never read again. Either the TTL or the re-warm had to go, and the
/// TTL is the one carrying a correctness argument, so the re-warm is what was missing.
/// </para>
/// </summary>
public sealed class LeaderboardWarmup : BackgroundService
{
    private readonly IRedisConnection _connection;
    private readonly InstanceIdentity _instance;
    private readonly LeaderboardCache _cache;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<LeaderboardWarmup> _logger;
    private readonly TimeSpan _rewarmInterval;
    private readonly TimeSpan _lockTtl;
    private readonly TaskCompletionSource _firstPass = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public LeaderboardWarmup(
        IRedisConnection connection,
        InstanceIdentity instance,
        LeaderboardCache cache,
        IServiceScopeFactory scopeFactory,
        ILogger<LeaderboardWarmup> logger)
        : this(connection, instance, cache, scopeFactory, logger, RewarmInterval, LockTtl)
    {
    }

    /// <summary>
    /// Test seam: the same service with its two durations scaled down, so the repeat behaviour can
    /// be proven in seconds instead of half an hour. Internal, and not a constructor the container
    /// can pick (it only considers public ones), so production always gets the two constants.
    /// </summary>
    internal LeaderboardWarmup(
        IRedisConnection connection,
        InstanceIdentity instance,
        LeaderboardCache cache,
        IServiceScopeFactory scopeFactory,
        ILogger<LeaderboardWarmup> logger,
        TimeSpan rewarmInterval,
        TimeSpan lockTtl)
    {
        _connection = connection;
        _instance = instance;
        _cache = cache;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _rewarmInterval = rewarmInterval;
        _lockTtl = lockTtl;
    }

    /// <summary>
    /// Completes when the first warm-up pass has finished, whatever its outcome. A test asserting on
    /// the boot warm cannot await <see cref="BackgroundService.ExecuteTask"/> any more, since that
    /// now runs until the host stops.
    /// </summary>
    internal Task FirstPassCompleted => _firstPass.Task;

    // BackgroundService.StartAsync does not wait for ExecuteAsync to finish before returning, so the
    // Postgres scan below already runs off the startup path, the same "logged, never fatal" stance
    // as GameRecoveryService.SweepUnresumedAsync's fire-and-forget sweep, just without needing the
    // extra indirection since a hosted service gets that for free.
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RunOnceAsync(stoppingToken);
            _firstPass.TrySetResult();

            // The same shape as RatingInactivitySweeper: a PeriodicTimer, and a pass that swallows
            // its own failures so one bad tick never ends the loop.
            using var timer = new PeriodicTimer(_rewarmInterval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await RunOnceAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown, not a failure.
        }
        finally
        {
            _firstPass.TrySetResult();
        }
    }

    private async Task RunOnceAsync(CancellationToken stoppingToken)
    {
        try
        {
            // With N instances warming together, exactly one must do the work; SET NX is the lock.
            // This key is ONLY a lock. It used to be the warm marker as well, which is what made a
            // failed warm-up indistinguishable from a finished one: the marker was set before the
            // Postgres scan below, so a scan that threw (Neon scale-to-zero at boot is the named case
            // in DEPLOYMENT.md) left readers being told the cache could be trusted for a full hour
            // while the ZSETs behind it were empty. Warmth now belongs to LeaderboardCache, per pool,
            // written only after that pool's rebuild actually replaced its keys.
            //
            // The TTL is a lock timeout and nothing more, so it is short: an instance that dies
            // mid-warm-up should not hold every other instance off. It is also, deliberately, far
            // shorter than the re-warm interval, so the lock is always free again by the next tick
            // and a re-warm is never skipped merely because the previous one held the key.
            var acquired = await _connection.Db.StringSetAsync(
                _connection.Key(WarmupLockKey), _instance.Id, _lockTtl, When.NotExists).WaitAsync(stoppingToken);
            if (!acquired)
            {
                _logger.LogInformation("Leaderboard warm-up already claimed by another instance; skipping.");
                return;
            }

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();

            // MatchmakingPools, not OfficialPools: the bot namespace receives rated games too
            // (RatingService writes bot:{preset} rows through GameRegistry), and GET
            // /api/v1/leaderboards/bot/{pool} reads the same cache. Warming only the human pools left
            // every lb:bot:* key absent while the old global marker still claimed the cache was warm,
            // so the bot endpoint took the cache-hit branch and served [] as a real ranking.
            // Never a hard-coded list; both lists are config-derived since 8.6.
            foreach (var pool in RatingConstants.MatchmakingPools)
            {
                var rows = await db.Ratings.Where(r => r.Pool == pool)
                    .Select(r => new { r.UserId, r.Value, r.RatingDeviation, r.LastPlayedAt })
                    .ToListAsync(stoppingToken);

                var warmed = await _cache.RebuildAsync(pool,
                    rows.Select(r => new LeaderboardCacheRow(r.UserId, r.Value, r.RatingDeviation, r.LastPlayedAt)).ToArray(),
                    stoppingToken);

                if (warmed)
                    _logger.LogInformation("Warmed leaderboard cache for pool {Pool}: {Count} row(s).", pool, rows.Count);
                else
                    _logger.LogWarning(
                        "Leaderboard cache for pool {Pool} was not warmed; that pool keeps answering from Postgres.", pool);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw; // shutdown, not a failed warm-up - let it end the loop rather than be logged
        }
        catch (Exception ex) when (IsRedisOutage(ex))
        {
            _logger.LogWarning(ex, "Leaderboard warm-up failed; the endpoint keeps using Postgres, which is a fully working leaderboard.");
        }
        catch (Exception ex)
        {
            // Anything else (Postgres unreachable at boot, etc.) gets the same stance: log and move
            // on. A warm-up failure must never take the process down, and the next tick retries.
            _logger.LogError(ex, "Leaderboard warm-up failed unexpectedly; the endpoint keeps using Postgres.");
        }
    }

    /// <summary>The warm-up lock. A lock only; warmth lives in <see cref="LeaderboardCache"/>.</summary>
    public const string WarmupLockKey = "lb:warmup-lock";

    /// <summary>Lock timeout, not a re-warm schedule. Short so a dead instance blocks nobody for long.</summary>
    public static readonly TimeSpan LockTtl = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How often the sorted sets are rebuilt and their warm markers refreshed. Derived from
    /// <see cref="LeaderboardCache.WarmTtl"/> rather than picked, because the one thing this number
    /// must satisfy is being shorter than the marker's own TTL: at half of it, a pool that was warmed
    /// is re-certified with a full hour of headroom to spare, and moving the TTL cannot silently
    /// leave the cache going cold between ticks again.
    /// </summary>
    public static readonly TimeSpan RewarmInterval = LeaderboardCache.WarmTtl / 2;

    // The one place in this feature where the two outage types are worth distinguishing, and the
    // reason this filter survives here after LeaderboardCache dropped it: Redis being unreachable is
    // an expected, operational state for an optional dependency (LogWarning), whereas anything else
    // reaching this catch is a bug or a Postgres failure and deserves LogError. LeaderboardCache's
    // catches all degrade identically to "ask Postgres", so a filter there distinguished nothing.
    private static bool IsRedisOutage(Exception ex) => ex is RedisConnectionException or RedisTimeoutException;
}
