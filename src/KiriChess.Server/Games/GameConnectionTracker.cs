using System.Collections.Concurrent;
using System.Text.Json;
using KiriChess.Server.Games.Distributed;
using KiriChess.Server.Infrastructure.Redis;
using Microsoft.AspNetCore.SignalR;
using StackExchange.Redis;

namespace KiriChess.Server.Games;

/// <summary>
/// Tracks which connection is which player in which game, and enforces the disconnect grace period:
/// when a player's connection drops, the opponent is notified and a timer is armed; if the player
/// does not re-join within the grace window, they forfeit through <see cref="GameActionExecutor.AbandonAsync"/>.
///
/// Phase 8.4 adds a cross-instance dimension: <see cref="_byConnection"/> and <see cref="_pending"/>
/// remain purely local (a connection and its grace timer only ever exist on the one instance that
/// physically holds the SignalR connection), but three behaviours that used to assume a single process
/// no longer hold once a player's opponent can be connected to a *different* instance, see
/// <see cref="IsConnectedAsync"/>, <see cref="Track"/>, and <see cref="HandleReconnectAsync"/>.
/// </summary>
public sealed class GameConnectionTracker
{
    private const int GraceSeconds = 30;

    /// <summary>
    /// How long a dropped player has to come back. Always <see cref="GraceSeconds"/> in production:
    /// the constructor parameter behind it exists so a test can prove what happens <i>after</i> a
    /// grace window elapses without spending thirty seconds per assertion, which is the only reason
    /// the second-disconnect defect (C-2-5) had no regression test before. Nothing configures it.
    /// </summary>
    private readonly TimeSpan _grace;

    // 1 hour, refreshed by GameOwnershipHeartbeat on every tick for every still-owned game, long
    // enough that a normally-paced game never expires the hash mid-play, short enough that a game
    // whose owner died does not leave a phantom connection entry forever (README key-schema table).
    private const int ConnsTtlSeconds = 3600;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly IHubContext<GameHub> _hub;
    private readonly GameRegistry _registry;
    private readonly IGameActionExecutor _actions;
    private readonly IRedisConnection _connection;
    private readonly InstanceIdentity _instance;
    private readonly InstanceRegistry _instances;
    private readonly ILogger<GameConnectionTracker> _logger;
    private readonly IGraceDisconnectStore? _graceDisconnects;
    private readonly ConcurrentDictionary<string, (Guid UserId, Guid GameId)> _byConnection = new();
    private readonly ConcurrentDictionary<(Guid GameId, Guid UserId), CancellationTokenSource> _pending = new();

    public GameConnectionTracker(
        IHubContext<GameHub> hub, GameRegistry registry, IGameActionExecutor actions,
        IRedisConnection connection, InstanceIdentity instance, InstanceRegistry instances,
        ILogger<GameConnectionTracker> logger, TimeSpan? graceOverride = null,
        IGraceDisconnectStore? graceDisconnects = null)
    {
        _grace = graceOverride ?? TimeSpan.FromSeconds(GraceSeconds);
        _hub = hub;
        _registry = registry;
        _actions = actions;
        _connection = connection;
        _instance = instance;
        _instances = instances;
        _logger = logger;
        // Null only in tests that do not exercise the count. Production DI always supplies the store.
        _graceDisconnects = graceDisconnects;
    }

    /// <summary>
    /// Registers a live connection as <paramref name="userId"/> in <paramref name="gameId"/>, and
    /// cancels any armed abandon timer for that (game, user), cluster-wide.
    ///
    /// Kept synchronous: a hub method must not block on a Redis round trip just to join a group. When
    /// Redis is disabled there is only ever one instance, so the pre-8.4 behaviour (check and cancel
    /// immediately, on this same process) is preserved exactly, synchronously. When Redis is enabled,
    /// the HSET and the reconnect notice are fired as a tracked background task with a logged catch:
    /// the reconnecting connection might be on a *different* instance from whichever one holds the
    /// armed timer, so only a cluster-wide notice (received by every instance via
    /// <see cref="GameCommandRouter"/>, which calls <see cref="HandleReconnectAsync"/> here) can find
    /// it, this instance cannot resolve that with a synchronous local check.
    /// </summary>
    public void Track(string connectionId, Guid userId, Guid gameId)
    {
        _byConnection[connectionId] = (userId, gameId);

        if (!_connection.Enabled)
        {
            if (CancelPendingAbandon(gameId, userId))
                _ = BroadcastOpponentReconnectedAsync(gameId, connectionId);
            return;
        }

        _ = PublishReconnectAsync(gameId, userId, connectionId);
    }

