using System.Text.Json;
using KiriChess.Server.Account;
using KiriChess.Server.Data;
using KiriChess.Server.Data.Entities;
using KiriChess.Server.Infrastructure.Redis;
using KiriChess.Server.Ratings;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using Testcontainers.PostgreSql;
using Xunit;

namespace KiriChess.Server.Tests;

/// <summary>
/// Covers the Phase 8.4 leaderboard cache contract from Phase 8.4:
/// the ZSETs are a derived cache, Postgres stays authoritative, and the Redis read path must never
/// scramble a ranking. These run everywhere, no Docker required,
/// <see cref="LeaderboardCacheContainerTests"/> and <see cref="LeaderboardEndpointParityTests"/> below
/// cover the real-Redis and real-Postgres paths.
/// </summary>
public sealed class LeaderboardCacheTests
{
    private static RedisOptions DisabledOptions() => new("", "kirichess:", 30, 10, 5);

    [Fact]
    public async Task Disabled_cache_returns_null_so_the_endpoint_falls_back()
    {
        using var connection = new RedisConnection(DisabledOptions(), new InstanceIdentity("test-instance"));
        var cache = new LeaderboardCache(connection, NullLogger<LeaderboardCache>.Instance);

        var top = await cache.TopAsync("steal1", 10, includeProvisional: true);
        Assert.Null(top);

        // No-op, must not throw even though there is nowhere for the write to go.
        await cache.UpsertAsync("steal1", Guid.NewGuid(), 1500, 60, DateTimeOffset.UtcNow);
    }

    // C-7-4. GameRegistry.PersistIfOverAsync states that "a cache write failure is logged and
    // swallowed inside UpsertAsync itself" and skips neither the MovePlayed broadcast nor
    // GameDistribution.OnGameEndedAsync on the strength of that claim. The filter behind it used to
    // admit only RedisConnectionException and RedisTimeoutException, so a RedisServerException (the
    // OOM / READONLY / MISCONF family a managed Redis returns under memory pressure or during a
    // failover) escaped, failed the player's final move after Postgres had already committed the
    // game and both ratings, and left the ownership lease dangling.
    [Fact]
    public async Task A_server_side_redis_error_does_not_escape_the_write_path()
    {
        var cache = new LeaderboardCache(new ServerErrorRedisConnection(), NullLogger<LeaderboardCache>.Instance);

        // The assertion is that this returns at all. An exception here is a failed game completion.
        await cache.UpsertAsync("steal1", Guid.NewGuid(), 1600, 55, DateTimeOffset.UtcNow);
    }

    // The read half of the same finding: a server-side Redis error on a read-only leaderboard must
    // degrade to "ask Postgres" (null), never to a 500.
    [Fact]
    public async Task A_server_side_redis_error_on_the_read_path_falls_back_instead_of_throwing()
    {
        var cache = new LeaderboardCache(new ServerErrorRedisConnection(), NullLogger<LeaderboardCache>.Instance);

        Assert.Null(await cache.TopAsync("steal1", 10, includeProvisional: true));
        Assert.False(await cache.IsWarmAsync("steal1"));
    }

    // C-7-2. A rebuild that fails must leave the pool COLD, so reads keep falling back to Postgres.
    // The marker used to be written before the work it certified, which is how a failed warm-up
    // could answer "this cache can be trusted" for a full hour over ZSETs that were never built.
    [Fact]
    public async Task A_failed_rebuild_reports_failure_and_leaves_the_pool_cold()
    {
        var connection = new ServerErrorRedisConnection();
        var cache = new LeaderboardCache(connection, NullLogger<LeaderboardCache>.Instance);

        var warmed = await cache.RebuildAsync("steal1", new LeaderboardCacheRow[] { new(Guid.NewGuid(), 1500.0, 50.0, DateTimeOffset.UtcNow) });

        Assert.False(warmed);
        Assert.Null(await cache.TopAsync("steal1", 10, includeProvisional: true));
    }

