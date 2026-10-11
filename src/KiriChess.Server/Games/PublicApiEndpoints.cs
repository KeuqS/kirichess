using System.Security.Claims;
using System.Text;
using KiriChess.Engine.Config;
using KiriChess.Engine;
using KiriChess.Engine.Core;
using KiriChess.Server.Account;
using KiriChess.Server.Auth;
using KiriChess.Server.Data;
using KiriChess.Server.Data.Entities;
using KiriChess.Server.Games.Distributed;
using KiriChess.Server.Infrastructure;
using KiriChess.Server.Infrastructure.Redis;
using KiriChess.Server.Moderation;
using KiriChess.Server.Ratings;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace KiriChess.Server.Games;

/// <summary>
/// The published bot/public HTTP surface (Phase 9f.5). First-party UI stays on unversioned
/// <c>/api/...</c>; this group is the promise to third parties. Authenticated only by the
/// <see cref="ApiTokenDefaults.Scheme"/> scheme - a session JWT is 401, and a token never satisfies
/// unversioned <c>RequireAuthorization()</c> routes.
/// </summary>
public static class PublicApiEndpoints
{
    public static IEndpointRouteBuilder MapPublicApiEndpoints(this IEndpointRouteBuilder app)
    {
        var v1 = app.MapGroup("/api/v1");

        // S-5-4: the coarse lever for this whole surface, checked once on the parent group so both
        // `read` and `play` (mapped from it below) inherit it - every route in this file funnels
        // through here the same way every game creation path funnels through
        // GameRegistry.CreateAsync for KillSwitch.Pairing. Runs inside endpoint execution, after
        // authentication/authorization/rate limiting have already run, so it never becomes a new
        // pre-auth oracle; it can only refuse a request that was already going to be served.
        v1.AddEndpointFilter(async (context, next) =>
        {
            var killSwitches = context.HttpContext.RequestServices.GetRequiredService<KillSwitchService>();
            if (!await killSwitches.IsEnabledAsync(KillSwitch.Api, context.HttpContext.RequestAborted))
                return Results.Json(new { error = "The public API is temporarily unavailable." },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            return await next(context);
        });

        var read = v1.MapGroup("")
            .RequireAuthorization(ApiTokenPolicies.Read)
            .RequireRateLimiting(RateLimitPolicies.ApiRead);

        // S-1-7 / C-6-9: a default policy on the group, not just on each route, so a route added here
        // later without its own .RequireRateLimiting call is never unlimited by omission - the exact
        // failure DELETE /challenges/{token} shipped with. A route that needs a different policy (the
        // moves/resign token-bucket, or read's ApiRead) still overrides it explicitly below; group
        // conventions apply before an endpoint's own, so the endpoint's call wins, matching how
        // EnableRateLimitingAttribute resolution is documented in RateLimiting.cs.
        var play = v1.MapGroup("")
            .RequireAuthorization(ApiTokenPolicies.Play)
            .RequireRateLimiting(RateLimitPolicies.ApiPlayCreate);

        read.MapGet("/account", (ClaimsPrincipal user) =>
        {
            var me = user.GetUserId();
            if (me is null) return Results.Unauthorized();
            var name = user.FindFirstValue("displayName") ?? "";
            var scopes = user.FindAll(ApiTokenClaims.Scope).Select(c => c.Value).Distinct().ToArray();
            return Results.Ok(new PublicAccountDto(me.Value, name, scopes));
        })
        .Produces<PublicAccountDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized);

        read.MapGet("/account/playing", async (
            ClaimsPrincipal user, GameRegistry registry, LiveGameIndex liveGames,
            IRedisConnection connection, CancellationToken ct) =>
        {
            var me = user.GetUserId();
            if (me is null) return Results.Unauthorized();

            if (connection.Enabled)
            {
                // Per-player index (I-1-5): scanning the freshest-200 live list can miss this
                // user's own game once the cluster is past that cap.
                var rows = (await liveGames.PlayingGamesAsync(me.Value, ct))
                    .Select(s => ToPlaying(s, me.Value))
                    .ToArray();
                return Results.Ok(rows);
            }

            var local = registry.ActiveSessions
                .Where(s => !s.IsOver && (s.WhitePlayerId == me.Value || s.BlackPlayerId == me.Value))
                .Select(s => new PublicPlayingGameDto(
                    s.Id, s.RatingPool, s.IsRated,
                    (s.WhitePlayerId == me.Value ? Side.White : Side.Black).ToString(),
                    s.WhitePlayerId, s.BlackPlayerId,
                    s.Position.SideToMove.ToString(), s.MoveCount))
                .ToArray();
            return Results.Ok(local);
        })
        .Produces<PublicPlayingGameDto[]>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized);

