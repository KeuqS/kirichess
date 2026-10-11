namespace KiriChess.Server.Analytics;

/// <summary>
/// Turns flat signup and game facts into the AD.6+11a.3 acquisition funnel and player-retention
/// curve. Pure and synchronous by design, for the same reason <see cref="BalanceAnalytics"/> is: the
/// maths is the part worth testing, and keeping it out of the query means it is tested with a literal
/// list instead of a Postgres container.
///
/// Retention here is ROLLING, not classic-day: "retained at day N" means the account played at least
/// one game at or after signup + N days, not that it played one inside a 24-hour window N days out.
/// See Phase 8a.5 scope decision 1 - at this project's volume a one-day bucket is
/// mostly zeroes, and a metric that reads 0% on a good week teaches nobody anything.
/// </summary>
public static class RetentionAnalytics
{
    /// <summary>Horizons on the retention curve, in ascending order. Pinned; the UI renders whatever it is sent.</summary>
    public static readonly int[] Horizons = [1, 3, 7, 14, 30];

    public const string GrainDay = "day";
    public const string GrainWeek = "week";

    /// <summary>
    /// Joins games onto accounts in memory. Deliberately not a correlated subquery per user: two flat
    /// capped queries and a dictionary is one round trip and is testable; N subqueries is neither.
    ///
    /// A game whose two seats hold the same account id counts ONCE - the same dedupe
    /// GameRegistry.PlayersInGame does. Games belonging to accounts outside <paramref name="signups"/>
    /// are ignored rather than being an error: the query window can legitimately return a game played
    /// by an account that signed up before it.
    /// </summary>
    public static IReadOnlyList<UserFact> BuildFacts(
        IReadOnlyList<SignupFact> signups,
        IReadOnlyList<GameParticipationFact> games)
    {
        // Seeded with every signup id first (as empty lists) so the fan-out below can skip games
        // belonging to non-cohort accounts with one dictionary lookup, and so an account with no
        // games still gets a UserFact.
        var byUser = new Dictionary<Guid, List<DateTimeOffset>>(signups.Count);
        foreach (var s in signups)
            byUser[s.UserId] = [];

        foreach (var g in games)
        {
            if (byUser.TryGetValue(g.WhitePlayerId, out var whiteTimes))
                whiteTimes.Add(g.StartedAt);

            if (g.BlackPlayerId != g.WhitePlayerId && byUser.TryGetValue(g.BlackPlayerId, out var blackTimes))
                blackTimes.Add(g.StartedAt);
        }

        var facts = new List<UserFact>(signups.Count);
        foreach (var s in signups)
        {
            var times = byUser[s.UserId];
            times.Sort();

            facts.Add(new UserFact(
                s.UserId,
                s.SignedUpAt,
                FirstGameAt: times.Count > 0 ? times[0] : null,
                SecondGameAt: times.Count > 1 ? times[1] : null,
                LastGameAt: times.Count > 0 ? times[^1] : null,
                GamesPlayed: times.Count));
        }

        return facts;
    }

    /// <summary>
    /// <paramref name="grain"/> is <see cref="GrainDay"/> or <see cref="GrainWeek"/>; anything else is
    /// treated as week. <paramref name="generatedAt"/> is passed in rather than read from the clock so
    /// every maturity decision in here is deterministic under test.
    /// </summary>
    public static RetentionSummaryDto Summarize(
        IReadOnlyList<UserFact> facts,
        int sinceDays,
        string grain,
        bool truncated,
        DateTimeOffset generatedAt)
    {
        var normalizedGrain = string.Equals(grain, GrainDay, StringComparison.Ordinal) ? GrainDay : GrainWeek;

        int total = facts.Count;
        var pooledRetention = ComputeRetentionCurve(facts, generatedAt);

        var funnel = BuildFunnel(facts, total, pooledRetention);

        var cohorts = facts
            .GroupBy(f => CohortStartFor(f.SignedUpAt, normalizedGrain))
            .OrderByDescending(g => g.Key)
            .Select(g =>
            {
                var cohortFacts = g.ToList();
                return new CohortRowDto(
                    g.Key,
                    Signups: cohortFacts.Count,
                    PlayedFirstGame: cohortFacts.Count(f => f.FirstGameAt is not null),
                    PlayedSecondGame: cohortFacts.Count(f => f.SecondGameAt is not null),
                    Retention: ComputeRetentionCurve(cohortFacts, generatedAt));
            })
            .ToList();

        return new RetentionSummaryDto(
            generatedAt, sinceDays, normalizedGrain, truncated, total, funnel, pooledRetention, cohorts);
    }

