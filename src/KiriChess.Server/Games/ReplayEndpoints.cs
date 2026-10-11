using System.Text;
using KiriChess.Engine;
using KiriChess.Server.Data;
using KiriChess.Server.Data.Entities;
using KiriChess.Server.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace KiriChess.Server.Games;

/// <summary>
/// Public, anonymous access to a **finished** game (Phase 8.2, extended by 9e.2): the JSON replay and
/// the downloadable PGN are two representations of the same row, and share the same security control.
/// Anonymous on purpose: a shared link has to work for someone who has never signed in, which is the
/// entire point of a shareable replay or a shareable PGN download.
///
/// The <c>Result != Ongoing</c> filter is that control, not a tidiness one, for both routes.
/// <see cref="LiveGamePersister"/> writes the move list of in-progress games to the same row after
/// every move, so serving an ongoing game here would hand anyone a live feed of a game they are not
/// in, an engine-assistance channel wide open to a second browser window. Watching a live game is
/// 8.1's spectator broadcast, off the live session, never off this row.
///
/// An ongoing game answers 404, identical to an unknown id: the existence of a live game between two
/// named players is itself information this endpoint has no business confirming.
/// </summary>
public static class ReplayEndpoints
{
    public static IEndpointRouteBuilder MapReplayEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/games/{id:guid}/replay", async (
            Guid id, KiriChessDbContext db, KiriChessEngine engine, IGameClock clock, HttpContext http,
            ILoggerFactory loggerFactory, KiriChessMetrics metrics, CancellationToken ct) =>
        {
            // GameReplayBuilder is a static class and cannot be an ILogger<T> type argument, so the
            // category is named explicitly instead - the same string ILogger<GameReplayBuilder> would
            // have produced.
            var logger = loggerFactory.CreateLogger("KiriChess.Server.Games.GameReplayBuilder");
            var row = await db.Games
                .AsNoTracking()
                .FirstOrDefaultAsync(g => g.Id == id && g.Result != GameResult.Ongoing, ct);
            if (row is null) return Results.NotFound();

            // Two ids, one query, and a solo/bot game is a single id, which `Contains` handles
            // without a special case. Names are looked up rather than joined so a deleted account
            // degrades to "Unknown player" instead of dropping the replay.
            var ids = new[] { row.WhitePlayerId, row.BlackPlayerId };
            var now = clock.UtcNow;
            var rows = await db.Users
                .AsNoTracking()
                .Where(u => ids.Contains(u.Id))
                .Select(u => new { u.Id, u.DisplayName, u.IsPatron, u.PatronTier, u.PatronKind, u.PatronExpiresAt })
                .ToDictionaryAsync(u => u.Id, ct);

            // Same rule as PatronBadge.For, inlined because this is an anonymous projection rather
            // than an ApplicationUser; see Account/PatronBadge.cs for the definition it duplicates,
            // including why a Monthly badge ignores the expiry column.
            string TierFor(Guid playerId) =>
                rows.TryGetValue(playerId, out var r) && r.IsPatron
                && (r.PatronKind == Account.PatronKind.Monthly || r.PatronExpiresAt is null || r.PatronExpiresAt > now)
                    ? r.PatronTier.ToString()
                    : GameReplayBuilder.NoPatronTier;

            rows.TryGetValue(row.WhitePlayerId, out var white);
            rows.TryGetValue(row.BlackPlayerId, out var black);

            ApplyImmutableCacheHeaders(http, id);
            return Results.Ok(GameReplayBuilder.Build(
                engine, row, white?.DisplayName, black?.DisplayName,
                TierFor(row.WhitePlayerId), TierFor(row.BlackPlayerId), logger, metrics));
        })
        .RequireRateLimiting(RateLimitPolicies.Replay)
        .Produces<ReplayResponse>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound);

        app.MapGet("/api/games/{id:guid}/pgn", async (
            Guid id, KiriChessDbContext db, KiriChessEngine engine, IConfiguration configuration,
            HttpContext http, ILoggerFactory loggerFactory, KiriChessMetrics metrics, CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("KiriChess.Server.Games.PgnBuilder");
            var row = await db.Games
                .AsNoTracking()
                .FirstOrDefaultAsync(g => g.Id == id && g.Result != GameResult.Ongoing, ct);
            if (row is null) return Results.NotFound();

            var ids = new[] { row.WhitePlayerId, row.BlackPlayerId };
            var names = await db.Users
                .AsNoTracking()
                .Where(u => ids.Contains(u.Id))
                .Select(u => new { u.Id, u.DisplayName })
                .ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);

            // Pre-game ratings for [WhiteElo]/[BlackElo]. At most two rows, and an unrated game has
            // none, which is exactly the case where the tags must be absent rather than "?" (docs/game/PGN.md).
            var elos = await db.RatingHistories
                .AsNoTracking()
                .Where(h => h.GameId == id)
                .Select(h => new { h.UserId, h.ValueBefore })
                .ToDictionaryAsync(h => h.UserId, h => h.ValueBefore, ct);

            names.TryGetValue(row.WhitePlayerId, out var whiteName);
            names.TryGetValue(row.BlackPlayerId, out var blackName);

            static int? Elo(Dictionary<Guid, double> map, Guid id) =>
                map.TryGetValue(id, out var v) ? (int)Math.Round(v) : null;

            var siteBaseUrl = configuration.GetValue("Frontend:BaseUrl", string.Empty)?.TrimEnd('/');
            var pgn = PgnBuilder.Build(
                engine, row, whiteName, blackName,
                Elo(elos, row.WhitePlayerId), Elo(elos, row.BlackPlayerId), siteBaseUrl, logger, metrics);

            ApplyImmutableCacheHeaders(http, id);
            // Results.File is what sets Content-Disposition: attachment, the same reason
            // AccountEndpoints.cs uses it rather than hand-writing the header. application/x-chess-pgn
            // is the content type every chess site serves .pgn as; a browser hands it straight to the
            // file the user meant to download.
            return Results.File(Encoding.UTF8.GetBytes(pgn), "application/x-chess-pgn",
                $"kirichess-{id}.pgn");
        })
        .RequireRateLimiting(RateLimitPolicies.Replay)
        // The body is a PGN text file, not JSON, so this route carries no application/json schema
        // and is listed in OpenApiContractTests.NoJsonBodyAllowlist with that reason.
        .Produces<string>(StatusCodes.Status200OK, "application/x-chess-pgn")
        .Produces(StatusCodes.Status404NotFound);

        return app;
    }

    /// <summary>
    /// C-8-4: a finished game's replay or PGN is a pure function of an immutable row - both routes
    /// filter <c>Result != Ongoing</c> precisely to guarantee that - yet every request re-ran the
    /// whole engine walk with no HTTP caching at all, so a link shared to an audience (AD.3's plan)
    /// hit Fly and Neon on every view instead of being absorbed by Cloudflare. Called only after the
    /// non-Ongoing row has already been loaded, by both this class and
    /// <see cref="PublicApiEndpoints"/>'s twin routes, so it is never reachable for a live game.
    ///
    /// The ETag is the game id alone, and that is a deliberate approximation rather than a strict
    /// content hash. The game row itself cannot change once it is no longer Ongoing, but the response
    /// also carries display names and patron tiers, which LoadNamesAsync reads live and which a
    /// player can change afterwards. So a rename or an expired badge takes up to max-age to appear on
    /// a replay. That is the trade C-8-4 asks for by name: the movetext is the reason the page is
    /// shared, absorbing a travelling link at the edge is the point, and a day-stale display name on
    /// a finished game is the cheaper of the two costs.
    /// </summary>
    internal static void ApplyImmutableCacheHeaders(HttpContext http, Guid gameId)
    {
        http.Response.Headers.CacheControl = "public, max-age=86400, immutable";
        http.Response.Headers.ETag = $"W/\"{gameId:N}\"";
    }
}