    [Fact]
    public void Ranks_follow_the_cache_order_not_the_database_order()
    {
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();
        var idC = Guid.NewGuid();
        var ids = new List<Guid> { idB, idC, idA }; // the cache's order, this is the ranking

        // Rows arrive in a different (shuffled) order, a WHERE Id IN (...) makes no ordering promise.
        var rows = new Dictionary<Guid, LeaderboardRow>
        {
            [idA] = new LeaderboardRow(idA, "Alice", 1400, 60, 10, PatronTier.None, null),
            [idC] = new LeaderboardRow(idC, "Carol", 1600, 60, 10, PatronTier.None, null),
            [idB] = new LeaderboardRow(idB, "Bob", 1500, 60, 10, PatronTier.None, null),
        };

        var entries = RatingEndpoints.RankByCacheOrder(ids, rows);

        Assert.Equal(new[] { idB, idC, idA }, entries.Select(e => e.UserId));
        Assert.Equal(new[] { 1, 2, 3 }, entries.Select(e => e.Rank));
    }

    [Fact]
    public void A_cached_id_with_no_rating_row_is_skipped_and_ranks_stay_contiguous()
    {
        var idA = Guid.NewGuid();
        var idGhost = Guid.NewGuid(); // in the ZSET but no matching row, a deleted account (8a.2)
        var idB = Guid.NewGuid();
        var ids = new List<Guid> { idA, idGhost, idB };

        var rows = new Dictionary<Guid, LeaderboardRow>
        {
            [idA] = new LeaderboardRow(idA, "Alice", 1600, 60, 10, PatronTier.None, null),
            [idB] = new LeaderboardRow(idB, "Bob", 1500, 60, 10, PatronTier.None, null),
        };

        var entries = RatingEndpoints.RankByCacheOrder(ids, rows);

        Assert.Equal(new[] { idA, idB }, entries.Select(e => e.UserId));
        Assert.Equal(new[] { 1, 2 }, entries.Select(e => e.Rank)); // contiguous, no gap left by the skip
    }

    [Fact]
    public void Provisional_flag_comes_from_the_database_row()
    {
        var settled = Guid.NewGuid();
        var settling = Guid.NewGuid();
        var ids = new List<Guid> { settled, settling };

        var rows = new Dictionary<Guid, LeaderboardRow>
        {
            [settled] = new LeaderboardRow(settled, "Settled", 1600, RatingConstants.ProvisionalRdThreshold, 20, PatronTier.None, null),
            [settling] = new LeaderboardRow(settling, "Settling", 1500, RatingConstants.ProvisionalRdThreshold + 1, 3, PatronTier.None, null),
        };

        var entries = RatingEndpoints.RankByCacheOrder(ids, rows);

        Assert.False(entries.Single(e => e.UserId == settled).Provisional);
        Assert.True(entries.Single(e => e.UserId == settling).Provisional);
    }
}

/// <summary>
/// An <see cref="IRedisConnection"/> that is enabled and whose every command fails the way a managed
/// Redis fails under memory pressure or during a failover. <see cref="RedisServerException"/> is
/// neither <see cref="RedisConnectionException"/> nor <see cref="RedisTimeoutException"/>, which is
/// the entire point: it is the case the old two-type filter in <c>LeaderboardCache</c> did not
/// cover. Throwing from the <see cref="Db"/> getter rather than from a command is enough, because
/// every method in that class reaches for <c>Db</c> inside its own try.
/// </summary>
public sealed class ServerErrorRedisConnection : IRedisConnection
{
    public bool Enabled => true;
    public IDatabase Db => throw new RedisServerException("OOM command not allowed when used memory > 'maxmemory'.");
    public ISubscriber Subscriber => throw new NotSupportedException("Not used by LeaderboardCache.");
    public string Key(string suffix) => $"test:{suffix}";
    public RedisChannel Channel(string suffix) => RedisChannel.Literal(Key(suffix));
    public bool IsConnected => false;
}

/// <summary>
/// Container-backed half of the leaderboard cache tests: real Redis, real ZADD/RENAME/SET NX
/// semantics. Requires Docker; skips cleanly (via <see cref="SkippableFactAttribute"/>) when no
/// Docker daemon is reachable. See <see cref="RedisFixture"/> for the shared container and why every
/// test below uses its own key prefix.
/// </summary>
public sealed class LeaderboardCacheContainerTests : IClassFixture<RedisFixture>
{
    private readonly RedisFixture _fixture;

    public LeaderboardCacheContainerTests(RedisFixture fixture) => _fixture = fixture;

