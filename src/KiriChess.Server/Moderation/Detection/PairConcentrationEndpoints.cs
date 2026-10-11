using System.Linq.Expressions;
using System.Security.Claims;
using KiriChess.Server.Auth;
using KiriChess.Server.Data;
using KiriChess.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace KiriChess.Server.Moderation.Detection;

/// <summary>
/// S-2-4: on-demand pair-concentration reports over stored rating history, same query/analyzer split
/// as <see cref="SandbaggingEndpoints"/> and the same two-phase sweep shape (candidates, then one
/// bounded row pull for all of them). Every route is admin-only, on every route, forever (Phase 8.5
/// scope decision 2), same as every other detector surface.
///
/// Registered in <c>Program.cs</c> beside <c>MapSandbaggingEndpoints()</c>, per
/// <c>.claude/rules/backend.md</c>. It was briefly mapped from inside <c>SandbaggingEndpoints</c>
/// instead, which worked but buried a route registration inside another feature's file, where the
/// next person auditing what this service exposes would not think to look.
/// </summary>
public static class PairConcentrationEndpoints
{
    /// <summary>Same reasoning and same number as <see cref="SandbaggingEndpoints"/>'s row cap.</summary>
    private const int MaxRows = 5_000;

    /// <summary>Same reasoning and same number as <see cref="SandbaggingEndpoints"/>'s sweep cap.</summary>
    private const int MaxSweepPlayers = 200;

    private const string TruncatedCaveat =
        "This report was computed from a truncated sample: the oldest rating rows in the window were dropped to bound query cost.";

    private const string TruncatedSweepCaveat =
        "This sweep truncated its row budget across candidate players: some games were dropped to bound query cost.";

