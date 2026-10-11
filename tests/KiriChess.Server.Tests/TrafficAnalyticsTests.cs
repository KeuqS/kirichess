using KiriChess.Server.Analytics;
using Xunit;

namespace KiriChess.Server.Tests;

/// <summary>
/// Covers the AD.6 / 11a.3 traffic maths from AD.6 / 11a.3 and
/// TASK-B-daily-counters.md: zero-fill, the page-vs-funnel split, the source breakdown, the signup
/// counter-vs-account-table divergence, and the first-session percentages. Pure and synchronous, so
/// this needs no Docker and no database.
/// </summary>
public sealed class TrafficAnalyticsTests
{
    private static readonly DateOnly Today = new(2026, 8, 18);
    private static readonly DateTimeOffset GeneratedAt = new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);

    private static TrafficSummaryDto Summarize(
        IReadOnlyList<DailyCounterFact> counters,
        int sinceDays = 7,
        IReadOnlyList<DateTimeOffset>? signupsAt = null,
        IReadOnlyList<DateTimeOffset>? gamesStartedAt = null,
        IReadOnlyList<DateTimeOffset>? gamesFinishedAt = null,
        bool truncated = false) =>
        TrafficAnalytics.Summarize(
            counters,
            signupsAt ?? [],
            gamesStartedAt ?? [],
            gamesFinishedAt ?? [],
            sinceDays,
            Today,
            truncated,
            GeneratedAt);

    [Fact]
    public void Zero_fill_returns_every_day_oldest_first_with_empty_days_at_zero()
    {
        var counters = new List<DailyCounterFact>
        {
            new(Today, CounterKeys.Landing, CounterSources.Direct, 3),
            new(Today.AddDays(-2), CounterKeys.Landing, CounterSources.Direct, 2),
        };

        var summary = Summarize(counters, sinceDays: 7);

        Assert.Equal(7, summary.Days.Count);
        Assert.Equal(Today.AddDays(-6), summary.Days[0].Date);
        Assert.Equal(Today, summary.Days[^1].Date);
        Assert.True(summary.Days.Select(d => d.Date).SequenceEqual(summary.Days.Select(d => d.Date).OrderBy(d => d)));

        var empty = summary.Days.First(d => d.Date == Today.AddDays(-5));
        Assert.Equal(0, empty.PageViews);
        Assert.Empty(empty.Pages);
    }

    [Fact]
    public void ByPage_aggregates_across_days_and_sources_with_ordinal_tie_break()
    {
        var counters = new List<DailyCounterFact>
        {
            new(Today, CounterKeys.Landing, CounterSources.Direct, 5),
            new(Today.AddDays(-1), CounterKeys.Landing, CounterSources.Reddit, 5),
            // A deliberate tie against Landing's total of 10, to pin the ordinal tie-break.
            new(Today, CounterKeys.About, CounterSources.Direct, 10),
        };

        var summary = Summarize(counters);

        Assert.Equal(
            new[] { CounterKeys.About, CounterKeys.Landing },
            summary.ByPage.Select(p => p.Key));
        Assert.Equal(new[] { 10L, 10L }, summary.ByPage.Select(p => p.Count));
    }

    [Fact]
    public void Funnel_keys_never_enter_page_view_totals()
    {
        var counters = new List<DailyCounterFact>
        {
            new(Today, CounterKeys.Landing, CounterSources.Direct, 4),
            new(Today, CounterKeys.QueueJoin, CounterSources.Direct, 100),
            new(Today, CounterKeys.LearnComplete, CounterSources.Direct, 50),
        };

        var summary = Summarize(counters);

        Assert.Equal(4, summary.TotalPageViews);
        Assert.Equal([CounterKeys.Landing], summary.ByPage.Select(p => p.Key));
        var day = summary.Days.Single(d => d.Date == Today);
        Assert.Equal(4, day.PageViews);
        Assert.Equal([CounterKeys.Landing], day.Pages.Select(p => p.Key));
    }

    [Fact]
    public void First_game_finish_and_rematch_offer_never_enter_page_view_totals()
    {
        var counters = new List<DailyCounterFact>
        {
            new(Today, CounterKeys.Landing, CounterSources.Direct, 4),
            new(Today, CounterKeys.FirstGameFinish, CounterSources.Direct, 100),
            new(Today, CounterKeys.RematchOffer, CounterSources.Direct, 50),
        };

        var summary = Summarize(counters);

        Assert.Equal(4, summary.TotalPageViews);
        Assert.Equal([CounterKeys.Landing], summary.ByPage.Select(p => p.Key));
        var day = summary.Days.Single(d => d.Date == Today);
        Assert.Equal(4, day.PageViews);
        Assert.Equal([CounterKeys.Landing], day.Pages.Select(p => p.Key));
    }

    [Fact]
    public void SignupsPerHundredLandingViews_is_null_not_zero_with_no_landing_views()
    {
        var counters = new List<DailyCounterFact> { new(Today, CounterKeys.About, CounterSources.Direct, 100) };
        var signupsAt = new List<DateTimeOffset> { GeneratedAt };

        var summary = Summarize(counters, signupsAt: signupsAt);

        Assert.Null(summary.SignupsPerHundredLandingViews);
    }

    [Fact]
    public void SignupsPerHundredLandingViews_is_computed_against_landing_only()
    {
        var counters = new List<DailyCounterFact>
        {
            new(Today, CounterKeys.Landing, CounterSources.Direct, 10),
            new(Today, CounterKeys.About, CounterSources.Direct, 990), // must be ignored by the ratio
        };
        var signupsAt = new List<DateTimeOffset> { GeneratedAt };

        var summary = Summarize(counters, signupsAt: signupsAt);

        Assert.Equal(10.0, summary.SignupsPerHundredLandingViews);
    }

    [Fact]
    public void TotalSignups_uses_account_timestamps_not_the_signup_counter()
    {
        var counters = new List<DailyCounterFact> { new(Today, CounterKeys.Signup, CounterSources.Qr, 1) };
        var signupsAt = new List<DateTimeOffset> { GeneratedAt, GeneratedAt, GeneratedAt };

        var summary = Summarize(counters, signupsAt: signupsAt);

        Assert.Equal(3, summary.TotalSignups);
        Assert.Equal(1, summary.BySource.Sum(s => s.Signups));
    }

    [Fact]
    public void BySource_splits_page_views_and_keeps_a_signup_only_source()
    {
        var counters = new List<DailyCounterFact>
        {
            new(Today, CounterKeys.Landing, CounterSources.Reddit, 5),
            new(Today, CounterKeys.Landing, CounterSources.Direct, 3),
            new(Today, CounterKeys.Signup, CounterSources.Qr, 1),
        };

        var summary = Summarize(counters);

        var reddit = summary.BySource.Single(s => s.Source == CounterSources.Reddit);
        var direct = summary.BySource.Single(s => s.Source == CounterSources.Direct);
        var qr = summary.BySource.Single(s => s.Source == CounterSources.Qr);

        Assert.Equal(5, reddit.PageViews);
        Assert.Equal(0, reddit.Signups);
        Assert.Equal(3, direct.PageViews);
        Assert.Equal(0, qr.PageViews);
        Assert.Equal(1, qr.Signups);
    }

    [Fact]
    public void FirstSession_percentages_over_their_own_denominators()
    {
        var counters = new List<DailyCounterFact>
        {
            new(Today, CounterKeys.Learn, CounterSources.Direct, 10),
            new(Today, CounterKeys.LearnComplete, CounterSources.Direct, 4),
            new(Today, CounterKeys.QueueJoin, CounterSources.Direct, 8),
            new(Today, CounterKeys.QueueMatch, CounterSources.Direct, 6),
        };

        var summary = Summarize(counters);

        Assert.Equal(10, summary.FirstSession.LearnStarted);
        Assert.Equal(40.0, summary.FirstSession.LearnCompletionPercent);
        Assert.Equal(75.0, summary.FirstSession.QueueMatchPercent);

        // No learn-start rows at all this time: the rate must be null, not 0.
        var noLearn = Summarize([new DailyCounterFact(Today, CounterKeys.QueueJoin, CounterSources.Direct, 1)]);
        Assert.Equal(0, noLearn.FirstSession.LearnStarted);
        Assert.Null(noLearn.FirstSession.LearnCompletionPercent);
    }

    [Fact]
    public void GameFinishPercent_is_not_clamped_above_100()
    {
        var gamesStartedAt = new List<DateTimeOffset> { GeneratedAt, GeneratedAt };
        var gamesFinishedAt = new List<DateTimeOffset> { GeneratedAt, GeneratedAt, GeneratedAt };

        var summary = Summarize([], gamesStartedAt: gamesStartedAt, gamesFinishedAt: gamesFinishedAt);

        Assert.Equal(2, summary.FirstSession.GamesStarted);
        Assert.Equal(3, summary.FirstSession.GamesFinished);
        Assert.Equal(150.0, summary.FirstSession.GameFinishPercent);
    }

    [Fact]
    public void Games_are_bucketed_by_utc_date_not_local()
    {
        // 23:30 UTC on the day before Today - must land on that UTC calendar day, never the next one.
        var lateNightUtc = new DateTimeOffset(2026, 8, 17, 23, 30, 0, TimeSpan.Zero);
        var gamesStartedAt = new List<DateTimeOffset> { lateNightUtc };

        var summary = Summarize([], sinceDays: 3, gamesStartedAt: gamesStartedAt);

        var day = summary.Days.Single(d => d.Date == new DateOnly(2026, 8, 17));
        Assert.Equal(1, day.GamesStarted);
        Assert.DoesNotContain(summary.Days, d => d.Date == new DateOnly(2026, 8, 18) && d.GamesStarted > 0);
    }

    [Fact]
    public void Empty_input_returns_zero_filled_window_zeroes_and_null_rates_without_throwing()
    {
        var summary = Summarize([], sinceDays: 5);

        Assert.Equal(5, summary.Days.Count);
        Assert.All(summary.Days, d =>
        {
            Assert.Equal(0, d.PageViews);
            Assert.Equal(0, d.Signups);
            Assert.Equal(0, d.GamesStarted);
            Assert.Equal(0, d.GamesFinished);
            Assert.Empty(d.Pages);
        });
        Assert.Equal(0, summary.TotalPageViews);
        Assert.Equal(0, summary.TotalSignups);
        Assert.Null(summary.SignupsPerHundredLandingViews);
        Assert.Empty(summary.ByPage);
        Assert.Empty(summary.BySource);
        Assert.Null(summary.FirstSession.LearnCompletionPercent);
        Assert.Null(summary.FirstSession.QueueMatchPercent);
        Assert.Null(summary.FirstSession.GameFinishPercent);
    }
}
