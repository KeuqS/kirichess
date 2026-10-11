using KiriChess.Server.Infrastructure.Redis;
using KiriChess.Server.Ratings;
using StackExchange.Redis;

namespace KiriChess.Server.Games;

/// <summary>One player waiting in a matchmaking pool.</summary>
public readonly record struct Waiting(Guid UserId, string ConnectionId, double Rating);

/// <summary>One row of the public open-seek list (Phase 9e.4): a pool with someone waiting in it, and since when.</summary>
public readonly record struct OpenSeek(string Pool, DateTimeOffset CreatedAt);

/// <summary>
/// Matchmaking queue: per-pool waiting lists, pairing the closest-rated *different* user. No DB, no
/// game creation, that lives in <see cref="MatchmakingService"/>, so this stays unit-testable.
///
/// Phase 8.4 (Task C): matchmaking is the one subsystem in this phase where going multi-instance
/// without a fix is actively harmful (see Phase 8.4 "Why
/// this task matters more than it looks"), two players who click "Play" at the same moment and land
/// on different instances would wait forever next to each other under the old per-process
/// <c>Dictionary&lt;string, List&lt;Waiting&gt;&gt;</c>. <see cref="TryPairAsync"/>/<see cref="RemoveAsync"/>/
/// <see cref="DepthsAsync"/> add a nullable Redis collaborator (README scope decision 1) that pairs
/// across every live instance via one Redis ZSET per pool. The original synchronous <see cref="TryPair"/>/
/// <see cref="Remove"/>/<see cref="Depths"/> below are unchanged, with no collaborator (the
/// parameterless constructor, what <c>MatchQueueTests</c> uses directly) the *Async members simply
/// delegate to them, so single-instance behaviour is not a degraded copy, it is the same code.
///
/// Phase 9e.4: <see cref="OpenSeeks"/>/<see cref="OpenSeeksAsync"/> add a read-only view over the
/// same waiting lists for the public seek list. They introduce no new matching logic, "accepting" a
/// seek is just <see cref="TryPair"/>/<see cref="TryPairAsync"/> joining that pool, same as it
/// always was, this only adds the ability to see who is already there before you queue blind.
/// </summary>
public sealed class MatchQueue
{
    #region Local (Redis-disabled) implementation, unchanged from before Phase 8.4

    private readonly object _lock = new();
    private readonly Dictionary<string, List<Waiting>> _pools = new();

    // Phase 9e.4: when a connection was enqueued (not matched), keyed by connection id. Display-only,
    // read by OpenSeeks; pairing itself never consults it. Kept alongside _pools rather than folded
    // into Waiting because Waiting is also the *return value* of a match, where "when did the loser
    // start waiting" is meaningless.
    private readonly Dictionary<string, DateTimeOffset> _createdAt = new();

    /// <summary>
    /// Adds <paramref name="entrant"/> to <paramref name="pool"/> and, if an opponent is already
    /// waiting, removes and returns the closest-rated one (a match). Returns null if the entrant was
    /// simply enqueued. A re-entry by the same connection replaces its prior waiting entry, and the
    /// entrant is removed from every *other* pool first (Phase 6e.3), sitting in six queues at once
    /// is not a feature, it is the cheapest way for one user to occupy the whole lobby.
    /// </summary>
    public Waiting? TryPair(string pool, Waiting entrant)
    {
        lock (_lock)
        {
            foreach (var other in _pools.Values)
            {
                var swept = other.Where(w => w.ConnectionId == entrant.ConnectionId || w.UserId == entrant.UserId);
                foreach (var w in swept) _createdAt.Remove(w.ConnectionId);
                other.RemoveAll(w => w.ConnectionId == entrant.ConnectionId || w.UserId == entrant.UserId);
            }

            var list = _pools.TryGetValue(pool, out var l) ? l : (_pools[pool] = new List<Waiting>());

            Waiting? best = null;
            double bestGap = double.MaxValue;
            foreach (var w in list)
            {
                if (w.UserId == entrant.UserId) continue;
                var gap = Math.Abs(w.Rating - entrant.Rating);
                if (gap < bestGap) { bestGap = gap; best = w; }
            }

            if (best is { } opponent)
            {
                list.RemoveAll(w => w.ConnectionId == opponent.ConnectionId);
                _createdAt.Remove(opponent.ConnectionId);
                return opponent;
            }

            list.Add(entrant);
            _createdAt[entrant.ConnectionId] = DateTimeOffset.UtcNow;
            return null;
        }
    }

