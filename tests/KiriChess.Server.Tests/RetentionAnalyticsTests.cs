using KiriChess.Server.Analytics;
using Xunit;

namespace KiriChess.Server.Tests;

/// <summary>
/// Covers the AD.6+11a.3 funnel and retention maths from AD.6 / 11a.3 and
/// Phase 8a.5: rolling (not classic-day) retention, the null-vs-zero eligibility
/// rule, the self-pairing and non-cohort dedupe in <see cref="RetentionAnalytics.BuildFacts"/>, and the
/// funnel's percentage rules. Pure and synchronous, so this needs no Docker and no database.
/// </summary>
public sealed class RetentionAnalyticsTests
{
    private static readonly DateTimeOffset GeneratedAt = new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);

    private static UserFact Fact(
        Guid userId, DateTimeOffset signedUpAt, DateTimeOffset? firstGameAt = null,
        DateTimeOffset? secondGameAt = null, DateTimeOffset? lastGameAt = null, int gamesPlayed = 0) =>
        new(userId, signedUpAt, firstGameAt, secondGameAt, lastGameAt, gamesPlayed);

    [Fact]
    public void Immature_cohort_yields_null_not_zero()
    {
        var signedUpAt = GeneratedAt.AddDays(-1); // one day old: cannot have reached day 7 yet
        var facts = new[] { Fact(Guid.NewGuid(), signedUpAt) };

        var summary = RetentionAnalytics.Summarize(facts, 90, RetentionAnalytics.GrainWeek, false, GeneratedAt);
        var day7 = Assert.Single(summary.Retention, p => p.Day == 7);

        Assert.Equal(0, day7.Eligible);
        // An assertion against 0 here would pass on a broken implementation that returns 0.0 - the
        // whole point of this case is that Percent must be NULL, not zero, when Eligible is 0.
        Assert.Null(day7.Percent);
    }

    [Fact]
    public void Mature_non_returner_yields_zero_not_null()
    {
        var signedUpAt = GeneratedAt.AddDays(-40);
        // Played only on day 0 - eligible for day 7 (40 days old) but did not return.
        var facts = new[] { Fact(Guid.NewGuid(), signedUpAt, firstGameAt: signedUpAt, lastGameAt: signedUpAt, gamesPlayed: 1) };

        var summary = RetentionAnalytics.Summarize(facts, 90, RetentionAnalytics.GrainWeek, false, GeneratedAt);
        var day7 = Assert.Single(summary.Retention, p => p.Day == 7);

        Assert.Equal(1, day7.Eligible);
        Assert.Equal(0, day7.Retained);
        Assert.Equal(0, day7.Percent);
    }

    [Fact]
    public void Retention_is_rolling_not_classic_day()
    {
        var signedUpAt = GeneratedAt.AddDays(-40);
        var lateGame = signedUpAt.AddDays(20); // one game, on day 20 only
        var facts = new[] { Fact(Guid.NewGuid(), signedUpAt, firstGameAt: lateGame, lastGameAt: lateGame, gamesPlayed: 1) };

        var summary = RetentionAnalytics.Summarize(facts, 90, RetentionAnalytics.GrainWeek, false, GeneratedAt);
        var byDay = summary.Retention.ToDictionary(p => p.Day);

        // Retained at every horizon at or before the day-20 game (rolling: "at or after N days", not
        // "on day N"), and eligible-but-not-retained at day 30, which is after the only game played.
        foreach (var day in new[] { 1, 3, 7, 14 })
        {
            Assert.Equal(1, byDay[day].Eligible);
            Assert.Equal(1, byDay[day].Retained);
        }

        Assert.Equal(1, byDay[30].Eligible);
        Assert.Equal(0, byDay[30].Retained);
    }

    [Fact]
    public void PlyCount_zero_games_never_reach_the_maths()
    {
        // The PlyCount > 0 filter lives in the query (AnalyticsEndpoints), not in this pure layer, so
        // this asserts the seam: an empty games list (as if every PlyCount == 0 row was already
        // filtered out) leaves the signup with no first game, and the funnel agrees.
        var signup = new SignupFact(Guid.NewGuid(), GeneratedAt.AddDays(-10));
        var facts = RetentionAnalytics.BuildFacts([signup], []);

        var fact = Assert.Single(facts);
        Assert.Null(fact.FirstGameAt);
        Assert.Equal(0, fact.GamesPlayed);

        var summary = RetentionAnalytics.Summarize(facts, 90, RetentionAnalytics.GrainWeek, false, GeneratedAt);
        Assert.Equal(1, summary.Funnel.Single(f => f.Step == FunnelSteps.Signup).Count);
        Assert.Equal(0, summary.Funnel.Single(f => f.Step == FunnelSteps.FirstGame).Count);
    }

    [Fact]
    public void Self_paired_game_counts_once()
    {
        var userId = Guid.NewGuid();
        var signup = new SignupFact(userId, GeneratedAt.AddDays(-10));
        var game = new GameParticipationFact(userId, userId, GeneratedAt.AddDays(-9));

        var facts = RetentionAnalytics.BuildFacts([signup], [game]);

        var fact = Assert.Single(facts);
        Assert.Equal(1, fact.GamesPlayed);
        Assert.Null(fact.SecondGameAt);
    }

    [Fact]
    public void Game_by_a_non_cohort_account_is_ignored()
    {
        var cohortUser = Guid.NewGuid();
        var strangerId = Guid.NewGuid();
        var signup = new SignupFact(cohortUser, GeneratedAt.AddDays(-10));
        // Two different ids so neither seat belongs to the cohort.
        var game = new GameParticipationFact(strangerId, Guid.NewGuid(), GeneratedAt.AddDays(-9));

        var facts = RetentionAnalytics.BuildFacts([signup], [game]);

        var fact = Assert.Single(facts);
        Assert.Equal(cohortUser, fact.UserId);
        Assert.Equal(0, fact.GamesPlayed);
    }

    [Fact]
    public void Cohort_grouping_by_iso_week_vs_day_and_newest_first()
    {
        // 2026-08-17 is a Monday; 2026-08-19 (same ISO week) is the Wednesday right after it.
        var monday = new DateTimeOffset(2026, 8, 17, 10, 0, 0, TimeSpan.Zero);
        var wednesday = new DateTimeOffset(2026, 8, 19, 15, 0, 0, TimeSpan.Zero);
        var facts = new[]
        {
            Fact(Guid.NewGuid(), monday),
            Fact(Guid.NewGuid(), wednesday),
        };

        var weekSummary = RetentionAnalytics.Summarize(facts, 90, RetentionAnalytics.GrainWeek, false, GeneratedAt);
        var weekCohort = Assert.Single(weekSummary.Cohorts);
        Assert.Equal(new DateOnly(2026, 8, 17), weekCohort.CohortStart);
        Assert.Equal(2, weekCohort.Signups);

        var daySummary = RetentionAnalytics.Summarize(facts, 90, RetentionAnalytics.GrainDay, false, GeneratedAt);
        Assert.Equal(2, daySummary.Cohorts.Count);
        // Newest cohort first.
        Assert.Equal(new DateOnly(2026, 8, 19), daySummary.Cohorts[0].CohortStart);
        Assert.Equal(new DateOnly(2026, 8, 17), daySummary.Cohorts[1].CohortStart);
    }

    [Fact]
    public void Funnel_is_monotonically_decreasing()
    {
        var facts = new[]
        {
            Fact(Guid.NewGuid(), GeneratedAt.AddDays(-20)), // no games
            Fact(Guid.NewGuid(), GeneratedAt.AddDays(-20), firstGameAt: GeneratedAt.AddDays(-19), lastGameAt: GeneratedAt.AddDays(-19), gamesPlayed: 1),
            Fact(Guid.NewGuid(), GeneratedAt.AddDays(-20),
                firstGameAt: GeneratedAt.AddDays(-19), secondGameAt: GeneratedAt.AddDays(-18),
                lastGameAt: GeneratedAt.AddDays(-18), gamesPlayed: 2),
            Fact(Guid.NewGuid(), GeneratedAt.AddDays(-20),
                firstGameAt: GeneratedAt.AddDays(-19), secondGameAt: GeneratedAt.AddDays(-18),
                lastGameAt: GeneratedAt.AddDays(-1), gamesPlayed: 3),
        };

        var summary = RetentionAnalytics.Summarize(facts, 90, RetentionAnalytics.GrainWeek, false, GeneratedAt);
        var signup = summary.Funnel.Single(f => f.Step == FunnelSteps.Signup).Count;
        var firstGame = summary.Funnel.Single(f => f.Step == FunnelSteps.FirstGame).Count;
        var secondGame = summary.Funnel.Single(f => f.Step == FunnelSteps.SecondGame).Count;

        Assert.True(signup >= firstGame);
        Assert.True(firstGame >= secondGame);
    }

    [Fact]
    public void PercentOfPrevious_is_null_only_for_signup_and_retained_d7()
    {
        var facts = new[]
        {
            Fact(Guid.NewGuid(), GeneratedAt.AddDays(-20),
                firstGameAt: GeneratedAt.AddDays(-19), secondGameAt: GeneratedAt.AddDays(-18),
                lastGameAt: GeneratedAt.AddDays(-18), gamesPlayed: 2),
            Fact(Guid.NewGuid(), GeneratedAt.AddDays(-20), firstGameAt: GeneratedAt.AddDays(-19), lastGameAt: GeneratedAt.AddDays(-19), gamesPlayed: 1),
        };

        var summary = RetentionAnalytics.Summarize(facts, 90, RetentionAnalytics.GrainWeek, false, GeneratedAt);
        var byStep = summary.Funnel.ToDictionary(f => f.Step);

        Assert.Null(byStep[FunnelSteps.Signup].PercentOfPrevious);
        Assert.Null(byStep[FunnelSteps.RetainedD7].PercentOfPrevious);
        Assert.NotNull(byStep[FunnelSteps.FirstGame].PercentOfPrevious);
        Assert.NotNull(byStep[FunnelSteps.SecondGame].PercentOfPrevious);
    }

    [Fact]
    public void Empty_input_reports_zeros_and_nulls_and_does_not_throw()
    {
        var summary = RetentionAnalytics.Summarize([], 90, RetentionAnalytics.GrainWeek, false, GeneratedAt);

        Assert.Equal(0, summary.TotalSignups);
        Assert.Empty(summary.Cohorts);
        Assert.All(summary.Retention, p => Assert.Null(p.Percent));
        Assert.All(summary.Funnel, f => Assert.Null(f.PercentOfSignups));
    }
}
