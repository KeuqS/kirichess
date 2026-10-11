using KiriChess.Engine.Config;
using KiriChess.Server.Analytics;
using KiriChess.Server.Data;
using KiriChess.Server.Games.Distributed;
using KiriChess.Server.Infrastructure.Redis;
using KiriChess.Server.Ratings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KiriChess.Server.Games;

/// <summary>Outcome of a successful pairing: the created game and the two connections to notify.</summary>
public sealed record MatchResult(
    Guid GameId, Guid WhiteId, string WhiteConnectionId, Guid BlackId, string BlackConnectionId, string Pool);

/// <summary>
/// Singleton matchmaking. Wraps <see cref="MatchQueue"/> with a DB rating lookup and game creation.
/// Only official pools (bounty1/burn1/steal1/steal2/steal3/burn2) are matchmade; games are
/// created rated.
///
/// Phase 8.4 (Task C): the "already in a game" guard and the pairing call itself both become
/// cluster-aware when Redis is enabled, <see cref="LiveGameIndex.PlayersInGameAsync"/> replaces the
/// per-process <c>GameRegistry.PlayersInGame</c> check, and <see cref="MatchQueue.TryPairAsync"/>
/// replaces <c>TryPair</c>. Colour randomisation, the abandonment verdict and <c>CreateAsync</c> are
/// unchanged: the entrant's own instance creates the matched game and therefore owns it (Task B claims
/// the lease inside <c>GameRegistry.CreateAsync</c>), which needs no coordination with the opponent's
/// instance.
///
/// Phase 9d.8: the already-in-a-game / sanction / abandonment trio is no longer inline here - it moved
/// verbatim into <see cref="PlayEligibilityPolicy"/> so that direct challenges obey the exact same
/// rules without a second copy that could drift. This class still owns pairing, rating lookup and game
/// creation, none of which a challenge (a pre-formed pair, not a search) needs.
/// </summary>
public sealed class MatchmakingService
{
    /// <summary>
    /// How many pairings one <see cref="TryJoinAsync"/> may discard for an ineligible opponent before
    /// giving up on this call (C-6-1). Each discard permanently deletes a stale queue entry, so the
    /// loop always terminates on its own; this only bounds one request's worth of work.
    /// </summary>
    private const int MaxPairingAttempts = 4;

    private readonly MatchQueue _queue;
    private readonly GameRegistry _registry;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly PlayEligibilityPolicy _eligibility;
    private readonly IRedisConnection _connection;
    private readonly InstanceRegistry _instances;
    private readonly ILogger<MatchmakingService> _logger;

    public MatchmakingService(
        GameRegistry registry, IServiceScopeFactory scopeFactory, PlayEligibilityPolicy eligibility,
        IRedisConnection connection, InstanceIdentity instance, InstanceRegistry instances,
        ILogger<MatchQueue> queueLogger, ILogger<MatchmakingService> logger)
    {
        _registry = registry;
        _scopeFactory = scopeFactory;
        _eligibility = eligibility;
        _connection = connection;
        _instances = instances;
        _logger = logger;
        _queue = new MatchQueue(connection, instance, queueLogger);
    }