    private async Task PublishReconnectAsync(Guid gameId, Guid userId, string connectionId)
    {
        try
        {
            var hash = _connection.Key($"game:conns:{gameId}");
            var batch = _connection.Db.CreateBatch();
            var set = batch.HashSetAsync(hash, connectionId, $"{_instance.Id}:{userId}");
            var expire = batch.KeyExpireAsync(hash, TimeSpan.FromSeconds(ConnsTtlSeconds));
            batch.Execute();
            await Task.WhenAll(set, expire);

            var notice = new GameReconnectNotice(gameId, userId, connectionId);
            var json = JsonSerializer.Serialize(notice, JsonOptions);
            await _connection.Subscriber.PublishAsync(_connection.Channel("game:reconnect"), json);
        }
        catch (Exception ex) when (IsRedisOutage(ex))
        {
            _logger.LogWarning(ex,
                "Failed to publish a reconnect notice for game {GameId}; if the opponent's disconnect timer is armed on another instance, it will run out and forfeit them.", gameId);
        }
    }

    /// <summary>
    /// Called on every instance when <c>{p}game:reconnect</c> fires (via <see cref="GameCommandRouter"/>).
    /// Only the instance actually holding the armed timer for (gameId, userId) does anything,
    /// everyone else, including the instance that published the notice, is a silent no-op through
    /// <see cref="CancelPendingAbandon"/>'s remove-once semantics. The <c>OpponentReconnected</c>
    /// broadcast that used to fire unconditionally in <c>GameHub.JoinGame</c> lives here instead, so it
    /// fires exactly once, on whichever instance actually cancelled a real timer. In single-instance
    /// mode this is the same instance that published the notice and the observable behaviour is
    /// unchanged (see <c>GameOwnershipTests</c>).
    /// </summary>
    public async Task HandleReconnectAsync(Guid gameId, Guid userId, string connectionId, CancellationToken ct = default)
    {
        if (!CancelPendingAbandon(gameId, userId)) return;
        await BroadcastOpponentReconnectedAsync(gameId, connectionId);
    }

    /// <summary>
    /// Retracts an <c>OpponentDisconnected</c> notice. <paramref name="reconnectingConnectionId"/> is
    /// the connection that just came back, excluded because it never saw the notice; pass null when
    /// nobody is coming back because the player never actually left (see <see cref="RunGraceAsync"/>'s
    /// still-connected branch), in which case the whole group needs it, the player's own surviving
    /// connections included: <c>OpponentDisconnected</c> goes to the whole group, so they were told too.
    /// </summary>
    private Task BroadcastOpponentReconnectedAsync(Guid gameId, string? reconnectingConnectionId)
    {
        var session = _registry.Get(gameId);
        if (session is not null && session.WhitePlayerId == session.BlackPlayerId)
            return Task.CompletedTask;
        if (reconnectingConnectionId is null)
            return GameHubGroups.SendToRoomAsync(_hub.Clients, gameId, "OpponentReconnected");

        return Task.WhenAll(
            _hub.Clients.GroupExcept(GameHubGroups.Players(gameId), reconnectingConnectionId)
                .SendAsync("OpponentReconnected"),
            _hub.Clients.Group(GameHubGroups.Spectators(gameId)).SendAsync("OpponentReconnected"));
    }

    /// <summary>Whether the user currently has a live tracked connection into the game, on this process only.</summary>
    public bool IsConnected(Guid gameId, Guid userId)
        => _byConnection.Values.Any(v => v.GameId == gameId && v.UserId == userId);

    /// <summary>
    /// Cross-instance version of <see cref="IsConnected"/>: reads <c>{p}game:conns:{gameId}</c> and
    /// counts only entries whose instance id is still live (<see cref="InstanceRegistry.LiveInstancesAsync"/>)
    ///, a machine that vanished must not keep a player looking connected forever. Falls back to the
    /// local check when Redis is disabled, or when Redis could not be reached: a Redis outage must
    /// degrade toward the single-instance answer, never toward "definitely not connected", because
    /// every caller of this method treats a false answer as license to end the game.
    /// </summary>
    public async Task<bool> IsConnectedAsync(Guid gameId, Guid userId, CancellationToken ct = default)
    {
        if (!_connection.Enabled) return IsConnected(gameId, userId);

        try
        {
            var entries = await _connection.Db.HashGetAllAsync(_connection.Key($"game:conns:{gameId}")).WaitAsync(ct);
            if (entries.Length == 0) return false;

            var live = await _instances.LiveInstancesAsync(ct);
            foreach (var entry in entries)
            {
                var value = (string?)entry.Value;
                if (value is null) continue;
                var parts = value.Split(':', 2);
                if (parts.Length != 2 || !Guid.TryParse(parts[1], out var uid) || uid != userId) continue;
                if (live.Contains(parts[0])) return true;
            }
            return false;
        }
        catch (Exception ex) when (IsRedisOutage(ex))
        {
            _logger.LogWarning(ex, "Failed to check cross-instance connectivity for game {GameId}; falling back to this instance's local-only view.", gameId);
            return IsConnected(gameId, userId);
        }
    }

