using System.Diagnostics;
using System.Text.Json;
using KiriChess.Engine;
using KiriChess.Engine.Core;
using KiriChess.Server.Data;
using KiriChess.Server.Data.Entities;
using KiriChess.Server.Games.Distributed;
using KiriChess.Server.Infrastructure;
using KiriChess.Server.Infrastructure.Redis;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace KiriChess.Server.Games;

/// <summary>
/// <see cref="GameActionExecutor"/>'s public surface, pulled out purely as a unit-test seam,
/// <c>GameConnectionTracker</c> and <c>GameHub</c> depend on this rather than the concrete class so a
/// test can substitute a lightweight fake instead of constructing the whole distributed graph. Every
/// real caller resolves the concrete singleton; there is no second implementation in production.
/// </summary>
public interface IGameActionExecutor
{
    Task<GameSnapshotDto> JoinGameAsync(Guid gameId, Guid userId, string connectionId, CancellationToken ct = default);

    /// <summary>
    /// Applies a move and broadcasts <c>MovePlayed</c>, then hands the caller back the very payload it
    /// broadcast. The hub ignores the return value (its clients already got the push); the HTTP move
    /// endpoints need it, because a move for a game this instance does not own is applied on the owning
    /// instance and there is no local session left here to read the resulting state off.
    /// </summary>
    Task<MovePlayedDto> SubmitMoveAsync(Guid gameId, Guid userId, string connectionId, string uci, CancellationToken ct = default);
    /// <summary>Ends the game by resignation and broadcasts <c>GameEnded</c>, returning the same payload for the same reason <see cref="SubmitMoveAsync"/> does.</summary>
    Task<GameEndedDto> ResignAsync(Guid gameId, Guid userId, string connectionId, CancellationToken ct = default);
    Task OfferDrawAsync(Guid gameId, Guid userId, string connectionId, CancellationToken ct = default);
    Task RespondDrawAsync(Guid gameId, Guid userId, string connectionId, bool accept, CancellationToken ct = default);
    Task AbandonAsync(Guid gameId, Guid userId, CancellationToken ct = default);
    Task<SpectatorSnapshotDto> JoinAsSpectatorAsync(Guid gameId, string connectionId, CancellationToken ct = default);
    Task<GameCommandReply> ExecuteAsync(GameCommandEnvelope envelope, CancellationToken ct = default);
    /// <summary>
    /// Takes over a game whose owner is gone. <paramref name="fromDeadOwner"/> is null for the plain
    /// "nobody holds the lease" case (a <c>SET NX</c> claim), or the id of an owner the caller has
    /// <b>proved</b> dead, which claims the lease even though that owner's key has not expired yet
    /// (C-5-3). Never pass an id the caller merely failed to reach.
    /// </summary>
    Task<bool> TryTakeoverAsync(Guid gameId, string? fromDeadOwner = null, CancellationToken ct = default);
}

