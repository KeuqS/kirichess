using KiriChess.Engine.Core;
using KiriChess.Server.Data;
using KiriChess.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace KiriChess.Server.Games;

/// <summary>
/// Shared finished-game history listing for first-party <c>GET /api/games/mine</c> and
/// <c>GET /api/v1/games/mine</c> (Phase 9f.5). Extracted so the two routes cannot drift.
/// </summary>
internal static class GameHistoryQuery
{
    /// <summary>
    /// <paramref name="before"/> and <paramref name="beforeId"/> are the keyset cursor: the
    /// <c>EndedAt</c> and id of the last row the caller already has. Both or neither. When both
    /// are set, <paramref name="skip"/> is ignored. Finished games always have an
    /// <c>EndedAt</c> (I-1-4); a finished row with a null end is not listed.
    /// </summary>
    public static async Task<GameHistoryEntryDto[]> ListMineAsync(
        KiriChessDbContext db, Guid userId, int? take, int? skip,
        DateTimeOffset? before, Guid? beforeId, CancellationToken ct)
    {
        var takeClamped = Math.Clamp(take ?? 20, 1, 100);

        // C-2-7: projected before materialising, naming only the ten scalar columns the DTO below
        // actually uses. Without this, ToListAsync pulled whole Game entities across the wire,
        // including Moves (the complete per-ply move log, jsonb) and ConfigSnapshot, neither of which
        // this handler reads: a hundred rows of history is a hundred complete move logs transferred to
        // render a list that shows none of them. PlyCount exists with its own doc comment specifically
        // so a consumer does not have to deserialise the move list, and this was the consumer that did
        // anyway.
        //
        // I-1-4. Order by EndedAt alone. COALESCE(EndedAt, CreatedAt) cannot use an index, and a
        // finished game always has EndedAt. The (player, EndedAt) indexes are partial on
        // EndedAt IS NOT NULL, so this predicate has to say the same thing. Keyset on
        // (EndedAt, Id) replaces OFFSET: a deep skip re-reads every newer row.
        var query = db.Games
            .AsNoTracking()
            .Where(g => g.Result != GameResult.Ongoing
                && g.EndedAt != null
                && (g.WhitePlayerId == userId || g.BlackPlayerId == userId));

        if (before is { } ended && beforeId is { } cursorId)
        {
            query = query.Where(g =>
                g.EndedAt!.Value < ended || (g.EndedAt == ended && g.Id.CompareTo(cursorId) < 0));
        }

        IQueryable<Game> page = query
            .OrderByDescending(g => g.EndedAt)
            .ThenByDescending(g => g.Id);
        if (before is null)
            page = page.Skip(Math.Max(skip ?? 0, 0));

        var rows = await page.Take(takeClamped)
            .Select(g => new
            {
                g.Id,
                g.WhitePlayerId,
                g.BlackPlayerId,
                g.Result,
                g.Termination,
                g.RatingPool,
                g.IsRated,
                g.PlyCount,
                g.CreatedAt,
                g.EndedAt,
            })
            .ToListAsync(ct);

        if (rows.Count == 0) return [];

        var opponentIds = rows
            .Select(g => g.WhitePlayerId == userId ? g.BlackPlayerId : g.WhitePlayerId)
            .Distinct()
            .ToArray();
        var names = await db.Users
            .AsNoTracking()
            .Where(u => opponentIds.Contains(u.Id))
            .Select(u => new { u.Id, u.DisplayName })
            .ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);

        var gameIds = rows.Select(g => g.Id).ToArray();
        var ratingDeltas = await db.RatingHistories
            .AsNoTracking()
            .Where(h => h.UserId == userId && gameIds.Contains(h.GameId))
            .Select(h => new { h.GameId, h.ValueBefore, h.ValueAfter })
            .ToDictionaryAsync(h => h.GameId, ct);

        return rows.Select(g =>
        {
            var mine = g.WhitePlayerId == userId ? Side.White : Side.Black;
            var opponentId = mine == Side.White ? g.BlackPlayerId : g.WhitePlayerId;
            names.TryGetValue(opponentId, out var opponentName);
            ratingDeltas.TryGetValue(g.Id, out var delta);
            return new GameHistoryEntryDto(
                g.Id,
                opponentId,
                opponentName ?? "Unknown player",
                mine.ToString(),
                g.Result.ToString(),
                g.Termination.ToString(),
                g.RatingPool,
                g.IsRated,
                g.PlyCount,
                g.CreatedAt,
                g.EndedAt,
                delta?.ValueBefore,
                delta?.ValueAfter);
        }).ToArray();
    }
}