    private (RedisConnection Connection, LeaderboardCache Cache) CreateCache()
    {
        var options = new RedisOptions(
            _fixture.Container!.GetConnectionString(), $"test-{Guid.NewGuid():N}:", 30, 10, 5);
        var connection = new RedisConnection(options, new InstanceIdentity("test-instance"));
        var cache = new LeaderboardCache(connection, NullLogger<LeaderboardCache>.Instance);
        return (connection, cache);
    }

    // TopAsync's "cache cannot answer" contract has two distinct triggers: disabled (covered above,
    // no Docker needed) and enabled-but-never-warmed, which needs a real, untouched Redis to be a
    // meaningful state rather than an assumption. Grouped here, not in the pure class, for that reason.
    [SkippableFact]
    public async Task An_unwarmed_cache_returns_null_not_an_empty_ranking()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var (connection, cache) = CreateCache();

        // Enabled, reachable, real Redis, but {p}lb:built was never set, so nothing has warmed it.
        var top = await cache.TopAsync("steal1", 10, includeProvisional: true);

        Assert.Null(top);
        connection.Dispose();
    }

    [SkippableFact]
    public async Task Upsert_places_a_player_in_both_sets_when_established()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var (connection, cache) = CreateCache();
        var userId = Guid.NewGuid();

        await cache.UpsertAsync("steal1", userId, 1600, 80, DateTimeOffset.UtcNow);

        var all = await connection.Db.SortedSetScoreAsync(connection.Key("lb:steal1"), userId.ToString());
        var established = await connection.Db.SortedSetScoreAsync(connection.Key("lb:steal1:established"), userId.ToString());
        Assert.Equal(1600, all);
        Assert.Equal(1600, established);

        connection.Dispose();
    }

    [SkippableFact]
    public async Task Upsert_removes_them_from_established_when_rd_rises()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var (connection, cache) = CreateCache();
        var userId = Guid.NewGuid();

        await cache.UpsertAsync("steal1", userId, 1600, 80, DateTimeOffset.UtcNow); // established
        await cache.UpsertAsync("steal1", userId, 1600, 200, DateTimeOffset.UtcNow); // RD rose above threshold, no game required

        var established = await connection.Db.SortedSetScoreAsync(connection.Key("lb:steal1:established"), userId.ToString());
        var all = await connection.Db.SortedSetScoreAsync(connection.Key("lb:steal1"), userId.ToString());
        Assert.Null(established);
        Assert.Equal(1600, all); // still in the "everyone" set

        connection.Dispose();
    }

    // C-7-1. Deviation alone leaves a settled player on the established board for months. Recency
    // is the other way off, and it must not require another game: the sweep calls this with the
    // row's original LastPlayedAt.
    [SkippableFact]
    public async Task Upsert_removes_them_from_established_when_the_last_game_ages_out()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var (connection, cache) = CreateCache();
        var userId = Guid.NewGuid();
        var played = DateTimeOffset.UtcNow;

        await cache.UpsertAsync("steal1", userId, 1600, 80, played);
        await cache.UpsertAsync(
            "steal1", userId, 1600, 80, played - RatingConstants.LeaderboardActiveWindow - TimeSpan.FromSeconds(1));

        var established = await connection.Db.SortedSetScoreAsync(connection.Key("lb:steal1:established"), userId.ToString());
        var all = await connection.Db.SortedSetScoreAsync(connection.Key("lb:steal1"), userId.ToString());
        Assert.Null(established);
        Assert.Equal(1600, all);

        connection.Dispose();
    }

    [SkippableFact]
    public async Task Rebuild_keeps_a_departed_player_off_the_established_set()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var (connection, cache) = CreateCache();
        var active = Guid.NewGuid();
        var departed = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await cache.RebuildAsync("steal1", new LeaderboardCacheRow[]
        {
            new(active, 1500, 50, now),
            new(departed, 1800, 50, now - RatingConstants.LeaderboardActiveWindow - TimeSpan.FromDays(1)),
        });

        Assert.Equal(new[] { active }, await cache.TopAsync("steal1", 10, includeProvisional: false));
        Assert.Equal(new[] { departed, active }, await cache.TopAsync("steal1", 10, includeProvisional: true));

        connection.Dispose();
    }

    [SkippableFact]
    public async Task Top_returns_highest_rating_first()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var (connection, cache) = CreateCache();
        var low = Guid.NewGuid();
        var mid = Guid.NewGuid();
        var high = Guid.NewGuid();
        await cache.UpsertAsync("steal1", low, 1400, 50, DateTimeOffset.UtcNow);
        await cache.UpsertAsync("steal1", mid, 1500, 50, DateTimeOffset.UtcNow);
        await cache.UpsertAsync("steal1", high, 1600, 50, DateTimeOffset.UtcNow);

        await connection.Db.StringSetAsync(connection.Key("lb:steal1:built"), "test", TimeSpan.FromMinutes(5));

        var top = await cache.TopAsync("steal1", 10, includeProvisional: true);

        Assert.Equal(new[] { high, mid, low }, top);
        connection.Dispose();
    }

    [SkippableFact]
    public async Task Rebuild_is_atomic()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var (connection, cache) = CreateCache();
        // No manual marker: RebuildAsync owns the pool's warmth now, and writes it only after the
        // RENAMEs below have actually swapped the live keys.

        var oldId = Guid.NewGuid();
        await cache.RebuildAsync("steal1", new LeaderboardCacheRow[] { new(oldId, 1500.0, 50.0, DateTimeOffset.UtcNow) });
        Assert.Equal(new[] { oldId }, await cache.TopAsync("steal1", 10, includeProvisional: true));

        var newA = Guid.NewGuid();
        var newB = Guid.NewGuid();
        await cache.RebuildAsync("steal1", new LeaderboardCacheRow[]
        {
            new(newA, 1700.0, 50.0, DateTimeOffset.UtcNow),
            new(newB, 1600.0, 50.0, DateTimeOffset.UtcNow),
        });

        // The temp key never survives a rebuild, if it did, a crash mid-rebuild could leave it
        // looking like live data to the next reader.
        Assert.False(await connection.Db.KeyExistsAsync(connection.Key("lb:steal1:building")));

        var newTop = await cache.TopAsync("steal1", 10, includeProvisional: true);
        Assert.Equal(new[] { newA, newB }, newTop);
        Assert.DoesNotContain(oldId, newTop!); // the old ranking is gone, not merged with the new one

        connection.Dispose();
    }

    // LeaderboardWarmup.ExecuteAsync's uniqueness guarantee reduces to exactly this one atomic
    // primitive (SET {p}lb:warmup-lock <instanceId> NX EX 300); exercising it directly, against the
    // same Redis, from two "instances" proves the guarantee without standing up two full hosted
    // services and a Postgres instance behind them. The key is now a lock and ONLY a lock: it no
    // longer doubles as the marker that tells readers the cache can be trusted.
    [SkippableFact]
    public async Task Warmup_lock_is_acquired_by_only_one_of_two_instances()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var prefix = $"test-{Guid.NewGuid():N}:";
        var options = new RedisOptions(_fixture.Container!.GetConnectionString(), prefix, 30, 10, 5);
        using var connectionA = new RedisConnection(options, new InstanceIdentity("instance-a"));
        using var connectionB = new RedisConnection(options, new InstanceIdentity("instance-b"));

        var acquiredA = await connectionA.Db.StringSetAsync(
            connectionA.Key(LeaderboardWarmup.WarmupLockKey), "instance-a", LeaderboardWarmup.LockTtl, When.NotExists);
        var acquiredB = await connectionB.Db.StringSetAsync(
            connectionB.Key(LeaderboardWarmup.WarmupLockKey), "instance-b", LeaderboardWarmup.LockTtl, When.NotExists);

        Assert.True(acquiredA);
        Assert.False(acquiredB);

        var owner = await connectionA.Db.StringGetAsync(connectionA.Key(LeaderboardWarmup.WarmupLockKey));
        Assert.Equal("instance-a", (string?)owner);

        // The lock does not make anything warm. Holding it while the Postgres scan behind it fails
        // is exactly the state that used to serve an empty leaderboard for an hour.
        var cache = new LeaderboardCache(connectionA, NullLogger<LeaderboardCache>.Instance);
        Assert.False(await cache.IsWarmAsync("steal1"));
    }

    // C-7-2, the positive half: warmth is created by the rebuild, after it, and it expires.
    [SkippableFact]
    public async Task A_successful_rebuild_is_what_makes_a_pool_warm()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var (connection, cache) = CreateCache();

        Assert.False(await cache.IsWarmAsync("steal1"));

        Assert.True(await cache.RebuildAsync("steal1", new LeaderboardCacheRow[] { new(Guid.NewGuid(), 1500.0, 50.0, DateTimeOffset.UtcNow) }));

        Assert.True(await cache.IsWarmAsync("steal1"));
        var ttl = await connection.Db.KeyTimeToLiveAsync(connection.Key("lb:steal1:built"));
        Assert.NotNull(ttl); // never a marker without an expiry: a stale ranking must not be trusted forever
        Assert.True(ttl <= LeaderboardCache.WarmTtl);

        connection.Dispose();
    }

    // C-7-3. The sharpest instance of the cache-versus-Postgres inconsistency: the warm-up iterated
    // OfficialPools, so lb:bot:* was never rebuilt, while the single global marker it set claimed
    // every pool was warm. GET /api/v1/leaderboards/bot/{pool} then took the cache-hit branch,
    // received an empty id list, and served [] as a real ranking with the true bot leaderboard
    // sitting in the Postgres branch immediately below. Warming one pool must not vouch for another.
    [SkippableFact]
    public async Task Warming_a_human_pool_does_not_make_its_bot_pool_read_as_warm()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var (connection, cache) = CreateCache();
        var botPool = RatingConstants.ToBotPool("steal1");

        await cache.RebuildAsync("steal1", new LeaderboardCacheRow[] { new(Guid.NewGuid(), 1500.0, 50.0, DateTimeOffset.UtcNow) });

        Assert.True(await cache.IsWarmAsync("steal1"));
        Assert.False(await cache.IsWarmAsync(botPool));

        // Null, not an empty array. Empty would be served to a bot developer as a real ranking;
        // null is what makes RatingEndpoints/PublicApiEndpoints fall through to Postgres.
        Assert.Null(await cache.TopAsync(botPool, 10, includeProvisional: true));

        // ...and once the bot pool is itself rebuilt, it answers.
        var bot = Guid.NewGuid();
        Assert.True(await cache.RebuildAsync(botPool, new LeaderboardCacheRow[] { new(bot, 1700.0, 50.0, DateTimeOffset.UtcNow) }));
        Assert.Equal(new[] { bot }, await cache.TopAsync(botPool, 10, includeProvisional: true));

        connection.Dispose();
    }

    // C-7-4's last detail: the Guid.Parse in TopAsync sat inside no exception filter at all, so a
    // ZSET member that is not a Guid (a hand-edited key, a future schema change, a shared Redis
    // whose prefix collided) was an unhandled FormatException on a public, anonymous endpoint.
    [SkippableFact]
    public async Task A_non_guid_member_in_the_zset_falls_back_instead_of_500ing()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var (connection, cache) = CreateCache();
        await cache.RebuildAsync("steal1", new LeaderboardCacheRow[] { new(Guid.NewGuid(), 1500.0, 50.0, DateTimeOffset.UtcNow) });
        await connection.Db.SortedSetAddAsync(connection.Key("lb:steal1"), "not-a-guid", 9999);

        Assert.Null(await cache.TopAsync("steal1", 10, includeProvisional: true));

        connection.Dispose();
    }
}

