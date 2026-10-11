using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.RateLimiting;
using KiriChess.Server.Infrastructure;
using KiriChess.Server.Infrastructure.Redis;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using Xunit;

namespace KiriChess.Server.Tests;

/// <summary>
/// Covers the Phase 6e.1 rate-limiting contract. Boots the app in a production-like configuration
/// with an unreachable database and no boot-time migration, so it never needs Docker (same shape as
/// <see cref="MetricsEndpointTests"/> and <see cref="HealthEndpointTests"/>), every assertion here
/// is about the limiter, which rejects long before a handler would touch Postgres.
///
/// Thresholds are overridden per test rather than exercised at their production defaults: a test
/// that had to send 31 requests to prove the auth policy works would be slow and would silently stop
/// testing anything the day someone loosens the default.
/// </summary>
public sealed class RateLimitingTests
{
    private static WebApplicationFactory<Program> BuildFactory(params (string Key, string? Value)[] overrides)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                var settings = new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Postgres"] = "Host=127.0.0.1;Port=1;Database=nope;Username=nope;Password=nope;Timeout=1;Command Timeout=1",
                    ["Jwt:Key"] = "rate-limit-test-signing-key-at-least-32-bytes",
                    ["RunMigrationsOnStartup"] = "false",
                    ["Cors:AllowedOrigins:0"] = "https://example.test",
                    // The dead host above is the point - these tests only care what the limiter does,
                    // and every request is expected to 500 on its way past it. Without this, Program.cs's
                    // EnableRetryOnFailure treats "connection refused" as transient and spends ~30s
                    // retrying each one, so a flood of 8 takes four minutes and the one-minute fixed
                    // window resets underneath it - the limiter never sees enough requests to reject any.
                    ["Database:MaxRetryCount"] = "0"
                };
                foreach (var (key, value) in overrides)
                    settings[key] = value;
                cfg.AddInMemoryCollection(settings);
            });
        });
    }

    /// <summary>Fires <paramref name="count"/> logins with a deliberately invalid body; returns every status.</summary>
    private static async Task<List<HttpStatusCode>> FloodLoginAsync(HttpClient client, int count)
    {
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < count; i++)
        {
            var response = await client.PostAsJsonAsync("/api/auth/login", new { Email = "nobody@example.test", Password = "wrong-password" });
            statuses.Add(response.StatusCode);
        }
        return statuses;
    }

    [Fact]
    public async Task Auth_policy_rejects_a_flood_with_429()
    {
        using var factory = BuildFactory(("RateLimiting:Auth:PermitsPerMinute", "3"));
        using var client = factory.CreateClient();

        var statuses = await FloodLoginAsync(client, 8);

        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
        // The first few must have been served (whatever they answered), or the limiter is rejecting
        // everything rather than everything past the permit count.
        Assert.DoesNotContain(HttpStatusCode.TooManyRequests, statuses.Take(3));
    }

    [Fact]
    public async Task Rejected_response_body_carries_the_error_field_the_frontend_reads()
    {
        using var factory = BuildFactory(("RateLimiting:Auth:PermitsPerMinute", "1"));
        using var client = factory.CreateClient();

        await FloodLoginAsync(client, 1);
        var rejected = await client.PostAsJsonAsync("/api/auth/login", new { Email = "nobody@example.test", Password = "wrong-password" });

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        // ui/lib/api.ts's extractErrorMessage reads `error` first; this is that contract.
        using var document = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync());
        Assert.True(document.RootElement.TryGetProperty("error", out var error));
        Assert.False(string.IsNullOrWhiteSpace(error.GetString()));
    }

    [Fact]
    public async Task Rejected_response_carries_a_retry_after_header()
    {
        using var factory = BuildFactory(("RateLimiting:Auth:PermitsPerMinute", "1"));
        using var client = factory.CreateClient();

        await FloodLoginAsync(client, 1);
        var rejected = await client.PostAsJsonAsync("/api/auth/login", new { Email = "nobody@example.test", Password = "wrong-password" });

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.True(rejected.Headers.Contains("Retry-After"));
    }

    [Fact]
    public async Task Disabled_configuration_never_rejects()
    {
        using var factory = BuildFactory(
            ("RateLimiting:Enabled", "false"),
            ("RateLimiting:Auth:PermitsPerMinute", "1"));
        using var client = factory.CreateClient();

        var statuses = await FloodLoginAsync(client, 8);

        Assert.DoesNotContain(HttpStatusCode.TooManyRequests, statuses);
    }

    [Fact]
    public async Task Health_endpoint_is_never_throttled()
    {
        // Global backstop of one request per minute: without the exemption the second call would 429,
        // and a throttled liveness probe means an orchestrator kills a perfectly healthy container.
        using var factory = BuildFactory(("RateLimiting:Global:PermitsPerMinute", "1"));
        using var client = factory.CreateClient();

        var first = await client.GetAsync("/health");
        var second = await client.GetAsync("/health");
        var third = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(HttpStatusCode.OK, third.StatusCode);
    }

    // --- Phase 8.4 (Task E): distributed rate limiting ---

    [Fact]
    public async Task Existing_in_memory_limiting_is_unchanged_without_redis()
    {
        // No Redis:Configuration override, so IRedisConnection.Enabled is false and every policy in
        // AddKiriChessRateLimiting must fall to the in-memory branch, the same behaviour the flood
        // test above already pins.
        using var factory = BuildFactory(("RateLimiting:Auth:PermitsPerMinute", "3"));
        using var client = factory.CreateClient();

        var statuses = await FloodLoginAsync(client, 8);

        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
        // Stronger than "the limiter still works": with Redis disabled, RedisRateLimitPartition is
        // never reached, so RedisConnection's lazy multiplexer is never created and IsConnected stays
        // false for the life of the app, proof that no Redis call was ever attempted, not just that
        // none happened to fail.
        var redis = factory.Services.GetRequiredService<IRedisConnection>();
        Assert.False(redis.IsConnected);
    }

    [Fact]
    public void The_hub_filter_is_untouched_by_this_phase()
    {
        // Guard against a future reader "finishing the job": HubRateLimitFilter is explicitly out of
        // scope for Task E (see its class comment) because a connection id already partitions
        // correctly at any instance count. This re-runs the exact bucket arithmetic the pre-8.4 tests
        // above pin, so a later change that wires Redis into TryAcquire fails this immediately.
        using var filter = BuildHubFilter(
            ("RateLimiting:Hub:QueueBurst", "2"),
            ("RateLimiting:Hub:QueueTokensPerSecond", "1"));

        Assert.True(filter.TryAcquire("conn-1", "JoinQueue"));
        Assert.True(filter.TryAcquire("conn-1", "JoinQueue"));
        Assert.False(filter.TryAcquire("conn-1", "JoinQueue"));
    }

    [Fact]
    public async Task A_redis_failure_permits_the_request()
    {
        // The most important test in this task. ThrowingRedisConnection stands in for a Redis outage:
        // Enabled is true (Redis was configured, this is a live-outage scenario, not a
        // never-configured one), but Db throws the moment anything touches it, which is exactly where
        // AcquireAsyncCore's script call would throw against a real, unreachable Redis. The governing
        // rule (RateLimitingSetup's header, inherited verbatim by RedisRateLimiter's class comment): a
        // rejected move can lose a game, so this must acquire, not reject.
        var connection = new ThrowingRedisConnection();
        var limiter = RedisRateLimiter.FixedWindow(connection, "rl:test:outage", limit: 1, TimeSpan.FromMinutes(1), NullLogger.Instance);

        using var lease = await limiter.AcquireAsync();

        Assert.True(lease.IsAcquired);
    }

    [Fact]
    public async Task A_redis_failure_permits_the_request_for_the_token_bucket_too()
    {
        // Same contract, the other algorithm: GameMove is the loosest, most latency-sensitive policy
        // in the file, so its fail-open path matters at least as much as the fixed-window one above.
        var connection = new ThrowingRedisConnection();
        var limiter = RedisRateLimiter.TokenBucket(connection, "rlt:test:outage", capacity: 1, tokensPerSecond: 1, NullLogger.Instance);

        using var lease = await limiter.AcquireAsync();

        Assert.True(lease.IsAcquired);
    }

    [Fact]
    public async Task Retry_after_metadata_is_populated_on_rejection()
    {
        // Guards the Step 2 refactor of AddKiriChessRateLimiting (moving from a plain
        // Action<RateLimiterOptions> to AddOptions<RateLimiterOptions>().Configure<IRedisConnection>())
        // rather than the Redis path specifically: with Redis disabled this still exercises the exact
        // same OnRejected/MetadataName.RetryAfter wiring the distributed path also relies on. The
        // distributed path's own RejectedLease carries the same metadata name against a real rejection
        // in RedisRateLimiterContainerTests.Rejected_lease_carries_retry_after_from_a_real_rejection.
        using var factory = BuildFactory(("RateLimiting:Auth:PermitsPerMinute", "1"));
        using var client = factory.CreateClient();

        await FloodLoginAsync(client, 1);
        var rejected = await client.PostAsJsonAsync("/api/auth/login", new { Email = "nobody@example.test", Password = "wrong-password" });

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.True(rejected.Headers.Contains("Retry-After"));
    }

    /// <summary>
    /// Stands in for "Redis is configured but unreachable": <see cref="Enabled"/> is true (unlike the
    /// disabled-Redis path, this is a live-outage scenario), but every member that would touch the
    /// network throws. <see cref="Db"/> is enough to exercise <c>RedisRateLimiter.AcquireAsyncCore</c>'s
    /// catch block, a real script-evaluate exception is caught in exactly the same place, one line
    /// later, and <c>IDatabase</c> is StackExchange's interface, not ours, so faking every one of its
    /// members just to prove one throws would test the fake, not the limiter.
    /// </summary>
    private sealed class ThrowingRedisConnection : IRedisConnection
    {
        public bool Enabled => true;
        public IDatabase Db => throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, "simulated Redis outage for a test");
        public ISubscriber Subscriber => throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, "simulated Redis outage for a test");
        public string Key(string suffix) => $"test:{suffix}";
        public RedisChannel Channel(string suffix) => RedisChannel.Literal(Key(suffix));
        public bool IsConnected => false;
    }

    // --- Hub filter (no host: the bucket arithmetic is the part worth testing) ---

    private sealed class DummyMeterFactory : IMeterFactory
    {
        private readonly List<Meter> _meters = new();

        public Meter Create(MeterOptions options)
        {
            var meter = new Meter(options);
            _meters.Add(meter);
            return meter;
        }

        public void Dispose()
        {
            foreach (var meter in _meters) meter.Dispose();
        }
    }

    private static HubRateLimitFilter BuildHubFilter(params (string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => (string?)s.Value))
            .Build();
        return new HubRateLimitFilter(configuration, new KiriChessMetrics(new DummyMeterFactory()));
    }

    [Fact]
    public void Hub_queue_bucket_rejects_churn_past_its_burst()
    {
        using var filter = BuildHubFilter(
            ("RateLimiting:Hub:QueueBurst", "3"),
            ("RateLimiting:Hub:QueueTokensPerSecond", "1"));

        var results = Enumerable.Range(0, 5).Select(_ => filter.TryAcquire("conn-1", "JoinQueue")).ToList();

        Assert.Equal(new[] { true, true, true, false, false }, results);
    }

    [Fact]
    public void Hub_move_bucket_is_far_looser_than_the_queue_bucket()
    {
        // The governing rule from RateLimitingSetup's header: a rejected move can cost a game, so the
        // move ceiling must sit far above any human or premove cadence. 40 moves fired back to back
        // is already implausible for a human; the default burst must still absorb it.
        using var filter = BuildHubFilter();

        var accepted = Enumerable.Range(0, 40).Count(_ => filter.TryAcquire("conn-1", "SubmitMove"));

        Assert.Equal(40, accepted);
    }

    [Fact]
    public void Hub_buckets_are_per_connection_not_global()
    {
        // One flooding connection must never throttle a different player's moves.
        using var filter = BuildHubFilter(
            ("RateLimiting:Hub:QueueBurst", "2"),
            ("RateLimiting:Hub:QueueTokensPerSecond", "1"));

        Assert.True(filter.TryAcquire("conn-1", "JoinQueue"));
        Assert.True(filter.TryAcquire("conn-1", "JoinQueue"));
        Assert.False(filter.TryAcquire("conn-1", "JoinQueue"));

        Assert.True(filter.TryAcquire("conn-2", "JoinQueue"));
    }

    [Fact]
    public void Hub_method_classes_have_independent_buckets()
    {
        // Exhausting the queue bucket must not spend the move bucket, they are different limits for
        // a reason, and sharing one would let queue churn cost somebody a game.
        using var filter = BuildHubFilter(
            ("RateLimiting:Hub:QueueBurst", "1"),
            ("RateLimiting:Hub:QueueTokensPerSecond", "1"));

        Assert.True(filter.TryAcquire("conn-1", "JoinQueue"));
        Assert.False(filter.TryAcquire("conn-1", "JoinQueue"));

        Assert.True(filter.TryAcquire("conn-1", "SubmitMove"));
    }

    [Fact]
    public void Hub_filter_disabled_by_configuration_still_acquires()
    {
        // TryAcquire itself is unconditional, the Enabled gate lives in InvokeMethodAsync, so the
        // bucket state stays consistent whether or not the gate is on. This pins that split.
        using var filter = BuildHubFilter(
            ("RateLimiting:Enabled", "false"),
            ("RateLimiting:Hub:QueueBurst", "1"),
            ("RateLimiting:Hub:QueueTokensPerSecond", "1"));

        Assert.True(filter.TryAcquire("conn-1", "JoinQueue"));
        Assert.False(filter.TryAcquire("conn-1", "JoinQueue"));
    }

    [Fact]
    public async Task Donations_policy_rejects_a_flood_with_429()
    {
        // $1-4: Auth then RateLimiter then Authorization, so an unauthenticated mint still spends
        // the donations permit. The first call is 401; the second is 429 once that permit is gone.
        using var factory = BuildFactory(("RateLimiting:Donations:PermitsPerHour", "1"));
        using var client = factory.CreateClient();

        var first = await client.PostAsync("/api/donations/reference", null);
        var second = await client.PostAsync("/api/donations/reference", null);

        Assert.Equal(HttpStatusCode.Unauthorized, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
    }

    [Fact]
    public async Task Exhausting_AccountData_does_not_throttle_donation_mint()
    {
        using var factory = BuildFactory(
            ("RateLimiting:AccountData:PermitsPerHour", "1"),
            ("RateLimiting:Donations:PermitsPerHour", "100"));
        using var client = factory.CreateClient();

        var firstExport = await client.GetAsync("/api/account/export");
        var secondExport = await client.GetAsync("/api/account/export");
        var mint = await client.PostAsync("/api/donations/reference", null);

        Assert.Equal(HttpStatusCode.Unauthorized, firstExport.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, secondExport.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, mint.StatusCode);
    }

    [Fact]
    public async Task AccountManage_policy_rejects_a_flood_with_429()
    {
        // Same Auth→RateLimiter→Authorization order as Donations: the first unauthenticated call
        // still spends a permit; the second is 429 once that permit is gone.
        using var factory = BuildFactory(("RateLimiting:AccountManage:PermitsPerMinute", "1"));
        using var client = factory.CreateClient();

        var first = await client.GetAsync("/api/account/notifications");
        var second = await client.GetAsync("/api/account/notifications");

        Assert.Equal(HttpStatusCode.Unauthorized, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
    }

    [Fact]
    public async Task Exhausting_AccountManage_does_not_throttle_export()
    {
        // The bug this pins: loading /profile (notifications + sessions + email) used to share the
        // five-per-hour export budget, so a few refreshes made "Download your data" return 429.
        using var factory = BuildFactory(
            ("RateLimiting:AccountManage:PermitsPerMinute", "1"),
            ("RateLimiting:AccountData:PermitsPerHour", "100"));
        using var client = factory.CreateClient();

        var firstPrefs = await client.GetAsync("/api/account/notifications");
        var secondPrefs = await client.GetAsync("/api/account/notifications");
        var export = await client.GetAsync("/api/account/export");

        Assert.Equal(HttpStatusCode.Unauthorized, firstPrefs.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, secondPrefs.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, export.StatusCode);
    }

    [Fact]
    public async Task Exhausting_AccountData_does_not_throttle_notification_prefs()
    {
        using var factory = BuildFactory(
            ("RateLimiting:AccountData:PermitsPerHour", "1"),
            ("RateLimiting:AccountManage:PermitsPerMinute", "100"));
        using var client = factory.CreateClient();

        var firstExport = await client.GetAsync("/api/account/export");
        var secondExport = await client.GetAsync("/api/account/export");
        var prefs = await client.GetAsync("/api/account/notifications");

        Assert.Equal(HttpStatusCode.Unauthorized, firstExport.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, secondExport.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, prefs.StatusCode);
    }
}

/// <summary>
/// Container-backed half of the Phase 8.4 (Task E) coverage: real Redis, real script execution, real
/// TTL expiry. Requires Docker; skips cleanly (via <see cref="SkippableFactAttribute"/>) when no
/// Docker daemon is reachable, mirrors <see cref="RedisFoundationContainerTests"/> exactly, including
/// the unique key per test (a shared <see cref="RedisFixture"/> container backs every test in the
/// class, so key collisions would otherwise make tests interfere with each other).
/// </summary>
public sealed class RedisRateLimiterContainerTests : IClassFixture<RedisFixture>
{
    private readonly RedisFixture _fixture;

    public RedisRateLimiterContainerTests(RedisFixture fixture) => _fixture = fixture;

    private RedisConnection CreateConnection() => new(
        new RedisOptions(
            _fixture.Container!.GetConnectionString(),
            $"test-{Guid.NewGuid():N}:",
            InstanceTtlSeconds: 30,
            HeartbeatSeconds: 10,
            CommandTimeoutSeconds: 5),
        new InstanceIdentity("test-instance"));

    [SkippableFact]
    public async Task Two_instances_share_one_fixed_window()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        using var connection = CreateConnection();
        var key = $"rl:test-policy:{Guid.NewGuid():N}";
        // Two separate RedisRateLimiter objects, standing in for two server instances, sharing only
        // the Redis connection and the partition key, no state passes between them in process. If
        // the combined count did not hit the limit, the counters would not really be shared.
        var limiterA = RedisRateLimiter.FixedWindow(connection, key, limit: 3, TimeSpan.FromMinutes(1), NullLogger.Instance);
        var limiterB = RedisRateLimiter.FixedWindow(connection, key, limit: 3, TimeSpan.FromMinutes(1), NullLogger.Instance);

        var results = new[]
        {
            (await limiterA.AcquireAsync()).IsAcquired,
            (await limiterB.AcquireAsync()).IsAcquired,
            (await limiterA.AcquireAsync()).IsAcquired,
            (await limiterB.AcquireAsync()).IsAcquired // 4th request against a limit of 3
        };

        Assert.Equal([true, true, true, false], results);
    }

    [SkippableFact]
    public async Task The_window_resets_after_it_expires()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        using var connection = CreateConnection();
        var key = $"rl:test-policy:{Guid.NewGuid():N}";
        var limiter = RedisRateLimiter.FixedWindow(connection, key, limit: 1, TimeSpan.FromSeconds(1), NullLogger.Instance);

        Assert.True((await limiter.AcquireAsync()).IsAcquired);
        Assert.False((await limiter.AcquireAsync()).IsAcquired);

        await Task.Delay(TimeSpan.FromSeconds(1.5));

        Assert.True((await limiter.AcquireAsync()).IsAcquired);
    }

    [SkippableFact]
    public async Task The_token_bucket_refills_over_time()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        using var connection = CreateConnection();
        var key = $"rlt:test-policy:{Guid.NewGuid():N}";
        // Capacity 1, 2 tokens/sec: half a second is enough to refill one whole token.
        var limiter = RedisRateLimiter.TokenBucket(connection, key, capacity: 1, tokensPerSecond: 2, NullLogger.Instance);

        Assert.True((await limiter.AcquireAsync()).IsAcquired);
        Assert.False((await limiter.AcquireAsync()).IsAcquired);

        await Task.Delay(TimeSpan.FromMilliseconds(600));

        Assert.True((await limiter.AcquireAsync()).IsAcquired);
    }

    [SkippableFact]
    public async Task Different_partitions_do_not_interfere()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        using var connection = CreateConnection();
        var suffix = Guid.NewGuid().ToString("N");
        var limiterUserA = RedisRateLimiter.FixedWindow(connection, $"rl:test-policy:user-a-{suffix}", limit: 1, TimeSpan.FromMinutes(1), NullLogger.Instance);
        var limiterUserB = RedisRateLimiter.FixedWindow(connection, $"rl:test-policy:user-b-{suffix}", limit: 1, TimeSpan.FromMinutes(1), NullLogger.Instance);

        Assert.True((await limiterUserA.AcquireAsync()).IsAcquired);
        Assert.False((await limiterUserA.AcquireAsync()).IsAcquired);

        // A different user's partition must not be affected by A's exhausted bucket.
        Assert.True((await limiterUserB.AcquireAsync()).IsAcquired);
    }

    [SkippableFact]
    public async Task Rejected_lease_carries_retry_after_from_a_real_rejection()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        // Companion to the pure Retry_after_metadata_is_populated_on_rejection test above: that one
        // guards the OnRejected/header wiring in the in-memory path; this one proves RedisRateLimiter's
        // own RejectedLease carries the same MetadataName.RetryAfter against a real script rejection.
        using var connection = CreateConnection();
        var key = $"rl:test-policy:{Guid.NewGuid():N}";
        var limiter = RedisRateLimiter.FixedWindow(connection, key, limit: 1, TimeSpan.FromSeconds(30), NullLogger.Instance);

        await limiter.AcquireAsync();
        using var rejected = await limiter.AcquireAsync();

        Assert.False(rejected.IsAcquired);
        Assert.True(rejected.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter));
        Assert.True(retryAfter.TotalSeconds > 0 && retryAfter.TotalSeconds <= 30);
    }
}
