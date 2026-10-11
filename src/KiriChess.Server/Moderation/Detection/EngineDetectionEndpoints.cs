using KiriChess.Engine.Core;
using KiriChess.Server.Data;
using KiriChess.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace KiriChess.Server.Moderation.Detection;

/// <summary>
/// Minimal-API endpoints for the Phase 8.5 Task C engine-assistance detector. Both routes are
/// admin-only, on every route, forever (Phase 8.5 scope decision 2): the detected
/// signal must never leak to the player it was computed about, or a cheater learns exactly what to
/// defeat. Kept out of ModerationEndpoints.cs (Task A's file) so the two tasks never touch the same
/// file while running in parallel.
///
/// B20a / S-2-3: there is deliberately no GET /api/moderation/detect/engine sweep sibling. S-2-3
/// asked for one in the sandbagging two-phase shape. The owner declined on 2026-09-08: this
/// detector is a noisy heuristic (200 plies / 5 games before it will speak, a false-positive
/// warning on every Watch/Elevated), and a ranked Elevated list on a one-person mod team is a
/// queue of accusations. Reports plus the named-user and named-game routes below are the
/// discovery path. Hiding a sweep with ExcludeFromDescription is not honest while the
/// sandbagging sweep is published; not shipping the route is the honest answer. Revisit only if
/// a confirmed engine case exists and named lookup is the bottleneck. If a sweep is ever added
/// it is a button, never a cron, it moves the committed OpenAPI document, and the
/// false-positive sentence belongs on the list itself.
/// </summary>
public static class EngineDetectionEndpoints
{
    /// <summary>
    /// Hard cap on games pulled for one subject. Every one of these deserializes a full jsonb move
    /// list, so this cap is doing more work than AnalyticsEndpoints.MaxRows does - it is the only thing
    /// between a moderator's click and a multi-second query. When it starts binding for real, the
    /// answer is a denormalised timing column, not a bigger number here.
    /// </summary>
    private const int MaxGames = 200;

    public static IEndpointRouteBuilder MapEngineDetectionEndpoints(this IEndpointRouteBuilder app)
    {
        // Named user and named game only. A parameterless GET here would be the sweep S-2-3
        // asked for and B20a declined; do not add one without an owner-cleared OpenAPI bump.
        var group = app.MapGroup("/api/moderation/detect/engine").RequireAuthorization(ModerationPolicies.Admin);

        group.MapGet("/{userId:guid}", async (
            Guid userId, int? sinceDays, int? maxGames, KiriChessDbContext db, CancellationToken ct) =>
        {
            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
            if (user is null) return Results.NotFound();

            int days = Math.Clamp(sinceDays ?? 90, 1, 3650);
            int cap = Math.Clamp(maxGames ?? 50, 1, MaxGames);
            var since = DateTimeOffset.UtcNow.AddDays(-days);

            // Newest first so a truncated window is a recent sample rather than an arbitrary one - the
            // same reasoning AnalyticsEndpoints applies to its own cap. Solo/practice games (7.3, a
            // client-side bot's moves are not the player's own timing), Ongoing/Aborted results and
            // NoShow/ServerRestart terminations are excluded up front, matching
            // BalanceAnalytics.IsExcluded's reasoning - none of those rows say anything about a human's
            // timing behaviour.
            var games = await db.Games
                .Where(g => (g.WhitePlayerId == userId || g.BlackPlayerId == userId)
                         && g.WhitePlayerId != g.BlackPlayerId
                         && g.Result != GameResult.Ongoing
                         && g.Result != GameResult.Aborted
                         && g.Termination != TerminationReason.NoShow
                         && g.Termination != TerminationReason.ServerRestart
                         && (g.RatingPool == null || !g.RatingPool.StartsWith(Ratings.RatingConstants.BotPoolPrefix))
                         && g.EndedAt != null && g.EndedAt >= since)
                .OrderByDescending(g => g.EndedAt)
                .Take(cap)
                .ToListAsync(ct);

            var facts = games
                .Select(g => BuildFact(g, userId))
                .Where(f => f is not null)
                .Select(f => f!)
                .ToList();

            var report = EngineAssistanceAnalyzer.Analyze(userId, user.DisplayName, facts, DateTimeOffset.UtcNow);
            return Results.Ok(report);
        })
        .Produces<DetectionReportDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status403Forbidden);

        // One report per player in the named game, so a moderator reviewing an EngineAssistance report
        // can look at the specific game it names. Expect Insufficient here most of the time: a single
        // game rarely clears MinimumPlies/MinimumGames on its own, and that is correct behaviour, not a
        // bug to work around by lowering the thresholds for this route - see
        // Only_one_games_worth_of_plies_is_still_insufficient in the endpoint tests.
        group.MapGet("/game/{gameId:guid}", async (Guid gameId, KiriChessDbContext db, CancellationToken ct) =>
        {
            var game = await db.Games.FirstOrDefaultAsync(g => g.Id == gameId, ct);
            if (game is null) return Results.NotFound();

            // A solo/practice game has one distinct player, not two - one report, not two identical ones.
            var playerIds = game.WhitePlayerId == game.BlackPlayerId
                ? new[] { game.WhitePlayerId }
                : new[] { game.WhitePlayerId, game.BlackPlayerId };

            var names = await db.Users
                .Where(u => playerIds.Contains(u.Id))
                .ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);

            var now = DateTimeOffset.UtcNow;
            var reports = playerIds.Select(playerId =>
            {
                var fact = BuildFact(game, playerId);
                var facts = fact is null
                    ? Array.Empty<GameTimingFact>()
                    : new[] { fact };
                return EngineAssistanceAnalyzer.Analyze(
                    playerId, names.GetValueOrDefault(playerId, "Unknown"), facts, now);
            }).ToArray();

            return Results.Ok(reports);
        })
        .Produces<DetectionReportDto[]>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status403Forbidden);

        return app;
    }

    /// <summary>
    /// Projects one <see cref="Game"/> row down to the subject player's own timing facts, or null when
    /// the game does not count toward this detector at all (solo/practice, unfinished, or excluded per
    /// BalanceAnalytics.IsExcluded's reasoning). Shared by both routes so the per-user and per-game
    /// paths can never disagree about which games are in scope. Getting the White/Black side mapping
    /// backwards here produces a plausible, entirely wrong report - the exact failure mode 8.2's replay
    /// plan warns about - which is why Subject_plies_are_read_from_the_correct_side pins it with
    /// asymmetric clocks.
    /// </summary>
    private static GameTimingFact? BuildFact(Game g, Guid subjectUserId)
    {
        if (g.WhitePlayerId == g.BlackPlayerId) return null;
        if (g.Result is GameResult.Ongoing or GameResult.Aborted) return null;
        if (g.Termination is TerminationReason.NoShow or TerminationReason.ServerRestart) return null;
        if (g.EndedAt is null) return null;

        bool isWhite = g.WhitePlayerId == subjectUserId;
        if (!isWhite && g.BlackPlayerId != subjectUserId) return null;

        var subjectPlies = g.Moves
            .Where(m => m.Mover == (isWhite ? Side.White : Side.Black))
            .Select(m => new MoveTimingFact(
                m.Ply, m.ThinkMs, m.LagMs, m.ReceivedAtUtc,
                isWhite ? m.WhiteTimeAfter : m.BlackTimeAfter))
            .ToList();

        return new GameTimingFact(g.Id, g.RatingPool, g.IsRated, g.EndedAt.Value, g.Moves.Count, subjectPlies);
    }
}
