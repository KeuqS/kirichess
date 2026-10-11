using System.Security.Claims;
using KiriChess.Server.Auth;
using KiriChess.Server.Data;
using KiriChess.Server.Moderation;
using Microsoft.EntityFrameworkCore;

namespace KiriChess.Server.Retention;

/// <summary>
/// Minimal-API endpoint for the Phase 8a.3 dormant-account report. GET and only GET - it never calls
/// AccountDeletionService, never writes to AspNetUsers, never issues a DELETE, and never sends an
/// email. See Phase 8a.3 scope decision 2: nothing is deployed, nobody has signed
/// up, no email has ever been delivered by this system (roadmap 8a.1), and 8a.2's erasure is
/// irreversible inside a single transaction. A reaper's first real execution would be against real
/// accounts, driven by a predicate no live data has ever exercised. This scan makes the policy
/// observable so a human can act; the trigger for revisiting automation is a verified mail path plus
/// real signups (docs/decisions/RETENTION.md).
///
/// This is the deliberate sibling of ContentEndpoints' display-name scan: MapGroup + admin auth,
/// Math.Clamp-ed query parameters, a bounded query, and the non-translatable classification done in
/// memory afterwards.
/// </summary>
public static class RetentionEndpoints
{
    // The same cap discipline as 8.3's balance dashboard: bounded query, and the truncation is
    // REPORTED rather than silently changing the answer.
    private const int MaxUsers = 50_000;

    public static IEndpointRouteBuilder MapRetentionEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/moderation/retention")
                       .RequireAuthorization(ModerationPolicies.Admin);

        admin.MapGet("/dormant", async (
            int? inactiveDays, int? take, int? skip, ClaimsPrincipal caller, KiriChessDbContext db,
            ILogger<Program> logger, CancellationToken ct) =>
        {
            var me = caller.GetUserId();
            if (me is null) return Results.Unauthorized();

            var inactiveDaysClamped = Math.Clamp(inactiveDays ?? 365, 30, 3650);
            var takeClamped = Math.Clamp(take ?? 100, 1, 500);
            var skipValue = Math.Max(skip ?? 0, 0);

            var now = DateTimeOffset.UtcNow;

            // Live accounts only, bounded. The DeletedAt == null filter is the one place this phase
            // deliberately differs from 8a.5 scope decision 7, which counts tombstones: 8a.5 counts
            // historical signups, where dropping a tombstone rewrites the past; this is a work queue,
            // where a tombstone is work already done.
            var users = await db.Users
                .Where(u => u.DeletedAt == null)
                .OrderBy(u => u.CreatedAt)
                .Take(MaxUsers + 1)               // +1 so a full page tells us truncation happened
                .Select(u => new { u.Id, u.DisplayName, u.CreatedAt, u.EmailConfirmed })
                .ToListAsync(ct);

            var truncated = users.Count > MaxUsers;
            if (truncated) users.RemoveAt(users.Count - 1);

            // Game activity: two grouped queries, not one query per user. Each returns at most one row
            // per player, so the result set is bounded by player count regardless of how many games
            // exist.
            //
            // Any Games row counts as activity, including PlyCount == 0. This is the OPPOSITE choice
            // from 8a.5's funnel, which requires PlyCount > 0: 8a.5 asks "did they engage" and wants
            // the honest, smaller number; this asks "was this account ever used" and every ambiguity
            // must resolve toward NOT flagging the account. A row exists from ply 0
            // (GameRegistry.CreateAsync), so someone who queued once and closed the tab is not
            // reported as never-played.
            //
            // A self-paired game (white == black, which DevSeeder can produce) is counted twice. That
            // inflates GamesPlayed by one and can never flag an account that should not be flagged, so
            // it is left alone rather than deduplicated with a third query.
            var white = await db.Games.GroupBy(g => g.WhitePlayerId)
                .Select(g => new { UserId = g.Key, Count = g.Count(), Last = (DateTimeOffset?)g.Max(x => x.CreatedAt) })
                .ToListAsync(ct);
            var black = await db.Games.GroupBy(g => g.BlackPlayerId)
                .Select(g => new { UserId = g.Key, Count = g.Count(), Last = (DateTimeOffset?)g.Max(x => x.CreatedAt) })
                .ToListAsync(ct);

            var activity = new Dictionary<Guid, (int Count, DateTimeOffset? Last)>();
            foreach (var row in white)
                activity[row.UserId] = (row.Count, row.Last);
            foreach (var row in black)
            {
                if (activity.TryGetValue(row.UserId, out var existing))
                {
                    var last = existing.Last is { } a && row.Last is { } b
                        ? (a > b ? a : b)
                        : existing.Last ?? row.Last;
                    activity[row.UserId] = (existing.Count + row.Count, last);
                }
                else
                {
                    activity[row.UserId] = (row.Count, row.Last);
                }
            }

            var sanctioned = (await db.UserSanctions.Select(s => s.UserId).Distinct().ToListAsync(ct)).ToHashSet();

            var matches = new List<DormantAccountRowDto>();
            foreach (var u in users)
            {
                activity.TryGetValue(u.Id, out var act);
                var fact = new DormancyFact(
                    u.Id, u.DisplayName, u.CreatedAt, u.EmailConfirmed,
                    act.Count, act.Last, sanctioned.Contains(u.Id));

                var reason = DormantAccountPolicy.Classify(fact, now, inactiveDaysClamped);
                if (reason is null) continue;

                matches.Add(new DormantAccountRowDto(
                    fact.UserId, fact.DisplayName, reason, fact.CreatedAt, fact.EmailConfirmed,
                    fact.GamesPlayed, fact.LastGameAt, fact.HasSanctions));
            }

            // DormantCount is the number of matches across the WHOLE scanned set, not the page.
            // Ordered by CreatedAt ascending (most overdue first) before Skip/Take, so paging is
            // stable.
            var ordered = matches.OrderBy(r => r.CreatedAt).ToList();
            var page = ordered.Skip(skipValue).Take(takeClamped).ToArray();

            // 9d.3: this report names every dormant account by id and email-confirmation state in one
            // response - the moderator identity and the scan parameters are what turns that into an
            // attributable read rather than a silent bulk pull.
            logger.LogInformation(
                "Moderator {ModeratorId} ran the dormant-account report (inactiveDays={InactiveDays}), " +
                "returning {MatchCount} of {ScannedCount} scanned accounts",
                me.Value, inactiveDaysClamped, ordered.Count, users.Count);

            return Results.Ok(new DormantAccountScanDto(
                now, inactiveDaysClamped, users.Count, ordered.Count, truncated, page));
        })
        .Produces<DormantAccountScanDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);

        return app;
    }

}