/// <summary>
/// The single code path every player action goes through (Phase 8.4), the reason two identical bugs
/// can never exist for one rule. Before this phase, the move/resign/draw/abandon/spectate logic lived
/// directly inside <see cref="GameHub"/> methods; it is lifted here, unchanged, so both a local caller
/// (the hub, for a game this instance owns) and a remote caller
/// (<see cref="GameCommandRouter"/>, forwarding on behalf of a game owned elsewhere) invoke exactly the
/// same implementation. Each public method is a two-part router: if the session is in the local
/// <see cref="GameRegistry"/>, run the corresponding <c>*LocalAsync</c> body directly; otherwise resolve
/// ownership (claiming the game via takeover when its previous owner is provably dead, see
/// Phase 8.4 scope decision 2, never on a whim) and forward the command over Redis.
///
/// <b>The player must not be able to tell, from any exception message, whether their action executed
/// locally or was forwarded</b> (README RPC rule 1): every <c>*LocalAsync</c> method throws exactly the
/// <see cref="HubException"/> the pre-8.4 hub method would have thrown, and the forwarding path
/// rethrows the owner's exact message verbatim.
/// </summary>
public sealed class GameActionExecutor : IGameActionExecutor
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly GameRegistry _registry;
    private readonly GameOwnership _ownership;
    private readonly InstanceRegistry _instances;
    private readonly InstanceIdentity _instance;
    private readonly IRedisConnection _connection;
    private readonly IGameCommandRouter _router;
    private readonly GameDistribution _distribution;
    private readonly IHubContext<GameHub> _hub;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly KiriChessEngine _engine;
    private readonly LagCompensationOptions _lagOptions;
    private readonly LiveGamePersister _persister;
    private readonly KiriChessMetrics _metrics;
    private readonly ILogger<GameActionExecutor> _logger;

    public GameActionExecutor(
        GameRegistry registry, GameOwnership ownership, InstanceRegistry instances, InstanceIdentity instance,
        IRedisConnection connection, IGameCommandRouter router, GameDistribution distribution,
        IHubContext<GameHub> hub, IServiceScopeFactory scopeFactory, KiriChessEngine engine,
        IOptions<LagCompensationOptions> lagOptions, LiveGamePersister persister, KiriChessMetrics metrics,
        ILogger<GameActionExecutor> logger)
    {
        _registry = registry;
        _ownership = ownership;
        _instances = instances;
        _instance = instance;
        _connection = connection;
        _router = router;
        _distribution = distribution;
        _hub = hub;
        _scopeFactory = scopeFactory;
        _engine = engine;
        _lagOptions = lagOptions.Value;
        _persister = persister;
        _metrics = metrics;
        _logger = logger;
    }

    // ------------------------------------------------------------------------------------------
    // Public entry points, one per GameCommandKind. Every one follows the same shape: local
    // session present? run it directly. Otherwise resolve ownership (claiming the game locally if
    // its owner is dead) and either proceed locally or forward.
    // ------------------------------------------------------------------------------------------

    public async Task<GameSnapshotDto> JoinGameAsync(Guid gameId, Guid userId, string connectionId, CancellationToken ct = default)
    {
        var session = _registry.Get(gameId);
        if (session is not null) return await JoinGameLocalAsync(session, userId, ct);

        var owner = await ResolveOwnerOrTakeoverAsync(gameId, ct);
        if (owner is null)
            return await JoinGameLocalAsync(_registry.Get(gameId) ?? throw new HubException("Game not found."), userId, ct);

        var reply = await ForwardAsync(owner, GameCommandKind.JoinGame, gameId, userId, connectionId, null, ct);
        return Deserialize<GameSnapshotDto>(reply);
    }

    public async Task<MovePlayedDto> SubmitMoveAsync(Guid gameId, Guid userId, string connectionId, string uci, CancellationToken ct = default)
    {
        var session = _registry.Get(gameId);
        if (session is not null) return await SubmitMoveLocalAsync(session, userId, uci, ct);

        var owner = await ResolveOwnerOrTakeoverAsync(gameId, ct);
        if (owner is null)
            return await SubmitMoveLocalAsync(_registry.Get(gameId) ?? throw new HubException("Game not found."), userId, uci, ct);

        var reply = await ForwardAsync(owner, GameCommandKind.SubmitMove, gameId, userId, connectionId, uci, ct);
        return Deserialize<MovePlayedDto>(reply);
    }

    public async Task<GameEndedDto> ResignAsync(Guid gameId, Guid userId, string connectionId, CancellationToken ct = default)
    {
        var session = _registry.Get(gameId);
        if (session is not null) return await ResignLocalAsync(session, userId, ct);

        var owner = await ResolveOwnerOrTakeoverAsync(gameId, ct);
        if (owner is null)
            return await ResignLocalAsync(_registry.Get(gameId) ?? throw new HubException("Game not found."), userId, ct);

        var reply = await ForwardAsync(owner, GameCommandKind.Resign, gameId, userId, connectionId, null, ct);
        return Deserialize<GameEndedDto>(reply);
    }

    public async Task OfferDrawAsync(Guid gameId, Guid userId, string connectionId, CancellationToken ct = default)
    {
        var session = _registry.Get(gameId);
        if (session is not null) { await OfferDrawLocalAsync(session, userId, connectionId, ct); return; }

        var owner = await ResolveOwnerOrTakeoverAsync(gameId, ct);
        if (owner is null)
        {
            await OfferDrawLocalAsync(_registry.Get(gameId) ?? throw new HubException("Game not found."), userId, connectionId, ct);
            return;
        }

        await ForwardAsync(owner, GameCommandKind.OfferDraw, gameId, userId, connectionId, null, ct);
    }

    public async Task RespondDrawAsync(Guid gameId, Guid userId, string connectionId, bool accept, CancellationToken ct = default)
    {
        var session = _registry.Get(gameId);
        if (session is not null) { await RespondDrawLocalAsync(session, userId, connectionId, accept, ct); return; }

        var owner = await ResolveOwnerOrTakeoverAsync(gameId, ct);
        if (owner is null)
        {
            await RespondDrawLocalAsync(_registry.Get(gameId) ?? throw new HubException("Game not found."), userId, connectionId, accept, ct);
            return;
        }

        await ForwardAsync(owner, GameCommandKind.RespondDraw, gameId, userId, connectionId, accept ? "true" : "false", ct);
    }

    /// <summary>
    /// The disconnect-grace-timer forfeit (Phase 6e / <see cref="GameConnectionTracker"/>), never a
    /// live SignalR call, there is no <paramref name="connectionId"/> because nobody is connected
    /// when this fires. Callers must re-check <see cref="GameConnectionTracker.IsConnectedAsync"/>
    /// themselves before calling this; it always attempts the forfeit.
    /// </summary>
    public async Task AbandonAsync(Guid gameId, Guid userId, CancellationToken ct = default)
    {
        var session = _registry.Get(gameId);
        if (session is not null) { await AbandonLocalAsync(session, userId, ct); return; }

        var owner = await ResolveOwnerOrTakeoverAsync(gameId, ct);
        if (owner is null)
        {
            await AbandonLocalAsync(_registry.Get(gameId) ?? throw new HubException("Game not found."), userId, ct);
            return;
        }

        // No real connection to exclude from the broadcast; string.Empty carries into the envelope
        // only so GroupExcept has a value that is never a real connection id.
        await ForwardAsync(owner, GameCommandKind.Abandon, gameId, userId, string.Empty, null, ct);
    }

    /// <summary>
    /// Anonymous, no <paramref name="userId"/>; see <see cref="GameHub.JoinAsSpectator"/>.
    ///
    /// C-5-7: this is the one entry point that resolves an owner <b>without</b>
    /// <see cref="TryTakeoverAsync"/>. Takeover claims a Redis lease and then queries Postgres for an
    /// Ongoing row; on the player paths that work is bought by an authenticated, rate-limited caller
    /// for a game they are actually in, and taking the game over is the point. Here the caller is an
    /// unauthenticated hub connection naming any id it likes, including a Guid no game has ever had,
    /// and a spectator needs nothing claimed: it only wants to read a board somebody else is holding.
    /// Leaving takeover on this path pointed one Postgres query and three Redis ops per cheap
    /// anonymous call at Neon, which 9c.7 makes the narrowest part of the capacity story.
    /// </summary>
    public async Task<SpectatorSnapshotDto> JoinAsSpectatorAsync(Guid gameId, string connectionId, CancellationToken ct = default)
    {
        var session = _registry.Get(gameId);
        if (session is not null) return await JoinAsSpectatorLocalAsync(session, ct);

        var owner = await ResolveOwnerForSpectatorAsync(gameId, ct);

        // No authenticated caller for a spectator command; Guid.Empty is never a real player id.
        var reply = await ForwardAsync(owner, GameCommandKind.JoinAsSpectator, gameId, Guid.Empty, connectionId, null, ct);
        return Deserialize<SpectatorSnapshotDto>(reply);
    }

    // ------------------------------------------------------------------------------------------
    // Inbound dispatch, GameCommandRouter calls this when this instance is the addressee of a
    // GameCommandEnvelope. It simply calls back into the same public entry points above: since this
    // instance is the owner (that is the only reason the envelope was routed here), _registry.Get
    // finds the session and the call takes the exact same *LocalAsync path a local caller would.
    // That is the entire point of this design, there is nothing forwarded-call-specific to write.
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// Executes a forwarded command and never throws: every failure, a domain rejection, a stale
    /// lease recovered mid-flight, or a genuine bug, becomes a <see cref="GameCommandReply"/> so the
    /// subscriber callback that calls this (<see cref="GameCommandRouter"/>) stays exception-free.
    /// </summary>
    public async Task<GameCommandReply> ExecuteAsync(GameCommandEnvelope envelope, CancellationToken ct = default)
    {
        try
        {
            string? payload = envelope.Kind switch
            {
                GameCommandKind.JoinGame =>
                    JsonSerializer.Serialize(await JoinGameAsync(envelope.GameId, envelope.UserId, envelope.CallerConnectionId, ct), JsonOptions),
                GameCommandKind.SubmitMove =>
                    JsonSerializer.Serialize(await SubmitMoveAsync(envelope.GameId, envelope.UserId, envelope.CallerConnectionId, envelope.Arg!, ct), JsonOptions),
                GameCommandKind.Resign =>
                    JsonSerializer.Serialize(await ResignAsync(envelope.GameId, envelope.UserId, envelope.CallerConnectionId, ct), JsonOptions),
                GameCommandKind.OfferDraw =>
                    await RunVoidAsync(OfferDrawAsync(envelope.GameId, envelope.UserId, envelope.CallerConnectionId, ct)),
                GameCommandKind.RespondDraw =>
                    await RunVoidAsync(RespondDrawAsync(envelope.GameId, envelope.UserId, envelope.CallerConnectionId, envelope.Arg == "true", ct)),
                GameCommandKind.Abandon =>
                    await RunVoidAsync(AbandonAsync(envelope.GameId, envelope.UserId, ct)),
                GameCommandKind.JoinAsSpectator =>
                    JsonSerializer.Serialize(await JoinAsSpectatorAsync(envelope.GameId, envelope.CallerConnectionId, ct), JsonOptions),
                _ => throw new InvalidOperationException($"Unknown GameCommandKind '{envelope.Kind}'.")
            };
            return new GameCommandReply(envelope.CorrelationId, true, null, payload);
        }
        catch (Exception ex) when (ex is InvalidGameActionException or HubException)
        {
            // The exact message the local path would have thrown (README RPC rule 1), rethrown
            // verbatim as a HubException on the calling instance.
            return new GameCommandReply(envelope.CorrelationId, false, ex.Message, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected failure executing forwarded {Kind} for game {GameId}.", envelope.Kind, envelope.GameId);
            return new GameCommandReply(envelope.CorrelationId, false, "The game server could not complete that action.", null);
        }
    }

    private static async Task<string?> RunVoidAsync(Task task)
    {
        await task;
        return null;
    }

    // ------------------------------------------------------------------------------------------
    // *LocalAsync, verbatim the bodies that used to live in GameHub, plus GameDistribution's
    // move-path refresh (Step 7). These are the only methods that ever touch GameSession directly.
    // ------------------------------------------------------------------------------------------

    private Task<GameSnapshotDto> JoinGameLocalAsync(GameSession session, Guid userId, CancellationToken ct)
    {
        if (userId != session.WhitePlayerId && userId != session.BlackPlayerId)
            throw new HubException("Not a player in this game.");
        return Task.FromResult(session.ToSnapshot());
    }

    private async Task<MovePlayedDto> SubmitMoveLocalAsync(GameSession session, Guid userId, string uci, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();

        Move move;
        try { move = Move.Parse(uci); }
        catch (Exception ex) { throw new HubException(ex.Message); }

        ApplyResult result;
        try { result = session.ApplyMove(userId, move); }
        catch (InvalidGameActionException ex) { throw new HubException(ex.Message); }

        var persistResult = await _registry.PersistIfOverAsync(session);
        if (!session.IsOver) _persister.EnqueueSnapshot(session);

        var payload = new MovePlayedDto(
            session.ToState(), uci, result.WhiteTimeDelta, result.BlackTimeDelta,
            result.Events.Select(e => e.ToDto()).ToArray(),
            result.IsGameOver ? Array.Empty<string>() : session.LegalMoveStrings(),
            result.IsGameOver,
            persistResult.RatingChange);
        // C-2-9 = C-8-7: one event name, two payloads. Players need LegalMoves to offer the
        // next ply; spectators get the same record with an empty list, matching the join
        // snapshot's omission. Same DTO type, so this is not an OpenAPI change.
        await _hub.Clients.Group(GameHubGroups.Players(session.Id)).SendAsync("MovePlayed", payload, ct);
        await _hub.Clients.Group(GameHubGroups.Spectators(session.Id))
            .SendAsync("MovePlayed", payload with { LegalMoves = Array.Empty<string>() }, ct);
        _metrics.MoveApplied(session.RatingPool, Stopwatch.GetElapsedTime(started).TotalMilliseconds);

        // Step 7: refresh the published live-state entry after the players get their move, not
        // before, the public live list is allowed to be a beat behind, the players are not.
        if (!session.IsOver) await _distribution.OnGameChangedAsync(session, ct);

        return payload;
    }

    private async Task<GameEndedDto> ResignLocalAsync(GameSession session, Guid userId, CancellationToken ct)
    {
        try { session.Resign(userId); }
        catch (InvalidGameActionException ex) { throw new HubException(ex.Message); }

        var persistResult = await _registry.PersistIfOverAsync(session);
        var payload = new GameEndedDto(session.ToState(), persistResult.RatingChange);
        await GameHubGroups.SendToRoomAsync(_hub.Clients, session.Id, "GameEnded", payload, ct);
        return payload;
    }

    private async Task OfferDrawLocalAsync(GameSession session, Guid userId, string connectionId, CancellationToken ct)
    {
        try { session.OfferDraw(userId); }
        catch (InvalidGameActionException ex) { throw new HubException(ex.Message); }

        await _hub.Clients.GroupExcept(GameHubGroups.Players(session.Id), connectionId)
            .SendAsync("DrawOffered", new DrawOfferedDto(session.DrawOfferedBy!.Value.ToString()), ct);
    }

    private async Task RespondDrawLocalAsync(GameSession session, Guid userId, string connectionId, bool accept, CancellationToken ct)
    {
        try { session.RespondDraw(userId, accept); }
        catch (InvalidGameActionException ex) { throw new HubException(ex.Message); }

        if (session.IsOver)
        {
            var persistResult = await _registry.PersistIfOverAsync(session);
            await GameHubGroups.SendToRoomAsync(
                _hub.Clients, session.Id, "GameEnded", new GameEndedDto(session.ToState(), persistResult.RatingChange), ct);
        }
        else
        {
            await _hub.Clients.GroupExcept(GameHubGroups.Players(session.Id), connectionId).SendAsync("DrawDeclined", ct);
        }
    }

    private async Task AbandonLocalAsync(GameSession session, Guid userId, CancellationToken ct)
    {
        // Deliberately not caught here: InvalidGameActionException("Game is already over.") propagates
        // to the caller exactly as the forwarding path's translated HubException would carry the same
        // message (README RPC rule 1), GameConnectionTracker's grace timer catches both.
        session.Abandon(userId);

        var persistResult = await _registry.PersistIfOverAsync(session);
        await GameHubGroups.SendToRoomAsync(
            _hub.Clients, session.Id, "GameEnded", new GameEndedDto(session.ToState(), persistResult.RatingChange), ct);
    }

    private async Task<SpectatorSnapshotDto> JoinAsSpectatorLocalAsync(GameSession session, CancellationToken ct)
    {
        var (whiteName, blackName, whitePatronTier, blackPatronTier) = await GameNameResolver.ResolveNamesAsync(_scopeFactory, session.WhitePlayerId, session.BlackPlayerId, ct);
        // Spectators is a placeholder: GameHub.JoinAsSpectator overwrites it with the caller's own
        // live local count before returning to the client. Cluster-wide spectator counting is Task
        // C's (README: {p}spectators:{gameId}), not this task's, see Step 8 of the plan.
        return session.ToSpectatorSnapshot(whiteName, blackName, whitePatronTier, blackPatronTier, spectators: 0);
    }

    // ------------------------------------------------------------------------------------------
    // Ownership resolution and forwarding (Step 5).
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// The ownership-resolution algorithm (README RPC contract / Step 5's numbered table). Returns
    /// null when this instance now owns the game, either it already held a lease with no local
    /// session (a stale lease from a previous incarnation, recovered here) or it just won a takeover
    ///, meaning the caller should proceed on the matching <c>*LocalAsync</c> method. Returns a remote
    /// instance id when the caller should forward instead.
    ///
    /// <b>Re-resolves at most once.</b> A loop here would become an infinite ping-pong between two
    /// instances that each believe the other owns the game; after one retry this throws
    /// <c>HubException("Game not found.")</c>, the same message single-instance mode already uses for
    /// a session that genuinely does not exist.
    /// </summary>
    private async Task<string?> ResolveOwnerOrTakeoverAsync(Guid gameId, CancellationToken ct)
    {
        if (!_connection.Enabled)
            // Single instance: the local registry lookup the caller already did is authoritative,
            // there is no owner elsewhere to resolve or forward to.
            throw new HubException("Game not found.");

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var owner = await _ownership.GetOwnerAsync(gameId, ct);

            if (owner is null)
            {
                // Nobody currently holds the lease. Try to take the game over ourselves.
                if (await TryTakeoverAsync(gameId, ct: ct)) return null;
                continue; // someone else may have just won the race, re-resolve once
            }

            if (owner == _instance.Id)
            {
                // We hold the lease but have no local session: a stale lease left behind by a
                // previous incarnation of this exact instance id. Release it and take the game over
                // properly rather than trusting a lease with nothing behind it.
                await _ownership.ReleaseAsync(gameId, ct);
                if (await TryTakeoverAsync(gameId, ct: ct)) return null;
                continue;
            }

            var ownerLive = await _instances.ProbeLiveAsync(owner, ct);
            if (ownerLive != true)
            {
                // The owner's heartbeat has lapsed: it is dead, or unreachable long enough to look
                // dead, which is the one condition under which migrating a live game is safe
                // (README scope decision 2).
                //
                // A hard false is proof, and it is proof this branch already relied on, so the takeover
                // displaces that named owner's lease rather than waiting for it to expire (C-5-3). Until
                // this was compare-and-take, a gracefully stopped instance left the lease behind while
                // its heartbeat key was already gone, and the SET NX here failed against a lease nothing
                // would ever renew: this branch, written for "provably dead, safe to migrate", was the
                // one branch that could not migrate.
                //
                // A null probe is not proof, only an unanswered question, and falls back to the plain
                // claim so a Redis blip cannot displace a live owner.
                if (await TryTakeoverAsync(gameId, ownerLive == false ? owner : null, ct)) return null;
                continue; // another instance won the takeover race, re-resolve and forward to it
            }

            return owner;
        }

        throw new HubException("Game not found.");
    }

    /// <summary>
    /// The read-only half of <see cref="ResolveOwnerOrTakeoverAsync"/>, for the anonymous spectator
    /// path only (C-5-7): at most one Redis GET plus one liveness read, never a lease claim and never
    /// a database query. Returns the instance to forward to, or throws the same
    /// <c>HubException("Game not found.")</c> every other path uses for a game this cluster is not
    /// holding.
    ///
    /// Three cases collapse to that one answer, and all three are correct for a spectator. Nobody
    /// holds the lease: nothing is live to watch. This instance holds the lease with no local session:
    /// a stale lease from a previous incarnation, which a player action recovers by taking the game
    /// over properly, and a spectator has no business doing so on their behalf. The owner's heartbeat
    /// has lapsed: whatever held the game is gone, and forwarding to it would only buy the caller a
    /// router timeout. A live game always has a live owner renewing its lease
    /// (<c>GameOwnershipHeartbeat</c>), so none of the three can hide a game a spectator should see.
    /// </summary>
    private async Task<string> ResolveOwnerForSpectatorAsync(Guid gameId, CancellationToken ct)
    {
        // Single instance: the local registry lookup the caller already did is authoritative, there
        // is no owner elsewhere to resolve or forward to.
        if (!_connection.Enabled) throw new HubException("Game not found.");

        var owner = await _ownership.GetOwnerAsync(gameId, ct);
        if (owner is null || owner == _instance.Id) throw new HubException("Game not found.");
        if (!await _instances.IsLiveAsync(owner, ct)) throw new HubException("Game not found.");

        return owner;
    }

    /// <summary>
    /// Attempts to take over an unowned or dead-owned game: claim the lease, load the Ongoing row,
    /// replay it, and register the result locally. Also called directly by
    /// <see cref="OrphanedGameSweeper"/> for games nobody has touched since their owner died.
    /// </summary>
    public async Task<bool> TryTakeoverAsync(Guid gameId, string? fromDeadOwner = null, CancellationToken ct = default)
    {
        // A named dead owner is displaced by compare-and-take; anything else is a plain SET NX, which
        // succeeds only once the previous lease has actually expired.
        var claimed = fromDeadOwner is null
            ? await _ownership.TryClaimAsync(gameId, ct)
            : await _ownership.TryTakeOverFromAsync(gameId, fromDeadOwner, ct);
        if (!claimed) return false; // lease still held by somebody alive, not ours to take

        Game? row;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
            row = await db.Games.FirstOrDefaultAsync(g => g.Id == gameId && g.Result == GameResult.Ongoing, ct);
        }

        if (row is null)
        {
            // Finished (or never existed), release the lease we just claimed rather than hold a
            // lease over nothing.
            await _ownership.ReleaseAsync(gameId, ct);
            return false;
        }

        // Same anchor-reset policy GameRecoveryService already applies after a full process restart
        // (GameSession.cs, Restore), acceptable here, and only here, because TryClaimAsync succeeding
        // proves the previous owner's lease had actually expired, i.e. it is provably dead. This costs
        // the players at most one lease TTL of un-charged time, exactly the restart case this reuses.
        var session = GameSession.Restore(_engine, row, null, _lagOptions);
        if (session.IsOver)
        {
            // The previous owner crashed between its last move-snapshot write and the final write;
            // replaying the log alone finished the game. Complete it properly rather than register it.
            _registry.Restore(session);
            await _registry.PersistIfOverAsync(session);
            return false;
        }

        _registry.Restore(session);
        await _distribution.OnGameChangedAsync(session, ct);
        _metrics.GameOwnershipTakeover();
        _logger.LogInformation("Took over game {GameId}: its previous owner's lease had expired.", gameId);
        return true;
    }

    /// <summary>
    /// Publishes an envelope to <paramref name="ownerInstanceId"/>, awaits the reply, records the
    /// <c>kirichess.game.command.forwarded</c> outcome, and translates a rejection or an unexpected
    /// failure into the <see cref="HubException"/> the caller sees, the local and forwarded paths are
    /// indistinguishable from the caller's side (README RPC rule 1).
    ///
    /// <c>internal</c> rather than <c>private</c> purely as a unit-test seam
    /// (<c>GameCommandRoutingTests</c> exercises this directly with a fake <see cref="IGameCommandRouter"/>
    /// to pin the message-parity property without needing a second live instance), every production
    /// call goes through the public <c>*Async</c> wrappers above via <see cref="ResolveOwnerOrTakeoverAsync"/>.
    /// </summary>
    internal async Task<GameCommandReply> ForwardAsync(
        string ownerInstanceId, string kind, Guid gameId, Guid userId, string connectionId, string? arg, CancellationToken ct)
    {
        var envelope = new GameCommandEnvelope(Guid.NewGuid().ToString("N"), _instance.Id, kind, gameId, userId, connectionId, arg);

        GameCommandReply reply;
        try
        {
            reply = await _router.SendAsync(ownerInstanceId, envelope, ct);
        }
        catch (HubException)
        {
            // GameCommandRouter.SendAsync's only thrown exception is the timeout HubException.
            _metrics.GameCommandForwarded(kind, "timeout");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to forward a {Kind} command for game {GameId} to instance {Owner}.", kind, gameId, ownerInstanceId);
            _metrics.GameCommandForwarded(kind, "error");
            throw new HubException("The game server did not respond. Try again.");
        }

        if (!reply.Ok)
        {
            _metrics.GameCommandForwarded(kind, "rejected");
            throw new HubException(reply.Error ?? "The game server could not complete that action.");
        }

        _metrics.GameCommandForwarded(kind, "ok");
        return reply;
    }

    /// <summary>
    /// A successful reply with no payload is not a shape this build ever sends: every command kind
    /// whose caller reads a value serializes one. It is reachable only across a rolling deploy, where
    /// the owning instance may still be running a build in which SubmitMove and Resign replied with a
    /// null payload, so it is answered with the executor's ordinary retryable message rather than an
    /// ArgumentNullException out of the JSON reader.
    /// </summary>
    private static T Deserialize<T>(GameCommandReply reply) =>
        (reply.Payload is null ? default : JsonSerializer.Deserialize<T>(reply.Payload, JsonOptions))
        ?? throw new HubException("The game server could not complete that action.");
}
