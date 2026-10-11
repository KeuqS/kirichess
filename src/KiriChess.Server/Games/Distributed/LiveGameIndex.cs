using System.Text.Json;
using KiriChess.Engine.Core;
using KiriChess.Server.Infrastructure.Redis;
using StackExchange.Redis;

namespace KiriChess.Server.Games.Distributed;

/// <summary>
/// Owns the Redis read-models a live game publishes for the rest of the cluster (Phase 8.4):
/// <c>{p}game:state:{gameId}</c> (the <see cref="LiveGameStateDto"/> itself), <c>{p}games:live</c>
/// (freshest-first index Task C's live-games list reads), <c>{p}players:ingame</c> (who is
/// currently seated in a live game, on any instance), and <c>{p}player:games:{userId}</c> (the
/// per-player live-game index I-1-5 uses so <c>/api/v1/account/playing</c> does not scan the
/// cluster). All of these are cache with a safety expiry, not a record: Postgres remains the durable
/// store (README "Out of scope"), so a crashed owner's stale entries must age out on their own rather
/// than needing an explicit cleanup pass.
/// </summary>
public sealed class LiveGameIndex
{
    // Held once rather than allocated per call, same discipline as every other JSON boundary in this
    // codebase (see GameCommandRouter, GameOwnershipHeartbeat).
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    // 1 hour, refreshed on every publish (README key-schema table): long enough that a normally-paced
    // game (heartbeat-refreshed every HeartbeatSeconds) never expires mid-play, short enough that an
    // owner that died without a graceful OnGameEndedAsync does not haunt the public live list forever.
    private const int StateTtlSeconds = 3600;

    private readonly IRedisConnection _connection;
    private readonly InstanceIdentity _instance;
    private readonly ILogger<LiveGameIndex> _logger;

    public LiveGameIndex(IRedisConnection connection, InstanceIdentity instance, ILogger<LiveGameIndex> logger)
    {
        _connection = connection;
        _instance = instance;
        _logger = logger;
    }

    /// <summary>
    /// Publishes/refreshes <c>{p}game:state:{gameId}</c> and <c>{p}games:live</c>. Does not touch
    /// <c>players:ingame</c> or <c>player:games:*</c>: set membership only changes when a game starts
    /// or ends (I-1-3), so those writes live in <see cref="SeatPlayersAsync"/> /
    /// <see cref="RemoveAsync"/>. Names and patron tiers are supplied by the caller rather than
    /// resolved here, <see cref="GameSession"/> has neither, and this method is called on the move
    /// path (<c>GameDistribution.OnGameChangedAsync</c>), so it must not add a DB round-trip of its
    /// own. A no-op when Redis is disabled.
    /// </summary>
    public async Task PublishAsync(GameSession session, string whiteName, string blackName, string whitePatronTier, string blackPatronTier, CancellationToken ct = default)
    {
        if (!_connection.Enabled) return;
        try
        {
            var dto = new LiveGameStateDto(
                session.Id,
                _instance.Id,
                session.WhitePlayerId,
                session.BlackPlayerId,
                whiteName,
                blackName,
                session.RatingPool,
                session.IsRated,
                session.Position.SideToMove.ToString(),
                session.Position.TimeFor(Side.White),
                session.Position.TimeFor(Side.Black),
                session.MoveCount,
                session.IsOver,
                session.CreatedAt,
                session.LastMoveAtUtc,
                DateTimeOffset.UtcNow,
                session.Position.Fen,
                whitePatronTier,
                blackPatronTier);
            var json = JsonSerializer.Serialize(dto, JsonOptions);
            var ttl = TimeSpan.FromSeconds(StateTtlSeconds);
            // Score is the expiry unix timestamp, not CreatedAt: LiveGamesAsync can then
            // ZREMRANGEBYSCORE away crashed-owner entries that sit past the read limit (I-1-3).
            var expiryScore = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + StateTtlSeconds;

            var db = _connection.Db;
            var batch = db.CreateBatch();
            var state = batch.StringSetAsync(_connection.Key($"game:state:{session.Id}"), json, ttl);
            var index = batch.SortedSetAddAsync(_connection.Key("games:live"), session.Id.ToString(), expiryScore);
            var whiteExpire = batch.KeyExpireAsync(_connection.Key($"player:games:{session.WhitePlayerId}"), ttl);
            var blackExpire = session.WhitePlayerId == session.BlackPlayerId
                ? Task.CompletedTask
                : batch.KeyExpireAsync(_connection.Key($"player:games:{session.BlackPlayerId}"), ttl);
            batch.Execute();
            await Task.WhenAll(state, index, whiteExpire, blackExpire).WaitAsync(ct);
        }
        catch (Exception ex) when (IsRedisOutage(ex))
        {
            _logger.LogWarning(ex, "Failed to publish live state for game {GameId}.", session.Id);
        }
    }

