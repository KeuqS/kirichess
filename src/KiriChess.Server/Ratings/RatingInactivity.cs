namespace KiriChess.Server.Ratings;

/// <summary>What one inactivity sweep did. Returned by <see cref="RatingInactivitySweep.RunAsync"/>
/// so a test can assert on it, the same split <c>RetentionSweepResult</c> uses.</summary>
public sealed record RatingInactivityResult(DateTimeOffset RanAt, int RowsInflated, int RowsCapped);

/// <summary>
/// The pure half of Glicko-2's second update: the rating deviation an idle player accrues between
/// rating periods. <see cref="Glicko2.Update"/> already implements one period of it (the
/// zero-results branch); nothing in production ever reached that branch, because the only route into
/// <c>Update</c> was a finished game, so RD only ever shrank and an inactive rating stayed certain
/// forever. This class is the arithmetic that decides how many periods a row has missed and what its
/// RD should therefore be, kept free of EF and of the clock so it can be tested with literal values.
/// </summary>
public static class RatingInactivity
{
    /// <summary>
    /// How many whole rating periods have elapsed since <paramref name="updatedAt"/>. Zero for a row
    /// touched within the current period, which is what makes the sweep idempotent: run it twice in
    /// one period and the second run has nothing to do.
    /// </summary>
    public static int PeriodsElapsed(DateTimeOffset updatedAt, DateTimeOffset now, TimeSpan period)
    {
        if (period <= TimeSpan.Zero) return 0;
        var idle = now - updatedAt;
        if (idle < period) return 0;
        return (int)Math.Min(int.MaxValue, idle.Ticks / period.Ticks);
    }

    /// <summary>
    /// The rating after <paramref name="periods"/> idle rating periods: Glicko-2's own zero-results
    /// update applied that many times, capped at <see cref="RatingConstants.DefaultRd"/>.
    ///
    /// Only <see cref="Glicko2Rating.Rd"/> moves. The rating value is not touched, because an idle
    /// player has produced no evidence that their strength changed, only that we are less sure of it;
    /// volatility is not touched, because Glicko-2 changes sigma from results and there are none.
    ///
    /// The loop is the paper's step applied period by period rather than a closed form on purpose:
    /// the one already-reviewed implementation of that step lives in <see cref="Glicko2"/>, and a
    /// second, algebraically-equivalent expression of it here is exactly the kind of duplicate that
    /// drifts. It exits as soon as the cap is reached, so an account idle for years costs a handful
    /// of iterations, not one per week.
    /// </summary>
    public static Glicko2Rating Inflate(Glicko2Rating rating, int periods)
    {
        if (periods <= 0) return rating;
        if (rating.Rd >= RatingConstants.DefaultRd)
            return rating with { Rd = RatingConstants.DefaultRd };

        var current = rating;
        for (var i = 0; i < periods; i++)
        {
            current = Glicko2.Update(current, Array.Empty<(Glicko2Rating, double)>());
            if (current.Rd >= RatingConstants.DefaultRd) break;
        }

        // Cap, never exceed. DefaultRd is "we know nothing about this player"; there is no state
        // less certain than that, and letting RD past it would hand a returning player more rating
        // swing than a brand new account gets.
        return current with { Rd = Math.Min(current.Rd, RatingConstants.DefaultRd) };
    }
}
