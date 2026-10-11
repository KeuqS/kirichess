using System.Security.Claims;
using KiriChess.Server.Auth;
using KiriChess.Server.Infrastructure;
using KiriChess.Server.Infrastructure.Push;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace KiriChess.Server.Games;

/// <summary>
/// Real-time game hub: play (move/resign/draw), matchmaking (queue), presence (disconnect grace), and
/// read-only spectating (Phase 8.1).
/// Clocks stay authoritative server-side; clients only interpolate. See phase3
///
/// Authorization is per-method, not per-class (Phase 8.1). <see cref="JoinAsSpectator"/> is the one
/// anonymous-reachable method, because the whole value of spectating on a brand-new variant is that a
/// stranger can watch a real game before deciding to sign up. Everything else carries
/// <c>[Authorize]</c> explicitly. This fails closed twice over: every player method also dereferences
/// <see cref="UserId"/>, which throws for a caller with no usable subject claim, so the attribute
/// makes the intent declarative rather than being the only thing holding the line. A test asserts
/// that every declared public method here is either attributed or on the anonymous allowlist, if you
/// add a method, that test is what will tell you which side of the line it landed on.
///
/// Phase 8.4: this hub carries no game logic of its own. Every player method is a thin wrapper around
/// <see cref="GameActionExecutor"/>, which runs the actual rule for a game this instance owns and
/// transparently forwards to whichever instance does when it does not, see
/// Phase 8.4 What stays here is exactly what is local to *this*
/// connection regardless of which instance owns the game: SignalR group membership, the tracked
/// connection/abandon-timer bookkeeping (<see cref="GameConnectionTracker"/>), and the spectator count
/// (<see cref="SpectatorTracker"/>, Task C's).
/// </summary>
public sealed class GameHub : Hub
{
    private readonly MatchmakingService _matchmaking;
    private readonly GameConnectionTracker _connections;
    private readonly MatchNoShowWatcher _matchNoShow;
    private readonly SpectatorTracker _spectators;
    private readonly IGameActionExecutor _actions;
    private readonly KillSwitchService _killSwitches;
    private readonly GameRegistry _registry;
    private readonly RematchService _rematches;
    private readonly FinishedGameReader _finishedGames;
    private readonly RematchNotificationService _rematchNotifications;
    private readonly PushNotificationService _pushNotifications;
    private readonly PlayEligibilityPolicy _eligibility;

    public GameHub(
        MatchmakingService matchmaking, GameConnectionTracker connections, MatchNoShowWatcher matchNoShow,
        SpectatorTracker spectators, IGameActionExecutor actions, KillSwitchService killSwitches,
        GameRegistry registry, RematchService rematches, FinishedGameReader finishedGames,
        RematchNotificationService rematchNotifications,
        PushNotificationService pushNotifications, PlayEligibilityPolicy eligibility)
    {
        _matchmaking = matchmaking;
        _connections = connections;
        _matchNoShow = matchNoShow;
        _spectators = spectators;
        _actions = actions;
        _killSwitches = killSwitches;
        _registry = registry;
        _rematches = rematches;
        _finishedGames = finishedGames;
        _rematchNotifications = rematchNotifications;
        _pushNotifications = pushNotifications;
        _eligibility = eligibility;
    }

