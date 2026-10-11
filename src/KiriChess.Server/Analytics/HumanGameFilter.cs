using KiriChess.Server.Data.Entities;
using KiriChess.Server.Ratings;

namespace KiriChess.Server.Analytics;

/// <summary>
/// M-4-4. AD.7 counts games between humans. A null <see cref="Game.RatingPool"/> is a custom
/// game and stays. <c>bot:{preset}</c> is the API-token namespace and is excluded.
///
/// The null arm is required. In SQL, <c>NOT LIKE</c> of a null is unknown, and a WHERE drops
/// unknown, so a filter written as only "not a bot pool" would also drop every custom game.
/// </summary>
internal static class HumanGameFilter
{
    public static IQueryable<Game> WhereHumanPool(this IQueryable<Game> games) =>
        games.Where(g => g.RatingPool == null || !g.RatingPool.StartsWith(RatingConstants.BotPoolPrefix));
}