        read.MapGet("/games/mine", async (
            ClaimsPrincipal user, KiriChessDbContext db,
            int? take, int? skip, DateTimeOffset? before, Guid? beforeId, CancellationToken ct) =>
        {
            var me = user.GetUserId();
            if (me is null) return Results.Unauthorized();
            if ((before is null) != (beforeId is null))
                return Results.BadRequest(new ErrorResponse("before and beforeId must be sent together."));

            // Same keyset as GET /api/games/mine. `skip` is ignored when `before` and `beforeId` are set.
            return Results.Ok(await GameHistoryQuery.ListMineAsync(db, me.Value, take, skip, before, beforeId, ct));
        })
        .Produces<GameHistoryEntryDto[]>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces<ErrorResponse>(StatusCodes.Status400BadRequest);

        read.MapGet("/games/{id:guid}", async (
            Guid id, ClaimsPrincipal user, GameRegistry registry, KiriChessEngine engine,
            KiriChessDbContext db, CancellationToken ct) =>
        {
            var me = user.GetUserId();
            if (me is null) return Results.Unauthorized();

            var live = registry.Get(id);
            if (live is not null)
            {
                if (!IsParticipant(live, me.Value)) return Results.NotFound();
                return Results.Ok(live.ToState());
            }

            var row = await db.Games.AsNoTracking().FirstOrDefaultAsync(g => g.Id == id, ct);
            if (row is null) return Results.NotFound();

            if (row.Result == GameResult.Ongoing)
            {
                if (!IsParticipant(row, me.Value)) return Results.NotFound();
                return Results.Ok(RestoreState(engine, row));
            }

            return Results.Ok(RestoreState(engine, row));
        })
        .Produces<GameStateResponse>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status404NotFound);

        read.MapGet("/games/{id:guid}/legal-moves", async (
            Guid id, ClaimsPrincipal user, GameRegistry registry, KiriChessEngine engine,
            KiriChessDbContext db, CancellationToken ct) =>
        {
            var me = user.GetUserId();
            if (me is null) return Results.Unauthorized();

            var live = registry.Get(id);
            if (live is not null)
            {
                if (!IsParticipant(live, me.Value) || live.IsOver) return Results.NotFound();
                return Results.Ok(live.LegalMoveStrings());
            }

            var row = await db.Games.AsNoTracking().FirstOrDefaultAsync(g => g.Id == id, ct);
            if (row is null || row.Result != GameResult.Ongoing || !IsParticipant(row, me.Value))
                return Results.NotFound();

            var session = GameSession.Restore(engine, row);
            if (session.IsOver) return Results.NotFound();
            return Results.Ok(session.LegalMoveStrings());
        })
        .Produces<string[]>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status404NotFound);

        read.MapGet("/games/{id:guid}/replay", async (
            Guid id, KiriChessDbContext db, KiriChessEngine engine, IGameClock clock, HttpContext http,
            ILoggerFactory loggerFactory, KiriChessMetrics metrics, CancellationToken ct) =>
        {
            // GameReplayBuilder is a static class and cannot be an ILogger<T> type argument, so the
            // category is named explicitly instead - the same string ILogger<GameReplayBuilder> would
            // have produced. Mirrors ReplayEndpoints's identical route.
            var logger = loggerFactory.CreateLogger("KiriChess.Server.Games.GameReplayBuilder");
            var row = await db.Games
                .AsNoTracking()
                .FirstOrDefaultAsync(g => g.Id == id && g.Result != GameResult.Ongoing, ct);
            if (row is null) return Results.NotFound();

            var names = await LoadNamesAsync(db, row, clock.UtcNow, ct);
            ReplayEndpoints.ApplyImmutableCacheHeaders(http, id);
            return Results.Ok(GameReplayBuilder.Build(
                engine, row, names.White, names.Black, names.WhitePatronTier, names.BlackPatronTier,
                logger, metrics));
        })
        .Produces<ReplayResponse>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status404NotFound);

        read.MapGet("/games/{id:guid}/pgn", async (
            Guid id, KiriChessDbContext db, KiriChessEngine engine, IConfiguration configuration,
            IGameClock clock, HttpContext http, ILoggerFactory loggerFactory, KiriChessMetrics metrics,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("KiriChess.Server.Games.PgnBuilder");
            var row = await db.Games
                .AsNoTracking()
                .FirstOrDefaultAsync(g => g.Id == id && g.Result != GameResult.Ongoing, ct);
            if (row is null) return Results.NotFound();

            var names = await LoadNamesAsync(db, row, clock.UtcNow, ct);
            var elos = await db.RatingHistories
                .AsNoTracking()
                .Where(h => h.GameId == id)
                .Select(h => new { h.UserId, h.ValueBefore })
                .ToDictionaryAsync(h => h.UserId, h => h.ValueBefore, ct);

            static int? Elo(Dictionary<Guid, double> map, Guid playerId) =>
                map.TryGetValue(playerId, out var v) ? (int)Math.Round(v) : null;

            var siteBaseUrl = configuration.GetValue("Frontend:BaseUrl", string.Empty)?.TrimEnd('/');
            var pgn = PgnBuilder.Build(
                engine, row, names.White, names.Black,
                Elo(elos, row.WhitePlayerId), Elo(elos, row.BlackPlayerId), siteBaseUrl, logger, metrics);
            ReplayEndpoints.ApplyImmutableCacheHeaders(http, id);
            return Results.File(Encoding.UTF8.GetBytes(pgn), "application/x-chess-pgn",
                $"kirichess-{id}.pgn");
        })
        .Produces<string>(StatusCodes.Status200OK, "application/x-chess-pgn")
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status404NotFound);

        read.MapGet("/ratings/me", async (ClaimsPrincipal user, KiriChessDbContext db) =>
        {
            var me = user.GetUserId();
            if (me is null) return Results.Unauthorized();
            var rows = await db.Ratings
                .Where(r => r.UserId == me.Value && r.Pool.StartsWith(RatingConstants.BotPoolPrefix))
                .ToListAsync();
            return Results.Ok(rows.Select(RatingEndpoints.ToDto).ToArray());
        })
        .Produces<RatingDto[]>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized);

        read.MapGet("/leaderboards/bot/{pool}", async (
            string pool, KiriChessDbContext db, LeaderboardCache cache, int? limit, bool? includeProvisional) =>
        {
            var key = pool.Trim().ToLowerInvariant();
            if (!RatingConstants.IsOfficialPool(key)) return Results.NotFound();
            var botPool = RatingConstants.ToBotPool(key);
            int take = Math.Clamp(limit ?? 50, 1, 200);

            // Patron tier is a time-sensitive read (a one-off badge lapses on a date), so both
            // branches below need the same instant. Inlined rather than via PatronBadge.TierExpression
            // for the same reason as RatingEndpoints's leaderboard: composing a second
            // Expression<Func<...>> into this Join projection is not guaranteed to translate.
            var now = DateTimeOffset.UtcNow;
            var ids = await cache.TopAsync(botPool, take, includeProvisional == true);
            if (ids is not null)
            {
                // Same re-check as the human leaderboard. A dropped id means the ZSET's top-N is no
                // longer the eligible top-N, so fall through to Postgres rather than serve a short board.
                var cachedRatings = db.Ratings.Where(r => r.Pool == botPool && ids.Contains(r.UserId));
                if (includeProvisional != true)
                    cachedRatings = cachedRatings.WhereCurrent(now);

                var rowsById = await cachedRatings
                    .Join(db.Users, r => r.UserId, u => u.Id,
                        (r, u) => new LeaderboardRow(r.UserId, u.DisplayName, r.Value, r.RatingDeviation, r.GamesPlayed,
                            u.IsPatron && (u.PatronKind == PatronKind.Monthly || u.PatronExpiresAt == null || u.PatronExpiresAt > now)
                                ? u.PatronTier : PatronTier.None,
                            u.AvatarUpdatedAt))
                    .ToDictionaryAsync(x => x.UserId);
                if (rowsById.Count == ids.Count)
                    return Results.Ok(RatingEndpoints.RankByCacheOrder(ids, rowsById));
            }

            var q = db.Ratings.Where(r => r.Pool == botPool);
            if (includeProvisional != true)
                q = q.WhereCurrent(now);

            var rows = await q
                .OrderByDescending(r => r.Value)
                .Take(take)
                .Join(db.Users, r => r.UserId, u => u.Id,
                    (r, u) => new
                    {
                        r.UserId, u.DisplayName, r.Value, r.RatingDeviation, r.GamesPlayed,
                        PatronTier = u.IsPatron
                            && (u.PatronKind == PatronKind.Monthly || u.PatronExpiresAt == null || u.PatronExpiresAt > now)
                                ? u.PatronTier : PatronTier.None,
                        u.AvatarUpdatedAt
                    })
                .ToListAsync();

            var entries = rows.Select((x, i) => new LeaderboardEntryDto(
                i + 1, x.UserId, x.DisplayName, x.Value, x.RatingDeviation, x.GamesPlayed,
                RatingConstants.IsProvisional(x.RatingDeviation), x.PatronTier.ToString(),
                AvatarUrls.For(x.UserId, x.AvatarUpdatedAt))).ToArray();
            return Results.Ok(entries);
        })
        .Produces<LeaderboardEntryDto[]>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status404NotFound);

        read.MapGet("/seeks", async (MatchmakingService matchmaking, KillSwitchService killSwitches, CancellationToken ct) =>
        {
            if (!await killSwitches.IsEnabledAsync(KillSwitch.SeekList, ct))
                return Results.Json(new { error = "The seek list is temporarily unavailable." },
                    statusCode: StatusCodes.Status503ServiceUnavailable);

            var seeks = await matchmaking.OpenSeeksAsync(RatingConstants.BotPools, ct);
            var rows = seeks.Select(s => new OpenSeekDto(s.Pool, s.CreatedAt)).ToArray();
            return Results.Ok(rows);
        })
        .Produces<OpenSeekDto[]>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status503ServiceUnavailable);

        play.MapPost("/games", async (
            CreateGameRequest req, ClaimsPrincipal user, GameRegistry registry, SanctionPolicy sanctions,
            PlayEligibilityPolicy eligibility, CancellationToken ct) =>
        {
            var me = user.GetUserId();
            if (me is null) return Results.Unauthorized();

            if (req.Rated)
                return Results.BadRequest(new { error = "API games are unrated. Join a bot pool over the hub for rated play." });

            var ban = await sanctions.BlockingSanctionAsync(me.Value, SanctionScope.Account, ct);
            if (ban is not null)
                return Results.Json(new { error = "Your account is suspended and cannot create games." },
                    statusCode: StatusCodes.Status403Forbidden);

            // An API token is minted by a signed-in user, so without this an unconfirmed account could
            // mint one and play the whole game surface through /api/v1, straight past the gate.
            try
            {
                await eligibility.EnsureEmailConfirmedAsync(me.Value, ct);
            }
            catch (InvalidGameActionException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status403Forbidden);
            }

            if (req.OpponentId is { } opponent && opponent != me.Value)
                return Results.BadRequest(new
                {
                    error = "Use a challenge link to invite another player. See POST /api/v1/challenges."
                });

            KiriChessConfig config;
            try
            {
                config = req.Custom is not null
                    ? GameEndpoints.BuildCustomConfig(req.Custom)
                    : GameEndpoints.BuildConfig(req.Preset, out _);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            var white = me.Value;
            var black = req.OpponentId ?? me.Value;
            GameSession session;
            try
            {
                session = await registry.CreateAsync(config, white, black, pool: null, rated: false);
            }
            catch (GameCreationPausedException ex)
            {
                // S-5-1, and the bot API is the path most likely to keep hammering through an
                // incident: a script does not read a banner. 503 is the one status a well-behaved
                // client backs off on. Not declared with .Produces, same frozen-document reason as
                // the first-party twin.
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            return Results.Created($"/api/v1/games/{session.Id}", session.ToState());
        })
        .RequireRateLimiting(RateLimitPolicies.ApiPlayCreate)
        .Produces<GameStateResponse>(StatusCodes.Status201Created)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces<ErrorResponse>(StatusCodes.Status403Forbidden)
        .Produces<ErrorResponse>(StatusCodes.Status400BadRequest);

        // The bot play surface /developers advertises. It goes through IGameActionExecutor rather than
        // reaching into GameRegistry itself, for the two reasons C-2-2 and S-1-15 name: a bot's move
        // has to reach the human's board over the hub, and it has to reach the instance that owns the
        // game. GameEndpoints.MoveFailure is shared so this route and /api/games/{id}/moves cannot
        // answer the same rejection differently.
        play.MapPost("/games/{id:guid}/moves", async (
            Guid id, MoveRequest req, ClaimsPrincipal user, IGameActionExecutor actions, CancellationToken ct) =>
        {
            var me = user.GetUserId();
            if (me is null) return Results.Unauthorized();

            MovePlayedDto played;
            try { played = await actions.SubmitMoveAsync(id, me.Value, connectionId: string.Empty, req.Uci, ct); }
            catch (HubException ex) { return GameEndpoints.MoveFailure(ex); }

            return Results.Ok(GameEndpoints.ToMoveResponse(played));
        })
        .RequireRateLimiting(RateLimitPolicies.ApiPlayMove)
        .Produces<MoveResponse>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status404NotFound)
        .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status403Forbidden);

        play.MapPost("/games/{id:guid}/resign", async (
            Guid id, ClaimsPrincipal user, IGameActionExecutor actions, CancellationToken ct) =>
        {
            var me = user.GetUserId();
            if (me is null) return Results.Unauthorized();

            GameEndedDto ended;
            try { ended = await actions.ResignAsync(id, me.Value, connectionId: string.Empty, ct); }
            catch (HubException ex) { return GameEndpoints.ResignFailure(ex); }

            return Results.Ok(ended.State);
        })
        .RequireRateLimiting(RateLimitPolicies.ApiPlayMove)
        .Produces<GameStateResponse>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status404NotFound)
        .Produces<ErrorResponse>(StatusCodes.Status400BadRequest);

        play.MapPost("/challenges", async (
            CreateChallengeRequest req, ClaimsPrincipal user, ChallengeService challenges,
            PlayEligibilityPolicy eligibility, CancellationToken ct) =>
        {
            var me = user.GetUserId();
            if (me is null) return Results.Unauthorized();

            var prepared = ChallengeEndpoints.TryPrepareCreate(req);
            if (prepared.Error is { } createError) return createError;

            try
            {
                await eligibility.EnsureAccountNotBannedAsync(me.Value, ct);
                await eligibility.EnsureEmailConfirmedAsync(me.Value, ct);
            }
            catch (InvalidGameActionException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status403Forbidden);
            }

            Challenge challenge;
            try
            {
                challenge = await challenges.CreateAsync(
                    me.Value, prepared.Pool, prepared.Colour, ct, prepared.CustomConfig,
                    challengerViaApi: true);
            }
            catch (InvalidGameActionException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status409Conflict);
            }

            return Results.Created(
                $"/api/v1/challenges/{challenge.Token}", challenge.ToDto(DateTimeOffset.UtcNow));
        })
        .RequireRateLimiting(RateLimitPolicies.ApiPlayCreate)
        .Produces<ChallengeDto>(StatusCodes.Status201Created)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status403Forbidden)
        .Produces(StatusCodes.Status409Conflict)
        .Produces(StatusCodes.Status429TooManyRequests);

        // S-4-6: this is a read - it lists the caller's own open challenges, it changes nothing - and
        // used to sit on the play group, so a read-only bot had to be handed the play scope (which also
        // grants game creation, moves and resignation) just to monitor its outstanding invitations. The
        // rate-limit policy already agreed with that reading before this line did (ApiRead, not
        // ApiPlayCreate), which was the tell that the group placement, not the policy, was the mistake.
        read.MapGet("/challenges/mine", async (ClaimsPrincipal user, ChallengeService challenges, CancellationToken ct) =>
        {
            var me = user.GetUserId();
            if (me is null) return Results.Unauthorized();
            var now = DateTimeOffset.UtcNow;
            var mine = await challenges.MineAsync(me.Value, ct);
            return Results.Ok(mine.Select(c => c.ToDto(now)).ToArray());
        })
        .Produces<ChallengeDto[]>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized);

        play.MapPost("/challenges/{token}/accept", async (
            string token, ClaimsPrincipal user, KiriChessDbContext db, ChallengeService challenges,
            PlayEligibilityPolicy eligibility, GameRegistry registry, MatchNoShowWatcher matchNoShow,
            IHubContext<GameHub> hub, PairingNotificationService notifications,
            MatchmakingService matchmaking, KillSwitchService killSwitches, CancellationToken ct) =>
        {
            var me = user.GetUserId();
            if (me is null) return Results.Unauthorized();
            return await ChallengeEndpoints.AcceptChallengeAsync(
                token, me.Value, db, challenges, eligibility, registry, matchNoShow, hub, notifications,
                matchmaking, killSwitches, ct, accepterViaApi: true);
        })
        .RequireRateLimiting(RateLimitPolicies.ApiPlayCreate)
        .Produces<ChallengeAcceptedDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status403Forbidden)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status409Conflict);

        play.MapDelete("/challenges/{token}", async (string token, ClaimsPrincipal user, ChallengeService challenges, CancellationToken ct) =>
        {
            var me = user.GetUserId();
            if (me is null) return Results.Unauthorized();
            var cancelled = await challenges.CancelAsync(token, me.Value, ct);
            return cancelled ? Results.NoContent() : Results.NotFound();
        })
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status404NotFound);

        return app;
    }

    private static PublicPlayingGameDto ToPlaying(LiveGameStateDto s, Guid me) => new(
        s.GameId, s.RatingPool, s.IsRated,
        (s.WhitePlayerId == me ? Side.White : Side.Black).ToString(),
        s.WhitePlayerId, s.BlackPlayerId, s.SideToMove, s.MoveCount);

    private static bool IsParticipant(GameSession s, Guid me) =>
        s.WhitePlayerId == me || s.BlackPlayerId == me;

    private static bool IsParticipant(Game row, Guid me) =>
        row.WhitePlayerId == me || row.BlackPlayerId == me;

    // RematchOfferedById stays null here: /api/v1 serves finished games to anyone, and who offered
    // a rematch is a fact about the two players, not a public one.
    private static GameStateResponse RestoreState(KiriChessEngine engine, Game row) =>
        GameMapping.FromPersisted(engine, row);

    /// <summary>
    /// Both players' display names and current patron tiers in one query. The PGN caller ignores the
    /// tiers (a PGN tag pair has nowhere sensible to put a cosmetic badge); the replay caller uses
    /// them. Tiers resolve to "None" for an account that no longer exists, because a replay outlives
    /// its players and must not fail on a missing row.
    /// </summary>
    private static async Task<(string? White, string? Black, string WhitePatronTier, string BlackPatronTier)> LoadNamesAsync(
        KiriChessDbContext db, Game row, DateTimeOffset now, CancellationToken ct)
    {
        var ids = new[] { row.WhitePlayerId, row.BlackPlayerId };
        var rows = await db.Users
            .AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .Select(u => new { u.Id, u.DisplayName, u.IsPatron, u.PatronTier, u.PatronKind, u.PatronExpiresAt })
            .ToDictionaryAsync(u => u.Id, ct);

        // Same rule as PatronBadge.For, inlined because this is an anonymous projection rather than
        // an ApplicationUser; see Account/PatronBadge.cs for the definition, including why a Monthly
        // badge ignores the expiry column.
        string TierFor(Guid playerId) =>
            rows.TryGetValue(playerId, out var r) && r.IsPatron
            && (r.PatronKind == PatronKind.Monthly || r.PatronExpiresAt is null || r.PatronExpiresAt > now)
                ? r.PatronTier.ToString()
                : GameReplayBuilder.NoPatronTier;

        rows.TryGetValue(row.WhitePlayerId, out var white);
        rows.TryGetValue(row.BlackPlayerId, out var black);
        return (white?.DisplayName, black?.DisplayName, TierFor(row.WhitePlayerId), TierFor(row.BlackPlayerId));
    }

}