    /// <summary>
    /// Adds both players to <c>{p}players:ingame</c> and this game to each player's
    /// <c>{p}player:games:{userId}</c> set. Called from create (and from the first publish after a
    /// recovery or a lease reclaim), never from the move path. Idempotent SADD. No-op when disabled.
    /// </summary>
    public async Task SeatPlayersAsync(GameSession session, CancellationToken ct = default)
    {
        if (!_connection.Enabled) return;
        try
        {
            var ttl = TimeSpan.FromSeconds(StateTtlSeconds);
            var gameId = (RedisValue)session.Id.ToString();
            var white = session.WhitePlayerId.ToString();
            var black = session.BlackPlayerId.ToString();
            var playerIds = session.WhitePlayerId == session.BlackPlayerId
                ? new RedisValue[] { white }
                : new RedisValue[] { white, black };

            var db = _connection.Db;
            var batch = db.CreateBatch();
            var players = batch.SetAddAsync(_connection.Key("players:ingame"), playerIds);
            var whiteGames = batch.SetAddAsync(_connection.Key($"player:games:{session.WhitePlayerId}"), gameId);
            var whiteTtl = batch.KeyExpireAsync(_connection.Key($"player:games:{session.WhitePlayerId}"), ttl);
            Task blackGames = Task.CompletedTask;
            Task blackTtl = Task.CompletedTask;
            if (session.WhitePlayerId != session.BlackPlayerId)
            {
                blackGames = batch.SetAddAsync(_connection.Key($"player:games:{session.BlackPlayerId}"), gameId);
                blackTtl = batch.KeyExpireAsync(_connection.Key($"player:games:{session.BlackPlayerId}"), ttl);
            }
            batch.Execute();
            await Task.WhenAll(players, whiteGames, whiteTtl, blackGames, blackTtl).WaitAsync(ct);
        }
        catch (Exception ex) when (IsRedisOutage(ex))
        {
            _logger.LogWarning(ex, "Failed to seat players for game {GameId}.", session.Id);
        }
    }

    /// <summary>
    /// Removes the state key, the live-index entry, both player seats, and this game from each
    /// player's per-player index. Called exactly once, when the game is persisted as finished
    /// (<c>GameDistribution.OnGameEndedAsync</c>, after the Postgres write, see that type's comment
    /// for the ordering reason). No-op when disabled.
    /// </summary>
    public async Task RemoveAsync(GameSession session, CancellationToken ct = default)
    {
        if (!_connection.Enabled) return;
        try
        {
            var gameId = (RedisValue)session.Id.ToString();
            var white = session.WhitePlayerId.ToString();
            var black = session.BlackPlayerId.ToString();
            var playerIds = session.WhitePlayerId == session.BlackPlayerId
                ? new RedisValue[] { white }
                : new RedisValue[] { white, black };

            var db = _connection.Db;
            var batch = db.CreateBatch();
            var state = batch.KeyDeleteAsync(_connection.Key($"game:state:{session.Id}"));
            var index = batch.SortedSetRemoveAsync(_connection.Key("games:live"), session.Id.ToString());
            var players = batch.SetRemoveAsync(_connection.Key("players:ingame"), playerIds);
            var whiteGames = batch.SetRemoveAsync(_connection.Key($"player:games:{session.WhitePlayerId}"), gameId);
            var blackGames = session.WhitePlayerId == session.BlackPlayerId
                ? Task.CompletedTask
                : batch.SetRemoveAsync(_connection.Key($"player:games:{session.BlackPlayerId}"), gameId);
            batch.Execute();
            await Task.WhenAll(state, index, players, whiteGames, blackGames).WaitAsync(ct);
        }
        catch (Exception ex) when (IsRedisOutage(ex))
        {
            _logger.LogWarning(ex, "Failed to remove live-state entries for game {GameId}; they will expire on their own TTL.", session.Id);
        }
    }

