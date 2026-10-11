using System.Security.Claims;
using KiriChess.Server.Account;
using KiriChess.Server.Auth;
using KiriChess.Server.Data;
using KiriChess.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace KiriChess.Server.Ratings;

/// <summary>Read-only endpoints for ratings, leaderboards, and rating history.</summary>
public static class RatingEndpoints
{
    public static IEndpointRouteBuilder MapRatingEndpoints(this IEndpointRouteBuilder app)
    {
        // Caller's own ratings across all pools (auth required).
        app.MapGet("/api/ratings/me", async (ClaimsPrincipal user, KiriChessDbContext db, CancellationToken ct) =>
        {
            var me = user.GetUserId();
            if (me is null) return Results.Unauthorized();
            var rows = await db.Ratings.Where(r => r.UserId == me.Value).ToListAsync(ct);
            return Results.Ok(rows.Select(ToDto).ToArray());
        })
        .RequireAuthorization()
        .Produces<RatingDto[]>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized);

        // A user's public ratings across all pools.
        app.MapGet("/api/ratings/users/{userId:guid}", async (Guid userId, KiriChessDbContext db, CancellationToken ct) =>
        {
            // C-7-8: filtered to the official human pools, matching the leaderboard (:47) and the
            // history endpoint (:110), which both reject a bot pool outright. Without this filter this
            // was the one route on the surface that exposed a bot:{preset} row, while the bot API
            // deliberately filters the other way (PublicApiEndpoints.cs:196-198) and nothing else
            // renders the result. Not a disclosure (the pool name is not secret), a consistency fix.
            var officialPools = RatingConstants.OfficialPools;
            var rows = await db.Ratings
                .Where(r => r.UserId == userId && officialPools.Contains(r.Pool))
                .ToListAsync(ct);
            return Results.Ok(rows.Select(ToDto).ToArray());
        })
        .Produces<RatingDto[]>(StatusCodes.Status200OK);

