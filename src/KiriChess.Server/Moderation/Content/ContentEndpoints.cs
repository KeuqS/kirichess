using System.Security.Claims;
using KiriChess.Server.Auth;
using KiriChess.Server.Data;
using KiriChess.Server.Data.Entities;
using KiriChess.Server.Infrastructure;
using KiriChess.Server.Moderation;
using Microsoft.EntityFrameworkCore;

namespace KiriChess.Server.Moderation.Content;

/// <summary>
/// Minimal-API endpoints for the Phase 8a.4 display-name surface: the player-facing check/rename pair
/// and the moderator-facing scan/forced-rename pair. Two groups with different auth, which is why this
/// does not simply hang off the existing <c>/api/moderation</c> group the way
/// <see cref="ModerationEndpoints"/> does.
/// </summary>
public static class ContentEndpoints
{
    public static IEndpointRouteBuilder MapContentEndpoints(this IEndpointRouteBuilder app)
    {
        // --- Player-facing: the account's own display name. ---
        var account = app.MapGroup("/api/account/display-name");

        account.MapGet("/check", async (
            string? candidate, ClaimsPrincipal caller, DisplayNameService displayNames, CancellationToken ct) =>
        {
            // Anonymous on purpose: the register form needs live feedback and the caller has no token
            // yet. This is a display-name enumeration oracle and that is acceptable - display names are
            // already public on /leaderboards, every game page and every replay, so it leaks nothing a
            // scraper could not read off the site. IP-partitioned rate limiting, same as every other
            // pre-auth route.
            //
            // excludeUserId is the caller when they happen to be signed in, so a logged-in player
            // checking their own current name is told it is available rather than "already taken".
            var me = caller.GetUserId();
            return Results.Ok(await displayNames.CheckAsync(candidate, me, ct));
        })
        .AllowAnonymous()
        .RequireRateLimiting(RateLimitPolicies.Auth)
        .Produces<DisplayNameVerdict>(StatusCodes.Status200OK);

        account.MapPost("/", async (
            ChangeDisplayNameRequest req, ClaimsPrincipal caller, DisplayNameService displayNames,
            ContentModerationOptions options, KiriChessDbContext db, CancellationToken ct) =>
        {
            var me = caller.GetUserId();
            if (me is null) return Results.Unauthorized();

            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == me.Value, ct);
            if (user is null) return Results.NotFound();

            // Cooldown, before the name check: the request can be perfectly well-formed and the name
            // perfectly fine - it is the TIMING that is refused, hence 409 rather than 400 (this also
            // lets the UI distinguish "pick another name" from "come back later" without string-
            // matching a message). DisplayNameChangedAt == null means the name has never been changed
            // since account creation, so no cooldown is owed - the first rename is always free.
            var now = DateTimeOffset.UtcNow;
            if (user.DisplayNameChangedAt is { } last)
            {
                var nextAllowed = last.AddDays(options.RenameCooldownDays);
                if (nextAllowed > now)
                {
                    return Results.Conflict(new
                    {
                        error = $"You can rename again on {nextAllowed:yyyy-MM-dd}."
                    });
                }
            }

            var verdict = await displayNames.CheckAsync(req.DisplayName, excludeUserId: me, ct);
            if (!verdict.Allowed)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["DisplayName"] = [verdict.Message] });

