using KiriChess.Server.Infrastructure.Redis;
using StackExchange.Redis;

namespace KiriChess.Server.Games.Distributed;

/// <summary>
/// Lease primitives over <c>{p}game:owner:{gameId}</c> (Phase 8.4), TTL = <see cref="RedisOptions.InstanceTtlSeconds"/>.
/// This is the whole concurrency control for "one instance owns a game": <see cref="TryClaimAsync"/>
/// succeeds only once the previous lease has expired, and the lease shares a TTL and a renewal tick
/// with the owner's own instance heartbeat (<see cref="GameOwnershipHeartbeat"/>), so an owner whose
/// heartbeat has lapsed cannot still be renewing its lease. There is deliberately no "steal the lease"
/// path, see Phase 8.4 scope decision 2 for why a game may migrate only once its
/// owner is provably dead, never on demand.
///
/// <see cref="RenewAsync"/> and <see cref="ReleaseAsync"/> are compare-and-swap Lua scripts, not
/// GET-then-SET/DEL: the whole point of the lease is that a paused instance which wakes up after its
/// lease has already expired must not be able to re-extend or delete a lease another instance now
/// holds, and a read followed by a separate write can never guarantee that, only an atomic
/// check-and-act on the Redis side can.
/// </summary>
public sealed class GameOwnership
{
    // Compare-and-renew: only the current holder's PEXPIRE goes through. Returns 0 (not this
    // instance's lease, or no lease at all) or the PEXPIRE result (1 on success).
    private const string RenewScript = """
        if redis.call('GET', KEYS[1]) == ARGV[1] then
            return redis.call('PEXPIRE', KEYS[1], ARGV[2])
        else
            return 0
        end
        """;

    // Compare-and-delete: only the current holder's DEL goes through.
    private const string ReleaseScript = """
        if redis.call('GET', KEYS[1]) == ARGV[1] then
            return redis.call('DEL', KEYS[1])
        else
            return 0
        end
        """;

    // Compare-and-take: replaces one named holder's lease with ours, or takes the key if it is gone.
    // ARGV[1] is the owner we expect to displace, ARGV[2] is us, ARGV[3] the TTL in seconds.
    private const string TakeOverScript = """
        local current = redis.call('GET', KEYS[1])
        if current == false or current == ARGV[1] then
            redis.call('SET', KEYS[1], ARGV[2], 'EX', ARGV[3])
            return 1
        else
            return 0
        end
        """;

    private readonly IRedisConnection _connection;
    private readonly InstanceIdentity _instance;
    private readonly RedisOptions _options;
    private readonly ILogger<GameOwnership> _logger;

    public GameOwnership(IRedisConnection connection, InstanceIdentity instance, RedisOptions options, ILogger<GameOwnership> logger)
    {
        _connection = connection;
        _instance = instance;
        _options = options;
        _logger = logger;
    }

    private string OwnerKey(Guid gameId) => _connection.Key($"game:owner:{gameId}");

    /// <summary>
    /// SET NX EX. True only if this instance now holds the lease. Single-instance (Redis disabled)
    /// always returns true, this instance owns everything there is, and every caller stays free of
    /// an <c>if (redisEnabled)</c> branch as a result. On a Redis failure, returns false: we must not
    /// take ownership we cannot prove another instance does not already hold.
    /// </summary>
    public async Task<bool> TryClaimAsync(Guid gameId, CancellationToken ct = default)
    {
        if (!_connection.Enabled) return true;

        try
        {
            return await _connection.Db.StringSetAsync(
                OwnerKey(gameId), _instance.Id,
                TimeSpan.FromSeconds(_options.InstanceTtlSeconds), When.NotExists).WaitAsync(ct);
        }
        catch (Exception ex) when (IsRedisOutage(ex))
        {
            _logger.LogWarning(ex, "Failed to claim the ownership lease for game {GameId}; not claiming ownership we cannot prove.", gameId);
            return false;
        }
    }

    /// <summary>
    /// Compare-and-renew: extends the TTL only while this instance still holds the lease. Single
    /// instance always returns true (there is nothing to renew, nothing to lose).
    ///
    /// <b>The failure-mode asymmetry here is deliberate.</b> A Redis exception (timeout, connection
    /// drop) returns <b>true</b>: if Redis is unreachable, this instance cannot renew, but neither can
    /// any other instance claim the lease, claiming needs Redis exactly as much as renewing does.
    /// Reporting failure here would evict a perfectly healthy live game over an infrastructure hiccup,
    /// ending real games for no reason.
    ///
    /// A <c>false</c> return means only that this instance does not currently hold the lease, the script
    /// also returns 0 when the owner key is simply missing (Redis restarted without persistence, was
    /// flushed, or evicted the key under <c>maxmemory</c>), which is not a takeover at all. Distinguishing
    /// a lapsed-but-unclaimed lease from one another instance genuinely took over is the caller's job, not
    /// this method's, see <see cref="GameOwnershipHeartbeat"/> for how it tells the two apart before
    /// deciding whether to evict.
    /// </summary>
    public async Task<bool> RenewAsync(Guid gameId, CancellationToken ct = default)
    {
        if (!_connection.Enabled) return true;

        try
        {
            var result = await _connection.Db.ScriptEvaluateAsync(
                RenewScript,
                new RedisKey[] { OwnerKey(gameId) },
                new RedisValue[] { _instance.Id, _options.InstanceTtlSeconds * 1000L }).WaitAsync(ct);
            return (long)result == 1;
        }
        catch (Exception ex) when (IsRedisOutage(ex))
        {
            _logger.LogWarning(ex, "Failed to renew the ownership lease for game {GameId}; treating this as a transient Redis blip rather than a lost lease (see this method's doc comment).", gameId);
            return true;
        }
    }

