using KiriChess.Server.Data;
using KiriChess.Server.Data.Entities;
using KiriChess.Server.Games;
using Microsoft.EntityFrameworkCore;

namespace KiriChess.Server.Ratings;

/// <summary>
/// Applies Glicko-2 rating changes for a finished, rated game and records per-game history.
/// Scoped: it runs inside the same DbContext scope/transaction that persists the game
/// (see <see cref="Games.GameRegistry.PersistIfOverAsync"/>), so no extra SaveChanges here.
/// </summary>
public sealed class RatingService
{
    /// <summary>
    /// Mutates/creates both players' <see cref="Rating"/> rows and adds two <see cref="RatingHistory"/>
    /// rows for <paramref name="game"/>. No-op (returns null) if the game is not eligible for rating,
    /// or if a history row for this game already exists (C-1-4: a re-persist must not rate it again).
    /// The caller owns SaveChanges; <paramref name="game"/> must already be Added to the same context.
    ///
    /// Returns the before/after value for each side (Phase 11a.2), so the caller can hand it straight
    /// to the <c>GameEnded</c>/<c>MovePlayed</c> broadcast without a second read of what this method
    /// just wrote.
    ///
    /// <paramref name="updates"/> is an optional collector for the Phase 8.4 leaderboard cache: when
    /// supplied, the two <see cref="LeaderboardUpdate"/>s applied here are appended to it. This method
    /// never touches Redis itself, this class is scoped to the caller's transaction and does not call
    /// SaveChanges, so a cache write here could easily outlive a SaveChanges that then rolls back. The
    /// caller (<see cref="Games.GameRegistry.PersistIfOverAsync"/>) writes the cache only after its own
    /// SaveChangesAsync has actually succeeded. Existing callers that omit <paramref name="updates"/>
    /// are unaffected, this is purely additive.
    /// </summary>
    public async Task<GameRatingChangeDto?> ApplyAsync(
        Game game, KiriChessDbContext db, ICollection<LeaderboardUpdate>? updates = null, CancellationToken ct = default)
    {
        if (!game.IsRated) return null;
        if (!RatingConstants.IsOfficialPool(game.RatingPool) && !RatingConstants.IsBotPool(game.RatingPool))
            return null;
        if (game.WhitePlayerId == game.BlackPlayerId) return null; // solo/practice is never rated
        if (game.Result is GameResult.Ongoing or GameResult.Aborted) return null; // aborted = no rating change, ever

        // C-1-4. History is written in the same SaveChanges as the finished game, so a row for
        // this game means the rating already landed. Skip rather than insert again: the unique
        // index on (GameId, UserId) would reject the duplicate and roll back the game-row update
        // that shares this SaveChanges. Two writers that both pass this read still collide on
        // that index; that throw is the backstop, not this check.
        if (await db.RatingHistories.AnyAsync(h => h.GameId == game.Id, ct))
            return null;

        var pool = game.RatingPool!;
        var white = await GetOrCreateAsync(db, game.WhitePlayerId, pool, ct);
        var black = await GetOrCreateAsync(db, game.BlackPlayerId, pool, ct);
        var whiteBefore = white.Value;
        var blackBefore = black.Value;

        var (whiteAfter, blackAfter) = RatingCalculator.Compute(
            game.Result, ToGlicko(white), ToGlicko(black));

        var endedAt = game.EndedAt ?? DateTimeOffset.UtcNow;
        db.RatingHistories.Add(BuildHistory(game.Id, white, whiteAfter, pool, endedAt));
        db.RatingHistories.Add(BuildHistory(game.Id, black, blackAfter, pool, endedAt));

        ApplyTo(white, whiteAfter, endedAt);
        ApplyTo(black, blackAfter, endedAt);

        if (updates is not null)
        {
            updates.Add(new LeaderboardUpdate(pool, white.UserId, white.Value, white.RatingDeviation, white.LastPlayedAt));
            updates.Add(new LeaderboardUpdate(pool, black.UserId, black.Value, black.RatingDeviation, black.LastPlayedAt));
        }

        return new GameRatingChangeDto(
            new PlayerRatingChangeDto(whiteBefore, whiteAfter.Rating),
            new PlayerRatingChangeDto(blackBefore, blackAfter.Rating));
    }

    private static async Task<Rating> GetOrCreateAsync(
        KiriChessDbContext db, Guid userId, string pool, CancellationToken ct)
    {
        var existing = await db.Ratings.FirstOrDefaultAsync(r => r.UserId == userId && r.Pool == pool, ct);
        if (existing is not null) return existing;

        var created = new Rating
        {
            UserId = userId,
            Pool = pool,
            Value = RatingConstants.DefaultRating,
            RatingDeviation = RatingConstants.DefaultRd,
            Volatility = RatingConstants.DefaultVolatility,
        };
        db.Ratings.Add(created);
        return created;
    }

    private static Glicko2Rating ToGlicko(Rating r) => new(r.Value, r.RatingDeviation, r.Volatility);

    private static void ApplyTo(Rating r, Glicko2Rating after, DateTimeOffset when)
    {
        r.Value = after.Rating;
        r.RatingDeviation = after.Rd;
        r.Volatility = after.Volatility;
        r.GamesPlayed += 1;
        r.LastPlayedAt = when;
        r.UpdatedAt = when;
    }

    private static RatingHistory BuildHistory(Guid gameId, Rating before, Glicko2Rating after, string pool, DateTimeOffset when)
        => new()
        {
            GameId = gameId,
            UserId = before.UserId,
            Pool = pool,
            ValueBefore = before.Value,
            ValueAfter = after.Rating,
            RdBefore = before.RatingDeviation,
            RdAfter = after.Rd,
            VolatilityBefore = before.Volatility,
            VolatilityAfter = after.Volatility,
            CreatedAt = when,
        };
}
