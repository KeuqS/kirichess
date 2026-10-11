using KiriChess.Server.Data;
using KiriChess.Server.Infrastructure;
using KiriChess.Server.Moderation;
using Microsoft.EntityFrameworkCore;

namespace KiriChess.Server.Analytics;

/// <summary>
/// The body of the anonymous beacon. <see cref="Source"/> is an allowlisted campaign bucket from a
/// <c>?src=</c> the client read out of its own URL - never a referrer (README scope decision 12).
/// </summary>
public sealed record CounterRequest(string? Key, string? Source);

/// <summary>
/// Atomic increment. A read-modify-write through the change tracker loses counts under concurrency -
/// two requests read 5, both write 6 - and this is the one write path in the codebase that is
/// genuinely concurrent by design. ON CONFLICT does it in one statement with no transaction and no
/// row lock held across a round trip.
///
/// One statement per event, no buffering. When that starts to hurt, an in-memory counter flushed on a
/// timer is the obvious next move; it is not needed to ship this (phase8a5 README scope decision 8).
///
/// <paramref name="delta"/> exists for CounterKeys.QueueMatch, which counts two people per pairing so
/// that its ratio against the per-person QueueJoin compares like with like. Every other caller passes 1.
/// </summary>
internal static class DailyCounterRecorder
{
    public static Task RecordAsync(
        KiriChessDbContext db, DateOnly date, string counterKey, string source, int delta, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "DailyCounters" ("Date", "CounterKey", "Source", "Count")
            VALUES ({date}, {counterKey}, {source}, {(long)delta})
            ON CONFLICT ("Date", "CounterKey", "Source")
            DO UPDATE SET "Count" = "DailyCounters"."Count" + {(long)delta}
            """, ct);
}

/// <summary>
/// AD.6 / 11a.3. The site's entire record of anonymous traffic and of the first-session funnel: a
/// counter per (UTC date, key, source).
///
/// The write route is deliberately anonymous - it is fired by every visitor, most of whom have no
/// account - and deliberately records nothing that could identify one. See
/// AD.6 / 11a.3 and Phase 8a.5 scope decisions 4 and 5 for why
/// that is load-bearing rather than merely frugal, and for why "views" here can never mean "people".
/// </summary>
public static class CounterEndpoints
{
    /// <summary>
    /// Cap on rows pulled into memory for one traffic summary. Same bargain as AnalyticsEndpoints.MaxRows:
    /// aggregating in the app is what keeps the maths unit-testable, and the cap is what stops that price
    /// becoming unbounded. Truncation is reported rather than silently changing the answer.
    /// </summary>
    private const int MaxRows = 50_000;

    public static IEndpointRouteBuilder MapCounterEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/analytics/counter", async (
            CounterRequest req, KiriChessDbContext db, CancellationToken ct) =>
        {
            // An unknown key is silently not recorded rather than a 400. This is a fire-and-forget
            // beacon: a client on a stale build sending a retired key should cost the visitor a
            // console error exactly never, and answering 400 would also turn the endpoint into an
            // oracle for the allowlist. The allowlist itself is the control.
            if (!CounterKeys.TryNormalize(req.Key, out var key)) return Results.NoContent();

            await DailyCounterRecorder.RecordAsync(
                db, DateOnly.FromDateTime(DateTime.UtcNow), key,
                CounterKeys.NormalizeSource(req.Source), delta: 1, ct);

            return Results.NoContent();
        })
        .AllowAnonymous()
        .RequireRateLimiting(RateLimitPolicies.Counter)
        .Produces(StatusCodes.Status204NoContent);

        app.MapGet("/api/analytics/traffic", async (KiriChessDbContext db, int? sinceDays, CancellationToken ct) =>
        {
            int days = Math.Clamp(sinceDays ?? 30, 1, 365);
            var now = DateTimeOffset.UtcNow;
            var today = DateOnly.FromDateTime(now.UtcDateTime);
            var from = today.AddDays(-(days - 1));
            var since = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

            // No cap needed here: the table is bounded at |CounterKeys.All| x |CounterSources.All| x
            // days by construction, so even a 365-day window is a few thousand rows.
            var counters = await db.DailyCounters
                .Where(c => c.Date >= from)
                .Select(c => new DailyCounterFact(c.Date, c.CounterKey, c.Source, c.Count))
                .ToListAsync(ct);

            // Three flat, capped timestamp projections, grouped by day in TrafficAnalytics rather than
            // in SQL. Deliberate: a GroupBy on a date-truncated timestamp is the one line here that
            // compiles and then fails at runtime when the provider cannot translate it, and pushing
            // the grouping into the pure function is what makes all of this testable with literal
            // lists.
            //
            // db.Users includes 8a.2 tombstones on purpose (phase8a5 README scope decision 7) and no
            // user id, display name or email leaves this endpoint - every field of the response is a
            // count or a rate.
            var signupsAt = await db.Users
                .Where(u => u.CreatedAt >= since)
                .OrderByDescending(u => u.CreatedAt).Take(MaxRows + 1)
                .Select(u => u.CreatedAt).ToListAsync(ct);

            // PlyCount > 0 is the whole difference between "started a game" and "played one": the row
            // exists from ply 0 (GameRegistry.CreateAsync), so a queue pop the player immediately
            // walked away from leaves a row behind. See phase8a5 README ground-truth corrections 2
            // and 3.
            // M-4-4. Bot-pool games are not the human funnel AD.7 reads.
            var gamesStartedAt = await db.Games
                .WhereHumanPool()
                .Where(g => g.PlyCount > 0 && g.CreatedAt >= since)
                .OrderByDescending(g => g.CreatedAt).Take(MaxRows + 1)
                .Select(g => g.CreatedAt).ToListAsync(ct);

            var gamesFinishedAt = await db.Games
                .WhereHumanPool()
                .Where(g => g.PlyCount > 0 && g.EndedAt != null && g.EndedAt >= since)
                .OrderByDescending(g => g.EndedAt).Take(MaxRows + 1)
                .Select(g => g.EndedAt!.Value).ToListAsync(ct);

            bool truncated = false;
            Trim(signupsAt, ref truncated);
            Trim(gamesStartedAt, ref truncated);
            Trim(gamesFinishedAt, ref truncated);

            return Results.Ok(TrafficAnalytics.Summarize(
                counters, signupsAt, gamesStartedAt, gamesFinishedAt, days, today, truncated, now));
        })
        .RequireAuthorization(ModerationPolicies.Admin)
        .Produces<TrafficSummaryDto>(StatusCodes.Status200OK);

        return app;
    }

    /// <summary>Trims one over-fetched list back to <see cref="MaxRows"/> and flags truncation.</summary>
    private static void Trim<T>(List<T> rows, ref bool truncated)
    {
        if (rows.Count <= MaxRows) return;
        truncated = true;
        rows.RemoveAt(rows.Count - 1);
    }

    // Deliberately no HttpContext, ClaimsPrincipal or Request.Headers parameter on the write handler
    // above - in particular never Referer - see AD.6 / 11a.3 step 5b.
    // Anything else would read, at review time, exactly like the beginning of a tracker.
}