    /// <summary>The owning instance id, or null when the lease has expired, never existed, or Redis could not be reached.</summary>
    public async Task<string?> GetOwnerAsync(Guid gameId, CancellationToken ct = default)
    {
        if (!_connection.Enabled) return _instance.Id;

        try
        {
            var value = await _connection.Db.StringGetAsync(OwnerKey(gameId)).WaitAsync(ct);
            return value.IsNullOrEmpty ? null : (string?)value;
        }
        catch (Exception ex) when (IsRedisOutage(ex))
        {
            _logger.LogWarning(ex, "Failed to read the owner of game {GameId}.", gameId);
            return null;
        }
    }

    /// <summary>
    /// Compare-and-take: claims the lease when it is unheld <i>or</i> still names
    /// <paramref name="deadOwnerId"/>, atomically, in one round trip. This is the second half of the
    /// C-5-3 fix and the only place a lease changes hands without having expired first.
    ///
    /// <b>Read the caller before reading this as a "steal the lease" path, which the class comment
    /// says does not exist.</b> It still does not. The lease TTL is one proof that an owner is gone;
    /// this method accepts the other proof the system already collects, a missing
    /// <c>{p}instance:{id}</c> heartbeat key, and the only caller
    /// (<c>GameActionExecutor.ResolveOwnerOrTakeoverAsync</c>) passes an owner id only after
    /// <see cref="InstanceRegistry.ProbeLiveAsync"/> has hard-answered "that instance is not alive".
    /// An unanswered probe is not an answer, and that caller falls back to <see cref="TryClaimAsync"/>
    /// instead, so a Redis blip can never be dressed up as a dead owner.
    ///
    /// Why it is needed at all, given the shutdown release: the release covers a graceful stop. This
    /// covers every stop that is not graceful, where the heartbeat key and the lease expire on the same
    /// TTL but not at the same instant, because the heartbeat is written at the top of a tick and the
    /// leases are renewed by a hook after it. That gap is the same unrecoverable state C-5-3 describes,
    /// just narrower, and a machine killed between releasing its leases and completing its shutdown
    /// lands in it too.
    ///
    /// Concurrency is unchanged: two instances racing to displace the same dead owner both run this
    /// script, and only the first sees <paramref name="deadOwnerId"/> still in the key, the second
    /// reads the winner's id and returns false.
    /// </summary>
    public async Task<bool> TryTakeOverFromAsync(Guid gameId, string deadOwnerId, CancellationToken ct = default)
    {
        if (!_connection.Enabled) return true;

        try
        {
            var result = await _connection.Db.ScriptEvaluateAsync(
                TakeOverScript,
                new RedisKey[] { OwnerKey(gameId) },
                new RedisValue[] { deadOwnerId, _instance.Id, _options.InstanceTtlSeconds }).WaitAsync(ct);
            return (long)result == 1;
        }
        catch (Exception ex) when (IsRedisOutage(ex))
        {
            _logger.LogWarning(ex, "Failed to take the ownership lease for game {GameId} from {DeadOwner}; not claiming ownership we cannot prove.", gameId, deadOwnerId);
            return false;
        }
    }

    /// <summary>Compare-and-delete: releases only a lease this instance holds. No-op single-instance or on Redis failure (it will simply expire on its own TTL).</summary>
    public async Task ReleaseAsync(Guid gameId, CancellationToken ct = default)
    {
        if (!_connection.Enabled) return;

        try
        {
            await _connection.Db.ScriptEvaluateAsync(
                ReleaseScript,
                new RedisKey[] { OwnerKey(gameId) },
                new RedisValue[] { _instance.Id }).WaitAsync(ct);
        }
        catch (Exception ex) when (IsRedisOutage(ex))
        {
            _logger.LogWarning(ex, "Failed to release the ownership lease for game {GameId}; it will expire on its own TTL.", gameId);
        }
    }

    private static bool IsRedisOutage(Exception ex) => ex is RedisConnectionException or RedisTimeoutException;
}
