using System.Linq.Expressions;
using System.Security.Claims;
using KiriChess.Server.Auth;
using KiriChess.Server.Data;
using KiriChess.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace KiriChess.Server.Moderation.Detection;

/// <summary>
/// Phase 8.5 Task B routes: on-demand sandbagging reports over stored rating history. Same shape as
/// <see cref="Analytics.AnalyticsEndpoints"/> - the query does the projection and caps the rows, a pure
/// static class does the maths, the endpoint just wires the two together. Every route is
/// admin-only (see Phase 8.5 scope decision 2); no route here writes anything.
/// </summary>
public static class SandbaggingEndpoints
{
    /// <summary>
    /// Hard cap on RatingHistory rows pulled for one subject. Same reasoning as
    /// AnalyticsEndpoints.MaxRows: aggregating in the app is what keeps the maths unit-testable, and
    /// the cap is what stops that price becoming unbounded. Two rows per game (subject and opponent),
    /// so this is ~2500 games.
    /// </summary>
    private const int MaxRows = 5_000;

    /// <summary>
    /// Cap on candidate players considered by the sweep. Pinned by the plan rather than tuned: if the
    /// sweep's row budget (<see cref="MaxRows"/> * 4) is ever too tight for real traffic, the fix is to
    /// lower this number, not raise the row budget - a wider row cap turns one moderator's click into
    /// an unbounded query again, which is exactly what the two-phase design exists to avoid.
    /// </summary>
    private const int MaxSweepPlayers = 200;

    private const string TruncatedCaveat =
        "This report was computed from a truncated sample: the oldest rating rows in the window were dropped to bound query cost.";

    private const string TruncatedSweepCaveat =
        "This sweep truncated its row budget across candidate players: some games were dropped to bound query cost.";

    public static IEndpointRouteBuilder MapSandbaggingEndpoints(this IEndpointRouteBuilder app)
    {
        // 9d.7: both routes are mapped from named method groups rather than lambdas, so the metadata
        // chains onto this app.MapGet(...) call, not onto GetForUserAsync/GetSweepAsync themselves.
        app.MapGet("/api/moderation/detect/sandbagging/{userId:guid}", GetForUserAsync)
            .RequireAuthorization(ModerationPolicies.Admin)
            .Produces<DetectionReportDto[]>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status403Forbidden);