    /// <summary>Removes any waiting entry with this connection id from every pool.</summary>
    public void Remove(string connectionId)
    {
        lock (_lock)
        {
            foreach (var list in _pools.Values)
                list.RemoveAll(w => w.ConnectionId == connectionId);
            _createdAt.Remove(connectionId);
        }
    }

    /// <summary>
    /// C-6-1: removes every waiting entry belonging to <paramref name="userId"/>, from every pool.
    ///
    /// <see cref="Remove"/> above is keyed by connection id because a hub disconnect is the only thing
    /// that knows one. The other three paths that create a live game (challenge accept, rematch accept,
    /// POST /api/games) are HTTP handlers or know only the opponent's user id, so a queue entry they
    /// have to clear is only addressable this way.
    ///
    /// Sweeping every pool and every match rather than stopping at the first is the same unit
    /// <see cref="TryPair"/>'s own step 1 already clears on a fresh join: "this user, anywhere".
    /// </summary>
    public void RemoveUser(Guid userId)
    {
        lock (_lock)
        {
            foreach (var list in _pools.Values)
            {
                var swept = list.Where(w => w.UserId == userId);
                foreach (var w in swept) _createdAt.Remove(w.ConnectionId);
                list.RemoveAll(w => w.UserId == userId);
            }
        }
    }

