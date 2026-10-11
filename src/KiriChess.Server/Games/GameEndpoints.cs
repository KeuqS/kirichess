using System.Security.Claims;
using KiriChess.Server.Account;
using KiriChess.Server.Auth;
using KiriChess.Engine.Config;
using KiriChess.Engine.Core;
using KiriChess.Server.Data;
using KiriChess.Server.Data.Entities;
using KiriChess.Server.Games.Distributed;
using KiriChess.Server.Infrastructure;
using KiriChess.Server.Infrastructure.Redis;
using KiriChess.Server.Ratings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace KiriChess.Server.Games;

/// <summary>Minimal-API endpoints for creating and playing games. All require an authenticated caller.</summary>
public static class GameEndpoints
{
    public static IEndpointRouteBuilder MapGameEndpoints(this IEndpointRouteBuilder app)
    {
        // 9d.7: every route below requires an authenticated caller (RequireAuthorization()), so every
        // one of them can answer 401. Chaining .Produces() directly on the MapGroup() result was tried
        // and does not compile - Microsoft.AspNetCore.OpenApi 10.0.10's Produces() only has an overload
        // for RouteHandlerBuilder (what MapGet/MapPost return), not RouteGroupBuilder (what MapGroup
        // returns), confirmed by the compiler (CS1929), not assumed - so .Produces(401) is repeated on
        // each route below instead.
        var group = app.MapGroup("/api/games").RequireAuthorization();

        group.MapPost("/", async (
            CreateGameRequest req, ClaimsPrincipal user, GameRegistry registry,
            PlayEligibilityPolicy eligibility, MatchmakingService matchmaking, CancellationToken ct) =>
        {
            var me = user.GetUserId();
            if (me is null) return Results.Unauthorized();

            // C-6-5: this was the one game-creating path that never went through PlayEligibilityPolicy,
            // so it ran neither the already-in-a-game guard nor anything else the policy owns. It now
            // runs the same two gates the challenge accept path runs, in the same order, and maps them
            // to the same status codes that path uses.
            //
            // Phase 8.5: only AccountBan blocks game creation here - a RatedPlayBan still permits an
            // unrated custom game, which is exactly what it is meant to leave open. That is also why
            // EnsureEligibleForRatedPlayAsync is deliberately *not* called: every game this endpoint
            // creates is now unrated (see the solo rule below), and the challenge accept path, which
            // is likewise always unrated, does not call it either. Adding it here would deny a player
            // under a RatedPlayBan the practice game that ban is explicitly meant to leave them.
            try
            {
                await eligibility.EnsureAccountNotBannedAsync(me.Value, ct);
                await eligibility.EnsureEmailConfirmedAsync(me.Value, ct);
                await eligibility.EnsureNotAlreadyInGameAsync(me.Value, ct);
            }
            catch (InvalidGameActionException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: ex.Message.Contains("already in a game")
                    ? StatusCodes.Status409Conflict
                    : StatusCodes.Status403Forbidden);
            }

            // Phase 9d.8. OpponentId exists so a caller can play both sides (bot/solo, see
            // CreateGameRequest's comment). Left open it lets any authenticated caller create a game in
            // a stranger's name - a consent hole that survived only because nothing in ui/ ever sent
            // the field. Consent now has a mechanism: POST /api/challenges. Anything else is refused.
            if (req.OpponentId is { } opponent && opponent != me.Value)
                return Results.BadRequest(new
                {
                    error = "Use a challenge link to invite another player. See POST /api/challenges."
                });

            KiriChessConfig config;
            string? pool = null;
            var rated = req.Rated;
            try
            {
                if (req.Custom is not null)
                {
                    // Custom games ignore Preset/Rated entirely: no pool, always unrated.
                    config = BuildCustomConfig(req.Custom);
                    pool = null;
                    rated = false;
                }
                else
                {
                    config = BuildConfig(req.Preset, out pool);
                }
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            var white = me.Value;
            var black = req.OpponentId ?? me.Value; // bot match (player owns both sides) when no opponent

            // C-6-5: a solo game is one account on both sides, so there is no result for a rating to
            // be computed from and nothing for a pool to mean. The row said otherwise - IsRated = true,
            // RatingPool = steal1 - and the only thing keeping the rating itself still was
            // RatingService refusing WhitePlayerId == BlackPlayerId two layers later. The invariant now
            // holds at the endpoint that writes the row, so the row says what it is: the rows are
            // counted by KiriChessMetrics.GameStarted and by the pool-balance query the
            // (RatingPool, EndedAt) index exists for, and both were countable at will.
            //
            // Since the OpponentId guard above already refuses every id but the caller's own, this
            // covers every game this endpoint creates. The /api/v1 twin has always passed
            // pool: null, rated: false for exactly this reason; the two now agree.
            if (white == black) { rated = false; pool = null; }

            // C-6-1: leave the queue before creating the game, for the same reason and in the same
            // position as the challenge accept path. A solo game still occupies the player as far as
            // EnsureNotAlreadyInGameAsync is concerned, so a queue entry left standing here pairs the
            // next stranger against somebody who is already at a board.
            await matchmaking.LeaveUserAsync(white, ct);

            GameSession session;
            try
            {
                session = await registry.CreateAsync(config, white, black, rated ? pool : null, rated && pool != null);
            }
            catch (GameCreationPausedException ex)
            {
                // S-5-1. Not declared with .Produces below, deliberately, for the same reason the
                // 409 above is not: declaring it would move docs/api/openapi.json and therefore
                // ui/lib/api.generated.ts, and this change is not allowed to regenerate either.
                // The status is still the right one to answer with; only the document is frozen.
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            return Results.Created($"/api/games/{session.Id}", session.ToState());
        })
        .RequireRateLimiting(RateLimitPolicies.GameCreate) // 6e.1: each call inserts a row, the cheapest write to abuse
        .Produces<GameStateResponse>(StatusCodes.Status201Created)
        .Produces(StatusCodes.Status401Unauthorized)
        // No .Produces(409) here on purpose: the already-in-a-game refusal above answers 409, but
        // declaring it would change docs/api/openapi.json and therefore ui/lib/api.generated.ts, and
        // this change is not allowed to regenerate either. Worth adding in a change that can.
        .Produces<ErrorResponse>(StatusCodes.Status403Forbidden)
        .Produces<ErrorResponse>(StatusCodes.Status400BadRequest);

        // The board a player walked away from. PlayEligibilityPolicy only says "You're already in a
        // game."; this is how the client turns that into a link. Registered as a literal so it is
        // never read as {id}. Same seated-player lookup as GET /api/v1/account/playing, narrowed to
        // the one game the first-party UI needs to navigate to.
        group.MapGet("/playing", async (
            ClaimsPrincipal user, GameRegistry registry, LiveGameIndex liveGames,
            IRedisConnection connection, CancellationToken ct) =>
        {
            var me = user.GetUserId();
            if (me is null) return Results.Unauthorized();

            if (connection.Enabled)
            {
                var row = (await liveGames.PlayingGamesAsync(me.Value, ct)).FirstOrDefault();
                if (row is null) return Results.NoContent();
                return Results.Ok(new CurrentGameResponse(
                    row.GameId, row.RatingPool, row.WhitePlayerId == row.BlackPlayerId));
            }

            var local = registry.ActiveSessions
                .FirstOrDefault(s => !s.IsOver && (s.WhitePlayerId == me.Value || s.BlackPlayerId == me.Value));
            if (local is null) return Results.NoContent();
            return Results.Ok(new CurrentGameResponse(
                local.Id, local.RatingPool, local.WhitePlayerId == local.BlackPlayerId));
        })
        .Produces<CurrentGameResponse>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status401Unauthorized);

        // C-8-5: a non-participant still gets 404, not 403, matching the /api/v1 twin on the live
        // path. A 403 would confirm the id names a real live game.
        //
        // P-3-1a: after PersistIfOverAsync evicts the session, the rematch-mail recipient still has
        // to read this id. The public twin serves finished games to anyone. This route does not: it
        // carries RematchOfferedById, which is a fact about the two players, and the public replay
        // of a finished game is GET /api/games/{id}/replay. So the DB fallback here is finished
        // plus participant only. An Ongoing row missing from the registry is still 404; restoring
        // that into GameRegistry would be a Phase 8.4 ownership change, which this path refuses.
        group.MapGet("/{id:guid}", async (
            Guid id, ClaimsPrincipal user, GameRegistry registry,
            FinishedGameReader finished, RematchService rematches, CancellationToken ct) =>
        {
            var me = user.GetUserId();
            if (me is null) return Results.Unauthorized();

            var s = registry.Get(id);
            if (s is not null)
            {
                if (!IsParticipant(s, me.Value)) return Results.NotFound();
                var live = s.ToState();
                if (s.IsOver)
                {
                    live = live with
                    {
                        RematchOfferedById = await rematches.PeekStandingOfferedByIdAsync(id, me.Value, ct)
                    };
                }
                return Results.Ok(live);
            }

            var restored = await finished.TryReadParticipantStateAsync(id, me.Value, ct);
            if (restored is null) return Results.NotFound();
            return Results.Ok(restored with
            {
                RematchOfferedById = await rematches.PeekStandingOfferedByIdAsync(id, me.Value, ct)
            });
        })
        .Produces<GameStateResponse>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status404NotFound);

