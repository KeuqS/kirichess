using System.Collections.Concurrent;
using KiriChess.Engine;
using KiriChess.Engine.Config;
using KiriChess.Server.Analytics;
using KiriChess.Server.Data;
using KiriChess.Server.Games.Distributed;
using KiriChess.Server.Infrastructure;
using KiriChess.Server.Ratings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KiriChess.Server.Games;

/// <summary>
/// Singleton registry of live <see cref="GameSession"/>s. The registry itself is a singleton, but
/// the <see cref="KiriChessDbContext"/> is scoped, so persistence happens through a fresh
/// <see cref="IServiceScopeFactory"/> scope, never by injecting the DbContext directly here.
///
/// Phase 8.4: after this task, every session held here is one this instance actually owns, a lease
/// claimed either at creation, at boot-time recovery, or by a takeover once a previous owner's lease
/// expired. <see cref="GameDistribution"/> is the one seam that tells the rest of the cluster about a
/// session's lifecycle, kept as a single collaborator rather than four so this registry stays pure
/// bookkeeping.
/// </summary>
public sealed class GameRegistry
{
    private readonly KiriChessEngine _engine;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly KiriChessMetrics _metrics;
    private readonly LagCompensationOptions _lagOptions;
    private readonly GameDistribution _distribution;
    private readonly KillSwitchService _killSwitches;
    private readonly ILogger<GameRegistry> _logger;
    private readonly ConcurrentDictionary<Guid, GameSession> _sessions = new();
    private readonly LivePlayerSet _playersInGame = new();

    public GameRegistry(
        KiriChessEngine engine,
        IServiceScopeFactory scopeFactory,
        KiriChessMetrics metrics,
        IOptions<LagCompensationOptions> lagOptions,
        GameDistribution distribution,
        KillSwitchService killSwitches,
        ILogger<GameRegistry> logger)
    {
        _engine = engine;
        _scopeFactory = scopeFactory;
        _metrics = metrics;
        _lagOptions = lagOptions.Value;
        _distribution = distribution;
        _killSwitches = killSwitches;
        _logger = logger;
    }

    /// <summary>
    /// Creates a session and inserts its Ongoing row, a game is durable from ply 0.
    /// </summary>
    /// <remarks>
    /// S-5-1: the <see cref="KillSwitch.Pairing"/> gate lives here rather than at the call sites,
    /// because there are five of them and only one used to be gated. <c>KillSwitch.cs</c> and the
    /// 10c.4 roadmap entry both describe this lever as "nobody enters a new game while it holds",
    /// but the only check was in <c>GameHub.JoinQueue</c>, so rematch accept, challenge accept,
    /// <c>POST /api/games</c> and <c>POST /api/v1/games</c> all kept inserting rows during the exact
    /// incident the switch exists for. An operator who believes creation is stopped while four of
    /// five paths still write is worse off than one with no lever at all.
    ///
    /// This is the one function every path funnels through, and it is the function that does the
    /// insert, so a gate here cannot be bypassed by adding a sixth caller later. The check is a
    /// cached dictionary lookup (see <see cref="KillSwitchService"/>), not a round trip, and it
    /// fails open on a database blip by design: a sick database must not also disable every gated
    /// feature on top of whatever it is already causing.
    /// </remarks>
    /// <exception cref="GameCreationPausedException">
    /// The <see cref="KillSwitch.Pairing"/> switch is off. Derives from
    /// <see cref="InvalidGameActionException"/> so that any call site which does not know about it
    /// still refuses cleanly rather than throwing a 500.
    /// </exception>
    public async Task<GameSession> CreateAsync(
        KiriChessConfig config, Guid white, Guid black, string? pool, bool rated,
        bool whiteViaApi = false, bool blackViaApi = false)
    {
        if (!await _killSwitches.IsEnabledAsync(KillSwitch.Pairing))
            throw new GameCreationPausedException();

        var session = new GameSession(
            _engine, config, white, black, pool, rated, lag: _lagOptions,
            whiteViaApi: whiteViaApi, blackViaApi: blackViaApi);
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        db.Games.Add(session.ToEntity());
        await db.SaveChangesAsync();
        _sessions[session.Id] = session;
        _playersInGame.Add(session.WhitePlayerId, session.BlackPlayerId);
        _metrics.GameStarted(session.RatingPool, session.IsRated);
        _logger.LogDebug(
            "Game {GameId} started. EconomyScale={EconomyScale}, StartTime={StartTimeSeconds}, " +
            "CaptorGain={CaptorGain}, VictimLoss={VictimLoss}",
            session.Id, config.EconomyScale, config.StartTimeSeconds,
            config.CaptureCaptorGainEnabled, config.CaptureVictimLossEnabled);

        // --- Multi-instance ownership (Phase 8.4) ---
        // A brand-new Guid can never already be owned, so this claim cannot lose a race. A failure
        // here means Redis itself is unreachable; the game is still fully playable on this instance
        // (it is already in _sessions above), refusing to start it over a lease write would be worse
        // than serving it single-instance until the next successful heartbeat renewal.
        await _distribution.OnGameCreatedAsync(session);

        return session;
    }