    /// <summary>Refreshes the connections hash TTL for a still-owned game (called by <see cref="GameOwnershipHeartbeat"/>). No-op when disabled.</summary>
    public async Task RefreshConnectionsTtlAsync(Guid gameId, CancellationToken ct = default)
    {
        if (!_connection.Enabled) return;
        try
        {
            await _connection.Db.KeyExpireAsync(_connection.Key($"game:conns:{gameId}"), TimeSpan.FromSeconds(ConnsTtlSeconds)).WaitAsync(ct);
        }
        catch (Exception ex) when (IsRedisOutage(ex))
        {
            _logger.LogWarning(ex, "Failed to refresh the connections TTL for game {GameId}.", gameId);
        }
    }

    /// <summary>
    /// Snapshot of the live tracked connections in a game, as (connectionId, userId) pairs, what
    /// <see cref="LatencyProbe"/> walks (Phase 6f.2). Never null.
    /// </summary>
    public IReadOnlyList<(string ConnectionId, Guid UserId)> ConnectionsIn(Guid gameId) =>
        _byConnection
            .Where(kv => kv.Value.GameId == gameId)
            .Select(kv => (kv.Key, kv.Value.UserId))
            .ToArray();

    /// <summary>
    /// Cancels a pending abandonment (the player returned in time). Returns true if one was armed.
    ///
    /// <b>Ownership rule for <see cref="_pending"/>, and the whole reason this is not a leak.</b>
    /// Whoever takes a <see cref="CancellationTokenSource"/> out of the dictionary, or replaces it,
    /// owns cancelling and disposing it, and does so exactly once. There are three such places: this
    /// method (removes, then cancels and disposes), <see cref="RunGraceAsync"/>'s arming
    /// <c>AddOrUpdate</c> (replaces a still-armed timer, then cancels and disposes the one it
    /// displaced), and <see cref="RunGraceAsync"/>'s <c>finally</c> (removes its own entry, and
    /// disposes only if that removal actually won). A timer that was cancelled by somebody else finds
    /// its entry already gone or already replaced, so it never disposes a source twice and never
    /// evicts a newer timer's entry.
    /// </summary>
    public bool CancelPendingAbandon(Guid gameId, Guid userId)
    {
        if (_pending.TryRemove((gameId, userId), out var cts))
        {
            cts.Cancel();
            cts.Dispose();
            return true;
        }
        return false;
    }

    /// <summary>Handles a dropped connection: HDELs the cross-instance record, notifies the group and arms the forfeit timer.</summary>
    public void OnDisconnected(string connectionId)
    {
        if (!_byConnection.TryRemove(connectionId, out var info)) return;

        if (_connection.Enabled)
            _ = RemoveConnHashFieldAsync(info.GameId, connectionId);

        // A locally-known finished game needs no grace timer. A session absent from this instance's
        // registry is *not* evidence the game is gone, it may simply be owned elsewhere, so the
        // timer is armed regardless; GameActionExecutor.AbandonAsync (via RouteAsync) safely no-ops
        // against an already-finished game either way.
        var localSession = _registry.Get(info.GameId);
        if (localSession is not null && localSession.IsOver) return;

        // Solo/bot games sit one human on both seats. Broadcasting "opponent disconnected" is a lie
        // (there is no opponent) and the reconnect race on the one connection flashes it at the
        // player about themselves.
        var notifyOpponent = localSession is null
            || localSession.WhitePlayerId != localSession.BlackPlayerId;
        // Captured here, before the grace task yields. A spare tab still open means the player
        // never left: the timer still arms (it has to notice that), but S-2-6 does not count it.
        // A flap that returns a moment later still counts, because this was already true.
        var playerLeft = !IsConnected(info.GameId, info.UserId);
        _ = RunGraceAsync(info.GameId, info.UserId, notifyOpponent, playerLeft);
    }

