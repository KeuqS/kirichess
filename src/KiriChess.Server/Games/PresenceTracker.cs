using System.Collections.Concurrent;
using KiriChess.Server.Infrastructure.Redis;
using StackExchange.Redis;

namespace KiriChess.Server.Games;

/// <summary>
/// Tracks which signed-in users currently have the site open, via their <see cref="PresenceHub"/>
/// connections. A user with several tabs open counts once; anonymous visitors are never tracked
/// (they still *receive* the counts, they just aren't in them).
///
/// The local <see cref="ConcurrentDictionary{TKey,TValue}"/> stays the authority for this instance's
/// own connections, <see cref="Track"/>/<see cref="Remove"/> are called from
/// <c>PresenceHub.OnConnectedAsync</c>/<c>OnDisconnectedAsync</c> and must stay fast and synchronous,
/// never blocking a hub call on a Redis round trip. Phase 8.4 adds a nullable Redis collaborator on
/// top (README scope decision 1): each process publishes its own signed-in users to
/// <c>{p}presence:{instanceId}</c>, and <see cref="OnlineUsersAsync"/> unions the still-live
/// instances' sets. With no collaborator (the parameterless constructor, what
/// <c>PresenceTrackerTests</c> uses directly) or a disabled one, every method answers from the local
/// dictionary alone, with no Redis call attempted, see <see cref="OnlineUsers"/>/<see cref="OnlineCount"/>,
/// which <see cref="OnlineUsersAsync"/> returns verbatim in that case.
/// </summary>
public sealed class PresenceTracker
{
    private readonly ConcurrentDictionary<string, Guid> _byConnection = new();

    private readonly IRedisConnection? _connection;
    private readonly InstanceIdentity _instance;
    private readonly InstanceRegistry? _instances;
    private readonly RedisOptions? _options;
    private readonly ILogger<PresenceTracker>? _logger;

    /// <summary>No Redis collaborator, the exact pre-8.4 local-only tracker.</summary>
    public PresenceTracker()
    {
        _instance = new InstanceIdentity("local");
    }

    public PresenceTracker(
        IRedisConnection connection, InstanceIdentity instance, InstanceRegistry instances, RedisOptions options,
        ILogger<PresenceTracker> logger)
    {
        _connection = connection;
        _instance = instance;
        _instances = instances;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Registers a live connection as belonging to <paramref name="userId"/>. When Redis is enabled
    /// and this is that user's *first* local connection, the user id is added to this instance's
    /// presence set (fired as a tracked background task, same pattern as
    /// <see cref="GameConnectionTracker.Track"/>, a hub method must not block on a Redis round trip
    /// just to note a connection). Detecting "first connection" is the whole difficulty here: a user
    /// with three tabs open must add themselves to the set exactly once, on the tab that opens it, not
    /// once per tab.
    /// </summary>
    public void Track(string connectionId, Guid userId)
    {
        var wasAlreadyOnline = _byConnection.Values.Contains(userId);
        _byConnection[connectionId] = userId;

        if (!wasAlreadyOnline && _connection is { Enabled: true } connection)
            _ = AddToRedisAsync(connection, userId);
    }

    /// <summary>
    /// Drops a connection. Unknown ids (anonymous visitors) are a no-op. When Redis is enabled and
    /// that was the user's *last* local connection, the user id is removed from this instance's
    /// presence set, the mirror image of <see cref="Track"/>'s first-connection check, and getting
    /// either direction wrong double-counts or under-counts a multi-tab user.
    /// </summary>
    public void Remove(string connectionId)
    {
        if (!_byConnection.TryRemove(connectionId, out var userId)) return;

        if (_connection is { Enabled: true } connection && !_byConnection.Values.Contains(userId))
            _ = RemoveFromRedisAsync(connection, userId);
    }

    /// <summary>Fresh snapshot of the distinct signed-in users with at least one open connection on this instance.</summary>
    public HashSet<Guid> OnlineUsers => _byConnection.Values.ToHashSet();

    /// <summary>Distinct signed-in users with at least one open connection on this instance.</summary>
    public int OnlineCount => OnlineUsers.Count;

    /// <summary>
    /// Cluster-wide version of <see cref="OnlineUsers"/> (Phase 8.4): unions this instance's set with
    /// every other still-live instance's presence set in one <c>SUNION</c>. Returns
    /// <see cref="OnlineUsers"/> verbatim with no collaborator, on a Redis failure, or when no live
    /// instances could be confirmed at all (an empty <see cref="InstanceRegistry.LiveInstancesAsync"/>
    /// result means "could not tell", never "nobody is online", see that method's doc comment; the
    /// safe fallback here is always this instance's own local view).
    /// </summary>
    public async Task<HashSet<Guid>> OnlineUsersAsync(CancellationToken ct = default)
    {
        if (_connection is not { Enabled: true } connection) return OnlineUsers;

        try
        {
            var live = await _instances!.LiveInstancesAsync(ct);
            if (live.Count == 0) return OnlineUsers;

            var keys = live.Select(id => (RedisKey)connection.Key($"presence:{id}")).ToArray();
            var members = await connection.Db.SetCombineAsync(SetOperation.Union, keys).WaitAsync(ct);

            var result = new HashSet<Guid>();
            foreach (var member in members)
                if (Guid.TryParse((string?)member, out var userId)) result.Add(userId);
            return result;
        }
        catch (Exception ex) when (IsRedisOutage(ex))
        {
            _logger?.LogWarning(ex, "Failed to union presence across instances; falling back to this instance's local view.");
            return OnlineUsers;
        }
    }

    /// <summary>Cluster-wide version of <see cref="OnlineCount"/>.</summary>
    public async Task<int> OnlineCountAsync(CancellationToken ct = default) => (await OnlineUsersAsync(ct)).Count;

    /// <summary>
    /// Refreshes this instance's presence-set TTL (<see cref="RedisOptions.InstanceTtlSeconds"/>).
    /// Called by <see cref="PresenceHeartbeat"/> on the shared instance-heartbeat cadence rather than
    /// on a timer of its own. No-op with no collaborator; harmless (a no-op <c>EXPIRE</c>) when nobody
    /// from this instance is currently online.
    /// </summary>
    public async Task RefreshTtlAsync(CancellationToken ct = default)
    {
        if (_connection is not { Enabled: true } connection) return;
        try
        {
            await connection.Db.KeyExpireAsync(
                connection.Key($"presence:{_instance.Id}"), TimeSpan.FromSeconds(_options!.InstanceTtlSeconds)).WaitAsync(ct);
        }
        catch (Exception ex) when (IsRedisOutage(ex))
        {
            _logger?.LogWarning(ex, "Failed to refresh this instance's presence-set TTL.");
        }
    }

    private async Task AddToRedisAsync(IRedisConnection connection, Guid userId)
    {
        try
        {
            await connection.Db.SetAddAsync(connection.Key($"presence:{_instance.Id}"), userId.ToString());
        }
        catch (Exception ex) when (IsRedisOutage(ex))
        {
            _logger?.LogWarning(ex, "Failed to add user {UserId} to this instance's presence set.", userId);
        }
    }

    private async Task RemoveFromRedisAsync(IRedisConnection connection, Guid userId)
    {
        try
        {
            await connection.Db.SetRemoveAsync(connection.Key($"presence:{_instance.Id}"), userId.ToString());
        }
        catch (Exception ex) when (IsRedisOutage(ex))
        {
            _logger?.LogWarning(ex, "Failed to remove user {UserId} from this instance's presence set.", userId);
        }
    }

    private static bool IsRedisOutage(Exception ex) => ex is RedisConnectionException or RedisTimeoutException;
}
