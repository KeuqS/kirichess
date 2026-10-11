using KiriChess.Server.Data;
using KiriChess.Server.Data.Entities;
using KiriChess.Server.Moderation;
using Microsoft.EntityFrameworkCore;

namespace KiriChess.Server.Analytics;

/// <summary>
/// The admin analytics surface: the Phase 8.3 balance dashboard's read model, plus the Phase 8a.5 /
/// AD.6+11a.3 acquisition funnel and retention curve.
///
/// AD.6+11a.3 replaced 8.3's static shared-secret bearer token with the real
/// <see cref="ModerationPolicies.Admin"/> role that Phase 8.5 introduced, closing the follow-up 8.3
/// left open. Two admin auth schemes in one codebase is one more than anybody audits, and the shared
/// secret was the weaker of the two. The 8.3 "fail closed, 404 when unconfigured" behaviour goes with
/// it: these routes now always exist and answer 401/403.
/// </summary>
public static class AnalyticsEndpoints
{
    /// <summary>
    /// Hard cap on rows pulled into memory for one summary. Aggregating in the app (rather than in
    /// SQL) is what keeps the balance maths unit-testable; this is the price, and the cap is what
    /// stops that price becoming unbounded. When this cap starts being hit for real, the answer is a
    /// rollup table, not a bigger number here.
    /// </summary>
    private const int MaxRows = 50_000;

    /// <summary>Cap on signup rows for one retention summary. Same bargain as <see cref="MaxRows"/>.</summary>
    private const int MaxUsers = 50_000;

    /// <summary>
    /// Cap on game rows for one retention summary. Higher than <see cref="MaxUsers"/> because a
    /// single engaged account contributes many rows, and truncating games would silently understate
    /// retention rather than merely shortening the window.
    /// </summary>
    private const int MaxGames = 200_000;

    public static IEndpointRouteBuilder MapAnalyticsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/analytics").RequireAuthorization(ModerationPolicies.Admin);

        group.MapGet("/balance", async (KiriChessDbContext db, int? sinceDays, bool? ratedOnly, CancellationToken ct) =>
        {
            int days = Math.Clamp(sinceDays ?? 90, 1, 3650);
            bool rated = ratedOnly ?? false;
            var since = DateTimeOffset.UtcNow.AddDays(-days);

            var q = db.Games.Where(g => g.Result != GameResult.Ongoing && g.EndedAt != null && g.EndedAt >= since);
            if (rated) q = q.Where(g => g.IsRated);

            // C-7-5: GameEndpoints.BuildCustomConfig (C-6-5) already stops POST /api/games from
            // writing IsRated=true onto a same-account row for new games, but every self-play row
            // created before that fix is still in the database and, absent this filter, still skews
            // this dashboard's win rate, draw rate and game-length statistics for whatever pool it
            // named. The neighbouring detector already applies the same exclusion explicitly
            // (EngineDetectionEndpoints.cs:47); this closes the read side for the rows C-6-5 cannot
            // retroactively fix.
            q = q.Where(g => g.WhitePlayerId != g.BlackPlayerId);

            // Newest first so a truncated window is a recent sample, not an arbitrary one.
            var facts = await q
                .OrderByDescending(g => g.EndedAt)
                .Take(MaxRows + 1)
                .Select(g => new GameFact(
                    g.RatingPool, g.IsRated, g.Result, g.Termination, g.PlyCount, g.CreatedAt, g.EndedAt))
                .ToListAsync(ct);

            bool truncated = facts.Count > MaxRows;
            if (truncated) facts.RemoveAt(facts.Count - 1);

            return Results.Ok(BalanceAnalytics.Summarize(facts, days, rated, truncated, DateTimeOffset.UtcNow));
        })
        .Produces<BalanceSummaryDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized);

        group.MapGet("/retention", async (KiriChessDbContext db, int? sinceDays, string? grain, CancellationToken ct) =>
        {
            int days = Math.Clamp(sinceDays ?? 90, 1, 3650);
            var grainValue = string.Equals(grain, RetentionAnalytics.GrainDay, StringComparison.OrdinalIgnoreCase)
                ? RetentionAnalytics.GrainDay
                : RetentionAnalytics.GrainWeek;

            var now = DateTimeOffset.UtcNow;
            var since = now.AddDays(-days);

            // db.Users includes 8a.2 tombstones, and that is deliberate (README scope decision 7): a
            // tombstoned row is a real signup that really happened, and dropping it would understate
            // every historical cohort by however many people have since deleted their accounts. No
            // user id ever leaves this endpoint - UserFact.UserId exists only to join games onto
            // accounts in memory and is never serialised.
            //
            // Newest first so a truncated window is a recent sample, not an arbitrary one - same rule
            // the balance query above follows.
            var signups = await db.Users
                .Where(u => u.CreatedAt >= since)
                .OrderByDescending(u => u.CreatedAt)
                .Take(MaxUsers + 1)
                .Select(u => new SignupFact(u.Id, u.CreatedAt))
                .ToListAsync(ct);

            bool truncated = signups.Count > MaxUsers;
            if (truncated) signups.RemoveAt(signups.Count - 1);

            // PlyCount > 0 is the whole difference between "started a game" and "played one": the row
            // exists from ply 0 (GameRegistry.CreateAsync), so a queue pop the player immediately
            // walked away from leaves a row behind. Counting that as a first game inflates the exact
            // number this endpoint is for. See README ground-truth corrections 2 and 3.
            //
            // Filtering games by the same `since` is complete, not merely cheap: every game played by
            // an account that signed up inside the window necessarily starts at or after that signup.
            // M-4-4. A bot-pool game is not "played again" for the human retention curve.
            var games = await db.Games
                .WhereHumanPool()
                .Where(g => g.PlyCount > 0 && g.CreatedAt >= since)
                .OrderByDescending(g => g.CreatedAt)
                .Take(MaxGames + 1)
                .Select(g => new GameParticipationFact(g.WhitePlayerId, g.BlackPlayerId, g.CreatedAt))
                .ToListAsync(ct);

            if (games.Count > MaxGames)
            {
                truncated = true;
                games.RemoveAt(games.Count - 1);
            }

            var facts = RetentionAnalytics.BuildFacts(signups, games);
            return Results.Ok(RetentionAnalytics.Summarize(facts, days, grainValue, truncated, now));
        })
        .Produces<RetentionSummaryDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized);

        return app;
    }
}