        // Pool leaderboard, highest rating first. `includeProvisional=true` shows every rated player,
        // including those still settling and those who have not played inside the active window.
        // The default is the established board: deviation at or under the provisional threshold, and
        // a rated game in this pool inside RatingConstants.LeaderboardActiveWindow.
        //
        // Redis path (Phase 8.4): LeaderboardCache is a derived cache over the same Ratings table,
        // never the source of truth (see the class comment on LeaderboardCache and roadmap line 150,
        // "Postgres index first; Redis sorted sets only when traffic demands"). TopAsync returns null
        // whenever the cache cannot answer, disabled, unreachable, or not yet warmed, and null
        // always falls through to the Postgres query below, unchanged, so a flushed or absent Redis
        // costs this endpoint nothing but latency.
        app.MapGet("/api/leaderboards/{pool}", async (
            string pool, KiriChessDbContext db, LeaderboardCache cache, int? limit, bool? includeProvisional,
            CancellationToken ct) =>
        {
            var key = pool.Trim().ToLowerInvariant();
            if (!RatingConstants.IsOfficialPool(key)) return Results.NotFound();

            int take = Math.Clamp(limit ?? 50, 1, 200);
            // Patron tier is a time-sensitive read (a one-off badge lapses on a date), so both the
            // cache-hit and fallback branches below need the same instant.
            var now = DateTimeOffset.UtcNow;

            var ids = await cache.TopAsync(key, take, includeProvisional == true, ct);
            if (ids is not null)
            {
                // One query for exactly those ids, then re-ranked in the ZSET's order, a
                // `WHERE Id IN (...)` makes no ordering guarantee, and ranking by whatever order
                // Postgres happens to return would silently scramble the leaderboard while still
                // looking plausible. See RankByCacheOrder.
                // Patron tier inlined here rather than via PatronBadge.TierExpression: composing a
                // second Expression<Func<...>> into this Join projection is not guaranteed to
                // translate, so the same rule (Account/PatronBadge.cs's TierExpression)
                // is duplicated directly in SQL-translatable form instead of risking a client-eval
                // fallback over the whole table.
                // Re-apply the established predicate on the way back from Postgres. The ZSET is a cache
                // of that predicate, rebuilt on a timer. A player who has since left the active window
                // is dropped here. If any id is dropped, the ZSET's top-N is no longer the eligible
                // top-N (the next eligible player may sit just outside the ids we fetched), so this
                // falls through to the Postgres query rather than serving a short board.
                var cachedRatings = db.Ratings.Where(r => r.Pool == key && ids.Contains(r.UserId));
                if (includeProvisional != true)
                    cachedRatings = cachedRatings.WhereCurrent(now);

                var rowsById = await cachedRatings
                    .Join(db.Users, r => r.UserId, u => u.Id,
                        (r, u) => new LeaderboardRow(r.UserId, u.DisplayName, r.Value, r.RatingDeviation, r.GamesPlayed,
                            u.IsPatron && (u.PatronKind == PatronKind.Monthly || u.PatronExpiresAt == null || u.PatronExpiresAt > now)
                                ? u.PatronTier : PatronTier.None,
                            u.AvatarUpdatedAt))
                    .ToDictionaryAsync(x => x.UserId, ct);

                if (rowsById.Count == ids.Count)
                    return Results.Ok(RankByCacheOrder(ids, rowsById));
            }

            var q = db.Ratings.Where(r => r.Pool == key);
            if (includeProvisional != true)
                q = q.WhereCurrent(now);

            var rows = await q
                .OrderByDescending(r => r.Value)
                .Take(take)
                .Join(db.Users, r => r.UserId, u => u.Id,
                    (r, u) => new
                    {
                        r.UserId, u.DisplayName, r.Value, r.RatingDeviation, r.GamesPlayed,
                        // Same inlined rule as the cache-hit branch above; see that comment.
                        PatronTier = u.IsPatron
                            && (u.PatronKind == PatronKind.Monthly || u.PatronExpiresAt == null || u.PatronExpiresAt > now)
                                ? u.PatronTier : PatronTier.None,
                        u.AvatarUpdatedAt
                    })
                .ToListAsync(ct);

            var entries = rows.Select((x, i) => new LeaderboardEntryDto(
                i + 1, x.UserId, x.DisplayName, x.Value, x.RatingDeviation, x.GamesPlayed,
                RatingConstants.IsProvisional(x.RatingDeviation), x.PatronTier.ToString(),
                AvatarUrls.For(x.UserId, x.AvatarUpdatedAt))).ToArray();
            return Results.Ok(entries);
        })
        .Produces<LeaderboardEntryDto[]>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound);

        // A user's rating history in a pool, oldest first (for graphs).
        app.MapGet("/api/ratings/users/{userId:guid}/history/{pool}", async (
            Guid userId, string pool, KiriChessDbContext db, int? limit, CancellationToken ct) =>
        {
            var key = pool.Trim().ToLowerInvariant();
            if (!RatingConstants.IsOfficialPool(key)) return Results.NotFound();

            int take = Math.Clamp(limit ?? 200, 1, 1000);
            var rows = await db.RatingHistories
                .Where(h => h.UserId == userId && h.Pool == key)
                .OrderBy(h => h.CreatedAt)
                .Take(take)
                .Select(h => new RatingHistoryPointDto(h.GameId, h.ValueBefore, h.ValueAfter, h.RdAfter, h.CreatedAt))
                .ToListAsync(ct);
            return Results.Ok(rows);
        })
        // 9d.7: this is the live drift the phase README documents (scope decision, "What is actually
        // broken today" item 4) - ui/lib/types.ts's hand-written RatingHistoryPointDto claims
        // { pool, rating, ratingDeviation, createdAt }, which shares NO field name with what this
        // handler actually constructs. The type is unused by any caller today, so nothing breaks; this
        // annotation states the server's REAL shape rather than "fixing" either side - Task B adopts it.
        .Produces<RatingHistoryPointDto[]>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound);

        return app;
    }

    internal static RatingDto ToDto(Rating r) => new(
        r.Pool, r.Value, r.RatingDeviation, r.Volatility, r.GamesPlayed,
        RatingConstants.IsProvisional(r.RatingDeviation), r.UpdatedAt);

    /// <summary>
    /// Turns a ZSET's id order plus a Postgres row lookup into ranked, contiguous leaderboard
    /// entries. Pure and DB/Redis-free by construction, so the ordering hazard in the Redis read path
    /// (Step 3 of Task D, a `WHERE Id IN (...)` result carries no ordering guarantee of its own) is
    /// testable without a database. Two rules baked in here, both pinned by the plan:
    ///
    /// - Rank comes from <paramref name="ids"/>'s order, never <paramref name="rowsById"/>'s.
    /// - An id with no matching row (a cache that outlived a deleted account, 8a.2 will make this a
    ///   real case) is skipped, not rendered as "Unknown"; ranks stay contiguous because they are
    ///   assigned only to rows that were actually found, after the skip.
    ///
    /// <see cref="LeaderboardEntryDto.Provisional"/> always comes from the Postgres row's
    /// RatingDeviation, never inferred from which ZSET the id came from.
    /// </summary>
    internal static LeaderboardEntryDto[] RankByCacheOrder(
        IReadOnlyList<Guid> ids, IReadOnlyDictionary<Guid, LeaderboardRow> rowsById)
    {
        var entries = new List<LeaderboardEntryDto>(ids.Count);
        var rank = 0;
        foreach (var id in ids)
        {
            if (!rowsById.TryGetValue(id, out var row)) continue;
            rank++;
            entries.Add(new LeaderboardEntryDto(
                rank, row.UserId, row.DisplayName, row.Value, row.RatingDeviation, row.GamesPlayed,
                RatingConstants.IsProvisional(row.RatingDeviation), row.PatronTier.ToString(),
                AvatarUrls.For(row.UserId, row.AvatarUpdatedAt)));
        }
        return entries.ToArray();
    }

}