        app.MapGet("/api/moderation/detect/sandbagging", GetSweepAsync)
            .RequireAuthorization(ModerationPolicies.Admin)
            .Produces<DetectionReportDto[]>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        return app;
    }

    private static async Task<IResult> GetForUserAsync(
        Guid userId, int? sinceDays, string? pool, ClaimsPrincipal caller, KiriChessDbContext db,
        ILogger<Program> logger, CancellationToken ct)
    {
        var me = caller.GetUserId();
        if (me is null) return Results.Unauthorized();

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return Results.NotFound();

        // 9d.3: same accountability argument as the dossier - a detector output about one named
        // player is exactly the kind of read a moderator should not be able to make untraceably.
        logger.LogInformation("Moderator {ModeratorId} ran sandbagging detection for user {UserId}", me.Value, userId);

        int days = Math.Clamp(sinceDays ?? 180, 1, 3650);
        var since = DateTimeOffset.UtcNow.AddDays(-days);
        var generatedAt = DateTimeOffset.UtcNow;

        // RatingHistory carries two rows per rated game, so the opponent's pre-game rating is the
        // sibling row with the same GameId. This query pulls every row belonging to a game the subject
        // played (both the subject's own row and the opponent's) in one round trip, and the pairing
        // happens in memory below - that is what avoids an N+1 of "fetch the opponent row per game",
        // and it is also why MaxRows is a hard cap rather than a suggestion.
        var query = DecisiveRatedRows(db, since)
            .Where(h => h.Game!.WhitePlayerId == userId || h.Game.BlackPlayerId == userId);
        if (pool is not null) query = query.Where(h => h.Pool == pool);

        var rows = await query
            .OrderByDescending(h => h.Game!.EndedAt)
            .Take(MaxRows + 1)
            .Select(RowProjection)
            .ToListAsync(ct);

        bool truncated = rows.Count > MaxRows;
        if (truncated) rows.RemoveAt(rows.Count - 1);

        var facts = PairFacts(userId, rows);

        DetectionReportDto[] reports;
        if (pool is not null)
        {
            // A specific pool was asked for, so answer even with zero games in it - the analyzer's
            // Insufficient gate already turns "no data" into an honest verdict rather than a missing
            // array entry.
            var report = SandbaggingAnalyzer.Analyze(userId, user.DisplayName, pool, facts, generatedAt);
            reports = [truncated ? WithCaveat(report, TruncatedCaveat) : report];
        }
        else
        {
            // No pool named: one report per pool the player actually has decisive rated games in.
            // A pool with zero games here is noise, not a data point - same call BalanceAnalytics
            // makes for an empty pool bucket.
            reports = facts.GroupBy(f => f.Pool)
                .Select(g => SandbaggingAnalyzer.Analyze(userId, user.DisplayName, g.Key, [.. g], generatedAt))
                .Select(r => truncated ? WithCaveat(r, TruncatedCaveat) : r)
                .ToArray();
        }

        return Results.Ok(reports);
    }

    private static async Task<IResult> GetSweepAsync(
        int? sinceDays, double? minScore, int? take, ClaimsPrincipal caller, KiriChessDbContext db,
        ILogger<Program> logger, CancellationToken ct)
    {
        var me = caller.GetUserId();
        if (me is null) return Results.Unauthorized();

        int days = Math.Clamp(sinceDays ?? 90, 1, 3650);
        double minScoreClamped = Math.Clamp(minScore ?? 0.40, 0, 1);
        int takeClamped = Math.Clamp(take ?? 25, 1, 100);
        var since = DateTimeOffset.UtcNow.AddDays(-days);
        var generatedAt = DateTimeOffset.UtcNow;

        // 9d.3: one line per sweep, not one per candidate examined - a sweep touches up to
        // MaxSweepPlayers players in a single click, and a per-candidate line would just be noise that
        // buries the one fact worth recording: who ran it, and with what parameters.
        logger.LogInformation(
            "Moderator {ModeratorId} ran a sandbagging sweep (sinceDays={SinceDays}, minScore={MinScore})",
            me.Value, days, minScoreClamped);

        // Phase 1: one grouped query for candidate user ids. Grouped by RatingHistory.UserId directly
        // rather than unioning Game.WhitePlayerId/BlackPlayerId - RatingHistory already carries one row
        // per user per game, so this is a single GROUP BY with no UNION needed.
        var candidateIds = await DecisiveRatedRows(db, since)
            .GroupBy(h => h.UserId)
            .Where(g => g.Count() >= SandbaggingAnalyzer.MinimumGames)
            .OrderByDescending(g => g.Count())
            .Take(MaxSweepPlayers)
            .Select(g => g.Key)
            .ToListAsync(ct);

        if (candidateIds.Count == 0) return Results.Ok(Array.Empty<DetectionReportDto>());

        // Phase 2: one query pulling every relevant RatingHistory + Game row for all candidates at
        // once. A game qualifies if either side is a candidate, so this also pulls in the RatingHistory
        // rows of non-candidate opponents - that is required for the pairing below, not a leak, since
        // only the candidates' own reports are ever returned.
        int sweepRowCap = MaxRows * 4;
        var rows = await DecisiveRatedRows(db, since)
            .Where(h => candidateIds.Contains(h.Game!.WhitePlayerId) || candidateIds.Contains(h.Game.BlackPlayerId))
            .OrderByDescending(h => h.Game!.EndedAt)
            .Take(sweepRowCap + 1)
            .Select(RowProjection)
            .ToListAsync(ct);

        bool truncated = rows.Count > sweepRowCap;
        if (truncated) rows.RemoveAt(rows.Count - 1);

        // Third and last round trip: display names for the candidates, one bounded query rather than
        // one per candidate.
        var names = await db.Users
            .Where(u => candidateIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);

        // GameId and UserId lookups over the in-memory row set, not further queries - this is where the
        // "no N+1" promise is actually kept, since everything past this point is CPU, not I/O.
        var byGame = rows.ToLookup(r => r.GameId);
        var byUser = rows.ToLookup(r => r.UserId);

        var reports = new List<DetectionReportDto>();
        foreach (var candidateId in candidateIds)
        {
            var facts = PairFactsForCandidate(candidateId, byUser[candidateId], byGame);
            var name = names.GetValueOrDefault(candidateId, "Unknown");

            foreach (var pool in facts.GroupBy(f => f.Pool))
            {
                var report = SandbaggingAnalyzer.Analyze(candidateId, name, pool.Key, [.. pool], generatedAt);
                reports.Add(truncated ? WithCaveat(report, TruncatedSweepCaveat) : report);
            }
        }

        var top = reports
            .Where(r => r.Score >= minScoreClamped)
            .OrderByDescending(r => r.Score)
            .Take(takeClamped)
            .ToArray();

        return Results.Ok(top);
    }

    /// <summary>
    /// Decisive, rated RatingHistory rows ending on or after <paramref name="since"/>. Excludes draws
    /// (no "loser" for the drawdown/short-loss maths), unrated/custom games (a rating attack needs a
    /// rating to attack), Ongoing/Aborted results and NoShow/ServerRestart terminations - the same
    /// infrastructure-artefact exclusions <see cref="Analytics.BalanceAnalytics.IsExcluded"/> applies,
    /// for the same reason. Solo/practice games need no explicit guard: RatingService.ApplyAsync never
    /// writes a RatingHistory row for WhitePlayerId == BlackPlayerId, so they cannot appear here.
    /// </summary>
    private static IQueryable<RatingHistory> DecisiveRatedRows(KiriChessDbContext db, DateTimeOffset since) =>
        db.RatingHistories.Where(h =>
            h.Game != null &&
            h.Game.IsRated &&
            h.Game.EndedAt != null &&
            h.Game.EndedAt >= since &&
            h.Game.Result != GameResult.Draw &&
            h.Game.Result != GameResult.Aborted &&
            h.Game.Result != GameResult.Ongoing &&
            h.Game.Termination != TerminationReason.NoShow &&
            h.Game.Termination != TerminationReason.ServerRestart &&
            (h.Game.RatingPool == null || !h.Game.RatingPool.StartsWith(Ratings.RatingConstants.BotPoolPrefix)));

    /// <summary>
    /// An Expression, not a compiled delegate, so EF Core can translate it into the SELECT list rather
    /// than pulling every RatingHistory column and projecting client-side - the same reason
    /// AnalyticsEndpoints projects its GameFact inline in the query instead of via a plain method call.
    /// </summary>
    private static readonly Expression<Func<RatingHistory, RatingHistoryRow>> RowProjection =
        h => new RatingHistoryRow(
            h.GameId, h.UserId, h.Pool, h.ValueBefore, h.ValueAfter, h.Game!.EndedAt!.Value,
            h.Game.Result, h.Game.Termination, h.Game.PlyCount,
            h.Game.WhitePlayerId, h.Game.BlackPlayerId);

    /// <summary>
    /// Pairs one subject's own RatingHistory rows with the sibling row for the same GameId, which is
    /// where the opponent's pre-game rating comes from - there is no other column that carries it. A
    /// game whose sibling row fell outside the row cap (possible at the truncation boundary) is dropped
    /// rather than faked with a default rating: a missing opponent rating must never silently become
    /// RatingConstants.DefaultRating, or a truncated window would quietly manufacture false gaps.
    /// </summary>
    private static List<SandbaggingGameFact> PairFacts(Guid userId, List<RatingHistoryRow> rows) =>
        PairFactsForCandidate(userId, rows.Where(r => r.UserId == userId), rows.ToLookup(r => r.GameId));

    private static List<SandbaggingGameFact> PairFactsForCandidate(
        Guid userId, IEnumerable<RatingHistoryRow> subjectRows, ILookup<Guid, RatingHistoryRow> byGame)
    {
        var facts = new List<SandbaggingGameFact>();
        foreach (var subject in subjectRows)
        {
            var opponent = byGame[subject.GameId].FirstOrDefault(r => r.UserId != userId);
            if (opponent.GameId == Guid.Empty) continue; // sibling row missing (truncation boundary)

            bool won = (subject.WhitePlayerId == userId && subject.Result == GameResult.WhiteWins) ||
                       (subject.BlackPlayerId == userId && subject.Result == GameResult.BlackWins);

            facts.Add(new SandbaggingGameFact(
                subject.GameId, subject.Pool, subject.EndedAt, won,
                subject.ValueBefore, subject.ValueAfter, opponent.ValueBefore,
                subject.PlyCount, subject.Termination));
        }

        // Oldest first - SandbaggingAnalyzer.Analyze requires and documents this ordering.
        facts.Sort((a, b) => a.EndedAt.CompareTo(b.EndedAt));
        return facts;
    }

    private static DetectionReportDto WithCaveat(DetectionReportDto report, string caveat) =>
        report with { Caveats = [.. report.Caveats, caveat] };

    /// <summary>Flat projection of one RatingHistory row joined to its Game, before pairing.</summary>
    private readonly record struct RatingHistoryRow(
        Guid GameId, Guid UserId, string Pool, double ValueBefore, double ValueAfter, DateTimeOffset EndedAt,
        GameResult Result, TerminationReason Termination, int PlyCount,
        Guid WhitePlayerId, Guid BlackPlayerId);
}