    /// <summary>
    /// Every live game's published state, freshest-first, capped at <paramref name="limit"/>. Reads
    /// <c>{p}games:live</c> then fetches every state key in one pipelined batch (never a loop of
    /// individual awaits, same pattern as <c>InstanceRegistry.LiveInstancesAsync</c>). An id whose
    /// state key has already expired is dropped from the result and <c>ZREM</c>'d. Entries whose
    /// expiry score has passed are dropped with <c>ZREMRANGEBYSCORE</c> before the limited read, so
    /// a crashed owner's finished-game ids that sit past the read window do not grow the set
    /// forever (I-1-3). Returns empty when Redis is disabled or on a Redis failure, never fabricated data.
    /// </summary>
    public async Task<IReadOnlyList<LiveGameStateDto>> LiveGamesAsync(int limit, CancellationToken ct = default)
    {
        if (!_connection.Enabled) return Array.Empty<LiveGameStateDto>();
        try
        {
            var db = _connection.Db;
            var liveKey = _connection.Key("games:live");
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await db.SortedSetRemoveRangeByScoreAsync(liveKey, double.NegativeInfinity, now).WaitAsync(ct);

            if (limit < 1) return Array.Empty<LiveGameStateDto>();

            // Remaining members are unexpired. Rank by score descending so the most recently
            // published (highest expiry) come first, the same freshest-first order the live list
            // used when the score was CreatedAt.
            var ids = await db.SortedSetRangeByRankAsync(liveKey, 0, limit - 1, Order.Descending).WaitAsync(ct);
            if (ids.Length == 0) return Array.Empty<LiveGameStateDto>();

            var batch = db.CreateBatch();
            var fetches = ids
                .Select(id => (Id: (string)id!, Value: batch.StringGetAsync(_connection.Key($"game:state:{id}"))))
                .ToList();
            batch.Execute();
            await Task.WhenAll(fetches.Select(f => f.Value)).WaitAsync(ct);

            var results = new List<LiveGameStateDto>();
            var expired = new List<RedisValue>();
            foreach (var (id, value) in fetches)
            {
                if (value.Result.IsNullOrEmpty) { expired.Add(id); continue; }
                try
                {
                    var dto = JsonSerializer.Deserialize<LiveGameStateDto>((string)value.Result!, JsonOptions);
                    if (dto is not null) results.Add(dto);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Corrupt live-state entry for game {GameId}; dropping it.", id);
                    expired.Add(id);
                }
            }

            if (expired.Count > 0)
                await db.SortedSetRemoveAsync(liveKey, expired.ToArray()).WaitAsync(ct);

            return results;
        }
        catch (Exception ex) when (IsRedisOutage(ex))
        {
            _logger.LogWarning(ex, "Failed to read the live-games index.");
            return Array.Empty<LiveGameStateDto>();
        }
    }

