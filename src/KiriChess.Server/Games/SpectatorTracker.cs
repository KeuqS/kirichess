using System.Collections.Concurrent;
using KiriChess.Server.Infrastructure.Redis;
using StackExchange.Redis;

namespace KiriChess.Server.Games;

/// <summary>
/// Which connections are watching which live game (Phase 8.1). Deliberately a separate type from
/// <see cref="GameConnectionTracker"/> rather than another dictionary inside it: that one arms and
/// cancels the disconnect-forfeit timer, and a spectator's flaky wifi must never be able to end
/// somebody else's game. Keeping the two sets in different types makes that structural instead of a
/// rule someone has to remember when editing the hub.
///
/// One connection watches at most one game, <see cref="AddAsync"/> moves it rather than fanning out,
/// which is what the UI does anyway (one page, one board) and keeps removal a single dictionary hit
/// with no per-game bookkeeping to fall out of sync.
///
/// Phase 8.4 (Task C): the local <see cref="ConcurrentDictionary{TKey,TValue}"/> remains this
/// instance's authority for its own connections, but every count is now Redis-backed when a
/// collaborator is supplied, <c>{p}spectators:{gameId}</c> is a SET of <c>{instanceId}:{connectionId}</c>
/// members, so a count for a game watched from three instances is the union across all three, not just
/// this one. With no collaborator (the parameterless constructor) every method answers purely from the
/// local dictionary, unchanged from before this phase.
/// </summary>
public sealed class SpectatorTracker
{
    // 1 hour, refreshed by PresenceHeartbeat on every instance-heartbeat tick (README key-schema
    // table), long enough a normally-paced spectated game never expires the set mid-watch, short
    // enough a game nobody is refreshing (its owner died) does not haunt a spectator count forever.
    private const int SpectatorTtlSeconds = 3600;

    private readonly ConcurrentDictionary<string, Guid> _byConnection = new();

    private readonly IRedisConnection? _connection;
    private readonly InstanceIdentity _instance;
    private readonly InstanceRegistry? _instances;
    private readonly ILogger<SpectatorTracker>? _logger;

    /// <summary>No Redis collaborator, the exact pre-8.4 local-only tracker.</summary>
    public SpectatorTracker()
    {
        _instance = new InstanceIdentity("local");
    }

    public SpectatorTracker(IRedisConnection connection, InstanceIdentity instance, InstanceRegistry instances, ILogger<SpectatorTracker> logger)
    {
        _connection = connection;
        _instance = instance;
        _instances = instances;
        _logger = logger;
    }

    /// <summary>
    /// Registers <paramref name="connectionId"/> as watching <paramref name="gameId"/>. If it was
    /// already watching a <em>different</em> game, that one is left first and returned, so the caller
    /// can broadcast the decremented count there. Returns null when there was nothing to leave,
    /// including the common case of re-joining the same game, which is exactly what a client does
    /// after an automatic reconnect and must not produce a spurious broadcast.
    /// </summary>
    public async Task<Guid?> AddAsync(string connectionId, Guid gameId, CancellationToken ct = default)
    {
        Guid? left = _byConnection.TryGetValue(connectionId, out var previous) && previous != gameId
            ? previous
            : null;
        _byConnection[connectionId] = gameId;

        if (_connection is { Enabled: true } connection)
        {
            try
            {
                var member = Member(connectionId);
                var db = connection.Db;
                var batch = db.CreateBatch();
                var tasks = new List<Task>();
                if (left is { } leftGame)
                    tasks.Add(batch.SetRemoveAsync(connection.Key($"spectators:{leftGame}"), member));
                var newKey = connection.Key($"spectators:{gameId}");
                tasks.Add(batch.SetAddAsync(newKey, member));
                tasks.Add(batch.KeyExpireAsync(newKey, TimeSpan.FromSeconds(SpectatorTtlSeconds)));
                batch.Execute();
                await Task.WhenAll(tasks).WaitAsync(ct);
            }
            catch (Exception ex) when (IsRedisOutage(ex))
            {
                _logger?.LogWarning(ex, "Failed to update spectator membership in Redis for connection {ConnectionId}.", connectionId);
            }
        }

        return left;
    }

    /// <summary>
    /// Unregisters a connection. Returns the game it was watching, or null if it was not watching one
    ///, which is the normal case for every player connection passing through
    /// <c>OnDisconnectedAsync</c>, so it is a return value and not an exception.
    /// </summary>
    public async Task<Guid?> RemoveAsync(string connectionId, CancellationToken ct = default)
    {
        if (!_byConnection.TryRemove(connectionId, out var gameId)) return null;

        if (_connection is { Enabled: true } connection)
        {
            try
            {
                await connection.Db.SetRemoveAsync(connection.Key($"spectators:{gameId}"), Member(connectionId)).WaitAsync(ct);
            }
            catch (Exception ex) when (IsRedisOutage(ex))
            {
                _logger?.LogWarning(ex, "Failed to remove spectator membership in Redis for connection {ConnectionId}.", connectionId);
            }
        }

        return gameId;
    }