    /// <summary>
    /// Open seeks (Phase 9e.4): one row per waiting player, oldest first, for the public seek list.
    /// <paramref name="maxAge"/> hides (but does not remove) an entry stale enough that it is
    /// probably a connection whose disconnect handler never ran; a genuine pairing attempt on that
    /// pool still finds and pairs it regardless, this is purely a display-time filter.
    /// </summary>
    public IReadOnlyList<OpenSeek> OpenSeeks(TimeSpan maxAge, IReadOnlyList<string>? pools = null)
    {
        var allowed = pools ?? RatingConstants.OfficialPools;
        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow;
            return _pools
                .Where(kv => allowed.Contains(kv.Key))
                .SelectMany(kv => kv.Value.Select(w => (Pool: kv.Key, w.ConnectionId)))
                .Select(e => (e.Pool, e.ConnectionId, CreatedAt: _createdAt.GetValueOrDefault(e.ConnectionId)))
                .Where(e => e.CreatedAt != default && now - e.CreatedAt <= maxAge)
                .OrderBy(e => e.CreatedAt)
                .Select(e => new OpenSeek(e.Pool, e.CreatedAt))
                .ToList();
        }
    }

    /// <summary>
    /// Waiting count per pool, snapshotted under the lock (6d.3). Pools that have ever been entered
    /// stay present with a count of zero, which is deliberate: a pool that drops off the scrape
    /// entirely is indistinguishable from a pool nobody has tried, and "0 waiting" is the more
    /// useful signal.
    /// </summary>
    public IReadOnlyDictionary<string, int> Depths()
    {
        lock (_lock)
        {
            return _pools.ToDictionary(kv => kv.Key, kv => kv.Value.Count);
        }
    }

    #endregion

    // Runs entirely inside one EVAL: pairing is a read-then-two-writes, and two instances running it
    // concurrently without atomicity could hand the same waiting player to both entrants, two games
    // for one person, which the "already in a game" check would then reject at random.
    //
    // KEYS = every matchmaking pool's queue key, in RatingConstants.MatchmakingPools order
    // (human official pools then bot:{preset}).
    // ARGV[1] = 1-based index into KEYS of the entrant's target pool
    // ARGV[2] = entrant member string "{instanceId}|{connectionId}|{userId}"
    // ARGV[3] = entrant connection id
    // ARGV[4] = entrant user id
    // ARGV[5] = entrant rating (the ZADD score)
    // ARGV[6..] = every currently-live instance id, for pruning dead candidates
    private const string PairScript = """
        local targetKey = KEYS[tonumber(ARGV[1])]
        local entrantMember = ARGV[2]
        local entrantConn = ARGV[3]
        local entrantUser = ARGV[4]
        local entrantScore = tonumber(ARGV[5])

        -- Step 1 (Phase 6e.3): remove the entrant's own previous entries, by connection id or user
        -- id, from every pool, not just the target one. One user waiting in six pools at once is the
        -- cheapest way to occupy the whole lobby, so a fresh join always sweeps every other pool first.
        for i = 1, #KEYS do
            local members = redis.call('ZRANGE', KEYS[i], 0, -1)
            for _, m in ipairs(members) do
                local sep1 = string.find(m, '|')
                local sep2 = string.find(m, '|', sep1 + 1)
                local conn = string.sub(m, sep1 + 1, sep2 - 1)
                local user = string.sub(m, sep2 + 1)
                if conn == entrantConn or user == entrantUser then
                    redis.call('ZREM', KEYS[i], m)
                end
            end
        end

        -- Live-instance lookup table, for O(1) membership checks below.
        local live = {}
        for i = 6, #ARGV do live[ARGV[i]] = true end

        -- Step 2: gather candidates around the entrant's score, both directions, 8 each way is
        -- plenty of headroom to skip self-matches and dead instances without scanning the whole ZSET.
        local above = redis.call('ZRANGEBYSCORE', targetKey, entrantScore, '+inf', 'WITHSCORES', 'LIMIT', 0, 8)
        local below = redis.call('ZREVRANGEBYSCORE', targetKey, entrantScore, '-inf', 'WITHSCORES', 'LIMIT', 0, 8)

        local best = nil
        local bestGap = nil

        local function consider(raw)
            for i = 1, #raw, 2 do
                local m = raw[i]
                local score = tonumber(raw[i + 1])
                local sep1 = string.find(m, '|')
                local sep2 = string.find(m, '|', sep1 + 1)
                local instanceId = string.sub(m, 1, sep1 - 1)
                local user = string.sub(m, sep2 + 1)
                if user == entrantUser then
                    -- never self-pair
                elseif not live[instanceId] then
                    -- Step 3: prune the dead instance's entry as we skip it, the only thing that
                    -- clears the queue of players whose machine disappeared mid-wait.
                    redis.call('ZREM', targetKey, m)
                else
                    local gap = math.abs(score - entrantScore)
                    -- Strict '<', matching the in-memory TryPair's tie-break: when two candidates are
                    -- equidistant the first one considered wins, arbitrary, but deterministic.
                    if bestGap == nil or gap < bestGap then
                        bestGap = gap
                        best = m
                    end
                end
            end
        end

        consider(above)
        consider(below)

        -- Step 4: a survivor is matched and removed; otherwise the entrant joins the queue.
        if best ~= nil then
            redis.call('ZREM', targetKey, best)
            return best
        end

        redis.call('ZADD', targetKey, entrantScore, entrantMember)
        return false
        """;

    private readonly IRedisConnection? _connection;
    private readonly InstanceIdentity _instance;
    private readonly ILogger<MatchQueue>? _logger;

    /// <summary>No Redis collaborator, the exact pre-8.4 in-memory queue, which is what MatchQueueTests exercises directly.</summary>
    public MatchQueue()
    {
        _instance = new InstanceIdentity("local");
    }

    /// <summary>Cluster-aware constructor (Phase 8.4), used by <see cref="MatchmakingService"/>.</summary>
    public MatchQueue(IRedisConnection connection, InstanceIdentity instance, ILogger<MatchQueue> logger)
    {
        _connection = connection;
        _instance = instance;
        _logger = logger;
    }

    /// <summary>
    /// Cluster-aware pairing (Phase 8.4). <paramref name="liveInstances"/> is supplied by the caller
    /// (<see cref="InstanceRegistry.LiveInstancesAsync"/>) rather than resolved here, so this class
    /// stays unit-testable against a fake set. With no collaborator, on a Redis failure, or when
    /// disabled, delegates to <see cref="TryPair"/> unchanged, a dead Redis must make matchmaking
    /// fall back to this instance's own queue, never 500 the caller. That fallback can strand a
    /// concurrent Redis-side waiter (their entry is in Redis, this call only ever looks at the local
    /// dictionary), which is an accepted cost of "wrong, not down" for the duration of the outage.
    /// </summary>
    public async Task<Waiting?> TryPairAsync(
        string pool, Waiting entrant, IReadOnlySet<string> liveInstances, CancellationToken ct = default)
    {
        if (_connection is not { Enabled: true } connection) return TryPair(pool, entrant);

        try
        {
            return await TryPairRedisAsync(connection, pool, entrant, liveInstances, ct);
        }
        catch (Exception ex) when (IsRedisOutage(ex))
        {
            _logger?.LogWarning(ex, "Failed to run the cluster pairing script for pool {Pool}; falling back to this instance's local queue for this call.", pool);
            return TryPair(pool, entrant);
        }
    }

    /// <summary>
    /// Cluster-aware version of <see cref="Remove"/>: ZREMs the connection from every pool. Delegates
    /// to <see cref="Remove"/> with no collaborator or on a Redis failure.
    ///
    /// The local <see cref="Remove"/> runs on <b>both</b> paths, not just the disabled one, and that is
    /// not redundancy. <see cref="TryPairAsync"/> falls back to the in-memory <see cref="TryPair"/>
    /// during a Redis outage, which enqueues the entrant into the local dictionary; once Redis comes
    /// back, a Redis-path removal that skipped the dictionary would leave that entry there forever,
    /// and the next outage would happily pair somebody against a connection that left hours ago. It is
    /// a no-op in the normal Redis path, nothing is ever added locally there, so the cost is one
    /// dictionary miss to close a leak that only a Redis outage can open.
    /// </summary>
    public Task RemoveAsync(string connectionId, CancellationToken ct = default)
    {
        Remove(connectionId);
        return RemoveWhereAsync(m => ConnectionOf(m) == connectionId, $"connection {connectionId}", ct);
    }

    /// <summary>
    /// Cluster-aware version of <see cref="RemoveUser"/> (C-6-1), and the reason this half of the
    /// finding is implementable at all: the Redis queue is one ZSET per pool under
    /// <c>{prefix}queue:{pool}</c>, shared by every instance, and an entry is addressed by its member
    /// string rather than routed to the instance that enqueued it. A player queued through a hub
    /// connection on instance A is therefore removed correctly by an HTTP call served by instance B;
    /// the instance id inside the member is only used for liveness pruning, never for addressing.
    ///
    /// The one case it does not reach is the same one <see cref="TryPairAsync"/> already documents and
    /// accepts: during a Redis outage the fallback enqueues into instance A's local dictionary, which
    /// no other instance can see. That is pre-existing "wrong, not down" behaviour for the duration of
    /// the outage, not a gap this method introduces.
    ///
    /// Costs exactly what <see cref="RemoveAsync"/> costs, since that one is already a scan: both read
    /// every pool's members in one batch and filter in process. The by-connection scan runs on every
    /// single hub disconnect, which is far more frequent than game creation.
    /// </summary>
    public Task RemoveUserAsync(Guid userId, CancellationToken ct = default)
    {
        RemoveUser(userId);
        return RemoveWhereAsync(
            m => UserOf(m) is { } u && Guid.TryParse(u, out var parsed) && parsed == userId,
            $"user {userId}", ct);
    }

    /// <summary>
    /// The shared Redis half of <see cref="RemoveAsync"/> and <see cref="RemoveUserAsync"/>: read every
    /// pool, drop every member <paramref name="matches"/> accepts. Shared rather than copied because
    /// the companion-hash cleanup below is the part that silently rots when one copy is updated.
    /// <paramref name="target"/> is for the log line only.
    /// </summary>
    private async Task RemoveWhereAsync(Func<RedisValue, bool> matches, string target, CancellationToken ct)
    {
        if (_connection is not { Enabled: true } connection) return;

        try
        {
            var pools = RatingConstants.MatchmakingPools;
            var db = connection.Db;
            var fetchBatch = db.CreateBatch();
            var fetches = pools
                .Select(p => (Pool: p, Members: fetchBatch.SortedSetRangeByScoreAsync(connection.Key($"queue:{p}"))))
                .ToList();
            fetchBatch.Execute();
            await Task.WhenAll(fetches.Select(f => f.Members)).WaitAsync(ct);

            var removeBatch = db.CreateBatch();
            var removeTasks = new List<Task>();
            var removedMembers = new List<RedisValue>();
            foreach (var (pool, membersTask) in fetches)
            {
                var toRemove = membersTask.Result
                    .Where(matches)
                    .ToArray();
                if (toRemove.Length > 0)
                {
                    removeTasks.Add(removeBatch.SortedSetRemoveAsync(connection.Key($"queue:{pool}"), toRemove));
                    removedMembers.AddRange(toRemove);
                }
            }
            // Phase 9e.4: the companion open-seek timestamp lives outside the ZSETs above, so an
            // explicit leave/disconnect must clear it too, or a player who left shows up "waiting"
            // until the display-time TTL catches up.
            if (removedMembers.Count > 0)
                removeTasks.Add(removeBatch.HashDeleteAsync(connection.Key("queue:created"), removedMembers.ToArray()));
            if (removeTasks.Count > 0)
            {
                removeBatch.Execute();
                await Task.WhenAll(removeTasks).WaitAsync(ct);
            }
        }
        catch (Exception ex) when (IsRedisOutage(ex))
        {
            _logger?.LogWarning(ex, "Failed to remove {Target} from the matchmaking queues in Redis.", target);
        }
    }

    /// <summary>
    /// Cluster-aware version of <see cref="Depths"/>: <c>ZCARD</c> per pool, batched, reporting every
    /// pool in <see cref="RatingConstants.MatchmakingPools"/> regardless of whether it has ever been
    /// entered, strictly better than the local <see cref="Depths"/>'s "stays present once entered"
    /// rule, and satisfies the same reasoning (a pool absent from the scrape is indistinguishable from
    /// one nobody has tried). Delegates to <see cref="Depths"/> with no collaborator or on a Redis failure.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, int>> DepthsAsync(CancellationToken ct = default)
    {
        if (_connection is not { Enabled: true } connection) return Depths();

        try
        {
            var pools = RatingConstants.MatchmakingPools;
            var db = connection.Db;
            var batch = db.CreateBatch();
            var fetches = pools
                .Select(p => (Pool: p, Count: batch.SortedSetLengthAsync(connection.Key($"queue:{p}"))))
                .ToList();
            batch.Execute();
            await Task.WhenAll(fetches.Select(f => f.Count)).WaitAsync(ct);
            return fetches.ToDictionary(f => f.Pool, f => (int)f.Count.Result);
        }
        catch (Exception ex) when (IsRedisOutage(ex))
        {
            _logger?.LogWarning(ex, "Failed to read matchmaking queue depths from Redis.");
            return Depths();
        }
    }

    /// <summary>
    /// Cluster-aware open seeks (Phase 9e.4), oldest wait first. Reads every pool's ZSET, looks each
    /// member's created-at up in the companion <c>queue:created</c> hash <see cref="TryPairRedisAsync"/>
    /// writes on enqueue, and drops (never guesses) an entry with no created record, the same "wrong,
    /// not fabricated" rule <c>LiveGameIndex.LiveGamesAsync</c> follows for its own expired entries.
    ///
    /// Also prunes entries whose owning instance is no longer in <paramref name="liveInstances"/>, the
    /// same zombie check <see cref="PairScript"/> runs during pairing, so a dead instance's stale seek
    /// disappears from the list even before another join happens to visit that exact pool. An entry
    /// older than <paramref name="maxAge"/> is filtered from the result but left in place, same as the
    /// local <see cref="OpenSeeks"/>: staleness here is about what to advertise, not what to delete.
    ///
    /// Delegates to <see cref="OpenSeeks"/> with no collaborator or on a Redis failure.
    /// </summary>
    public Task<IReadOnlyList<OpenSeek>> OpenSeeksAsync(
        IReadOnlySet<string> liveInstances, TimeSpan maxAge, CancellationToken ct = default) =>
        OpenSeeksAsync(liveInstances, maxAge, RatingConstants.OfficialPools, ct);

    /// <summary>
    /// Same as <see cref="OpenSeeksAsync(IReadOnlySet{string}, TimeSpan, CancellationToken)"/>, but
    /// restricted to <paramref name="pools"/>. First-party <c>/api/seeks</c> passes official keys so
    /// humans never see bot waits; the public API passes <see cref="RatingConstants.BotPools"/>.
    /// </summary>
    public async Task<IReadOnlyList<OpenSeek>> OpenSeeksAsync(
        IReadOnlySet<string> liveInstances, TimeSpan maxAge, IReadOnlyList<string> pools, CancellationToken ct = default)
    {
        if (_connection is not { Enabled: true } connection) return OpenSeeks(maxAge, pools);

        try
        {
            var db = connection.Db;
            var fetchBatch = db.CreateBatch();
            var fetches = pools
                .Select(p => (Pool: p, Members: fetchBatch.SortedSetRangeByScoreAsync(connection.Key($"queue:{p}"))))
                .ToList();
            fetchBatch.Execute();
            await Task.WhenAll(fetches.Select(f => f.Members)).WaitAsync(ct);

            var entries = fetches
                .SelectMany(f => f.Members.Result.Select(m => (f.Pool, Member: m)))
                .ToList();
            if (entries.Count == 0) return Array.Empty<OpenSeek>();

            var createdKey = connection.Key("queue:created");
            var createdValues = await db.HashGetAsync(createdKey, entries.Select(e => e.Member).ToArray()).WaitAsync(ct);

            var now = DateTimeOffset.UtcNow;
            var results = new List<OpenSeek>();
            var dead = new List<(string Pool, RedisValue Member)>();

            for (var i = 0; i < entries.Count; i++)
            {
                var (pool, member) = entries[i];
                var instanceId = InstanceOf(member);
                if (instanceId is null || !liveInstances.Contains(instanceId))
                {
                    dead.Add((pool, member));
                    continue;
                }
                if (createdValues[i].IsNullOrEmpty) continue; // no created record (yet, or a lost race): skip rather than guess
                var createdAt = DateTimeOffset.FromUnixTimeSeconds((long)createdValues[i]);
                if (now - createdAt > maxAge) continue; // stale: hidden from the list, not deleted
                results.Add(new OpenSeek(pool, createdAt));
            }

            if (dead.Count > 0)
            {
                var removeBatch = db.CreateBatch();
                var removeTasks = new List<Task>();
                foreach (var group in dead.GroupBy(d => d.Pool))
                    removeTasks.Add(removeBatch.SortedSetRemoveAsync(
                        connection.Key($"queue:{group.Key}"), group.Select(g => g.Member).ToArray()));
                removeTasks.Add(removeBatch.HashDeleteAsync(createdKey, dead.Select(d => d.Member).ToArray()));
                removeBatch.Execute();
                await Task.WhenAll(removeTasks).WaitAsync(ct);
            }

            return results.OrderBy(r => r.CreatedAt).ToList();
        }
        catch (Exception ex) when (IsRedisOutage(ex))
        {
            // C-6-7: every sibling in this file (TryPairAsync, RemoveAsync, DepthsAsync, and the
            // disabled-Redis branch of this very method above) falls back to the in-memory view on an
            // outage. Returning empty here instead told a first-time visitor "nobody is home" during
            // exactly the window TryPairAsync was falling back and filling the local dictionary with
            // real waiting players - the single message most likely to lose them.
            _logger?.LogWarning(ex, "Failed to read the open-seek list from Redis; falling back to this instance's local queue.");
            return OpenSeeks(maxAge, pools);
        }
    }

    private async Task<Waiting?> TryPairRedisAsync(
        IRedisConnection connection, string pool, Waiting entrant, IReadOnlySet<string> liveInstances, CancellationToken ct)
    {
        var pools = RatingConstants.MatchmakingPools;
        var targetIndex = -1;
        for (var i = 0; i < pools.Count; i++)
            if (pools[i] == pool) { targetIndex = i; break; }
        if (targetIndex < 0)
            throw new ArgumentException($"'{pool}' is not a matchmaking pool.", nameof(pool));

        var keys = pools.Select(p => (RedisKey)connection.Key($"queue:{p}")).ToArray();

        var argv = new List<RedisValue>
        {
            targetIndex + 1, // Lua arrays are 1-based
            Member(entrant),
            entrant.ConnectionId,
            entrant.UserId.ToString(),
            entrant.Rating,
        };
        argv.AddRange(liveInstances.Select(id => (RedisValue)id));

        var result = await connection.Db.ScriptEvaluateAsync(PairScript, keys, argv.ToArray()).WaitAsync(ct);

        // Phase 9e.4: the companion queue:created hash is kept outside PairScript's atomic EVAL (a
        // display-only side channel, not part of the pairing decision), so these two calls run best
        // effort right after. Failures here must never surface as a pairing failure, the ZADD/ZREM
        // above already committed: catch locally so the outer TryPairAsync catch, which exists to
        // fall back to *un-paired* local pairing, is never reached for a problem that has nothing to
        // pair left to retry.
        try
        {
            if (result.IsNull)
                await connection.Db.HashSetAsync(connection.Key("queue:created"), Member(entrant), DateTimeOffset.UtcNow.ToUnixTimeSeconds()).WaitAsync(ct);
            else
                await connection.Db.HashDeleteAsync(connection.Key("queue:created"), (string)result!).WaitAsync(ct);
        }
        catch (Exception ex) when (IsRedisOutage(ex))
        {
            _logger?.LogWarning(ex, "Failed to update the open-seek timestamp for pool {Pool}; the seek list may be briefly stale.", pool);
        }

        if (result.IsNull) return null;
        return ParseMember((string)result!);
    }

    private string Member(Waiting w) => $"{_instance.Id}|{w.ConnectionId}|{w.UserId}";

    // Rating is not carried in the member string (it is the ZSET score, already spent once the
    // script ZREMs the match) and is unused by every caller past this point, MatchmakingService only
    // reads UserId/ConnectionId off the returned opponent, so it is left at its default.
    private static Waiting ParseMember(string member)
    {
        var parts = member.Split('|', 3);
        return new Waiting(Guid.Parse(parts[2]), parts[1], 0);
    }

    private static string? ConnectionOf(RedisValue member) =>
        ((string?)member)?.Split('|', 3) is [_, var connectionId, _] ? connectionId : null;

    private static string? UserOf(RedisValue member) =>
        ((string?)member)?.Split('|', 3) is [_, _, var userId] ? userId : null;

    private static string? InstanceOf(RedisValue member) =>
        ((string?)member)?.Split('|', 3) is [var instanceId, _, _] ? instanceId : null;

    private static bool IsRedisOutage(Exception ex) => ex is RedisConnectionException or RedisTimeoutException;
}