    /// <summary>
    /// Live games this user is seated in, read from <c>{p}player:games:{userId}</c> rather than by
    /// scanning the cluster-wide live list (I-1-5). Stale ids whose state key has expired are
    /// dropped from the set. Empty when disabled or on a Redis failure.
    /// </summary>
    public async Task<IReadOnlyList<LiveGameStateDto>> PlayingGamesAsync(Guid userId, CancellationToken ct = default)
    {
        if (!_connection.Enabled) return Array.Empty<LiveGameStateDto>();
        try
        {
            var db = _connection.Db;
            var setKey = _connection.Key($"player:games:{userId}");
            var ids = await db.SetMembersAsync(setKey).WaitAsync(ct);
            if (ids.Length == 0) return Array.Empty<LiveGameStateDto>();

            var batch = db.CreateBatch();
            var fetches = ids
                .Select(id => (Id: (string)id!, Value: batch.StringGetAsync(_connection.Key($"game:state:{id}"))))
                .ToList();
            batch.Execute();
            await Task.WhenAll(fetches.Select(f => f.Value)).WaitAsync(ct);

            var results = new List<LiveGameStateDto>();
            var stale = new List<RedisValue>();
            foreach (var (id, value) in fetches)
            {
                if (value.Result.IsNullOrEmpty) { stale.Add(id); continue; }
                try
                {
                    var dto = JsonSerializer.Deserialize<LiveGameStateDto>((string)value.Result!, JsonOptions);
                    if (dto is null || dto.IsOver || (dto.WhitePlayerId != userId && dto.BlackPlayerId != userId))
                    {
                        stale.Add(id);
                        continue;
                    }
                    results.Add(dto);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Corrupt live-state entry for game {GameId}; dropping it.", id);
                    stale.Add(id);
                }
            }

            if (stale.Count > 0)
                await db.SetRemoveAsync(setKey, stale.ToArray()).WaitAsync(ct);

            return results;
        }
        catch (Exception ex) when (IsRedisOutage(ex))
        {
            _logger.LogWarning(ex, "Failed to read playing games for user {UserId}.", userId);
            return Array.Empty<LiveGameStateDto>();
        }
    }

    /// <summary>Distinct user ids sitting in a live game on any instance. Empty when disabled or on a Redis failure.</summary>
    public async Task<IReadOnlySet<Guid>> PlayersInGameAsync(CancellationToken ct = default)
    {
        if (!_connection.Enabled) return new HashSet<Guid>();
        try
        {
            var members = await _connection.Db.SetMembersAsync(_connection.Key("players:ingame")).WaitAsync(ct);
            var result = new HashSet<Guid>();
            foreach (var member in members)
                if (Guid.TryParse((string?)member, out var id)) result.Add(id);
            return result;
        }
        catch (Exception ex) when (IsRedisOutage(ex))
        {
            _logger.LogWarning(ex, "Failed to read players-in-game from Redis.");
            return new HashSet<Guid>();
        }
    }

    /// <summary>
    /// Cluster-wide seated-player count via <c>SCARD</c>, for callers that do not need the id set
    /// (I-1-3). Zero when disabled or on a Redis failure.
    /// </summary>
    public async Task<int> PlayersInGameCountAsync(CancellationToken ct = default)
    {
        if (!_connection.Enabled) return 0;
        try
        {
            return (int)await _connection.Db.SetLengthAsync(_connection.Key("players:ingame")).WaitAsync(ct);
        }
        catch (Exception ex) when (IsRedisOutage(ex))
        {
            _logger.LogWarning(ex, "Failed to count players-in-game from Redis.");
            return 0;
        }
    }

    /// <summary>
    /// <c>SISMEMBER</c> on <c>{p}players:ingame</c>. The eligibility path only needs a yes/no for one
    /// user, not the whole seated set (I-1-3). False when disabled or on a Redis failure.
    /// </summary>
    public async Task<bool> IsPlayerInGameAsync(Guid userId, CancellationToken ct = default)
    {
        if (!_connection.Enabled) return false;
        try
        {
            return await _connection.Db.SetContainsAsync(_connection.Key("players:ingame"), userId.ToString()).WaitAsync(ct);
        }
        catch (Exception ex) when (IsRedisOutage(ex))
        {
            _logger.LogWarning(ex, "Failed to check whether user {UserId} is seated.", userId);
            return false;
        }
    }

    private static bool IsRedisOutage(Exception ex) => ex is RedisConnectionException or RedisTimeoutException;
}
