namespace KiriChess.Server.Retention;

/// <summary>
/// One account flattened to the facts dormancy depends on. Built in memory from three bounded
/// queries (see RetentionEndpoints) rather than a correlated subquery per user.
/// </summary>
public readonly record struct DormancyFact(
    Guid UserId,
    string DisplayName,
    DateTimeOffset CreatedAt,
    bool EmailConfirmed,
    int GamesPlayed,
    DateTimeOffset? LastGameAt,
    bool HasSanctions);

/// <summary>
/// Phase 8a.3. Decides whether an account is dormant and, if so, why. Pure and clock-free so the
/// predicate - the part whose bug would, in a world where this drove deletion, destroy accounts - is
/// tested with literal values and no database.
///
/// It decides and nothing else. This phase builds no reaper; see Phase 8a.3 scope
/// decision 2 for why that is a decision rather than an unfinished half.
/// </summary>
public static class DormantAccountPolicy
{
    /// <summary>
    /// Returns one of <see cref="DormancyReasons"/>, or null when the account is not dormant.
    /// </summary>
    public static string? Classify(in DormancyFact fact, DateTimeOffset now, int inactiveDays)
    {
        var cutoff = now - TimeSpan.FromDays(inactiveDays);

        // An account younger than the window has not had the OPPORTUNITY to go dormant. Reporting it
        // would make the count a function of how recently people signed up, which is the same
        // immature-denominator trap 8a.5 names in its scope decision 2.
        if (fact.CreatedAt >= cutoff) return null;

        if (fact.GamesPlayed == 0)
            return fact.EmailConfirmed ? DormancyReasons.NeverPlayed : DormancyReasons.NeverActivated;

        return fact.LastGameAt is { } last && last < cutoff ? DormancyReasons.Inactive : null;
    }
}
