using System.Security.Claims;
using KiriChess.Server.Auth;
using KiriChess.Server.Data;
using KiriChess.Server.Data.Entities;
using KiriChess.Server.Games;
using KiriChess.Server.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace KiriChess.Server.Account;

/// <summary>
/// Minimal-API endpoints for the patron-badge reference handshake: mint the value a donate link
/// carries as <c>client_reference_id</c>, and read the caller's own current badge.
/// </summary>
public static class DonationEndpoints
{
    public static IEndpointRouteBuilder MapDonationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/donations").RequireAuthorization();

        group.MapPost("/reference", async (
            ClaimsPrincipal caller, KiriChessDbContext db, IGameClock clock, CancellationToken ct) =>
        {
            var me = caller.GetUserId();
            if (me is null) return Results.Unauthorized();

            var now = clock.UtcNow;

            // Reuse the caller's newest unconsumed, unexpired row if one exists - what stops a user
            // clicking five tiers from accumulating rows without bound.
            var existing = await db.DonationReferences
                .Where(r => r.UserId == me.Value && r.ConsumedAt == null && r.ExpiresAt > now)
                .OrderByDescending(r => r.CreatedAt)
                .FirstOrDefaultAsync(ct);
            if (existing is not null)
                return Results.Ok(new DonationReferenceDto(existing.Reference, existing.ExpiresAt));

            var row = new DonationReference
            {
                UserId = me.Value,
                Reference = Guid.NewGuid().ToString("N"),
                CreatedAt = now,
                ExpiresAt = now.AddHours(24)
            };
            db.DonationReferences.Add(row);
            await db.SaveChangesAsync(ct);

            return Results.Ok(new DonationReferenceDto(row.Reference, row.ExpiresAt));
        })
        .RequireRateLimiting(RateLimitPolicies.Donations)
        .Produces<DonationReferenceDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized);

        group.MapGet("/me", async (
            ClaimsPrincipal caller, KiriChessDbContext db, IGameClock clock, CancellationToken ct) =>
        {
            var me = caller.GetUserId();
            if (me is null) return Results.Unauthorized();

            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == me.Value, ct);
            if (user is null) return Results.Unauthorized();

            var now = clock.UtcNow;

            // IsPatron is derived from the badge, not read off the column. A one-off badge lapses on
            // a date with nothing sweeping the row, so the raw column stays true after it expires,
            // and reporting that next to a Tier of "None" would hand the UI a payload that
            // contradicts itself. PatronBadge.For is the one definition of "is there a badge now".
            var tier = PatronBadge.For(user, now);
            return Results.Ok(new PatronStatusDto(
                tier != PatronTier.None, tier.ToString(), user.PatronKind.ToString(), user.PatronSince, user.PatronExpiresAt));
        })
        .RequireRateLimiting(RateLimitPolicies.Donations)
        .Produces<PatronStatusDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized);

        return app;
    }
}