            DisplayNameService.Apply(user, req.DisplayName.Trim(), verdict.Normalized, now);

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // The unique index is the only thing that can arbitrate a race, exactly as on the
                // register path - two concurrent renames to the same name, one wins.
                return Results.BadRequest(new { error = "That name was just taken. Try another." });
            }

            return Results.Ok(new DisplayNameResponse(user.DisplayName, user.DisplayNameChangedAt!.Value));
        })
        // Do not revoke refresh tokens, re-issue a JWT, or touch RefreshTokenService here. The access
        // token carries a stale `displayName` claim (TokenService.cs) until the next refresh, and that
        // is fine: nothing authorizes on it. The client updates its stored name from this response.
        .RequireAuthorization()
        .RequireRateLimiting(RateLimitPolicies.DisplayName)
        .Produces<DisplayNameResponse>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status409Conflict)
        .ProducesValidationProblem()
        .Produces<ErrorResponse>(StatusCodes.Status400BadRequest);

        // --- Moderator-facing. Same policy as every other admin route in this codebase. ---
        var admin = app.MapGroup("/api/moderation/content")
                       .RequireAuthorization(ModerationPolicies.Admin);

        admin.MapGet("/display-names", async (
            bool? problemsOnly, int? take, int? skip, ContentModerationOptions options, KiriChessDbContext db, CancellationToken ct) =>
        {
            var takeClamped = Math.Clamp(take ?? 100, 1, 500);
            var skipValue = Math.Max(skip ?? 0, 0);
            var onlyProblems = problemsOnly ?? true;

            // Paged on purpose - do not load every user into memory. Ordered by CreatedAt so repeated
            // scans with increasing `skip` see a stable walk of the table.
            var page = await db.Users
                .OrderBy(u => u.CreatedAt)
                .Skip(skipValue).Take(takeClamped)
                .Select(u => new { u.Id, u.DisplayName, StoredNormalized = u.NormalizedDisplayName, u.CreatedAt })
                .ToListAsync(ct);

            if (page.Count == 0) return Results.Ok(Array.Empty<DisplayNameScanRowDto>());

            // Recomputing the identity key is a C# function, not translatable to SQL - that is the
            // entire point of this route (README scope decision 5: the migration's SQL backfill is
            // weaker than the C# normalizer, and this is how a moderator finds what it left
            // inconsistent).
            var recomputed = page.ToDictionary(r => r.Id, r => DisplayNameNormalizer.NormalizeForIdentity(r.DisplayName));

            // One grouped pass over the page, not one query per row - same shape as
            // ModerationEndpoints.ToReportDtosAsync. Collisions are computed WITHIN THIS PAGE ONLY: a
            // true global collision search needs the recomputed key for every user at once, which is
            // the unpaged scan this route deliberately is not. problemsOnly=false with a large `take`
            // is the escape hatch. Rows with no alphanumeric substance (empty recomputed key) are
            // excluded from grouping so they are not reported as "colliding" with each other via the
            // shared empty key - NotEnoughSubstance already flags those through Rejection.
            var byRecomputedKey = page
                .Where(r => recomputed[r.Id].Length > 0)
                .ToLookup(r => recomputed[r.Id]);

            var rows = page.Select(r =>
            {
                var key = recomputed[r.Id];
                var drift = !string.Equals(r.StoredNormalized, key, StringComparison.Ordinal);
                // Evaluates a grandfathered name against today's policy - exactly the question the
                // scan exists to answer.
                var rejection = ContentPolicy.Default.CheckDisplayName(r.DisplayName, options).Rejection;
                var collides = key.Length == 0
                    ? Array.Empty<string>()
                    : byRecomputedKey[key].Where(other => other.Id != r.Id).Select(other => other.DisplayName).ToArray();

                return new DisplayNameScanRowDto(
                    r.Id, r.DisplayName, r.StoredNormalized, key, drift, rejection, collides, r.CreatedAt);
            }).ToArray();

            // Filtered after recomputation, since the predicate is not translatable to SQL.
            if (onlyProblems)
                rows = rows.Where(r => r.NormalizedDrift || r.Rejection is not null || r.CollidesWithDisplayNames.Length > 0).ToArray();

            return Results.Ok(rows);
        })
        .Produces<DisplayNameScanRowDto[]>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);

        admin.MapPost("/display-names/{userId:guid}", async (
            Guid userId, ModeratorRenameRequest req, ClaimsPrincipal caller, DisplayNameService displayNames,
            KiriChessDbContext db, ILogger<Program> logger, CancellationToken ct) =>
        {
            var me = caller.GetUserId();
            if (me is null) return Results.Unauthorized();

            if (string.IsNullOrWhiteSpace(req.Reason))
                return Results.BadRequest(new { error = "Reason is required." });

            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
            if (user is null) return Results.NotFound();

            // A moderator is not exempt from the policy - they cannot rename somebody to a slur, a
            // reserved word, or a name already taken.
            var verdict = await displayNames.CheckAsync(req.DisplayName, excludeUserId: userId, ct);
            if (!verdict.Allowed)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["DisplayName"] = [verdict.Message] });

            var now = DateTimeOffset.UtcNow;

            // Bypass the cooldown AND reset it to null - not to UtcNow. This is the entire reason the
            // forced path exists (README scope decision 7): a player moved off a bad name gets an
            // immediate free rename to pick their own, rather than being stuck 30 days with whatever
            // the moderator typed.
            DisplayNameService.Apply(user, req.DisplayName.Trim(), verdict.Normalized, changedAt: null);
            await db.SaveChangesAsync(ct);

            // Never log the OLD display name here: a name refused for profanity is itself a blocklist
            // term, and README scope decision 2 keeps those out of logs. Only the new name and the
            // moderator's stated reason are logged.
            logger.LogInformation(
                "Moderator {ModeratorId} force-renamed user {UserId} to {NewDisplayName}. Reason: {Reason}",
                me.Value, userId, user.DisplayName, req.Reason);

            // DisplayNameChangedAt is null after the reset above (no cooldown owed), so it cannot be
            // echoed as the response's ChangedAt (non-nullable). `now` communicates "this happened
            // just now" without implying a cooldown was started.
            return Results.Ok(new DisplayNameResponse(user.DisplayName, now));
        })
        .Produces<DisplayNameResponse>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status404NotFound)
        .ProducesValidationProblem()
        .Produces(StatusCodes.Status403Forbidden);

        return app;
    }

}