    /// <summary>
    /// Enters <paramref name="userId"/> into <paramref name="pool"/>. Returns a <see cref="MatchResult"/>
    /// if paired immediately, otherwise null (still waiting). Throws <see cref="ArgumentException"/> for
    /// a non-official pool, or <see cref="InvalidGameActionException"/> (Phase 6e.3) if the caller is
    /// already in a live game or is under an abandonment cooldown - and, since C-6-1, if this call had
    /// to discard <see cref="MaxPairingAttempts"/> pairings in a row because the waiting player each
    /// one returned had since become ineligible.
    /// </summary>
    public async Task<MatchResult?> TryJoinAsync(
        Guid userId, string connectionId, string pool, CancellationToken ct = default, bool botPool = false)
    {
        var key = pool.Trim().ToLowerInvariant();
        string queueKey;
        string presetKey;
        if (botPool)
        {
            queueKey = RatingConstants.RemapApiTokenQueuePool(key)
                ?? throw new ArgumentException($"'{pool}' is not a matchmaking pool.", nameof(pool));
            if (!RatingConstants.TryGetOfficialPoolFromBot(queueKey, out presetKey))
                throw new ArgumentException($"'{pool}' is not a matchmaking pool.", nameof(pool));
        }
        else
        {
            if (!RatingConstants.IsOfficialPool(key))
                throw new ArgumentException($"'{pool}' is not a matchmaking pool.", nameof(pool));
            queueKey = key;
            presetKey = key;
        }

        // Phase 9d.8: both checks now live in PlayEligibilityPolicy (extracted verbatim, same order,
        // same messages) so a direct challenge cannot drift from the queue's rules. The queue is always
        // rated (class header), so the rated-eligibility gate always runs here; friend challenges are
        // always unrated and skip it.
        await _eligibility.EnsureNotAlreadyInGameAsync(userId, ct);
        await _eligibility.EnsureEligibleForRatedPlayAsync(userId, ct);
        // Last of the three so a ban still outranks it: an account that is suspended should hear that,
        // not be told to check its inbox.
        await _eligibility.EnsureEmailConfirmedAsync(userId, ct);


        double rating = await LookupRatingAsync(userId, queueKey, ct);
        var liveInstances = _connection.Enabled
            ? await _instances.LiveInstancesAsync(ct)
            : (IReadOnlySet<string>)new HashSet<string>();
        var entrant = new Waiting(userId, connectionId, rating);

        // C-6-1. The entrant was checked above; the player who was already waiting was checked when
        // *they* joined and never again, so a verdict taken minutes ago decided this pairing. Three
        // things can have changed in between: they entered a game through one of the paths that does
        // not leave the queue, an AccountBan or RatedPlayBan landed on them, or an abandonment
        // cooldown started. Creating the game anyway produces exactly the state
        // EnsureNotAlreadyInGameAsync exists to prevent, and the entrant's game is dead on arrival -
        // it sits Ongoing until MatchNoShowWatcher aborts it 45 seconds later.
        //
        // A rejected candidate has already been removed from the queue by TryPairAsync (the pairing
        // script ZREMs the match before returning it), so each rejection permanently deletes one stale
        // entry and the loop strictly makes progress. Looping rather than giving up is what keeps the
        // *innocent* party whole: the entrant is not in the queue at this point either - the script
        // returns a match instead of enqueueing - so a plain "drop the pairing and return null" would
        // silently strand them showing a Searching spinner attached to nothing. Going round again
        // re-runs the same call, which either pairs them with the next-closest candidate or enqueues
        // them properly.
        //
        // The cap exists only so a pool full of stale entries cannot turn one JoinQueue into an
        // unbounded run of eligibility lookups. Hitting it is reported to the entrant rather than
        // swallowed, for the same reason: an error they can retry beats a spinner that never resolves.
        // A retry starts from a queue that is now several stale entries lighter.
        Waiting? opponent = null;
        var exhausted = false;
        for (var attempt = 0; ; attempt++)
        {
            var candidate = await _queue.TryPairAsync(queueKey, entrant, liveInstances, ct);
            if (candidate is not { } c) break; // simply enqueued: still waiting, nothing to check
            if (await IsStillPairableAsync(c.UserId, ct)) { opponent = c; break; }

            _logger.LogInformation(
                "Dropped a pairing in pool {Pool}: waiting player {OpponentId} is no longer eligible to be matched; their stale queue entry has been discarded.",
                queueKey, c.UserId);

            if (attempt + 1 >= MaxPairingAttempts) { exhausted = true; break; }
        }

        // 11a.3. Two counters, and they are the only measurement of the launch cliff that a browser
        // cannot report: a queue entry that never pairs leaves no row in Postgres at all, so without
        // this the funnel is blind exactly where an empty lobby loses a first-time visitor. Recorded
        // here rather than beaconed from the client because this is the one vantage point that knows
        // the truth and cannot be blocked by an extension.
        //
        // Abandonment is queue-join minus queue-match and is deliberately not a third counter: Leave()
        // also runs on every disconnect, including for callers who were never queued.
        //
        // QueueMatch is +2 because a pairing gives a game to two people and QueueJoin is per person; a
        // ratio between a per-pairing and a per-person number would be silently half of the truth.
        //
        // M-4-4. A bot-pool join is an API token in the bot: namespace. AD.7 counts humans.
        // Recording it would inflate QueueJoin and QueueMatch the moment those pools take traffic.
        if (!botPool)
            await RecordQueueCountersAsync(paired: opponent is not null, ct);

        if (exhausted)
            throw new InvalidGameActionException(
                "Couldn't find an available opponent just now. Please try again.");
        if (opponent is not { } opp) return null;

        // Randomise colours so neither queue position implies a side.
        var config = KiriChessConfig.Presets[presetKey].Clone();
        bool entrantIsWhite = Random.Shared.Next(2) == 0;
        var whiteId = entrantIsWhite ? userId : opp.UserId;
        var blackId = entrantIsWhite ? opp.UserId : userId;
        var whiteConn = entrantIsWhite ? connectionId : opp.ConnectionId;
        var blackConn = entrantIsWhite ? opp.ConnectionId : connectionId;

        var session = await _registry.CreateAsync(config, whiteId, blackId, queueKey, rated: true);
        return new MatchResult(session.Id, whiteId, whiteConn, blackId, blackConn, queueKey);
    }

