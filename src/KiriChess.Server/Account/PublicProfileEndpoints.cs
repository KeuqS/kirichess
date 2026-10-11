using KiriChess.Server.Data;
using KiriChess.Server.Data.Entities;
using KiriChess.Server.Games;
using KiriChess.Server.Infrastructure;
using KiriChess.Server.Ratings;
using Microsoft.EntityFrameworkCore;

namespace KiriChess.Server.Account;

/// <summary>
/// Public, anonymous read of one account: GET /api/users/{id}.
///
/// Anonymous for the same reason /leaderboards and /replay are (Phase 8.1/8.2): the leaderboard
/// already hands every visitor a user id and a name, and "who is this player" is a question a
/// signed-out visitor deciding whether to sign up asks first. It carries
/// <see cref="RateLimitPolicies.Replay"/>, the per-IP policy the other anonymous read paths use,
/// because there is no user to partition an anonymous caller by.
///
/// An erased account (8a.2 tombstone, <see cref="ApplicationUser.DeletedAt"/>) answers 404,
/// identical to an unknown id. The tombstone row survives only to keep foreign keys valid; it is
/// not a profile, and its scrubbed columns are not something to render. This is the one filter in
/// here that is a correctness control rather than a tidiness one - dropping it would publish a
/// "deletedaccount" page for every user who has exercised their erasure right.
/// </summary>
public static class PublicProfileEndpoints
{
    public static IEndpointRouteBuilder MapPublicProfileEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/users/{id:guid}", async (
            Guid id, KiriChessDbContext db, IGameClock clock, CancellationToken ct) =>
        {
            // Loaded as a full entity rather than a projection so PatronBadge.For - the canonical
            // in-memory answer to "does a badge show right now" - can be reused instead of the
            // badge rule being spelled out one more time. Same call GameEndpoints' live list
            // makes, and one row.
            var user = await db.Users
                .AsNoTracking()
                .Where(u => u.Id == id && u.DeletedAt == null)
                .Select(u => new
                {
                    u.Id,
                    u.DisplayName,
                    u.CreatedAt,
                    u.IsPatron,
                    u.PatronKind,
                    u.PatronExpiresAt,
                    u.PatronTier,
                    u.AvatarUpdatedAt,
                })
                .FirstOrDefaultAsync(ct);
            if (user is null) return Results.NotFound();

            var ratings = await db.Ratings
                .AsNoTracking()
                .Where(r => r.UserId == id)
                .OrderBy(r => r.Pool)
                .ToListAsync(ct);

            // Finished games only, matching what a visitor could actually open from /replay. An
            // ongoing game is excluded here for the same reason ReplayEndpoints hides it: whether
            // this player is in a live game right now is not this endpoint's to confirm.
            var gamesPlayed = await db.Games
                .AsNoTracking()
                .CountAsync(g => g.Result != GameResult.Ongoing
                    && (g.WhitePlayerId == id || g.BlackPlayerId == id), ct);

            var badgeUser = new ApplicationUser
            {
                IsPatron = user.IsPatron,
                PatronKind = user.PatronKind,
                PatronExpiresAt = user.PatronExpiresAt,
                PatronTier = user.PatronTier,
            };

            return Results.Ok(new PublicProfileDto(
                user.Id,
                user.DisplayName,
                user.CreatedAt,
                PatronBadge.For(badgeUser, clock.UtcNow).ToString(),
                gamesPlayed,
                ratings.Select(RatingEndpoints.ToDto).ToArray(),
                AvatarUrls.For(user.Id, user.AvatarUpdatedAt)));
        })
        .RequireRateLimiting(RateLimitPolicies.Replay)
        .Produces<PublicProfileDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound);

        app.MapGet("/api/users/{id:guid}/avatar", async (
            Guid id, AvatarService avatars, CancellationToken ct) =>
        {
            var file = await avatars.GetPublicAsync(id, ct);
            if (file is null) return Results.NotFound();
            return Results.File(file.Bytes, JpegAvatar.ContentType, enableRangeProcessing: false);
        })
        .RequireRateLimiting(RateLimitPolicies.Replay)
        .Produces(StatusCodes.Status200OK, contentType: JpegAvatar.ContentType)
        .Produces(StatusCodes.Status404NotFound);

        return app;
    }
}
