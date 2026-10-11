using System.Threading.RateLimiting;
using KiriChess.Server.Infrastructure.Redis;
using StackExchange.Redis;

namespace KiriChess.Server.Infrastructure;

/// <summary>
/// The two counting shapes <see cref="RedisRateLimiter"/> runs, mirroring the two the in-memory
/// fallback already uses in <see cref="RateLimitingSetup"/>: fixed window for the bursty-but-bounded
/// HTTP policies, token bucket for <see cref="RateLimitPolicies.GameMove"/>, which needs a steady drip
/// rather than a once-a-minute reset.
/// </summary>
internal enum RedisRateLimiterKind
{
    FixedWindow,
    TokenBucket
}

/// <summary>
/// A <see cref="RateLimiter"/> backed by a Redis key instead of process memory, so N instances behind
/// one Redis share a single counter instead of each instance granting the full configured limit, the
/// leak <c>Phase 8.4</c> exists to close. Constructed by
/// <see cref="RedisRateLimitPartition"/>, one instance per partition key, and never disposed by this
/// file: see <see cref="IdleDuration"/>.
///
/// <b>Fails open, not closed, inherited verbatim from the 6e.1 governing rule on
/// <see cref="RateLimitingSetup"/>.</b> A rejected move can lose a game: <c>GameSession</c> charges
/// think time whether or not a request was ever accepted, so a limiter that rejects because its
/// backing store is unreachable turns a Redis blip into lost games. Every failure path here,
/// timeout, connection exception, a script error nobody anticipated, falls through to a single
/// <c>catch (Exception)</c> that returns an acquired lease rather than propagating. There is
/// deliberately no narrower catch clause: narrowing it to <c>RedisConnectionException</c> or
/// <c>RedisTimeoutException</c> (the shape <see cref="InstanceRegistry"/> uses for a background
/// heartbeat, where a thrown exception would take the process down) would let an unanticipated
/// exception type propagate onto the request path instead, which is exactly the outcome this class
/// exists to prevent.
/// </summary>
internal sealed class RedisRateLimiter : RateLimiter
{
    // INCR-then-conditional-EXPIRE has to be one script, not two round trips: if the process died
    // between a bare INCR and a follow-up EXPIRE call, the counter key would never expire and every
    // caller sharing that partition would stay locked out forever instead of just for the rest of the
    // window. Returns {acquired, retryAfterSeconds}.
    private const string FixedWindowScript = @"
local n = redis.call('INCR', KEYS[1])
if n == 1 then redis.call('EXPIRE', KEYS[1], ARGV[2]) end
if n > tonumber(ARGV[1]) then return {0, redis.call('TTL', KEYS[1])} end
return {1, 0}";

    // ARGV[3] ('now', milliseconds) is passed in from the .NET caller's clock rather than read via
    // Lua's TIME: redis.call('TIME') is non-deterministic across replicas and was historically
    // rejected inside scripts for exactly that reason. Passing the timestamp in keeps the script
    // replica-safe and lets a test drive the clock explicitly instead of racing a real one. This
    // bucket is approximate under concurrency in a way the in-memory TokenBucketRateLimiter is not,
    // two requests racing the same read-modify-write can both see tokens available and both take one
    //, and that approximation is deliberately in the permissive direction, which the governing rule
    // above says is the right one to be wrong in.
    private const string TokenBucketScript = @"
local capacity = tonumber(ARGV[1])
local rate = tonumber(ARGV[2])
local now = tonumber(ARGV[3])
local ttl = tonumber(ARGV[4])

local data = redis.call('HMGET', KEYS[1], 'tokens', 'ts')
local tokens = tonumber(data[1])
local ts = tonumber(data[2])
if tokens == nil then
    tokens = capacity
    ts = now
end

local elapsedSeconds = math.max(0, now - ts) / 1000.0
tokens = math.min(capacity, tokens + elapsedSeconds * rate)

local acquired = 0
local retryAfter = 0
if tokens >= 1 then
    tokens = tokens - 1
    acquired = 1
else
    retryAfter = rate > 0 and math.ceil((1 - tokens) / rate) or ttl
end

redis.call('HSET', KEYS[1], 'tokens', tostring(tokens), 'ts', tostring(now))
redis.call('EXPIRE', KEYS[1], ttl)

return {acquired, retryAfter}";

