using System.Security.Claims;
using KiriChess.Server.Auth;
using KiriChess.Engine.Config;
using KiriChess.Server.Data;
using KiriChess.Server.Data.Entities;
using KiriChess.Server.Infrastructure;
using KiriChess.Server.Ratings;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace KiriChess.Server.Games;

/// <summary>
/// Phase 9d.8. Minimal-API endpoints for the direct challenge / play-a-friend link. Two auth postures
/// on one route table, same shape as <see cref="GameEndpoints"/>'s live-games list: an authorized
/// <c>MapGroup</c> for everything that mutates or is personal, plus one anonymous route mapped on
/// <c>app</c> directly (a logged-out friend must be able to read a challenge before signing up - see
/// Phase 9d.8 fact 6 and scope decision 4's growth-mechanism framing).
/// </summary>
public static class ChallengeEndpoints
{
    public static IEndpointRouteBuilder MapChallengeEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/challenges").RequireAuthorization();

        group.MapPost("/", async (
            CreateChallengeRequest req, ClaimsPrincipal user, ChallengeService challenges,
            PlayEligibilityPolicy eligibility, CancellationToken ct) =>
        {
            var me = user.GetUserId();
            if (me is null) return Results.Unauthorized();

            var prepared = TryPrepareCreate(req);
            if (prepared.Error is { } createError) return createError;

            // AccountBan must not be able to mint an invitation at all. RatedPlayBan does not apply:
            // friend challenges are always unrated (a shared link is too easy to farm Glicko).
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
                    me.Value, prepared.Pool, prepared.Colour, ct, prepared.CustomConfig);
            }
            catch (InvalidGameActionException ex)
            {
                // The open-challenge cap (6e.3) - a state conflict, not an eligibility verdict.
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status409Conflict);
            }

            return Results.Created(
                $"/api/challenges/{challenge.Token}", challenge.ToDto(DateTimeOffset.UtcNow));
        })
        .RequireRateLimiting(RateLimitPolicies.ChallengeCreate) // 6e.3: each call mints an invitation
        .Produces<ChallengeDto>(StatusCodes.Status201Created)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status403Forbidden)
        .Produces(StatusCodes.Status409Conflict)
        .Produces(StatusCodes.Status429TooManyRequests);

        group.MapGet("/mine", async (ClaimsPrincipal user, ChallengeService challenges, CancellationToken ct) =>
        {
            var me = user.GetUserId();
            if (me is null) return Results.Unauthorized();

            var now = DateTimeOffset.UtcNow;
            var mine = await challenges.MineAsync(me.Value, ct);
            return Results.Ok(mine.Select(c => c.ToDto(now)).ToArray());
        })
        .Produces<ChallengeDto[]>(StatusCodes.Status200OK);

        // The accept handler - the one piece that must be exactly right. Order is load-only-in-scope
        // -> claim -> create -> notify. Do not reorder (README scope decision 3).
        group.MapPost("/{token}/accept", async (
            string token, ClaimsPrincipal user, KiriChessDbContext db, ChallengeService challenges,
            PlayEligibilityPolicy eligibility, GameRegistry registry, MatchNoShowWatcher matchNoShow,
            IHubContext<GameHub> hub, PairingNotificationService notifications,
            MatchmakingService matchmaking, KillSwitchService killSwitches, CancellationToken ct) =>
        {
            var me = user.GetUserId();
            if (me is null) return Results.Unauthorized();
            return await AcceptChallengeAsync(
                token, me.Value, db, challenges, eligibility, registry, matchNoShow, hub, notifications,
                matchmaking, killSwitches, ct);
        })
        .Produces<ChallengeAcceptedDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status403Forbidden)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status409Conflict);

        group.MapDelete("/{token}", async (string token, ClaimsPrincipal user, ChallengeService challenges, CancellationToken ct) =>
        {
            var me = user.GetUserId();
            if (me is null) return Results.Unauthorized();

            // Challenger only, any time while Open - the revocation half of "unguessability is not the
            // protection" (README scope decision 1). CancelAsync's own guard already excludes a
            // wrong-owner or already-resolved row, so "not mine" and "does not exist" both read as 404
            // here rather than leaking which one it was.
            var cancelled = await challenges.CancelAsync(token, me.Value, ct);
            return cancelled ? Results.NoContent() : Results.NotFound();
        })
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status404NotFound);

        // Anonymous, mapped on `app` rather than `group` (which carries RequireAuthorization()) - a
        // logged-out friend has to be able to see who challenged them before deciding to sign up, which
        // is the entire growth mechanism this feature exists for (README fact 6). It returns a display
        // name, a pool, a status and the economy on offer; nothing that identifies the account
        // beyond the display name - see ChallengeDto's own doc comment.
        app.MapGet("/api/challenges/{token}", async (string token, ChallengeService challenges, CancellationToken ct) =>
        {
            var challenge = await challenges.FindAsync(token, ct);
            if (challenge is null) return Results.NotFound(new { error = "That challenge link is not valid." });

            return Results.Ok(challenge.ToDto(DateTimeOffset.UtcNow));
        })
        .Produces<ChallengeDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound);

        return app;
    }

    /// <summary>
    /// Shared accept path for first-party <c>POST /api/challenges/{token}/accept</c> and
    /// <c>POST /api/v1/challenges/{token}/accept</c>. The claim-then-create order is load-bearing
    /// (README scope decision 3); do not reorder.
    /// </summary>
    internal static async Task<IResult> AcceptChallengeAsync(
        string token, Guid me, KiriChessDbContext db, ChallengeService challenges,
        PlayEligibilityPolicy eligibility, GameRegistry registry, MatchNoShowWatcher matchNoShow,
        IHubContext<GameHub> hub, PairingNotificationService notifications,
        MatchmakingService matchmaking, KillSwitchService killSwitches, CancellationToken ct,
        bool accepterViaApi = false)
    {
        var challenge = await challenges.FindAsync(token, ct);
        if (challenge is null) return Results.NotFound(new { error = "That challenge link is not valid." });

        var now = DateTimeOffset.UtcNow;
        if (ChallengeService.StatusOf(challenge, now) != ChallengeStatus.Open)
            return Results.Json(new { error = "That challenge is no longer open." },
                statusCode: StatusCodes.Status409Conflict);

        // C-6-8: the pool was validated as official at create time (TryPrepareCreate) and never
        // re-validated here, so a pool key retired from config/kirichess.rules.json while this
        // challenge is still inside its one-hour TTL used to hit KiriChessConfig.Presets[pool] below
        // and throw KeyNotFoundException, a 500 for the friend clicking the link.
        // KiriChessConfig.PoolLabel's own stated convention for exactly this situation is to fall
        // back to the key rather than throw, so this checks and answers the same 409 the status check
        // above already uses, instead of indexing. Checked before the claim (ExecuteUpdateAsync)
        // below, not after, so a retired pool never consumes the one live challenge link the friend
        // was holding.
        if (challenge.CustomConfig is null && !KiriChessConfig.Presets.ContainsKey(challenge.Pool))
            return Results.Json(new { error = "That challenge is no longer open." },
                statusCode: StatusCodes.Status409Conflict);

        if (challenge.ChallengerId == me)
            return Results.BadRequest(new { error = "You can't accept your own challenge." });

        try
        {
            await eligibility.EnsureAccountNotBannedAsync(me, ct);
            // Only the accepter is gated. The challenger was checked when they minted the link, and
            // walling a friend out of a game because the *other* side has not read their mail yet
            // punishes the wrong account for something they cannot fix.
            await eligibility.EnsureEmailConfirmedAsync(me, ct);
            await eligibility.EnsureNotAlreadyInGameAsync(me, ct);
            await eligibility.EnsureNotAlreadyInGameAsync(
                challenge.ChallengerId, ct, "Your opponent is already in a game.");
        }
        catch (InvalidGameActionException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: ex.Message.Contains("already in a game")
                ? StatusCodes.Status409Conflict
                : StatusCodes.Status403Forbidden);
        }

        // S-5-1: checked BEFORE the claim below, which is the same ordering rule this file already
        // applies to the eligibility checks above. GameRegistry.CreateAsync is gated too and that
        // gate is the actual guarantee, but reaching it from here would mean the challenge is
        // already stamped AcceptedAt with no game to show for it. A switch flipped during an
        // incident must not also consume the one live challenge link the player was holding.
        if (!await killSwitches.IsEnabledAsync(KillSwitch.Pairing, ct))
            return Results.Json(new { error = "New games are temporarily paused. Please try again shortly." },
                statusCode: StatusCodes.Status503ServiceUnavailable);

        var claimed = await db.Challenges
            .Where(c => c.Id == challenge.Id && c.AcceptedAt == null && c.CancelledAt == null && c.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.AcceptedAt, now)
                .SetProperty(c => c.AcceptedById, me), ct);
        if (claimed == 0)
            return Results.Json(new { error = "That challenge is no longer open." },
                statusCode: StatusCodes.Status409Conflict);

        var challengerIsWhite = challenge.Colour switch
        {
            ChallengeColour.White => true,
            ChallengeColour.Black => false,
            _ => Random.Shared.Next(2) == 0,
        };
        var white = challengerIsWhite ? challenge.ChallengerId : me;
        var black = challengerIsWhite ? me : challenge.ChallengerId;

        KiriChessConfig config;
        if (challenge.CustomConfig is not null)
            config = challenge.CustomConfig.Clone();
        else
            config = KiriChessConfig.Presets[challenge.Pool].Clone();

        // C-6-1: entering a game and standing in a matchmaking queue must not be simultaneous states.
        // Neither of these two is necessarily queued, and RemoveUserAsync is a no-op when they are not.
        // Before CreateAsync, not after: leaving first leaves a window where a player is in neither,
        // which costs nothing, while creating first leaves the window where a stranger can still be
        // paired against somebody who has just sat down at another board.
        await matchmaking.LeaveUserAsync(white, ct);
        await matchmaking.LeaveUserAsync(black, ct);

        // The label follows the request that sat down, not the account. A token on either
        // side is a bot for this game; the other side, if it used the site, is not.
        var whiteViaApi = white == challenge.ChallengerId ? challenge.ChallengerViaApi : accepterViaApi;
        var blackViaApi = black == challenge.ChallengerId ? challenge.ChallengerViaApi : accepterViaApi;
        var session = await registry.CreateAsync(
            config, white, black, pool: null, rated: false,
            whiteViaApi: whiteViaApi, blackViaApi: blackViaApi);

        await db.Challenges.Where(c => c.Id == challenge.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.GameId, session.Id), ct);

        matchNoShow.Arm(session.Id, white, black);

        await hub.Clients.Group($"challenge:{token}").SendAsync(
            "ChallengeAccepted",
            new ChallengeAcceptedEvent(token, session.Id, challengerIsWhite ? "White" : "Black", challenge.Pool),
            ct);

        await notifications.NotifyChallengeAcceptedAsync(
            challenge.ChallengerId, session.Id, challenge.Pool, ct);

        return Results.Ok(new ChallengeAcceptedDto(
            session.Id, challengerIsWhite ? "Black" : "White", challenge.Pool));
    }

    internal readonly record struct PreparedCreate(
        string Pool, ChallengeColour Colour, KiriChessConfig? CustomConfig, IResult? Error);

    /// <summary>
    /// Shared by first-party POST /api/challenges and POST /api/v1/challenges: official pool XOR a
    /// custom config, plus a valid colour. Returns <see cref="PreparedCreate.Error"/> set on a 400.
    /// </summary>
    internal static PreparedCreate TryPrepareCreate(CreateChallengeRequest req)
    {
        if (!Enum.TryParse<ChallengeColour>(req.Colour, ignoreCase: true, out var colour))
        {
            return new PreparedCreate("", default, null, Results.BadRequest(new
            {
                error = $"Unknown colour '{req.Colour}'. Known: {string.Join(", ", Enum.GetNames<ChallengeColour>())}"
            }));
        }

        if (req.Custom is not null)
        {
            var mixedPool = req.Pool?.Trim().ToLowerInvariant();
            if (!string.IsNullOrEmpty(mixedPool)
                && mixedPool != ChallengeService.CustomPool
                && RatingConstants.IsOfficialPool(mixedPool))
            {
                return new PreparedCreate("", colour, null, Results.BadRequest(new
                {
                    error = "Send either a rated pool or a custom config, not both."
                }));
            }

            try
            {
                var config = GameEndpoints.BuildCustomConfig(req.Custom);
                return new PreparedCreate(ChallengeService.CustomPool, colour, config, null);
            }
            catch (Exception ex)
            {
                return new PreparedCreate("", colour, null, Results.BadRequest(new { error = ex.Message }));
            }
        }

        var pool = req.Pool?.Trim().ToLowerInvariant();
        if (!RatingConstants.IsOfficialPool(pool))
        {
            return new PreparedCreate("", colour, null, Results.BadRequest(new
            {
                error = $"Unknown pool '{req.Pool}'. Known: {string.Join(", ", RatingConstants.OfficialPools)}"
            }));
        }

        return new PreparedCreate(pool!, colour, null, null);
    }

}