    /// <summary>
    /// Drops a connection from all queues (called on <c>GameHub.LeaveQueue</c> and on disconnect).
    /// Kept synchronous, <c>GameHub.OnDisconnectedAsync</c> must not block on a Redis round trip just
    /// to leave the queue. When Redis is enabled the removal runs as a tracked background task;
    /// <see cref="MatchQueue.RemoveAsync"/> already logs and swallows a Redis outage internally, same
    /// pattern as <see cref="GameConnectionTracker.Track"/>.
    /// </summary>
    public void Leave(string connectionId)
    {
        if (_connection.Enabled) { _ = _queue.RemoveAsync(connectionId); return; }
        _queue.Remove(connectionId);
    }

    /// <summary>
    /// C-6-1: drops <paramref name="userId"/> from every queue, whichever connection put them there.
    /// Every path that creates a live game calls this for both participants, so that standing in a
    /// queue and sitting in a game cannot be simultaneous states.
    ///
    /// Awaited rather than fire-and-forget, unlike <see cref="Leave"/>. The reason Leave is
    /// deliberately not awaited does not apply here: it is called from
    /// <c>GameHub.OnDisconnectedAsync</c>, which must not block on a Redis round trip. The callers of
    /// this method are ordinary request handlers, and awaiting closes the window in which a concurrent
    /// pairing could still find the entry.
    ///
    /// Called immediately *before* the game is created, never after: leaving first opens a harmless
    /// window in which the player is in neither, while creating first opens the exact race this is
    /// meant to close.
    /// </summary>
    public Task LeaveUserAsync(Guid userId, CancellationToken ct = default) =>
        _queue.RemoveUserAsync(userId, ct);

    /// <summary>Waiting count per pool, for the 6d.3 queue-depth gauge. Synchronous in-memory view
    /// only; see <see cref="QueueDepthsAsync"/>, which is what the gauge actually reads (C-6-6).</summary>
    public IReadOnlyDictionary<string, int> QueueDepths => _queue.Depths();

    /// <summary>
    /// C-6-6: the queue-depth gauge's actual data source. <see cref="QueueDepths"/> above reads
    /// <see cref="MatchQueue.Depths"/>, the synchronous pre-8.4 in-memory dictionary that nothing on
    /// the Redis path ever populates, so the gauge went dark the moment Redis was enabled while
    /// looking like a healthy zero. <see cref="MatchQueue.DepthsAsync"/> was written for exactly this
    /// gauge and had no caller anywhere in the application; this exposes it.
    /// </summary>
    public Task<IReadOnlyDictionary<string, int>> QueueDepthsAsync(CancellationToken ct = default) =>
        _queue.DepthsAsync(ct);