    /// <summary>
    /// Puts a recovered session back into the live set (boot-time recovery only). Deliberately does
    /// not increment <c>games.started</c>, a recovered game was already counted by the process that
    /// created it, and counting it again would make a restart look like a traffic spike.
    /// </summary>
    public void Restore(GameSession session)
    {
        if (!_sessions.TryAdd(session.Id, session))
        {
            _sessions[session.Id] = session;
            return;
        }
        _playersInGame.Add(session.WhitePlayerId, session.BlackPlayerId);
    }

    public GameSession? Get(Guid id)
    {
        if (!_sessions.TryGetValue(id, out var s)) return null;
        s.CheckFlagFall(); // lazy flag check on every read
        return s;
    }

    /// <summary>Snapshot of the currently-live sessions. Never null.</summary>
    public IReadOnlyList<GameSession> ActiveSessions => _sessions.Values.ToArray();

    /// <summary>
    /// I-1-6 / AS-8. Iterate live sessions without copying them to an array. The flag-fall sweep
    /// uses this; <see cref="ActiveSessions"/> stays for callers that need a snapshot.
    /// </summary>
    public IEnumerable<GameSession> EnumerateSessions()
    {
        foreach (var pair in _sessions)
            yield return pair.Value;
    }

    /// <summary>Distinct players sitting in a live game, two per ongoing game, deduped (a solo game is one).</summary>
    public IReadOnlySet<Guid> PlayersInGame => _playersInGame;

    /// <summary>
    /// If the session is over: evict it, upsert the final row, and apply ratings, exactly once.
    /// <see cref="GameEndResult.Persisted"/> is true only for the call that actually persisted (racing
    /// callers get false); <see cref="GameEndResult.RatingChange"/> is whatever
    /// <see cref="Ratings.RatingService.ApplyAsync"/> computed, null for every unrated/solo/aborted game.
    /// </summary>
    public async Task<GameEndResult> PersistIfOverAsync(GameSession session)
    {
        if (!session.IsOver) return GameEndResult.NotOver;
        if (!_sessions.TryRemove(session.Id, out _)) return GameEndResult.NotOver; // already persisted by a racing caller
        _playersInGame.Remove(session.WhitePlayerId, session.BlackPlayerId);

        var entity = session.ToEntity();
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();

        var tails = await db.GameMoves.Where(m => m.GameId == entity.Id).ToListAsync();
        db.GameMoves.RemoveRange(tails);

        var existing = await db.Games.FirstOrDefaultAsync(g => g.Id == entity.Id);
        if (existing is null)
        {
            db.Games.Add(entity);
        }
        else
        {
            existing.Result = entity.Result;
            existing.Termination = entity.Termination;
            existing.Moves = entity.Moves;
            existing.PlyCount = entity.PlyCount;
            existing.EndedAt = entity.EndedAt;
            existing.NoShowAtFaultPlayerId = entity.NoShowAtFaultPlayerId;
            // WhiteDisconnectCount and BlackDisconnectCount are written during the game by
            // GraceDisconnectStore, not carried on the session. Copying them from ToEntity()
            // would zero a count the row already holds.
            entity = existing;
        }

        // Phase 5: apply Glicko-2 rating changes in the same transaction (no-op for unrated/solo/aborted).
        // C-7-6: two finishes for one player each read the Rating row and write it back. xmin makes
        // the second save fail instead of dropping the first game's change, and the same failure is
        // how a pair of first games collide on IX_Ratings_UserId_Pool. One recompute from the row
        // that won. The game update shares this SaveChanges, so a failed attempt rolls it back too
        // and the retry writes it again.
        var ratings = scope.ServiceProvider.GetRequiredService<RatingService>();
        var leaderboardUpdates = new List<LeaderboardUpdate>();
        GameRatingChangeDto? ratingChange = null;
        await RatingRowConflict.CommitAsync(
            db,
            async () =>
            {
                leaderboardUpdates.Clear();
                ratingChange = await ratings.ApplyAsync(entity, db, leaderboardUpdates);
            },
            ex => _logger.LogInformation(
                ex,
                "Rating row for game {GameId} changed underneath this finish; recomputing once.",
                session.Id));
        _metrics.GameEnded(session.RatingPool, session.IsRated, session.Termination.ToString());

        if (entity.PlyCount > 0 && entity.EndedAt is not null)
            await RecordFirstGameFinishAsync(db, scope.ServiceProvider);

        // --- Leaderboard cache write-through (Phase 8.4, I-1-2) ---
        // Only after SaveChangesAsync above has actually committed: writing the cache first and
        // having the SaveChanges fail would leave Redis reporting a rating Postgres never recorded.
        // UpsertAsync already logs and swallows every failure, and LeaderboardCache is a singleton
        // so it outlives this persist scope. Fire-and-forget so a flag-fall tick does not wait on
        // 2-3 Redis round trips per player before the next flagged game can persist.
        var leaderboard = scope.ServiceProvider.GetRequiredService<KiriChess.Server.Ratings.LeaderboardCache>();
        var pendingLeaderboard = leaderboardUpdates.ToArray();
        _ = WriteLeaderboardCacheAsync(leaderboard, pendingLeaderboard);

        // --- Multi-instance ownership (Phase 8.4) ---
        // Only after the Postgres write is safe: a crash between "row saved" and "lease released"
        // leaves a recoverable finished-in-Postgres-but-still-leased row (harmless, the lease just
        // expires on its own TTL); releasing first would let another instance take over a game that is
        // no longer safe to touch. See GameDistribution.OnGameEndedAsync.
        await _distribution.OnGameEndedAsync(session);

        return new GameEndResult(true, ratingChange);
    }