    // Shared across every RedisRateLimiter in the process, not just this partition: one Redis outage
    // must produce one warning every WarningInterval, not one per rejected-then-permitted request. A
    // single busy GameMove bucket alone can call AcquireAsyncCore dozens of times a second; without a
    // process-wide gate an outage would turn into a logging incident of its own, which is the exact
    // "rate-limited" logging the plan calls for.
    private static long _lastWarningTicksUtc;
    private static readonly TimeSpan WarningInterval = TimeSpan.FromSeconds(30);

    private readonly RedisRateLimiterKind _kind;
    private readonly IRedisConnection _redis;
    private readonly string _key;
    private readonly int _limitOrCapacity;
    private readonly int _windowSecondsOrTokensPerSecond;
    private readonly int _ttlSeconds;
    private readonly ILogger _logger;

    private RedisRateLimiter(
        RedisRateLimiterKind kind, IRedisConnection redis, string key,
        int limitOrCapacity, int windowSecondsOrTokensPerSecond, int ttlSeconds, ILogger logger)
    {
        _kind = kind;
        _redis = redis;
        _key = key;
        _limitOrCapacity = limitOrCapacity;
        _windowSecondsOrTokensPerSecond = windowSecondsOrTokensPerSecond;
        _ttlSeconds = ttlSeconds;
        _logger = logger;
    }

    public static RedisRateLimiter FixedWindow(IRedisConnection redis, string key, int limit, TimeSpan window, ILogger logger) =>
        new(RedisRateLimiterKind.FixedWindow, redis, key, limit, (int)window.TotalSeconds, (int)window.TotalSeconds, logger);

    public static RedisRateLimiter TokenBucket(IRedisConnection redis, string key, int capacity, int tokensPerSecond, ILogger logger)
    {
        // "Idle TTL = 10x the window" (README key-schema table) for a bucket with no literal window:
        // the natural stand-in is the time to refill from empty, capacity / tokensPerSecond. Ten times
        // that is long enough that a caller who stops for a while comes back to a key that has already
        // expired, which is indistinguishable from a full bucket, the correct state for someone who
        // has not spent a token in ages, while still not pinning a HASH in Redis forever for every
        // partition ever seen.
        var refillSeconds = tokensPerSecond > 0 ? (double)capacity / tokensPerSecond : capacity;
        var ttlSeconds = Math.Max(1, (int)Math.Ceiling(refillSeconds * 10));
        return new(RedisRateLimiterKind.TokenBucket, redis, key, capacity, tokensPerSecond, ttlSeconds, logger);
    }

    // Never used by the middleware, Microsoft.AspNetCore.RateLimiting only ever calls the async path
    //, but must still always return an acquired lease if something ever does call it: a synchronous
    // Redis call on a request thread is exactly the stall this server cannot afford, and permitting is
    // the fail-open behaviour this whole file exists to guarantee anyway.
    protected override RateLimitLease AttemptAcquireCore(int permitCount) => AcquiredLease.Instance;

