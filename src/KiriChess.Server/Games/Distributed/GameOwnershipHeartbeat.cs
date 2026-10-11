using KiriChess.Server.Infrastructure.Redis;

namespace KiriChess.Server.Games.Distributed;

/// <summary>
/// Renews this instance's ownership leases on the shared <see cref="InstanceHeartbeat"/> cadence
/// (Phase 8.4) rather than running its own timer, see <see cref="IInstanceHeartbeatHook"/>'s doc
/// comment for why a lease renewal running on a different schedule from the instance heartbeat itself
/// would be exactly the split-brain risk this exists to prevent: an instance whose heartbeat is late
/// but whose lease renewal is somehow still on time would look alive to the ownership check and dead to
/// everything else.
///
/// A false <see cref="GameOwnership.RenewAsync"/> means only that this instance does not currently hold
/// the lease, it does <b>not</b> by itself prove another instance took the game over. The compare-and-
/// swap script also returns false when the owner key is simply gone (Redis restarted without
/// persistence, was flushed, or evicted the key under <c>maxmemory</c> pressure), and in that case
/// nobody owns the game and this instance is still the only one with the live session. Evicting on that
/// signal alone would drop every live game on the server the instant Redis restarted, which
/// Phase 8.4's out-of-scope list explicitly forbids: a flushed Redis must cost at
/// most a re-warm, never a game. So a failed renew is followed by a claim attempt: if nobody holds the
/// lease, this instance reclaims it and keeps playing; only if the claim also fails, proving another
/// instance really did take over, does this evict the (now stale) local replica and skip persisting it,
/// because the new owner holds the authoritative copy and our write would clobber theirs.
/// </summary>
public sealed class GameOwnershipHeartbeat : IInstanceHeartbeatHook, IInstanceShutdownHook
{
    // Enough to clear a full machine's worth of leases inside a shutdown grace without opening a
    // socket per game. Each release is one small Lua eval, so this is latency-bound, not CPU-bound.
    private const int ReleaseConcurrency = 16;

    private readonly GameRegistry _registry;
    private readonly GameOwnership _ownership;
    private readonly GameDistribution _distribution;
    private readonly GameConnectionTracker _connections;
    private readonly IRedisConnection _connection;
    private readonly ILogger<GameOwnershipHeartbeat> _logger;

    public GameOwnershipHeartbeat(
        GameRegistry registry, GameOwnership ownership, GameDistribution distribution,
        GameConnectionTracker connections, IRedisConnection connection, ILogger<GameOwnershipHeartbeat> logger)
    {
        _registry = registry;
        _ownership = ownership;
        _distribution = distribution;
        _connections = connections;
        _connection = connection;
        _logger = logger;
    }

    public async Task OnHeartbeatAsync(CancellationToken ct)
    {
        if (!_connection.Enabled) return; // single-instance: nothing to renew, nothing to lose

        foreach (var session in _registry.ActiveSessions)
        {
            if (session.IsOver) continue;

            if (!await _ownership.RenewAsync(session.Id, ct))
            {
                // A false renew means only "we do not hold this lease right now", it does NOT prove
                // another instance took the game. RenewAsync's compare-and-swap also returns false when
                // the owner key is simply gone (Redis restarted without persistence, was flushed, or
                // evicted the key under maxmemory), and in that case nobody owns this game and we are
                // still the only instance with the live session. Evicting on that signal alone would drop
                // every live game on the server the moment Redis restarted, which README.md's out-of-scope
                // list explicitly forbids: a flushed Redis must cost at most a re-warm, never a game.
                if (await _ownership.TryClaimAsync(session.Id, ct))
                {
                    // The lease had lapsed but nobody had claimed it, and we just re-took it. Keep playing.
                    _logger.LogWarning(
                        "The ownership lease for game {GameId} had lapsed with no new owner; reclaimed it " +
                        "and kept the live session. This means a Redis restart, a flush, or a partition " +
                        "longer than the lease TTL.", session.Id);
                    // A flush also emptied players:ingame. This process still thinks it seated the
                    // game, so forget that marker and let OnGameChangedAsync SADD again (I-1-3).
                    _distribution.ForgetSeat(session.Id);
                }
                else
                {
                    // TryClaimAsync's SET NX failed, so somebody else genuinely holds the lease: another
                    // instance took the game over after ours expired. Two owners applying moves to one
                    // game is the worst outcome available here, evict the local replica and never persist
                    // it, because the new owner holds the authoritative copy and our write would clobber
                    // theirs.
                    _logger.LogError(
                        "Lost the ownership lease for game {GameId}: another instance claimed it after ours " +
                        "lapsed, meaning we were unreachable long enough for the lease to expire. Evicting " +
                        "the local (now stale) replica; the new owner has the authoritative copy.", session.Id);
                    _registry.Evict(session.Id);
                    continue;
                }
            }

            // Refresh the published live-state entry (1h TTL, far longer than the heartbeat cadence) so
            // a game nobody has moved in recently does not silently drop off the public live list.
            await _distribution.OnGameChangedAsync(session, ct);
            await _connections.RefreshConnectionsTtlAsync(session.Id, ct);
        }
    }

    /// <summary>
    /// Graceful stop (C-5-3): hands every lease this instance holds back to the cluster, before
    /// <see cref="InstanceHeartbeat.StopAsync"/> deregisters the instance, see
    /// <see cref="IInstanceShutdownHook"/> for why that order is the fix.
    ///
    /// Without this, a deploy left <c>{p}game:owner:*</c> keys behind for up to
    /// <see cref="RedisOptions.InstanceTtlSeconds"/> while the heartbeat key was already gone. That
    /// combination is the one state nothing handles: the owner reads as provably dead, so
    /// <c>ResolveOwnerOrTakeoverAsync</c> takes the migrate branch, and the takeover's <c>SET NX</c>
    /// then fails against the dead owner's surviving lease. The player gets
    /// <c>"Game not found."</c> and <c>OrphanedGameSweeper</c> skips the same game for the same reason.
    ///
    /// Releasing is safe here for the same reason it is safe in <c>TryTakeoverAsync</c>'s finished-game
    /// path: <see cref="GameOwnership.ReleaseAsync"/> is a compare-and-delete, so it can only ever
    /// remove a lease this instance actually holds. It is also unconditionally correct to run it on a
    /// game that has already ended: the lease has no reader left either way.
    ///
    /// This runs after <c>LiveGamePersister.StopAsync</c> has flushed, because that service is
    /// registered later in Program.cs than <see cref="InstanceHeartbeat"/> and hosted services stop in
    /// reverse registration order. That ordering matters: releasing first would let a sibling take the
    /// game over off a Postgres row this process had not finished writing.
    /// </summary>
    public async Task OnInstanceStoppingAsync(CancellationToken ct)
    {
        if (!_connection.Enabled) return; // single-instance: no lease was ever taken

        var sessions = _registry.ActiveSessions;
        if (sessions.Count == 0) return;

        await Parallel.ForEachAsync(
            sessions,
            new ParallelOptions { MaxDegreeOfParallelism = ReleaseConcurrency, CancellationToken = ct },
            async (session, token) => await _ownership.ReleaseAsync(session.Id, token));

        _logger.LogInformation(
            "Released {Count} game-ownership lease(s) on shutdown; another instance can take these games over immediately rather than after the lease TTL.",
            sessions.Count);
    }
}