    private Guid UserId
    {
        get
        {
            var raw = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier) ?? Context.User?.FindFirstValue("sub");
            return Guid.TryParse(raw, out var id) ? id : throw new HubException("Unauthenticated.");
        }
    }

    [Authorize(Policy = ApiTokenPolicies.HubPlay)]
    public async Task<GameSnapshotDto> JoinGame(string gameId)
    {
        var id = ParseGameId(gameId);
        var me = UserId;
        GameSnapshotDto snapshot;
        var trackConnection = true;
        try
        {
            snapshot = await _actions.JoinGameAsync(id, me, Context.ConnectionId);
        }
        catch (HubException ex) when (ex.Message == "Game not found.")
        {
            // P-3-1a: PersistIfOverAsync has already removed the live session. Reading Postgres
            // here is the durable path; putting the row back into GameRegistry would make a
            // finished game look live to abandon timers, matchmaking occupancy, and ownership.
            // C-1-2: the snapshot read, not the state read. The finished row's move log rides back
            // with it, so a rejoin after the game ended rebuilds the same move list and clock graph
            // a rejoin one ply earlier would have.
            var finished = await _finishedGames.TryReadParticipantSnapshotAsync(id, me);
            // A fresh HubException, not `throw;`: after the await above, rethrowing the original
            // arrives at the SignalR client as "An unexpected error occurred invoking 'JoinGame'".
            if (finished is null)
                throw new HubException("Game not found.");
            snapshot = finished;
            trackConnection = false;
        }

        // The group join is local and unrouted: SignalR group membership belongs to the connection,
        // and with the backplane attached (Phase 8.4) a group spans instances. It is not, however,
        // unconditional (C-2-6/C-5-5/S-1-9). It used to run before the line above, so a caller who
        // named any game id at all stayed subscribed to that game's MovePlayed, GameEnded,
        // DrawOffered, OpponentDisconnected and RematchAccepted pushes for the life of the
        // connection even though JoinGameAsync had just thrown "Not a player in this game." Being
        // local is an argument for the join being unrouted, not for it being unauthorized: it
        // produced a watcher the Spectating kill switch could not turn off and SpectatorTracker
        // never counted, which is the situation JoinAsSpectator and 10c.4 exist to prevent.
        //
        // C-5-4: the group is named by the parsed id, never the raw client string. Guid.TryParse
        // accepts the "N", "B", "P" and "X" formats and any casing, while every broadcast site
        // (GameActionExecutor, FlagFallMonitor) sends to GameHubGroups.Players, so a caller who
        // joined with a dashless or braced id landed in a group nothing ever sends to and sat
        // through a silent game.
        //
        // Finished-game joins still enter the group (RematchAccepted is sent there) but must not
        // Track: that arms the abandon timer for a session that no longer exists.
        await Groups.AddToGroupAsync(Context.ConnectionId, GameHubGroups.Players(id));
        if (trackConnection)
            _connections.Track(Context.ConnectionId, me, id);

        if (snapshot.State.Status != "Ongoing")
        {
            snapshot = snapshot with
            {
                State = snapshot.State with
                {
                    RematchOfferedById = await _rematches.PeekStandingOfferedByIdAsync(id, me)
                }
            };
        }

        return snapshot;
    }

    [Authorize(Policy = ApiTokenPolicies.HubPlay)]
    public Task SubmitMove(string gameId, string uci) =>
        _actions.SubmitMoveAsync(ParseGameId(gameId), UserId, Context.ConnectionId, uci);

    [Authorize(Policy = ApiTokenPolicies.HubPlay)]
    public Task Resign(string gameId) =>
        _actions.ResignAsync(ParseGameId(gameId), UserId, Context.ConnectionId);

    [Authorize(Policy = ApiTokenPolicies.HubPlay)]
    public Task OfferDraw(string gameId) =>
        _actions.OfferDrawAsync(ParseGameId(gameId), UserId, Context.ConnectionId);

    [Authorize(Policy = ApiTokenPolicies.HubPlay)]
    public Task RespondDraw(string gameId, bool accept) =>
        _actions.RespondDrawAsync(ParseGameId(gameId), UserId, Context.ConnectionId, accept);

    /// <summary>
    /// Phase 11a.1. Deliberately not routed through <see cref="IGameActionExecutor"/>: that layer
    /// exists to forward a call to whichever instance owns the *live* <see cref="GameSession"/>
    /// (Phase 8.4), and a finished game has no owning instance, its row is in Postgres and nothing
    /// else. <see cref="RematchService"/> talks to Postgres directly instead, which is what makes the
    /// accept guard below correct across instances in the first place.
    /// </summary>
    [Authorize(Policy = ApiTokenPolicies.HubPlay)]
    public async Task OfferRematch(string gameId)
    {
        var id = ParseGameId(gameId);
        RematchOfferOutcome outcome;
        try { outcome = await _rematches.OfferAsync(id, UserId); }
        catch (InvalidGameActionException ex) { throw new HubException(ex.Message); }

        // GroupExcept, not Group: the offering player already knows they offered, and 11a.2's
        // OfferDraw/DrawOffered follows the identical shape for the identical reason.
        // C-5-4: id.ToString(), not the raw gameId string, for the same reason JoinGame names its
        // group that way - the two must be the same string or the offer reaches nobody.
        await Clients.GroupExcept(GameHubGroups.Players(id), Context.ConnectionId).SendAsync("RematchOffered");

        // C-6-3: the two reach-back channels fire only for a call that actually created the offer,
        // never for one that found it already there. They used to fire per hub call, which meant a
        // burst of offers on one game produced a burst of identical mails and pushes - a
        // targeted-notification channel wearing a game feature's clothes. The group broadcast above
        // stays unconditional because it is bounded by the group's own membership: it reaches people
        // who are already looking at this page, costs nothing, and is not a notification.
        if (outcome.IsNewOffer)
        {
            // Phase 11b.1: covers the case the group broadcast above cannot, the opponent already
            // navigated away. Fire-and-forget, see RematchNotificationService's own doc comment for
            // why it waits before deciding that's what happened.
            _rematchNotifications.Arm(id, outcome.OpponentId);

            // 11b.1 follow-up: push, unlike the mail above, is sent immediately rather than after a
            // delay - see PushNotificationService's own doc comment for why. Discarded rather than
            // awaited so a slow push provider never adds latency to this hub call, the same reasoning
            // Arm above is not awaited either.
            _ = _pushNotifications.NotifyRematchOffered(id, outcome.OpponentId);
        }
    }

    /// <summary>
    /// Peeks the offer, runs the same eligibility gate <c>ChallengeEndpoints</c> runs on its two
    /// players before claiming (ban / already-in-a-game / rated-eligibility - a rematch is still a new
    /// game, and skipping this would let it re-open a door those checks exist to keep shut), claims the
    /// offer (exactly once, see <see cref="RematchService.TryClaimAsync"/>), creates the new game with
    /// colours swapped from the finished one, and tells both players. Both, not just the caller,
    /// because unlike a normal accept the offering player has no open request awaiting a response, they
    /// need to be told a game now exists at all.
    /// </summary>
    [Authorize(Policy = ApiTokenPolicies.HubPlay)]
    public async Task AcceptRematch(string gameId)
    {
        var id = ParseGameId(gameId);
        var candidate = await _rematches.PeekOpenOfferAsync(id, UserId)
            ?? throw new HubException("That rematch offer is no longer open.");

        // Every check below runs BEFORE the claim, not after (ChallengeEndpoints' own order, README
        // scope decision 3 there): claiming first and rejecting after would burn the one live offer on
        // a caller who was never going to get a game.
        var opponentId = candidate.WhitePlayerId == UserId ? candidate.BlackPlayerId : candidate.WhitePlayerId;
        try
        {
            await _eligibility.EnsureAccountNotBannedAsync(UserId);
            await _eligibility.EnsureAccountNotBannedAsync(opponentId);
            // The caller only, matching the challenge accept path: the message names no player, so
            // raising it for the opponent would tell this caller to check an inbox that is not theirs.
            // Near-unreachable in practice - both players cleared the gate to reach the finished game
            // this offer hangs off - and kept as the belt to that braces.
            await _eligibility.EnsureEmailConfirmedAsync(UserId);
            await _eligibility.EnsureNotAlreadyInGameAsync(UserId);
            await _eligibility.EnsureNotAlreadyInGameAsync(opponentId, message: "Your opponent is already in a game.");
            // The 8.5 split: a RatedPlayBan only matters for a rated rematch, exactly the game the
            // finished one was (see RematchService.TryClaimAsync's colour swap, rated-ness never changes).
            if (candidate.Rated)
            {
                await _eligibility.EnsureEligibleForRatedPlayAsync(UserId);
                await _eligibility.EnsureEligibleForRatedPlayAsync(opponentId);
            }
        }
        catch (InvalidGameActionException ex) { throw new HubException(ex.Message); }

        // S-5-1: a rematch accept was one of the four creation paths this switch never stopped.
        // Checked here, among the other before-the-claim checks and for the same stated reason:
        // GameRegistry.CreateAsync is gated as well and that gate is the guarantee, but reaching it
        // would burn the one live offer on a caller who was never going to get a game.
        if (!await _killSwitches.IsEnabledAsync(KillSwitch.Pairing))
            throw new HubException("New games are temporarily paused. Please try again shortly.");

        var acceptance = await _rematches.TryClaimAsync(id, UserId)
            ?? throw new HubException("That rematch offer is no longer open.");

        // C-6-1: same reason and same ordering as the challenge accept path. Note this needs the user
        // id, not Context.ConnectionId: LeaveQueue and OnDisconnectedAsync can use the connection
        // because it is their own, but here one of the two players is the opponent, whose queue
        // connection this instance may not even hold.
        await _matchmaking.LeaveUserAsync(UserId);
        await _matchmaking.LeaveUserAsync(opponentId);

        var session = await _registry.CreateAsync(
            acceptance.Config, acceptance.NewWhitePlayerId, acceptance.NewBlackPlayerId, acceptance.Pool, acceptance.Rated);
        await _rematches.RecordNewGameAsync(id, session.Id);

        // Same join-deadline safety net a matched or challenge-accepted game gets: a player who
        // accepted and then never actually opens the new game must not leave the other one waiting
        // forever in an Ongoing game with no exit.
        _matchNoShow.Arm(session.Id, acceptance.NewWhitePlayerId, acceptance.NewBlackPlayerId);

        // C-5-4: the finished game's canonical id, matching the group JoinGame put both players in.
        await Clients.Group(GameHubGroups.Players(id)).SendAsync(
            "RematchAccepted",
            new RematchAcceptedDto(
                session.Id, acceptance.NewWhitePlayerId, acceptance.NewBlackPlayerId, acceptance.Pool, acceptance.Rated));
    }

    [Authorize(Policy = ApiTokenPolicies.HubPlay)]
    public async Task JoinQueue(string pool)
    {
        // Phase 10c.4: the stop-the-world lever for when the database is unwell. Checked before the
        // queue join itself, which is exactly the write this switch exists to stop.
        //
        // S-5-1 added the real gate inside GameRegistry.CreateAsync, so this one is no longer what
        // makes the switch correct. Keep it anyway, and do not delete it as redundant: this is the
        // only path where the refusal has to happen before a write that is not a game. Without it a
        // player still joins the queue, still waits, and is only refused at the moment the pairing
        // would have produced a game, which is a queue full of people who cannot be paired. It also
        // says "matchmaking", which is the true and more useful sentence here.
        if (!await _killSwitches.IsEnabledAsync(KillSwitch.Pairing))
            throw new HubException("Matchmaking is temporarily paused. Please try again shortly.");

        // S-2-5: this is the entire human/bot pool boundary, and it is keyed on which credential the
        // caller used, not on whether they are actually a script. A caller who authenticates with the
        // account's own password and drives this same hub is indistinguishable from a browser here
        // and lands in the human pool against a human rating - that is not something any site can
        // close, since a password-authenticated bot looks exactly like a signed-in human at every
        // layer below this line. The bot pool is therefore a courtesy to honest bot authors, not a
        // control; the only thing actually standing behind the human pool is the engine-assistance
        // detector, which is named-account / named-game lookup after a report (S-2-1 arithmetic,
        // S-2-3 discovery declined as B20a: no pool-wide sweep). /developers now says in as many
        // words that playing the human pool from a script is prohibited regardless of which
        // credential authenticates it.
        var apiToken = Context.User?.FindFirst(ApiTokenClaims.TokenUse)?.Value == ApiTokenClaims.Api;
        MatchResult? match;
        try { match = await _matchmaking.TryJoinAsync(UserId, Context.ConnectionId, pool, botPool: apiToken); }
        catch (ArgumentException ex) { throw new HubException(ex.Message); }
        catch (InvalidGameActionException ex) { throw new HubException(ex.Message); }
        if (match is null) return;

        await Clients.Client(match.WhiteConnectionId).SendAsync("MatchFound", new MatchFoundDto(match.GameId, "White", match.Pool));
        await Clients.Client(match.BlackConnectionId).SendAsync("MatchFound", new MatchFoundDto(match.GameId, "Black", match.Pool));
        _matchNoShow.Arm(match.GameId, match.WhiteId, match.BlackId);
    }

    [Authorize(Policy = ApiTokenPolicies.HubPlay)]
    public Task LeaveQueue()
    {
        _matchmaking.Leave(Context.ConnectionId);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Phase 9d.8. Subscribes this connection to its own pending challenge so the accept endpoint can
    /// push "your friend joined" without the lobby polling for an hour.
    ///
    /// Keyed by token rather than by user id on purpose (README scope decision 4): Clients.User depends
    /// on SignalR's DefaultUserIdProvider finding ClaimTypes.NameIdentifier, and this server issues
    /// `sub` - which is exactly why UserId above has a fallback. A group needs no such assumption and
    /// crosses instances through the Redis backplane, and it also covers the challenger having the link
    /// open in two tabs.
    ///
    /// Holding the token is not a secret worth protecting from the holder: the group only ever receives
    /// the "a game was created" notice, and the accept endpoint is what enforces who may accept.
    ///
    /// C-5-4 does not reach these two. A challenge token is not a Guid and has no second spelling:
    /// <c>ChallengeService.NewToken</c> mints 22 characters of URL-safe base64, the accept endpoint
    /// looks it up with an exact, case-sensitive <c>c.Token == token</c>, and it broadcasts to
    /// <c>challenge:{token}</c> built from that same string. A token that would land in the wrong
    /// group is a token no challenge row matches, so there is nothing here to canonicalise.
    /// </summary>
    [Authorize(Policy = ApiTokenPolicies.HubPlay)]
    public Task WatchChallenge(string token) =>
        Groups.AddToGroupAsync(Context.ConnectionId, $"challenge:{token}");

    [Authorize(Policy = ApiTokenPolicies.HubPlay)]
    public Task UnwatchChallenge(string token) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, $"challenge:{token}");

    /// <summary>
    /// Joins a live game read-only (Phase 8.1). Anonymous on purpose, see the class comment.
    ///
    /// Deliberately does <b>not</b> touch <see cref="GameConnectionTracker"/>: a spectator is not a
    /// player, and their connection must never arm or cancel a disconnect-forfeit timer in a game
    /// they are only watching. That is the entire reason this is a separate method rather than a
    /// relaxed <see cref="JoinGame"/>.
    ///
    /// Spectators sit in <see cref="GameHubGroups.Spectators"/>, not the player group (C-2-9 =
    /// C-8-7). Sharing the player group meant every later <c>MovePlayed</c> handed them the
    /// legal-move list the join snapshot withholds. Mutating paths still refuse non-players at the
    /// domain layer (<c>ApplyMove</c> checks <c>PlayerOnTurn</c>;
    /// <c>Resign</c>/<c>OfferDraw</c>/<c>RespondDraw</c> go through <c>SideOf</c>, which throws
    /// "Not a player in this game.").
    ///
    /// Re-invoking with the same id, what the client does after an automatic reconnect, returns a
    /// fresh snapshot without double-counting.
    ///
    /// The spectator count/broadcast stays entirely local and unrouted (Task C's cluster-aware
    /// spectator counting lands on top of this); only the state snapshot itself, which may require
    /// forwarding to whichever instance owns the game, goes through <see cref="GameActionExecutor"/>.
    ///
    /// S-2-7 - the position on record, so it reads as a decision rather than a gap: this feed is
    /// anonymous, undelayed, and <see cref="SpectatorTracker"/> holds only a live count in memory, no
    /// row naming who watched which game. That means a confederate relaying moves to an assisted
    /// player needs no account and leaves nothing behind; the assisted player's own move timing is
    /// the only trace, which routes the whole vector through the engine-assistance detector
    /// (S-2-1 arithmetic; S-2-3 stays report-driven lookup, B20a). Two things make that an
    /// acceptable trade today rather than a gap to close: (1)
    /// anonymous spectating is the point of the feature on a brand-new variant - a stranger watches a
    /// real game before deciding to sign up, which is exactly why this is the one anonymous-reachable
    /// method on the hub; (2) an off-the-shelf chess engine does not know this variant's clock
    /// economy (captures paying the captor, per-piece tolls, check chips), so relayed advice is
    /// materially worse here than at standard chess. Deliberately not logging who watched: that would
    /// be a new category of personal data about people who have done nothing wrong, for a defence
    /// that already exists elsewhere (the timing detector). If the trigger ever fires - a credible
    /// relay report, or the first tournament with a prize, an invited field, or titled players - the
    /// fix is a delayed feed behind <see cref="KillSwitch.Spectating"/> (already checked below), not a
    /// spectator-identity store.
    /// </summary>
    public async Task<SpectatorSnapshotDto> JoinAsSpectator(string gameId)
    {
        // Phase 10c.4: the coarse lever behind 10a.2's connection budget - for when even the
        // reserved anonymous share is the problem. Checked before touching SpectatorTracker or a
        // group, the same "before the thing it would otherwise still let happen" ordering as
        // JoinQueue's check above.
        if (!await _killSwitches.IsEnabledAsync(KillSwitch.Spectating))
            throw new HubException("Spectating is temporarily disabled. Please try again shortly.");

        var id = ParseGameId(gameId);

        // B04 leftover (PROGRESS.md: "JoinAsSpectator has the same shape of bug as C-2-6"): this
        // used to AddAsync / join the group / broadcast, then call JoinAsSpectatorAsync. A caller
        // naming a nonexistent id was counted and grouped, and a failed join also left a previous
        // game via AddAsync before the throw. JoinGame's post-B04 order is snapshot first, then
        // group; the same trade-off (a move can land in the gap) is acceptable here because the
        // spectator already received a full snapshot. Do not catch-and-join: let HubException
        // propagate so a failed join never touches the tracker or the group.
        var snapshot = await _actions.JoinAsSpectatorAsync(id, Context.ConnectionId);

        if (await _spectators.AddAsync(Context.ConnectionId, id) is { } left)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, GameHubGroups.Spectators(left));
            await BroadcastSpectatorsAsync(left);
        }

        // C-5-4: canonical id, never the raw client string. C-2-9 = C-8-7: the spectator group,
        // not the player group, so later MovePlayed sends can withhold LegalMoves.
        await Groups.AddToGroupAsync(Context.ConnectionId, GameHubGroups.Spectators(id));
        await BroadcastSpectatorsAsync(id);

        return snapshot with { Spectators = await _spectators.CountForAsync(id) };
    }

    private async Task BroadcastSpectatorsAsync(Guid gameId) =>
        await GameHubGroups.SendToRoomAsync(
            Clients, gameId, "SpectatorsChanged", new SpectatorsChangedDto(await _spectators.CountForAsync(gameId)));

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _matchmaking.Leave(Context.ConnectionId);
        _connections.OnDisconnected(Context.ConnectionId);
        // The single spectator exit path. SignalR removes the connection from its groups itself; all
        // that is left is the count and the broadcast that tells the room about it.
        if (await _spectators.RemoveAsync(Context.ConnectionId) is { } watched)
            await BroadcastSpectatorsAsync(watched);
        await base.OnDisconnectedAsync(exception);
    }

    private static Guid ParseGameId(string gameId)
        => Guid.TryParse(gameId, out var id) ? id : throw new HubException("Invalid game id.");
}