    protected override async ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken)
    {
        try
        {
            RedisValue[] values = _kind == RedisRateLimiterKind.FixedWindow
                ? new RedisValue[] { _limitOrCapacity, _windowSecondsOrTokensPerSecond }
                : new RedisValue[] { _limitOrCapacity, _windowSecondsOrTokensPerSecond, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), _ttlSeconds };
            var script = _kind == RedisRateLimiterKind.FixedWindow ? FixedWindowScript : TokenBucketScript;
            var keys = new RedisKey[] { _redis.Key(_key) };

            var raw = await _redis.Db
                .ScriptEvaluateAsync(script, keys, values)
                .WaitAsync(cancellationToken);
            var reply = (RedisValue[])raw!;

            var acquired = (long)reply[0] == 1;
            if (acquired) return AcquiredLease.Instance;
            return new RejectedLease(TimeSpan.FromSeconds((long)reply[1]));
        }
        catch (Exception ex)
        {
            // See the class doc comment for why this is a deliberately broad catch: every failure
            // shape, timeout, connection exception, an unexpected script error, permits.
            LogFailureIfDue(ex);
            return AcquiredLease.Instance;
        }
    }

    public override TimeSpan? IdleDuration => null;

    // Neither is read by anything in this server; the built-in limiters' statistics exist for
    // observability this class does not (yet) need.
    public override RateLimiterStatistics? GetStatistics() => null;

    private void LogFailureIfDue(Exception ex)
    {
        var now = DateTime.UtcNow.Ticks;
        var last = Interlocked.Read(ref _lastWarningTicksUtc);
        if (now - last < WarningInterval.Ticks) return;
        if (Interlocked.CompareExchange(ref _lastWarningTicksUtc, now, last) != last) return; // another thread just logged
        _logger.LogWarning(ex,
            "Redis rate limiter call failed for {Key}; permitting the request. A rejected request can " +
            "cost more than an unlimited one on this server, see RateLimitingSetup's governing rule.",
            _key);
    }

    /// <summary>Always-acquired, no-metadata lease. Stateless, so one shared instance is enough.</summary>
    private sealed class AcquiredLease : RateLimitLease
    {
        public static readonly AcquiredLease Instance = new();
        public override bool IsAcquired => true;
        public override IEnumerable<string> MetadataNames => [];
        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            metadata = null;
            return false;
        }
    }

    /// <summary>
    /// Rejected lease carrying the script's TTL as <see cref="MetadataName.RetryAfter"/>, the exact
    /// metadata name <see cref="RateLimitingSetup"/>'s <c>OnRejected</c> already reads to set the
    /// <c>Retry-After</c> header, so it keeps working with no change on this side.
    /// </summary>
    private sealed class RejectedLease : RateLimitLease
    {
        private readonly TimeSpan _retryAfter;
        public RejectedLease(TimeSpan retryAfter) => _retryAfter = retryAfter;
        public override bool IsAcquired => false;
        public override IEnumerable<string> MetadataNames => [MetadataName.RetryAfter.Name];
        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            if (metadataName == MetadataName.RetryAfter.Name)
            {
                metadata = _retryAfter;
                return true;
            }
            metadata = null;
            return false;
        }
    }
}

/// <summary>
/// Builds <see cref="RateLimitPartition{TKey}"/> values backed by <see cref="RedisRateLimiter"/>,
/// mirroring the shape of <see cref="RateLimitPartition"/>'s own <c>Get*Limiter</c> statics so the
/// policy bodies in <see cref="RateLimitingSetup"/> read the same whether they end up on the Redis
/// path or the in-memory one.
/// </summary>
internal static class RedisRateLimitPartition
{
    public static RateLimitPartition<string> FixedWindow(
        string policy, string partitionKey, IRedisConnection redis, ILogger logger, int limit, TimeSpan window) =>
        RateLimitPartition.Get(partitionKey, key =>
            (RateLimiter)RedisRateLimiter.FixedWindow(redis, $"rl:{policy}:{key}", limit, window, logger));

    public static RateLimitPartition<string> TokenBucket(
        string policy, string partitionKey, IRedisConnection redis, ILogger logger, int capacity, int tokensPerSecond) =>
        RateLimitPartition.Get(partitionKey, key =>
            (RateLimiter)RedisRateLimiter.TokenBucket(redis, $"rlt:{policy}:{key}", capacity, tokensPerSecond, logger));
}