    private static async Task WriteLeaderboardCacheAsync(
        KiriChess.Server.Ratings.LeaderboardCache cache,
        KiriChess.Server.Ratings.LeaderboardUpdate[] updates)
    {
        try
        {
            foreach (var update in updates)
                await cache.UpsertAsync(
                    update.Pool, update.UserId, update.Rating, update.RatingDeviation, update.LastPlayedAt);
        }
        catch (Exception)
        {
            // UpsertAsync already swallows; this is only so a future throw cannot become an
            // unobserved-task crash on the persist path we just took off the tick's critical path.
        }
    }

    /// <summary>
    /// Best-effort DailyCounters write. A counter fault must never fail persist.
    /// Logger is resolved from the persist scope so this type's constructor stays unchanged.
    /// </summary>
    private static async Task RecordFirstGameFinishAsync(KiriChessDbContext db, IServiceProvider services)
    {
        try
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            await DailyCounterRecorder.RecordAsync(
                db, today, CounterKeys.FirstGameFinish, CounterSources.Direct, 1, CancellationToken.None);
        }
        catch (Exception ex)
        {
            services.GetService<ILogger<GameRegistry>>()
                ?.LogWarning(ex, "Failed to record first-game-finish; the persist itself succeeded.");
        }
    }

    /// <summary>
    /// Drops a session from the registry without persisting or rating it (Phase 8.4). Used only when
    /// this instance discovers, via a failed ownership-lease renewal
    /// (<see cref="GameOwnershipHeartbeat"/>), that another instance now owns the game: the
    /// new owner holds the authoritative copy, and writing ours to Postgres would clobber theirs.
    /// </summary>
    public void Evict(Guid id)
    {
        if (!_sessions.TryRemove(id, out var session))
            return;
        _playersInGame.Remove(session.WhitePlayerId, session.BlackPlayerId);
    }
}

/// <summary>Result of <see cref="GameRegistry.PersistIfOverAsync"/>. See that method's doc comment.</summary>
public sealed record GameEndResult(bool Persisted, GameRatingChangeDto? RatingChange)
{
    public static readonly GameEndResult NotOver = new(false, null);
}

/// <summary>
/// S-5-1. Thrown by <see cref="GameRegistry.CreateAsync"/> while the <see cref="KillSwitch.Pairing"/>
/// switch is off.
///
/// Derives from <see cref="InvalidGameActionException"/> so that a path which already catches that
/// base type degrades to an intelligible refusal rather than a 500. Be precise about what that is
/// worth: it only helps where a <c>catch</c> actually wraps the creation call, and on the challenge
/// accept path it does not, which was measured rather than assumed (removing that path's own check
/// turns its 503 into a 500, and <c>KillSwitchTests</c> pins exactly that). So deriving is a
/// softener, not the mechanism. The mechanism is the gate in <see cref="GameRegistry.CreateAsync"/>
/// plus an explicit refusal on each path that needs a specific status or a specific ordering.
///
/// Catch the specific type BEFORE the base one wherever the distinction matters: C# picks the first
/// matching catch, not the closest.
/// </summary>
public sealed class GameCreationPausedException()
    : InvalidGameActionException("New games are temporarily paused. Please try again shortly.");