    private async Task RemoveConnHashFieldAsync(Guid gameId, string connectionId)
    {
        try
        {
            await _connection.Db.HashDeleteAsync(_connection.Key($"game:conns:{gameId}"), connectionId);
        }
        catch (Exception ex) when (IsRedisOutage(ex))
        {
            _logger.LogWarning(ex, "Failed to remove connection {ConnectionId} from game {GameId}'s connections hash; it will expire on its own TTL.", connectionId, gameId);
        }
    }

    private async Task RunGraceAsync(Guid gameId, Guid userId, bool notifyOpponent, bool playerLeft)
    {
        // Named record, not an anonymous object (Phase 9e.9): OpenAPI can only point a schema at a real
        // type. Byte-identical on the wire, SignalR's payload serializer still camelCases GraceSeconds
        // to graceSeconds, exactly what the anonymous object spelled by hand.
        if (notifyOpponent)
            await GameHubGroups.SendToRoomAsync(
                _hub.Clients, gameId, "OpponentDisconnected", new OpponentDisconnectedDto(GraceSeconds));

        var key = (gameId, userId);
        var cts = new CancellationTokenSource();

        // Atomic arm. _pending is keyed by (game, user), not by connection, so a player with two tabs
        // open, or one tab that drops twice inside the 30s window, reaches here twice. A bare indexer
        // assignment would drop the first timer's source on the floor without cancelling it: the orphan
        // would keep running and, at the end of *its* window, remove the newer timer's entry, so the
        // player's eventual reconnect would find nothing armed and the opponent would never be told
        // they came back. Displacing under AddOrUpdate cancels and disposes what it replaces.
        _pending.AddOrUpdate(key, cts, (_, displaced) =>
        {
            displaced.Cancel();
            displaced.Dispose();
            return cts;
        });

        // S-2-6. The arm itself is the event: a return inside the window cancels the timer and
        // never becomes an Abandoned game, so this is the only place the flap exists.
        if (playerLeft)
            _graceDisconnects?.RecordArmed(gameId, userId);

        try
        {
            await Task.Delay(_grace, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return; // reconnected within grace, on this or another instance, HandleReconnectAsync already broadcast it
        }
        finally
        {
            // Remove only our own entry, never whatever is armed now: a newer timer may already have
            // replaced us. The KeyValuePair overload is a compare-and-remove on the source reference,
            // and winning it is also what makes this the disposer (see CancelPendingAbandon's rule).
            if (_pending.TryRemove(new KeyValuePair<(Guid GameId, Guid UserId), CancellationTokenSource>(key, cts)))
                cts.Dispose();
        }

        // Last line of defense before forfeiting somebody who might, in fact, still be at the board:
        // a lost reconnect notice (a rare Redis blip) must not cost a connected player their game.
        if (await IsConnectedAsync(gameId, userId))
        {
            // They are connected, so the notice this timer broadcast is now false and has to be
            // retracted, or it stands for the rest of the game: the client sets its banner on
            // OpponentDisconnected and clears it on OpponentReconnected and on nothing else.
            //
            // Two ways to get here, and both used to end with a permanently stuck banner. The common
            // one is a player with two tabs open closing one: the surviving tab means no forfeit and
            // also means no reconnect, so nothing was ever going to clear it. The rare one is the blip
            // this guard was written for, where the player did reconnect but the cross-instance notice
            // was lost, so CancelPendingAbandon never fired on the instance holding the timer.
            //
            // Nobody is excluded from this one. There is no reconnecting connection to spare, and the
            // player's own surviving tabs were shown the notice too, because OpponentDisconnected goes
            // to the whole group.
            if (notifyOpponent) await BroadcastOpponentReconnectedAsync(gameId, reconnectingConnectionId: null);
            return;
        }

        try
        {
            await _actions.AbandonAsync(gameId, userId);
        }
        catch (InvalidGameActionException)
        {
            // Already over, the finishing path (wherever it ran) persisted it.
        }
        catch (HubException)
        {
            // Same condition, reported by a remote owner and translated by GameActionExecutor's
            // forwarding path (README RPC rule 1), the message is the same either way, so both
            // exception types mean exactly the same thing here: nothing left to do.
        }
    }

    private static bool IsRedisOutage(Exception ex) => ex is RedisConnectionException or RedisTimeoutException;
}