    /// <summary>
    /// How many connections are watching a game, across every live instance. Zero for an unwatched or
    /// unknown game. Reads <c>{p}spectators:{gameId}</c> and counts only members whose instance id is
    /// still live, pruning the dead ones with <c>SREM</c> while it is there, self-healing, the same
    /// pattern <see cref="InstanceRegistry"/> uses. A spectator count is on a public page; a machine
    /// that died two days ago must not still be "watching".
    /// </summary>
    public async Task<int> CountForAsync(Guid gameId, CancellationToken ct = default)
    {
        if (_connection is not { Enabled: true } connection) return CountForLocal(gameId);

        try
        {
            var key = connection.Key($"spectators:{gameId}");
            var members = await connection.Db.SetMembersAsync(key).WaitAsync(ct);
            if (members.Length == 0) return 0;

            var live = await _instances!.LiveInstancesAsync(ct);
            var count = 0;
            var dead = new List<RedisValue>();
            foreach (var member in members)
            {
                if (InstanceOf(member) is { } instanceId && live.Contains(instanceId)) count++;
                else dead.Add(member);
            }
            if (dead.Count > 0) await connection.Db.SetRemoveAsync(key, dead.ToArray()).WaitAsync(ct);
            return count;
        }
        catch (Exception ex) when (IsRedisOutage(ex))
        {
            _logger?.LogWarning(ex, "Failed to read the spectator count for game {GameId} from Redis.", gameId);
            return CountForLocal(gameId);
        }
    }

    /// <summary>
    /// Counts for exactly the games named in <paramref name="gameIds"/>, the live-games list's ≤50
    /// ids, batched into a single round trip via <see cref="IBatch"/> rather than one
    /// <c>SMEMBERS</c> per row. Unlike the pre-8.4 <c>Counts()</c> this no longer enumerates "every
    /// watched game": with Redis there is no cheap way to list every game with a spectator across the
    /// whole cluster, and the only caller (<c>GET /api/games/live</c>) already knows the exact ids it
    /// needs counts for.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, int>> CountsAsync(IReadOnlyCollection<Guid> gameIds, CancellationToken ct = default)
    {
        if (_connection is not { Enabled: true } connection) return CountsLocal();
        if (gameIds.Count == 0) return new Dictionary<Guid, int>();

        try
        {
            var db = connection.Db;
            var batch = db.CreateBatch();
            var fetches = gameIds
                .Select(id => (Id: id, Members: batch.SetMembersAsync(connection.Key($"spectators:{id}"))))
                .ToList();
            batch.Execute();
            await Task.WhenAll(fetches.Select(f => f.Members)).WaitAsync(ct);

            var live = await _instances!.LiveInstancesAsync(ct);
            var result = new Dictionary<Guid, int>();
            var dead = new List<(Guid GameId, RedisValue Member)>();
            foreach (var (id, membersTask) in fetches)
            {
                var count = 0;
                foreach (var member in membersTask.Result)
                {
                    if (InstanceOf(member) is { } instanceId && live.Contains(instanceId)) count++;
                    else dead.Add((id, member));
                }
                result[id] = count;
            }

            if (dead.Count > 0)
            {
                var pruneBatch = db.CreateBatch();
                var pruneTasks = dead
                    .Select(d => pruneBatch.SetRemoveAsync(connection.Key($"spectators:{d.GameId}"), d.Member))
                    .ToArray();
                pruneBatch.Execute();
                await Task.WhenAll(pruneTasks).WaitAsync(ct);
            }

            return result;
        }
        catch (Exception ex) when (IsRedisOutage(ex))
        {
            _logger?.LogWarning(ex, "Failed to read batched spectator counts from Redis.");
            return CountsLocal();
        }
    }

    /// <summary>
    /// Refreshes the spectator-set TTL for every game this instance has a local spectator connection
    /// in right now. Called by <see cref="PresenceHeartbeat"/> on the shared instance-heartbeat
    /// cadence. No-op with no collaborator.
    /// </summary>
    public async Task RefreshTtlAsync(CancellationToken ct = default)
    {
        if (_connection is not { Enabled: true } connection) return;

        var gameIds = _byConnection.Values.Distinct().ToArray();
        if (gameIds.Length == 0) return;

        try
        {
            var db = connection.Db;
            var batch = db.CreateBatch();
            var tasks = gameIds
                .Select(id => batch.KeyExpireAsync(connection.Key($"spectators:{id}"), TimeSpan.FromSeconds(SpectatorTtlSeconds)))
                .ToArray();
            batch.Execute();
            await Task.WhenAll(tasks).WaitAsync(ct);
        }
        catch (Exception ex) when (IsRedisOutage(ex))
        {
            _logger?.LogWarning(ex, "Failed to refresh spectator-set TTLs for this instance's watched games.");
        }
    }

    private int CountForLocal(Guid gameId)
    {
        var count = 0;
        foreach (var watched in _byConnection.Values)
            if (watched == gameId) count++;
        return count;
    }

    private IReadOnlyDictionary<Guid, int> CountsLocal()
    {
        var counts = new Dictionary<Guid, int>();
        foreach (var watched in _byConnection.Values)
            counts[watched] = counts.GetValueOrDefault(watched) + 1;
        return counts;
    }

    private string Member(string connectionId) => $"{_instance.Id}:{connectionId}";

    /// <summary>Extracts the instance id half of a <c>{instanceId}:{connectionId}</c> member, or null if malformed.</summary>
    private static string? InstanceOf(RedisValue member) =>
        ((string?)member)?.Split(':', 2) is [var instanceId, _] ? instanceId : null;

    private static bool IsRedisOutage(Exception ex) => ex is RedisConnectionException or RedisTimeoutException;
}
