using KiriChess.Server.Data.Entities;

namespace KiriChess.Server.Ratings;

/// <summary>
/// Who belongs on an established leaderboard. Deviation at or under
/// <see cref="RatingConstants.ProvisionalRdThreshold"/>, and a rated game in that pool inside
/// <see cref="RatingConstants.LeaderboardActiveWindow"/>. The second half is C-7-1: Glicko-2 does
/// inflate an idle rating, and it does it so slowly that deviation alone never unseats a departed
/// leader in any time a visitor would notice.
/// </summary>
public static class EstablishedBoard
{
    public static bool Contains(double ratingDeviation, DateTimeOffset lastPlayedAt, DateTimeOffset now)
        => ratingDeviation <= RatingConstants.ProvisionalRdThreshold && lastPlayedAt >= ActiveSince(now);

    public static DateTimeOffset ActiveSince(DateTimeOffset now)
        => now - RatingConstants.LeaderboardActiveWindow;

    /// <summary>
    /// The established-board predicate in a form EF translates. <paramref name="now"/> is captured
    /// outside the expression so the comparison is a constant, not a client-eval call.
    /// </summary>
    public static IQueryable<Rating> WhereCurrent(this IQueryable<Rating> ratings, DateTimeOffset now)
    {
        var activeSince = ActiveSince(now);
        return ratings.Where(r =>
            r.RatingDeviation <= RatingConstants.ProvisionalRdThreshold &&
            r.LastPlayedAt >= activeSince);
    }
}
