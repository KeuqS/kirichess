using StackExchange.Redis;

namespace KiriChess.Server.Infrastructure.Redis;

/// <summary>
/// The live-instance set (Phase 8.4). Every process writes a heartbeat key with a TTL and adds itself
/// to a durable set; an instance is "alive" when its heartbeat key still exists. The set and the
/// heartbeat are deliberately two keys rather than one: a SET has no per-member TTL, and expiring the
/// whole set would erase every live instance to evict one dead one.
///
/// Consumers use this to decide whether state left behind by another instance is still meaningful,
/// spectator connections, presence entries and queue entries all carry an instance id for exactly
/// that reason, so a machine that vanished mid-game does not leave a phantom spectator on the board
/// forever.
/// </summary>
public sealed class InstanceRegistry
{
    private readonly IRedisConnection _connection;
    private readonly InstanceIdentity _instance;
    private readonly RedisOptions _options;
    private readonly ILogger<InstanceRegistry> _logger;

    public InstanceRegistry(IRedisConnection connection, InstanceIdentity instance, RedisOptions options, ILogger<InstanceRegistry> logger)
    {
        _connection = connection;
        _instance = instance;
        _options = options;
        _logger = logger;
    }

    /// <summary>Adds this instance to the durable set and writes its first heartbeat. No-op when disabled.</summary>
    public async Task RegisterAsync(CancellationToken ct = default)
    {
        if (!_connection.Enabled) return;
        try
        {
            var db = _connection.Db;
            var batch = db.CreateBatch();
            var add = batch.SetAddAsync(_connection.Key("instances"), _instance.Id);
            var heartbeat = batch.StringSetAsync(
                _connection.Key($"instance:{_instance.Id}"),
                DateTimeOffset.UtcNow.ToString("O"),
                TimeSpan.FromSeconds(_options.InstanceTtlSeconds));
            batch.Execute();
            await Task.WhenAll(add, heartbeat).WaitAsync(ct);
        }
        catch (Exception ex) when (IsRedisOutage(ex))
        {
            _logger.LogWarning(ex, "Failed to register this instance in Redis; continuing single-instance for now.");
        }
    }

    /// <summary>Refreshes this instance's heartbeat TTL. No-op when disabled.</summary>
    public async Task HeartbeatAsync(CancellationToken ct = default)
    {
        if (!_connection.Enabled) return;
        try
        {
            await _connection.Db.StringSetAsync(
                _connection.Key($"instance:{_instance.Id}"),
                DateTimeOffset.UtcNow.ToString("O"),
                TimeSpan.FromSeconds(_options.InstanceTtlSeconds)).WaitAsync(ct);
        }
        catch (Exception ex) when (IsRedisOutage(ex))
        {
            _logger.LogWarning(ex, "Failed to write this instance's heartbeat; a lapsed heartbeat looks like a dead instance to everyone else.");
        }
    }

    /// <summary>
    /// Reads <c>{p}instances</c>, checks every member's heartbeat key in one pipelined batch (never a
    /// loop of awaits), returns the survivors, and self-heals by pruning the dead ones from the set
    /// with <c>SREM</c>, no separate sweeper needed.
    ///
    /// <b>An empty result on Redis failure means "we could not tell", never "everyone is dead".</b>
    /// A caller that reacts to an empty set here by treating every game as unowned would cause a mass
    /// takeover storm the instant Redis has one bad second, check for a thrown exception having been
    /// swallowed, not for emptiness, before making a decision that big.
    /// </summary>
    public async Task<IReadOnlySet<string>> LiveInstancesAsync(CancellationToken ct = default)
    {
        if (!_connection.Enabled) return new HashSet<string>();
        try
        {
            var db = _connection.Db;
            var members = await db.SetMembersAsync(_connection.Key("instances")).WaitAsync(ct);
            if (members.Length == 0) return new HashSet<string>();

            var batch = db.CreateBatch();
            var checks = members
                .Select(m => (Id: (string)m!, Exists: batch.KeyExistsAsync(_connection.Key($"instance:{m}"))))
                .ToList();
            batch.Execute();
            await Task.WhenAll(checks.Select(c => c.Exists)).WaitAsync(ct);

            var live = new HashSet<string>();
            var dead = new List<RedisValue>();
            foreach (var (id, exists) in checks)
            {
                if (exists.Result) live.Add(id);
                else dead.Add(id);
            }

            if (dead.Count > 0)
                await db.SetRemoveAsync(_connection.Key("instances"), dead.ToArray()).WaitAsync(ct);

            return live;
        }
        catch (Exception ex) when (IsRedisOutage(ex))
        {
            _logger.LogWarning(ex, "Failed to read live instances from Redis; returning an empty set. This means Redis did not answer, not that every instance died.");
            return new HashSet<string>();
        }
    }

    /// <summary>Whether a specific instance id's heartbeat key still exists. False when disabled or on Redis failure.</summary>
    public async Task<bool> IsLiveAsync(string instanceId, CancellationToken ct = default)
        => await ProbeLiveAsync(instanceId, ct) ?? false;

    /// <summary>
    /// Three-valued liveness: true or false when Redis answered, <c>null</c> when it could not be
    /// reached. <see cref="IsLiveAsync"/> collapses null to false, which is the right default for a
    /// caller that only wants to know whether to forward a command, an unreachable Redis and a dead
    /// instance are equally useless to forward to.
    ///
    /// It is the wrong default for a caller that is about to <i>act</i> on the answer, though, which
    /// is why the distinction exists: <c>GameActionExecutor.ResolveOwnerOrTakeoverAsync</c> only
    /// replaces a named owner's lease outright when this returns a hard false. On null it falls back to
    /// the plain <c>SET NX</c> claim, so a Redis blip can never be mistaken for proof that the current
    /// owner is dead.
    /// </summary>
    public async Task<bool?> ProbeLiveAsync(string instanceId, CancellationToken ct = default)
    {
        if (!_connection.Enabled) return false;
        try
        {
            return await _connection.Db.KeyExistsAsync(_connection.Key($"instance:{instanceId}")).WaitAsync(ct);
        }
        catch (Exception ex) when (IsRedisOutage(ex))
        {
            _logger.LogWarning(ex, "Failed to check liveness for instance {InstanceId}.", instanceId);
            return null;
        }
    }

    /// <summary>Graceful shutdown: removes this instance from the set and deletes its heartbeat key immediately, rather than waiting out the TTL.</summary>
    public async Task DeregisterAsync(CancellationToken ct = default)
    {
        if (!_connection.Enabled) return;
        try
        {
            var db = _connection.Db;
            var batch = db.CreateBatch();
            var remove = batch.SetRemoveAsync(_connection.Key("instances"), _instance.Id);
            var delete = batch.KeyDeleteAsync(_connection.Key($"instance:{_instance.Id}"));
            batch.Execute();
            await Task.WhenAll(remove, delete).WaitAsync(ct);
        }
        catch (Exception ex) when (IsRedisOutage(ex))
        {
            _logger.LogWarning(ex, "Failed to deregister this instance from Redis on shutdown; its heartbeat key will simply expire.");
        }
    }

    // A Redis outage degrades to single-instance behaviour; it must never take the process down.
    private static bool IsRedisOutage(Exception ex) => ex is RedisConnectionException or RedisTimeoutException;
}