    /// <summary>
    /// Monday-anchored ISO week start, or the UTC date itself for day grain. UTC throughout: a cohort
    /// boundary that moves with the reader's timezone is a cohort boundary that produces two different
    /// answers for the same data.
    /// </summary>
    public static DateOnly CohortStartFor(DateTimeOffset signedUpAt, string grain)
    {
        var date = DateOnly.FromDateTime(signedUpAt.UtcDateTime);
        if (grain == GrainDay) return date;

        // DayOfWeek is Sunday=0..Saturday=6; this rotates it to Monday=0..Sunday=6 so the subtraction
        // below always lands on the Monday of the same ISO week.
        int daysSinceMonday = ((int)date.DayOfWeek + 6) % 7;
        return date.AddDays(-daysSinceMonday);
    }

    /// <summary>
    /// The four funnel steps in order. <see cref="FunnelStepDto.PercentOfSignups"/> is always over
    /// that step's own <see cref="FunnelStepDto.Eligible"/>, which equals total signups for the first
    /// three steps and the mature subset for the fourth.
    /// </summary>
    private static IReadOnlyList<FunnelStepDto> BuildFunnel(
        IReadOnlyList<UserFact> facts, int total, IReadOnlyList<RetentionPointDto> pooledRetention)
    {
        int firstGameCount = facts.Count(f => f.FirstGameAt is not null);
        int secondGameCount = facts.Count(f => f.SecondGameAt is not null);
        var d7 = pooledRetention.Single(p => p.Day == 7);

        var signup = new FunnelStepDto(
            FunnelSteps.Signup, "Signed up", total, total,
            PercentOfSignups: Percent(total, total),
            PercentOfPrevious: null); // no step precedes signup

        var firstGame = new FunnelStepDto(
            FunnelSteps.FirstGame, "Played a game", firstGameCount, total,
            PercentOfSignups: Percent(firstGameCount, total),
            PercentOfPrevious: Percent(firstGameCount, signup.Count));

        var secondGame = new FunnelStepDto(
            FunnelSteps.SecondGame, "Played a second game", secondGameCount, total,
            PercentOfSignups: Percent(secondGameCount, total),
            PercentOfPrevious: Percent(secondGameCount, firstGame.Count));

        // PercentOfPrevious is deliberately null here: the previous step's Count includes accounts too
        // young to be eligible for day 7 at all, so a ratio against it would compare two different
        // populations rather than measure a real conversion.
        var retainedD7 = new FunnelStepDto(
            FunnelSteps.RetainedD7, "Came back after a week", d7.Retained, d7.Eligible,
            PercentOfSignups: Percent(d7.Retained, d7.Eligible),
            PercentOfPrevious: null);

        return [signup, firstGame, secondGame, retainedD7];
    }

    private static IReadOnlyList<RetentionPointDto> ComputeRetentionCurve(
        IReadOnlyList<UserFact> facts, DateTimeOffset generatedAt) =>
        Horizons.Select(day => ComputePoint(facts, day, generatedAt)).ToList();

    /// <summary>
    /// <see cref="RetentionPointDto.Percent"/> is null - never zero - when <see cref="RetentionPointDto.Eligible"/>
    /// is 0. This is README scope decision 2 and it is the single most important line in this file: a
    /// cohort too young to have reached this horizon has not failed it.
    /// </summary>
    private static RetentionPointDto ComputePoint(IReadOnlyList<UserFact> facts, int day, DateTimeOffset generatedAt)
    {
        int eligible = 0, retained = 0;

        foreach (var f in facts)
        {
            var threshold = f.SignedUpAt.AddDays(day);
            if (generatedAt < threshold) continue; // the account has not HAD the chance yet

            eligible++;
            if (f.LastGameAt is not null && f.LastGameAt >= threshold)
                retained++;
        }

        return new RetentionPointDto(day, eligible, retained, Percent(retained, eligible));
    }

    /// <summary>Matches <see cref="BalanceAnalytics"/>'s rounding convention; do not invent a second one in this folder.</summary>
    private static double? Percent(int count, int denominator) =>
        denominator == 0 ? null : Math.Round(100.0 * count / denominator, 1);
}