    public static IEndpointRouteBuilder MapPairConcentrationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/moderation/detect/pair-concentration/{userId:guid}", GetForUserAsync)
            .RequireAuthorization(ModerationPolicies.Admin)
            .Produces<DetectionReportDto[]>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status403Forbidden);

        app.MapGet("/api/moderation/detect/pair-concentration", GetSweepAsync)
            .RequireAuthorization(ModerationPolicies.Admin)
            .Produces<DetectionReportDto[]>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        return app;
    }

    private static async Task<IResult> GetForUserAsync(
        Guid userId, int? sinceDays, string? pool, ClaimsPrincipal caller, KiriChessDbContext db, ILogger<Program> logger)
    {
        var me = caller.GetUserId();
        if (me is null) return Results.Unauthorized();

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null) return Results.NotFound();

        // 9d.3: same accountability argument as the other two detectors - a report about one named
        // player must not be a read that leaves no trace.
        logger.LogInformation("Moderator {ModeratorId} ran pair-concentration detection for user {UserId}", me.Value, userId);

        int days = Math.Clamp(sinceDays ?? 180, 1, 3650);
        var since = DateTimeOffset.UtcNow.AddDays(-days);
        var generatedAt = DateTimeOffset.UtcNow;

        var query = RatedRows(db, since)
            .Where(h => h.Game!.WhitePlayerId == userId || h.Game.BlackPlayerId == userId);
        if (pool is not null) query = query.Where(h => h.Pool == pool);

        var rows = await query
            .OrderByDescending(h => h.Game!.EndedAt)
            .Take(MaxRows + 1)
            .Select(RowProjection)
            .ToListAsync();

        bool truncated = rows.Count > MaxRows;
        if (truncated) rows.RemoveAt(rows.Count - 1);

        var facts = PairFacts(userId, rows);

        DetectionReportDto[] reports;
        if (pool is not null)
        {
            var report = PairConcentrationAnalyzer.Analyze(userId, user.DisplayName, pool, facts, generatedAt);
            reports = [truncated ? WithCaveat(report, TruncatedCaveat) : report];
        }
        else
        {
            reports = facts.GroupBy(f => f.Pool)
                .Select(g => PairConcentrationAnalyzer.Analyze(userId, user.DisplayName, g.Key, [.. g], generatedAt))
                .Select(r => truncated ? WithCaveat(r, TruncatedCaveat) : r)
                .ToArray();
        }

        return Results.Ok(reports);
    }

    private static async Task<IResult> GetSweepAsync(
        int? sinceDays, double? minScore, int? take, ClaimsPrincipal caller, KiriChessDbContext db, ILogger<Program> logger)
    {
        var me = caller.GetUserId();
        if (me is null) return Results.Unauthorized();

        int days = Math.Clamp(sinceDays ?? 90, 1, 3650);
        double minScoreClamped = Math.Clamp(minScore ?? 0.40, 0, 1);
        int takeClamped = Math.Clamp(take ?? 25, 1, 100);
        var since = DateTimeOffset.UtcNow.AddDays(-days);
        var generatedAt = DateTimeOffset.UtcNow;

        logger.LogInformation(
            "Moderator {ModeratorId} ran a pair-concentration sweep (sinceDays={SinceDays}, minScore={MinScore})",
            me.Value, days, minScoreClamped);

        var candidateIds = await RatedRows(db, since)
            .GroupBy(h => h.UserId)
            .Where(g => g.Count() >= PairConcentrationAnalyzer.MinimumGames)
            .OrderByDescending(g => g.Count())
            .Take(MaxSweepPlayers)
            .Select(g => g.Key)
            .ToListAsync();

        if (candidateIds.Count == 0) return Results.Ok(Array.Empty<DetectionReportDto>());

        int sweepRowCap = MaxRows * 4;
        var rows = await RatedRows(db, since)
            .Where(h => candidateIds.Contains(h.Game!.WhitePlayerId) || candidateIds.Contains(h.Game.BlackPlayerId))
            .OrderByDescending(h => h.Game!.EndedAt)
            .Take(sweepRowCap + 1)
            .Select(RowProjection)
            .ToListAsync();

        bool truncated = rows.Count > sweepRowCap;
        if (truncated) rows.RemoveAt(rows.Count - 1);

        var names = await db.Users
            .Where(u => candidateIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.DisplayName);

        var byGame = rows.ToLookup(r => r.GameId);
        var byUser = rows.ToLookup(r => r.UserId);

        var reports = new List<DetectionReportDto>();
        foreach (var candidateId in candidateIds)
        {
            var facts = PairFactsForCandidate(candidateId, byUser[candidateId], byGame);
            var name = names.GetValueOrDefault(candidateId, "Unknown");

            foreach (var pool in facts.GroupBy(f => f.Pool))
            {
                var report = PairConcentrationAnalyzer.Analyze(candidateId, name, pool.Key, [.. pool], generatedAt);
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
    /// Rated RatingHistory rows ending on or after <paramref name="since"/>. Unlike
    /// <see cref="SandbaggingEndpoints.DecisiveRatedRows"/> this keeps draws - a drawn game still
    /// moves rating (however slightly) and still counts as a game played against that opponent, both
    /// of which matter to concentration and net-flow. Aborted/Ongoing results and the same
    /// infrastructure-artefact terminations are still excluded, for the same reason
    /// <see cref="Analytics.BalanceAnalytics.IsExcluded"/> excludes them: they say nothing about a
    /// player's real opponent pattern.
    /// </summary>
    private static IQueryable<RatingHistory> RatedRows(KiriChessDbContext db, DateTimeOffset since) =>
        db.RatingHistories.Where(h =>
            h.Game != null &&
            h.Game.IsRated &&
            h.Game.EndedAt != null &&
            h.Game.EndedAt >= since &&
            h.Game.Result != GameResult.Aborted &&
            h.Game.Result != GameResult.Ongoing &&
            h.Game.Termination != TerminationReason.NoShow &&
            h.Game.Termination != TerminationReason.ServerRestart &&
            (h.Game.RatingPool == null || !h.Game.RatingPool.StartsWith(Ratings.RatingConstants.BotPoolPrefix)));

    private static readonly Expression<Func<RatingHistory, RatingHistoryRow>> RowProjection =
        h => new RatingHistoryRow(
            h.GameId, h.UserId, h.Pool, h.ValueBefore, h.ValueAfter, h.Game!.EndedAt!.Value,
            h.Game.WhitePlayerId, h.Game.BlackPlayerId);

    /// <summary>
    /// Pairs one subject's own rows with the sibling row for the same game to read off the opponent's
    /// id - same truncation-safety rule as <see cref="SandbaggingEndpoints.PairFacts"/>: a game whose
    /// sibling row fell outside the row cap is dropped, never guessed at.
    /// </summary>
    private static List<PairConcentrationGameFact> PairFacts(Guid userId, List<RatingHistoryRow> rows) =>
        PairFactsForCandidate(userId, rows.Where(r => r.UserId == userId), rows.ToLookup(r => r.GameId));

    private static List<PairConcentrationGameFact> PairFactsForCandidate(
        Guid userId, IEnumerable<RatingHistoryRow> subjectRows, ILookup<Guid, RatingHistoryRow> byGame)
    {
        var facts = new List<PairConcentrationGameFact>();
        foreach (var subject in subjectRows)
        {
            var opponent = byGame[subject.GameId].FirstOrDefault(r => r.UserId != userId);
            if (opponent.GameId == Guid.Empty) continue; // sibling row missing (truncation boundary)

            facts.Add(new PairConcentrationGameFact(
                subject.GameId, subject.Pool, subject.EndedAt, opponent.UserId,
                subject.ValueBefore, subject.ValueAfter));
        }

        return facts;
    }

    private static DetectionReportDto WithCaveat(DetectionReportDto report, string caveat) =>
        report with { Caveats = [.. report.Caveats, caveat] };

    /// <summary>Flat projection of one RatingHistory row joined to its Game, before pairing.</summary>
    private readonly record struct RatingHistoryRow(
        Guid GameId, Guid UserId, string Pool, double ValueBefore, double ValueAfter, DateTimeOffset EndedAt,
        Guid WhitePlayerId, Guid BlackPlayerId);
}