    /// <summary>
    /// Test-only reach-through to the queue this service owns. Exists because
    /// <c>kirichess.matchmaking.queue.depth</c> is an observable gauge that emits nothing at all while
    /// the queue is empty, so <c>MetricsContractTests</c> cannot check its exported wire name (which
    /// 9c.5's <c>queue_stuck</c> alert is written against) without first putting somebody in it, and
    /// the public route in, <see cref="TryJoinAsync"/>, needs a database this Docker-free test does
    /// not have. Internal rather than public, and not a general-purpose accessor: nothing in the
    /// application should be reaching past this service to the queue.
    /// </summary>
    internal MatchQueue Queue => _queue;

    // Phase 9e.4: how long a seek stays advertised on the public list. Not a queue-membership TTL,
    // see MatchQueue.OpenSeeksAsync, a genuinely still-connected player past this age simply stops
    // being shown; joining their pool directly still pairs them.
    private static readonly TimeSpan SeekMaxAge = TimeSpan.FromMinutes(10);

    /// <summary>Open seeks (Phase 9e.4) for the public seek list, oldest wait first.</summary>
    public Task<IReadOnlyList<OpenSeek>> OpenSeeksAsync(CancellationToken ct = default) =>
        OpenSeeksAsync(RatingConstants.OfficialPools, ct);

    /// <summary>
    /// Open seeks restricted to <paramref name="pools"/>. The first-party list stays on official
    /// keys; the public/bot API passes <see cref="RatingConstants.BotPools"/>.
    /// </summary>
    public async Task<IReadOnlyList<OpenSeek>> OpenSeeksAsync(IReadOnlyList<string> pools, CancellationToken ct = default)
    {
        var liveInstances = _connection.Enabled
            ? await _instances.LiveInstancesAsync(ct)
            : (IReadOnlySet<string>)new HashSet<string>();
        return await _queue.OpenSeeksAsync(liveInstances, SeekMaxAge, pools, ct);
    }

    /// <summary>
    /// C-6-1: the same two gates <see cref="TryJoinAsync"/> runs on the entrant, re-run against the
    /// player <see cref="MatchQueue.TryPairAsync"/> just returned. Returns false rather than rethrowing
    /// because this is not the opponent's request: their ineligibility is a reason to drop the pairing
    /// and keep looking, not an error to hand the entrant, and the entrant must never be told anything
    /// about who was in the queue.
    /// </summary>
    private async Task<bool> IsStillPairableAsync(Guid opponentId, CancellationToken ct)
    {
        try
        {
            await _eligibility.EnsureNotAlreadyInGameAsync(opponentId, ct);
            await _eligibility.EnsureEligibleForRatedPlayAsync(opponentId, ct);
            return true;
        }
        catch (InvalidGameActionException)
        {
            return false;
        }
    }

    private async Task<double> LookupRatingAsync(Guid userId, string pool, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        var row = await db.Ratings.FirstOrDefaultAsync(r => r.UserId == userId && r.Pool == pool, ct);
        return row?.Value ?? RatingConstants.DefaultRating;
    }

    /// <summary>
    /// Fire-and-forget analytics. Wrapped because a counter is never worth failing a queue join for: if
    /// the counter write throws - a transient database blip, a migration not yet applied - the player
    /// still gets their game and we lose one increment. Logged at warning so a persistent failure is
    /// visible rather than silent.
    ///
    /// Source is always CounterSources.Direct here: the server has no idea what query string the player
    /// originally arrived with and must not be given one (AD.6 / 11a.3 decision 14).
    /// </summary>
    private async Task RecordQueueCountersAsync(bool paired, CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
            var today = DateOnly.FromDateTime(DateTime.UtcNow);

            await DailyCounterRecorder.RecordAsync(db, today, CounterKeys.QueueJoin, CounterSources.Direct, 1, ct);
            if (paired)
                await DailyCounterRecorder.RecordAsync(db, today, CounterKeys.QueueMatch, CounterSources.Direct, 2, ct);
        }
        catch (Exception ex)
        {
            // ct being already cancelled also lands here (the recorder throws OperationCanceledException),
            // which is correct: a cancelled join must not surface as an unhandled fault.
            _logger.LogWarning(ex, "Failed to record matchmaking counters; the queue join itself succeeded.");
        }
    }
}