/// <summary>
/// Proves the Definition of Done's strongest claim by actually running both response paths, not by
/// inspecting the code: with identical Postgres data, the Redis-cache-backed response and the
/// Postgres-only response from <c>GET /api/leaderboards/{pool}</c> are byte-for-byte the same JSON.
/// Requires Docker (both Postgres and Redis); skips cleanly with neither.
/// </summary>
public sealed class LeaderboardEndpointParityTests : IClassFixture<RedisFixture>, IAsyncLifetime
{
    private readonly RedisFixture _redis;
    private PostgreSqlContainer? _pg;

    public LeaderboardEndpointParityTests(RedisFixture redis) => _redis = redis;

    public async Task InitializeAsync()
    {
        if (!DockerAvailability.IsAvailable) return;
        _pg = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
        await _pg.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_pg is not null) await _pg.DisposeAsync();
    }

    [SkippableFact]
    public async Task Cache_backed_and_postgres_backed_responses_are_byte_identical()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        const string pool = "steal1";
        var connectionString = _pg!.GetConnectionString();

        // Seed directly against Postgres (same pattern as DatabaseSmokeTests), five distinct
        // ratings, all comfortably established, so ordering is unambiguous and the comparison below
        // is not vacuously "both empty".
        var dbOptions = new DbContextOptionsBuilder<KiriChessDbContext>().UseNpgsql(connectionString).Options;
        await using (var db = new KiriChessDbContext(dbOptions))
        {
            await db.Database.MigrateAsync();
            for (var i = 0; i < 5; i++)
            {
                var user = new ApplicationUser
                {
                    UserName = $"parity{i}@example.com",
                    Email = $"parity{i}@example.com",
                    DisplayName = $"Parity Player {i}",
                };
                db.Users.Add(user);
                db.Ratings.Add(new Rating
                {
                    UserId = user.Id,
                    Pool = pool,
                    Value = 1500 + i * 37, // distinct values: no ties to disambiguate
                    RatingDeviation = 60,   // comfortably under the provisional threshold
                    Volatility = 0.06,
                    GamesPlayed = 10,
                });
            }
            await db.SaveChangesAsync();
        }

        // Path 1: Redis left unconfigured, today's exact Postgres query.
        await using var postgresOnlyFactory = BuildFactory(connectionString, redisConfiguration: null);
        using var postgresClient = postgresOnlyFactory.CreateClient();
        var postgresResponse = await postgresClient.GetAsync($"/api/leaderboards/{pool}?limit=10");
        postgresResponse.EnsureSuccessStatusCode();
        var postgresJson = await postgresResponse.Content.ReadAsStringAsync();

        // Path 2: Redis enabled, cache deterministically warmed from the same rows (rather than
        // racing the hosted LeaderboardWarmup, which this test does not need to also prove).
        await using var cachedFactory = BuildFactory(connectionString, _redis.Container!.GetConnectionString());

        // Guard the whole point of this test. Redis reaching the host through configuration alone is
        // not enough to enable it, Program.cs reads RedisOptions before Build(), and a
        // WebApplicationFactory's in-memory configuration lands after that, so BuildFactory also
        // injects a live IRedisConnection through ConfigureTestServices. Without this assertion the
        // test silently degrades into comparing two Postgres responses to each other, which is
        // exactly what it did until 2026-08-06 and which passes just as green as the real thing.
        Assert.True(
            cachedFactory.Services.GetRequiredService<IRedisConnection>().Enabled,
            "The cache-backed host has Redis disabled, so this test is comparing Postgres to Postgres.");

        using (var scope = cachedFactory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
            var cache = scope.ServiceProvider.GetRequiredService<LeaderboardCache>();
            var rows = await db.Ratings.Where(r => r.Pool == pool)
                .Select(r => new { r.UserId, r.Value, r.RatingDeviation, r.LastPlayedAt })
                .ToListAsync();
            await cache.RebuildAsync(pool,
                rows.Select(r => new LeaderboardCacheRow(r.UserId, r.Value, r.RatingDeviation, r.LastPlayedAt)).ToArray());
        }

        using var cachedClient = cachedFactory.CreateClient();
        var cachedResponse = await cachedClient.GetAsync($"/api/leaderboards/{pool}?limit=10");
        cachedResponse.EnsureSuccessStatusCode();
        var cachedJson = await cachedResponse.Content.ReadAsStringAsync();

        Assert.Equal(postgresJson, cachedJson);

        var entries = JsonSerializer.Deserialize<ParityEntry[]>(postgresJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(entries);
        Assert.Equal(5, entries!.Length);
        Assert.Equal(Enumerable.Range(1, 5), entries.Select(e => e.Rank));
    }

    /// <summary>
    /// C-7-3 end to end through the real hosted service, because the pool list it iterates is the
    /// actual defect and a cache-level test cannot see it. The warm-up used to loop
    /// <c>RatingConstants.OfficialPools</c>, which excludes the <c>bot:</c> namespace even though
    /// that namespace receives rated games and has its own public endpoint. It must loop
    /// <c>MatchmakingPools</c>.
    /// </summary>
    [SkippableFact]
    public async Task The_warmup_warms_bot_pools_not_only_human_pools()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var connectionString = _pg!.GetConnectionString();
        await using var factory = BuildFactory(connectionString, _redis.Container!.GetConnectionString());
        var connection = factory.Services.GetRequiredService<IRedisConnection>();
        Assert.True(connection.Enabled, "Redis is disabled, so this test would prove nothing.");

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
            await db.Database.MigrateAsync();
            var user = new ApplicationUser
            {
                UserName = "botwarm@example.com",
                Email = "botwarm@example.com",
                DisplayName = "Bot Warm Player",
            };
            db.Users.Add(user);
            db.Ratings.Add(new Rating
            {
                UserId = user.Id,
                Pool = RatingConstants.ToBotPool("steal1"),
                Value = 1700,
                RatingDeviation = 60,
                Volatility = 0.06,
                GamesPlayed = 12,
            });
            await db.SaveChangesAsync();
        }

        var cache = factory.Services.GetRequiredService<LeaderboardCache>();
        var warmup = new LeaderboardWarmup(
            connection,
            new InstanceIdentity($"warmup-{Guid.NewGuid():N}"),
            cache,
            factory.Services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<LeaderboardWarmup>.Instance);

        // StartAsync does not await ExecuteAsync (that is the point of a BackgroundService), so wait
        // on a task rather than sleeping and hoping. FirstPassCompleted, not ExecuteTask: since
        // C-1-3 the service re-warms on a timer, so ExecuteTask does not finish until the host stops.
        await warmup.StartAsync(CancellationToken.None);
        await warmup.FirstPassCompleted;
        await warmup.StopAsync(CancellationToken.None); // the re-warm loop would otherwise outlive the test

        foreach (var pool in RatingConstants.MatchmakingPools)
            Assert.True(await cache.IsWarmAsync(pool), $"Pool '{pool}' was left cold by the warm-up.");

        Assert.NotNull(await cache.TopAsync(RatingConstants.ToBotPool("steal1"), 10, includeProvisional: true));
    }

    /// <summary>
    /// C-1-3. <see cref="LeaderboardCache.WarmTtl"/> gives each pool's warm marker one hour, on the
    /// stated argument that a marker which never expires lets a long-lived process serve a ranking
    /// that has drifted from Postgres. Nothing rewrote that marker: the warm-up ran once at boot and
    /// returned, so an hour later every pool read cold and stayed cold for the life of the process,
    /// with the ZSETs still being written on every rating change and never read again. The service
    /// now repeats on <see cref="LeaderboardWarmup.RewarmInterval"/>.
    ///
    /// The two durations are scaled down through the internal constructor rather than waited out;
    /// the marker's expiry is simulated with a DEL, which is what a TTL lapse leaves behind.
    /// </summary>
    [SkippableFact]
    public async Task The_warmup_re_warms_a_pool_whose_marker_has_lapsed()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var connectionString = _pg!.GetConnectionString();
        await using var factory = BuildFactory(connectionString, _redis.Container!.GetConnectionString());
        var connection = factory.Services.GetRequiredService<IRedisConnection>();
        Assert.True(connection.Enabled, "Redis is disabled, so this test would prove nothing.");

        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<KiriChessDbContext>().Database.MigrateAsync();

        var pool = RatingConstants.MatchmakingPools[0];
        var cache = factory.Services.GetRequiredService<LeaderboardCache>();
        var warmup = new LeaderboardWarmup(
            connection,
            new InstanceIdentity($"warmup-{Guid.NewGuid():N}"),
            cache,
            factory.Services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<LeaderboardWarmup>.Instance,
            rewarmInterval: TimeSpan.FromMilliseconds(250),
            // Shorter than the interval, exactly as the production pair is: the lock has to be free
            // again by the next tick or the re-warm this test is about would never run.
            lockTtl: TimeSpan.FromMilliseconds(100));

        try
        {
            await warmup.StartAsync(CancellationToken.None);
            await warmup.FirstPassCompleted;
            Assert.True(await cache.IsWarmAsync(pool), "The boot pass left the pool cold, so nothing below means anything.");

            // What the one-hour TTL does on its own, an hour in.
            await connection.Db.KeyDeleteAsync(connection.Key($"lb:{pool}:built"));
            Assert.False(await cache.IsWarmAsync(pool));

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            var rewarmed = false;
            while (!rewarmed && DateTime.UtcNow < deadline)
            {
                rewarmed = await cache.IsWarmAsync(pool);
                if (!rewarmed) await Task.Delay(100);
            }

            Assert.True(rewarmed,
                $"Pool '{pool}' was never re-warmed: the cache goes cold {LeaderboardCache.WarmTtl} after boot and never recovers.");
        }
        finally
        {
            await warmup.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// C-7-2, and the reason it needs the real hosted service rather than a cache-level test: the
    /// bug was an ORDERING one between two different components. The marker that tells readers "this
    /// cache can be trusted" was written by the warm-up BEFORE the Postgres scan it certified, so a
    /// scan that threw (Neon scale-to-zero at boot is the named case in DEPLOYMENT.md) left the
    /// marker standing for its full hour: IsWarmAsync answered true, TopAsync read ZSETs nobody had
    /// built, and returned an empty list. Empty is not the fallback signal, only null is, so an
    /// empty board was served to readers as a real ranking on a site that has players.
    ///
    /// Here the scan is made to fail by pointing the host at a Postgres that is not listening.
    /// </summary>
    [SkippableFact]
    public async Task A_warmup_whose_scan_fails_leaves_reads_falling_back_to_postgres()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        // A port nothing is listening on: every query through this context throws.
        const string deadPostgres = "Host=127.0.0.1;Port=1;Database=nope;Username=nope;Password=nope;Timeout=2;Command Timeout=2";
        await using var factory = BuildFactory(deadPostgres, _redis.Container!.GetConnectionString(), runMigrationsOnStartup: false);
        var connection = factory.Services.GetRequiredService<IRedisConnection>();
        Assert.True(connection.Enabled, "Redis is disabled, so this test would prove nothing.");

        var cache = factory.Services.GetRequiredService<LeaderboardCache>();
        var warmup = new LeaderboardWarmup(
            connection,
            new InstanceIdentity($"warmup-{Guid.NewGuid():N}"),
            cache,
            factory.Services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<LeaderboardWarmup>.Instance);

        await warmup.StartAsync(CancellationToken.None);
        await warmup.FirstPassCompleted; // the warm-up swallows its own failure; that part is by design
        await warmup.StopAsync(CancellationToken.None); // the re-warm loop would otherwise outlive the test

        foreach (var pool in RatingConstants.MatchmakingPools)
        {
            Assert.False(await cache.IsWarmAsync(pool),
                $"Pool '{pool}' reads as warm after a warm-up that never built anything.");

            // Null, never an empty array. Null is the only value RatingEndpoints and
            // PublicApiEndpoints treat as "ask Postgres".
            Assert.Null(await cache.TopAsync(pool, 10, includeProvisional: true));
        }
    }

    private static WebApplicationFactory<Program> BuildFactory(
        string postgresConnectionString, string? redisConfiguration, bool runMigrationsOnStartup = true)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                var settings = new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Postgres"] = postgresConnectionString,
                    // Development defaults this to true, which would migrate at boot and make a
                    // deliberately unreachable Postgres fail the host build instead of the one query
                    // the test is about.
                    ["RunMigrationsOnStartup"] = runMigrationsOnStartup ? "true" : "false",
                    ["Jwt:Key"] = "e2e-test-signing-key-at-least-32-bytes-long",
                    ["Jwt:Issuer"] = "kirichess",
                    ["Jwt:Audience"] = "kirichess",
                    ["Jwt:ExpiryMinutes"] = "60",
                };
                if (redisConfiguration is not null)
                {
                    settings["Redis:Configuration"] = redisConfiguration;
                    settings["Redis:KeyPrefix"] = $"test-{Guid.NewGuid():N}:";
                }
                cfg.AddInMemoryCollection(settings);
            });

            // Configuration alone cannot turn Redis on in a WebApplicationFactory host: Program.cs
            // reads RedisOptions eagerly, before Build(), because the SignalR backplane and the Data
            // Protection key ring are service registrations with no post-Build() hook, and a
            // factory's ConfigureAppConfiguration lands after that read. So the options above are
            // necessary but not sufficient, and the live connection has to be injected here.
            //
            // What that buys is everything resolved from DI, LeaderboardCache included, which is all
            // this test needs. What it does NOT buy is the backplane or the key ring, both of which
            // were registered (or not) before this delegate ran. Do not read this pattern as a way to
            // test those; see TwoInstanceGameTests for what that costs.
            if (redisConfiguration is not null)
                builder.ConfigureTestServices(services =>
                {
                    var identity = new InstanceIdentity($"test-{Guid.NewGuid():N}");
                    var options = new RedisOptions(redisConfiguration, $"test-{Guid.NewGuid():N}:", 30, 10, 5);
                    services.AddSingleton(identity);
                    services.AddSingleton(options);
                    services.AddSingleton<IRedisConnection>(new RedisConnection(options, identity));
                });
        });

    private sealed record ParityEntry(int Rank, Guid UserId, string DisplayName, double Rating, double RatingDeviation, int GamesPlayed, bool Provisional);
}