        // Legal moves stay registry-only. A finished game has none to return, and C-8-5's
        // non-participant 404 still holds for a live game.
        group.MapGet("/{id:guid}/legal-moves", (Guid id, ClaimsPrincipal user, GameRegistry registry) =>
        {
            var me = user.GetUserId();
            if (me is null) return Results.Unauthorized();

            var s = registry.Get(id);
            if (s is null) return Results.NotFound();
            // The twin's registry path is `!IsParticipant(live, me) || live.IsOver`, so both clauses
            // are carried here. The IsOver half changes nothing a caller can observe: LegalMoveStrings
            // goes through GameSession.LegalMoves, which returns an empty array as soon as IsOver, so
            // the only difference is an empty 200 versus a 404 for a participant asking about a game
            // that has already ended. It is carried anyway rather than argued about, because C-8-5 is
            // a parity finding and an unexplained one-clause difference between two routes doing the
            // same job is the exact thing it is about. This is the endpoint the finding names.
            if (!IsParticipant(s, me.Value) || s.IsOver) return Results.NotFound();
            return Results.Ok(s.LegalMoveStrings());
        })
        .Produces<string[]>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/{id:guid}/moves", async (
            Guid id, MoveRequest req, ClaimsPrincipal user, IGameActionExecutor actions, CancellationToken ct) =>
        {
            var me = user.GetUserId();
            if (me is null) return Results.Unauthorized();

            MovePlayedDto played;
            try { played = await actions.SubmitMoveAsync(id, me.Value, connectionId: string.Empty, req.Uci, ct); }
            catch (HubException ex) { return MoveFailure(ex); }

            return Results.Ok(ToMoveResponse(played));
        })
        .RequireRateLimiting(RateLimitPolicies.GameMove) // 6e.1: deliberately the loosest policy, a rejected move can cost a game, see RateLimitingSetup
        .Produces<MoveResponse>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status404NotFound)
        .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status403Forbidden); // not the player on turn

        group.MapPost("/{id:guid}/resign", async (
            Guid id, ClaimsPrincipal user, IGameActionExecutor actions, CancellationToken ct) =>
        {
            var me = user.GetUserId();
            if (me is null) return Results.Unauthorized();

            GameEndedDto ended;
            try { ended = await actions.ResignAsync(id, me.Value, connectionId: string.Empty, ct); }
            catch (HubException ex) { return ResignFailure(ex); }

            return Results.Ok(ended.State);
        })
        .Produces<GameStateResponse>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status404NotFound)
        .Produces<ErrorResponse>(StatusCodes.Status400BadRequest);

        // The caller's own finished-game history (Phase 9e.1), newest first. Named "mine" rather than
        // taking a {userId}, matching GET /api/challenges/mine, because a game list keyed by someone
        // else's id has no anonymity story the way /api/ratings/users/{id} does - this is a private
        // read of one's own record, not a public profile.
        //
        // Result != Ongoing is the same 8.2 security control ReplayEndpoints.cs enforces: every row
        // this returns must also be independently fetchable from /replay, so the list never advertises
        // a game this endpoint (or a client following its link) cannot actually show.
        group.MapGet("/mine", async (
            ClaimsPrincipal user, KiriChessDbContext db,
            int? take, int? skip, DateTimeOffset? before, Guid? beforeId, CancellationToken ct) =>
        {
            var me = user.GetUserId();
            if (me is null) return Results.Unauthorized();
            if ((before is null) != (beforeId is null))
                return Results.BadRequest(new ErrorResponse("before and beforeId must be sent together."));

            // I-1-4. `before`/`beforeId` are the keyset cursor (the last row already shown).
            // When they are set, `skip` is ignored. `skip` stays so an existing caller still pages.
            var entries = await GameHistoryQuery.ListMineAsync(db, me.Value, take, skip, before, beforeId, ct);
            return Results.Ok(entries);
        })
        .Produces<GameHistoryEntryDto[]>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces<ErrorResponse>(StatusCodes.Status400BadRequest);

        // Public live-games list (Phase 8.1). Anonymous on purpose, exactly like the leaderboards: a
        // stranger deciding whether this variant is worth signing up for is precisely who it is for,
        // and on a site whose problem is an empty lobby that is the whole value of the feature.
        //
        // Mapped on `app` rather than `group` because `group` carries RequireAuthorization().
        //
        // Phase 8.4 (Task C): with Redis enabled this reads Task B's published live-state index
        // (LiveGameIndex) instead of the per-process GameRegistry, a game owned by a different
        // instance from the one serving this request would otherwise never appear. Names and clocks
        // come straight off LiveGameStateDto, so the Redis path issues no database query at all (the
        // disabled path below still does one `Users` query per request, same as before this phase).
        app.MapGet("/api/games/live", async (
            GameRegistry registry, SpectatorTracker spectators, KiriChessDbContext db,
            LiveGameIndex liveGames, IRedisConnection connection, int? limit, CancellationToken ct) =>
        {
            var take = Math.Clamp(limit ?? 20, 1, 50);

            if (connection.Enabled)
                return await LiveFromRedisAsync(liveGames, spectators, db, take, ct);

            // Redis-disabled path: exactly today's code, untouched.
            //
            // Solo/practice games (both sides the same player, every bot game, per roadmap 7.3) are
            // excluded: a client-side bot playing itself is not a spectacle, and its moves come from a
            // machine the server does not trust. They stay watchable by direct link; they are just
            // never advertised.
            //
            // ActiveSessions does not lazily flag-check the way registry.Get does, so a game that
            // flagged within the last second can still appear. FlagFallMonitor sweeps every second and
            // the !IsOver filter catches everything already swept, a one-second cosmetic window, not
            // a correctness bug, and not worth putting a sweep on a public read path.
            var live = registry.ActiveSessions
                .Where(s => !s.IsOver && s.WhitePlayerId != s.BlackPlayerId)
                .ToArray();
            if (live.Length == 0) return Results.Ok(Array.Empty<LiveGameDto>());

            var playerIds = live
                .SelectMany(s => new[] { s.WhitePlayerId, s.BlackPlayerId })
                .Distinct()
                .ToArray();
            // Loaded as full entities, not a DisplayName-only projection, so PatronBadge.For (the
            // canonical in-memory rule, Account/PatronBadge.cs) can be reused directly rather than
            // re-deriving it here.
            var players = await db.Users
                .Where(u => playerIds.Contains(u.Id))
                .ToDictionaryAsync(u => u.Id);
            var now = DateTimeOffset.UtcNow;

            string NameFor(Guid id) => players.TryGetValue(id, out var u) ? u.DisplayName : "Unknown";
            string TierFor(Guid id) => players.TryGetValue(id, out var u) ? PatronBadge.For(u, now).ToString() : nameof(PatronTier.None);

            var counts = await spectators.CountsAsync(live.Select(s => s.Id).ToArray(), ct);
            var ratings = await LoadRatingsAsync(db, playerIds, live.Select(s => s.RatingPool), ct);
            var rows = live
                .Select(s => new
                {
                    Dto = new LiveGameDto(
                        s.Id,
                        NameFor(s.WhitePlayerId),
                        NameFor(s.BlackPlayerId),
                        TierFor(s.WhitePlayerId),
                        TierFor(s.BlackPlayerId),
                        s.RatingPool,
                        s.IsRated,
                        s.LiveRemaining(Side.White),
                        s.LiveRemaining(Side.Black),
                        s.Position.SideToMove.ToString(),
                        s.MoveCount,
                        counts.GetValueOrDefault(s.Id),
                        s.CreatedAt,
                        s.Position.Fen),
                    CombinedElo = RatingOf(ratings, s.WhitePlayerId, s.RatingPool)
                                + RatingOf(ratings, s.BlackPlayerId, s.RatingPool),
                })
                // Highest combined Elo (both players' ratings in this game's pool) first, so the
                // strongest matchups lead the grid; spectators/move-count stay as the tiebreak for
                // equal-Elo games rather than deciding the order outright.
                .OrderByDescending(r => r.CombinedElo)
                .ThenByDescending(r => r.Dto.Spectators)
                .ThenByDescending(r => r.Dto.MoveCount)
                .Take(take)
                .Select(r => r.Dto)
                .ToArray();
            return Results.Ok(rows);
        })
        .Produces<LiveGameDto[]>(StatusCodes.Status200OK);

        return app;
    }

    /// <summary>
    /// The Redis-enabled path of <c>GET /api/games/live</c> (Phase 8.4, Task C). Reads Task B's
    /// published live-state index instead of the per-process <c>GameRegistry</c>, so a game owned by
    /// any instance in the cluster, not just the one serving this request, can appear.
    /// </summary>
    private static async Task<IResult> LiveFromRedisAsync(
        LiveGameIndex liveGames, SpectatorTracker spectators, KiriChessDbContext db, int take, CancellationToken ct)
    {
        // Headroom: solo/practice games are filtered out below, so the raw read asks for more than
        // `take` to still have enough left after the filter (same 7.3 exclusion as the disabled path).
        var states = await liveGames.LiveGamesAsync(take * 3, ct);
        var filtered = states.Where(IsListable).ToArray();
        if (filtered.Length == 0) return Results.Ok(Array.Empty<LiveGameDto>());

        var counts = await spectators.CountsAsync(filtered.Select(s => s.GameId).ToArray(), ct);
        var playerIds = filtered.SelectMany(s => new[] { s.WhitePlayerId, s.BlackPlayerId }).Distinct().ToArray();
        var ratings = await LoadRatingsAsync(db, playerIds, filtered.Select(s => s.RatingPool), ct);
        var now = DateTimeOffset.UtcNow;

        var rows = filtered
            .Select(s =>
            {
                var (whiteTime, blackTime) = ApproximateClocks(s, now);
                return new
                {
                    Dto = new LiveGameDto(
                        s.GameId,
                        s.WhiteName,
                        s.BlackName,
                        // Tolerate an entry published before this field existed (same nullable-trailing
                        // contract as Fen, see LiveGameStateDto's doc comment).
                        s.WhitePatronTier ?? nameof(PatronTier.None),
                        s.BlackPatronTier ?? nameof(PatronTier.None),
                        s.RatingPool,
                        s.IsRated,
                        whiteTime,
                        blackTime,
                        s.SideToMove,
                        s.MoveCount,
                        counts.GetValueOrDefault(s.GameId),
                        s.CreatedAt,
                        s.Fen),
                    CombinedElo = RatingOf(ratings, s.WhitePlayerId, s.RatingPool)
                                + RatingOf(ratings, s.BlackPlayerId, s.RatingPool),
                };
            })
            // Same combined-Elo-first ordering as the Redis-disabled path.
            .OrderByDescending(r => r.CombinedElo)
            .ThenByDescending(r => r.Dto.Spectators)
            .ThenByDescending(r => r.Dto.MoveCount)
            .Take(take)
            .Select(r => r.Dto)
            .ToArray();
        return Results.Ok(rows);
    }

    /// <summary>
    /// Batch-loads each distinct (player, pool) rating in one query, for the combined-Elo ordering
    /// on <c>GET /api/games/live</c>. A pool of <c>null</c> (a custom/unrated game, see
    /// <c>BalanceAnalytics</c>'s doc comment on the same nullability) contributes no key to query for;
    /// <see cref="RatingOf"/> falls back to <see cref="RatingConstants.DefaultRating"/> for it.
    /// </summary>
    private static async Task<Dictionary<(Guid UserId, string Pool), double>> LoadRatingsAsync(
        KiriChessDbContext db, IEnumerable<Guid> playerIds, IEnumerable<string?> pools, CancellationToken ct)
    {
        var poolKeys = pools.Where(p => p is not null).Cast<string>().Distinct().ToArray();
        if (poolKeys.Length == 0) return new Dictionary<(Guid, string), double>();

        var ids = playerIds.Distinct().ToArray();
        return await db.Ratings
            .Where(r => ids.Contains(r.UserId) && poolKeys.Contains(r.Pool))
            .ToDictionaryAsync(r => (r.UserId, r.Pool), r => r.Value, ct);
    }

    /// <summary>A player's rating in a pool, or the Glicko-2 default for a null pool or a missing row.</summary>
    private static double RatingOf(
        IReadOnlyDictionary<(Guid, string), double> ratings, Guid userId, string? pool)
        => pool is not null && ratings.TryGetValue((userId, pool), out var v)
            ? v
            : RatingConstants.DefaultRating;

    /// <summary>
    /// The 7.3 solo/practice-game exclusion (same rule as the disabled path's <c>WhitePlayerId != BlackPlayerId</c>
    /// filter), applied on <see cref="LiveGameStateDto"/> instead of <c>GameSession</c>. Internal and
    /// pure so <c>DistributedLiveStateTests</c> can pin it without a database or a Redis connection.
    /// </summary>
    internal static bool IsListable(LiveGameStateDto s) => !s.IsOver && s.WhitePlayerId != s.BlackPlayerId;

    /// <summary>
    /// C-8-5. The same predicate <c>PublicApiEndpoints</c> applies to its <c>/api/v1</c> twins, kept
    /// as a one-line copy rather than shared, because the two route groups are deliberately allowed to
    /// diverge on everything else and a shared helper would suggest otherwise. A solo/practice game has
    /// the same id on both sides, so its owner still passes.
    /// </summary>
    private static bool IsParticipant(GameSession s, Guid me) =>
        s.WhitePlayerId == me || s.BlackPlayerId == me;

    // ------------------------------------------------------------------------------------------
    // The HTTP play surface's shared translation layer (C-2-2 / S-1-15). Both /api/games and
    // /api/v1/games submit moves and resignations through IGameActionExecutor, the same single path
    // GameHub uses, so a move made over HTTP broadcasts MovePlayed and a resignation broadcasts
    // GameEnded, and both reach the owning instance when this one does not hold the game. What the
    // executor does not do is speak HTTP: it reports every rejection as a HubException carrying the
    // message the hub would have shown. These two helpers are the only place that message becomes a
    // status code, and they exist so the two route groups cannot answer the same rejection
    // differently.
    //
    // Matching on the message text is deliberate, not laziness. The executor's whole contract is that
    // a caller cannot tell a local rejection from one forwarded off-instance (GameActionExecutor's
    // class comment, README RPC rule 1), and a forwarded rejection arrives as nothing but that
    // string, so the text is the only signal that survives the round trip. Every message matched here
    // is thrown by GameSession or by the executor itself, never composed from caller input.
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// Maps a rejected move onto the status and body POST <c>{group}/games/{id}/moves</c> has always
    /// returned. Both handlers used to run their own <c>IsOver</c> and <c>PlayerOnTurn</c> checks
    /// before calling into the session, which is why "Game is already over." is re-worded: the
    /// hand-rolled check answered "Game is over." and the session's own guard was unreachable, so a
    /// client written against today's API keeps the body it has always parsed.
    /// </summary>
    internal static IResult MoveFailure(HubException ex) => ex.Message switch
    {
        "Game not found." => Results.NotFound(),
        "Not your turn." => Results.Forbid(),
        "Game is already over." => Results.BadRequest(new { error = "Game is over." }),
        // An unparseable UCI and an illegal move, both 400 with the engine's own wording, as before.
        _ => Results.BadRequest(new { error = ex.Message }),
    };

    /// <summary>
    /// The same for a rejected resignation. No re-wording here: the resign handlers never pre-checked
    /// anything, so "Game is already over." and "Not a player in this game." are already the exact
    /// strings those endpoints returned, at the exact status.
    /// </summary>
    internal static IResult ResignFailure(HubException ex) => ex.Message switch
    {
        "Game not found." => Results.NotFound(),
        _ => Results.BadRequest(new { error = ex.Message }),
    };

    /// <summary>
    /// Narrows the payload the executor broadcast to the fields the HTTP response has always carried.
    /// <c>MovePlayedDto</c> additionally holds the echoed UCI, the legal-move list and the rating
    /// change; <c>MoveResponse</c> deliberately keeps none of them, because adding a field here would
    /// change the published OpenAPI document.
    /// </summary>
    internal static MoveResponse ToMoveResponse(MovePlayedDto played) =>
        new(played.State, played.WhiteDelta, played.BlackDelta, played.Events, played.GameOver);

    /// <summary>
    /// Display-only approximation of the side-to-move's clock (README scope decision 5): wall-clock
    /// seconds elapsed since <see cref="LiveGameStateDto.LastMoveAtUtc"/>, no lag credit, floored at
    /// zero, and never charged, every authoritative clock read still happens on the owning instance,
    /// off <c>GameSession</c>'s monotonic anchor. The side <em>not</em> on move gets the DTO's base
    /// figure untouched, exactly as it would read if nobody had moved since the snapshot. Internal and
    /// pure for the same reason as <see cref="IsListable"/>.
    /// </summary>
    internal static (int WhiteTimeSeconds, int BlackTimeSeconds) ApproximateClocks(LiveGameStateDto s, DateTimeOffset now)
    {
        var elapsed = (int)Math.Max(0, (now - s.LastMoveAtUtc).TotalSeconds);
        var whiteTime = s.SideToMove == "White" ? Math.Max(0, s.WhiteTimeSeconds - elapsed) : s.WhiteTimeSeconds;
        var blackTime = s.SideToMove == "Black" ? Math.Max(0, s.BlackTimeSeconds - elapsed) : s.BlackTimeSeconds;
        return (whiteTime, blackTime);
    }

    internal static KiriChessConfig BuildConfig(string? preset, out string? pool)
    {
        // A missing preset must never silently pick an economy. Previously a null/blank preset fell
        // back to Flagfall (a Steal economy), so any caller that forgot to send the chosen tier, e.g.
        // a player who picked Mainspring (Burn), silently got a Steal game. Fail loudly instead so the
        // selected tier is always what reaches the engine.
        if (string.IsNullOrWhiteSpace(preset))
            throw new InvalidOperationException(
                $"A preset is required. Known: {string.Join(", ", KiriChessConfig.Presets.Keys)}");
        var key = preset.Trim().ToLowerInvariant();
        if (!KiriChessConfig.Presets.TryGetValue(key, out var cfg))
            throw new InvalidOperationException($"Unknown preset '{preset}'. Known: {string.Join(", ", KiriChessConfig.Presets.Keys)}");
        // Which pools are rated is declared in config/kirichess.rules.json, not repeated here.
        pool = RatingConstants.IsOfficialPool(key) ? key : null;
        return cfg.Clone();
    }

    /// <summary>
    /// Builds a <see cref="KiriChessConfig"/> from the Custom game builder's DTO. Starts from engine
    /// defaults (so any field the client omitted still has a sane value) and overwrites every field
    /// the DTO carries, then validates, throws <see cref="InvalidOperationException"/> or
    /// <see cref="FormatException"/> on any out-of-range value or unknown MoveCostMode, which the
    /// caller turns into a 400.
    /// </summary>
    internal static KiriChessConfig BuildCustomConfig(CustomConfigDto dto)
    {
        if (!Enum.TryParse<MoveCostMode>(dto.MoveCostMode, ignoreCase: true, out var moveCostMode))
            throw new InvalidOperationException(
                $"Unknown MoveCostMode '{dto.MoveCostMode}'. Known: {string.Join(", ", Enum.GetNames<MoveCostMode>())}");

        var config = new KiriChessConfig
        {
            StartTimeSeconds = dto.StartTimeSeconds,
            BaseIncrement = dto.BaseIncrement,
            EconomyScale = dto.EconomyScale,
            PieceValues = new Dictionary<PieceType, int>
            {
                { PieceType.Pawn, dto.PieceValues.Pawn },
                { PieceType.Knight, dto.PieceValues.Knight },
                { PieceType.Bishop, dto.PieceValues.Bishop },
                { PieceType.Rook, dto.PieceValues.Rook },
                { PieceType.Queen, dto.PieceValues.Queen },
            },
            CaptureMult = dto.CaptureMult,
            CaptureCaptorGainEnabled = dto.CaptureCaptorGainEnabled,
            CaptureVictimLossEnabled = dto.CaptureVictimLossEnabled,
            MoveCostMode = moveCostMode,
            MoveCostPerPiece = new Dictionary<PieceType, int>
            {
                { PieceType.Pawn, dto.MoveCostPerPiece.Pawn },
                { PieceType.Knight, dto.MoveCostPerPiece.Knight },
                { PieceType.Bishop, dto.MoveCostPerPiece.Bishop },
                { PieceType.Rook, dto.MoveCostPerPiece.Rook },
                { PieceType.Queen, dto.MoveCostPerPiece.Queen },
                { PieceType.King, dto.MoveCostPerPiece.King },
            },
            CastleCostFlat = dto.CastleCostFlat,
            MoveCostFlat = dto.MoveCostFlat,
            MoveCostPerSquare = dto.MoveCostPerSquare,
            CheckCheckerGain = dto.CheckCheckerGain,
            CheckVictimLoss = dto.CheckVictimLoss,
            DoubleCheckChipMultiplier = dto.DoubleCheckChipMultiplier,
            PromotionCostMult = dto.PromotionCostMult,
            RepeatedPositionNullifiesTime = dto.RepeatedPositionNullifiesTime,
            ThreefoldRepetitionIsDraw = dto.ThreefoldRepetitionIsDraw,
            FlagFallLoses = dto.FlagFallLoses,
            FlagVsInsufficientMaterialIsDraw = dto.FlagVsInsufficientMaterialIsDraw,
        };

        config.Validate();
        return config;
    }
}
