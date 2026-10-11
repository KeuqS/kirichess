using KiriChess.Server.Analytics;
using KiriChess.Server.Data.Entities;
using Xunit;

namespace KiriChess.Server.Tests;

/// <summary>
/// Covers the Phase 8.3 balance maths from Phase 8.3: the pinned exclusion set,
/// the per-pool/overall aggregation, and the numeric rules (rounding, medians, the zero-games
/// contract). Pure and synchronous, so this needs no Docker and no database.
/// </summary>
public sealed class BalanceAnalyticsTests
{
    private static readonly DateTimeOffset GeneratedAt = new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset BaseCreated = new(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);

    private static GameFact Fact(
        string? pool,
        GameResult result,
        TerminationReason termination,
        int plyCount = 40,
        double durationSeconds = 200,
        bool isRated = true,
        bool ended = true) =>
        new(
            pool,
            isRated,
            result,
            termination,
            plyCount,
            BaseCreated,
            ended ? BaseCreated.AddSeconds(durationSeconds) : null);

    [Fact]
    public void Excluded_rows_never_enter_a_percentage()
    {
        var facts = new List<GameFact>();
        facts.AddRange(Enumerable.Range(0, 2).Select(_ =>
            Fact("steal1", GameResult.WhiteWins, TerminationReason.Checkmate)));
        facts.AddRange(Enumerable.Range(0, 8).Select(_ =>
            Fact("steal1", GameResult.Aborted, TerminationReason.ServerRestart)));

        var summary = BalanceAnalytics.Summarize(facts, 90, false, false, GeneratedAt);
        var pool = Assert.Single(summary.Pools);

        Assert.Equal(2, pool.Games);
        Assert.Equal(8, pool.ExcludedGames);
        Assert.Equal(100, pool.CheckmatePercent);
    }

    [Theory]
    [InlineData(GameResult.WhiteWins, TerminationReason.NoShow)]
    [InlineData(GameResult.Aborted, TerminationReason.Abandoned)]
    [InlineData(GameResult.Ongoing, TerminationReason.None)]
    public void Noshow_aborted_and_ongoing_rows_are_excluded(GameResult result, TerminationReason termination)
    {
        var fact = Fact("steal1", result, termination);
        Assert.True(BalanceAnalytics.IsExcluded(fact));
    }

    [Fact]
    public void Row_with_null_endedAt_is_excluded()
    {
        var fact = Fact("steal1", GameResult.WhiteWins, TerminationReason.Checkmate, ended: false);
        Assert.True(BalanceAnalytics.IsExcluded(fact));
    }

    [Fact]
    public void Checkmate_vs_flagfall_split_on_a_realistic_mix()
    {
        var facts = new List<GameFact>();
        facts.AddRange(Enumerable.Range(0, 5).Select(_ => Fact("bounty1", GameResult.WhiteWins, TerminationReason.Checkmate)));
        facts.AddRange(Enumerable.Range(0, 3).Select(_ => Fact("bounty1", GameResult.BlackWins, TerminationReason.Flagfall)));
        facts.AddRange(Enumerable.Range(0, 2).Select(_ => Fact("bounty1", GameResult.Draw, TerminationReason.DrawByAgreement)));

        var summary = BalanceAnalytics.Summarize(facts, 90, false, false, GeneratedAt);
        var pool = Assert.Single(summary.Pools);

        Assert.Equal(10, pool.Games);
        Assert.Equal(50, pool.CheckmatePercent);
        Assert.Equal(30, pool.FlagfallPercent);
    }

    [Fact]
    public void White_win_rate_across_white_black_and_draw()
    {
        var facts = new List<GameFact>();
        facts.AddRange(Enumerable.Range(0, 6).Select(_ => Fact("steal2", GameResult.WhiteWins, TerminationReason.Checkmate)));
        facts.AddRange(Enumerable.Range(0, 3).Select(_ => Fact("steal2", GameResult.BlackWins, TerminationReason.Flagfall)));
        facts.Add(Fact("steal2", GameResult.Draw, TerminationReason.Stalemate));

        var summary = BalanceAnalytics.Summarize(facts, 90, false, false, GeneratedAt);
        var pool = Assert.Single(summary.Pools);

        Assert.Equal(60, pool.WhiteWinPercent);
        Assert.Equal(30, pool.BlackWinPercent);
        Assert.Equal(10, pool.DrawPercent);
    }

    [Fact]
    public void Null_rating_pool_lands_in_custom_and_sorts_after_official_pools()
    {
        var facts = new List<GameFact>
        {
            Fact(null, GameResult.WhiteWins, TerminationReason.Checkmate),
            Fact("burn2", GameResult.BlackWins, TerminationReason.Flagfall),
            Fact("bounty1", GameResult.Draw, TerminationReason.Stalemate),
        };

        var summary = BalanceAnalytics.Summarize(facts, 90, false, false, GeneratedAt);

        Assert.Equal(3, summary.Pools.Count);
        Assert.Equal(BalanceAnalytics.CustomPool, summary.Pools[^1].Pool);
        // "bounty1" precedes "burn2" in RatingConstants.OfficialPools, so it must sort first here.
        Assert.Equal("bounty1", summary.Pools[0].Pool);
        Assert.Equal("burn2", summary.Pools[1].Pool);
    }

    [Fact]
    public void Pools_with_no_rows_are_absent_and_present_pools_follow_official_order()
    {
        var facts = new List<GameFact>
        {
            Fact("burn2", GameResult.WhiteWins, TerminationReason.Checkmate),
            Fact("bounty1", GameResult.BlackWins, TerminationReason.Flagfall),
        };

        var summary = BalanceAnalytics.Summarize(facts, 90, false, false, GeneratedAt);

        Assert.Equal(new[] { "bounty1", "burn2" }, summary.Pools.Select(p => p.Pool));
    }

    [Fact]
    public void Overall_is_recomputed_from_rows_not_averaged_from_pools()
    {
        var facts = new List<GameFact>();
        // A small pool that is 100% white wins...
        facts.AddRange(Enumerable.Range(0, 2).Select(_ => Fact("burn1", GameResult.WhiteWins, TerminationReason.Checkmate)));
        // ...and a much larger pool that is 100% black wins. A naive average-of-pools would say 50/50;
        // the row-level truth is 8/10 black.
        facts.AddRange(Enumerable.Range(0, 8).Select(_ => Fact("steal3", GameResult.BlackWins, TerminationReason.Flagfall)));

        var summary = BalanceAnalytics.Summarize(facts, 90, false, false, GeneratedAt);

        Assert.Equal(BalanceAnalytics.AllPools, summary.Overall.Pool);
        Assert.Equal(10, summary.Overall.Games);
        Assert.Equal(20, summary.Overall.WhiteWinPercent);
        Assert.Equal(80, summary.Overall.BlackWinPercent);
    }

    [Fact]
    public void Averages_and_medians_including_even_sized_median_and_duration_clamp()
    {
        var facts = new List<GameFact>
        {
            Fact("steal1", GameResult.WhiteWins, TerminationReason.Checkmate, plyCount: 10, durationSeconds: 100),
            Fact("steal1", GameResult.WhiteWins, TerminationReason.Checkmate, plyCount: 20, durationSeconds: 200),
            Fact("steal1", GameResult.WhiteWins, TerminationReason.Checkmate, plyCount: 30, durationSeconds: 300),
            // EndedAt earlier than CreatedAt: a negative duration must clamp to zero, not go negative.
            Fact("steal1", GameResult.WhiteWins, TerminationReason.Checkmate, plyCount: 40, durationSeconds: -50),
        };

        var summary = BalanceAnalytics.Summarize(facts, 90, false, false, GeneratedAt);
        var pool = Assert.Single(summary.Pools);

        Assert.Equal(25, pool.AvgPlies); // (10+20+30+40)/4
        Assert.Equal(25, pool.MedianPlies); // even-sized: mean of the two middle values (20, 30)
        Assert.Equal(150, pool.AvgDurationSeconds); // (100+200+300+0)/4
        Assert.Equal(150, pool.MedianDurationSeconds); // mean of (100, 200)
    }

    [Fact]
    public void Empty_input_reports_zeros_everywhere_and_no_nan()
    {
        var summary = BalanceAnalytics.Summarize(Array.Empty<GameFact>(), 90, false, false, GeneratedAt);

        Assert.Equal(0, summary.TotalGames);
        Assert.Equal(0, summary.ExcludedGames);
        Assert.Empty(summary.Pools);
        Assert.Equal(0, summary.Overall.WhiteWinPercent);
        Assert.Equal(0, summary.Overall.BlackWinPercent);
        Assert.Equal(0, summary.Overall.DrawPercent);
        Assert.Equal(0, summary.Overall.CheckmatePercent);
        Assert.Equal(0, summary.Overall.FlagfallPercent);
        Assert.Equal(0, summary.Overall.AvgPlies);
        Assert.Equal(0, summary.Overall.MedianPlies);
        Assert.Equal(0, summary.Overall.AvgDurationSeconds);
        Assert.Equal(0, summary.Overall.MedianDurationSeconds);
        Assert.Empty(summary.Overall.Terminations);
    }

    [Fact]
    public void Terminations_are_ordered_by_count_descending_then_reason_ascending()
    {
        var facts = new List<GameFact>();
        facts.AddRange(Enumerable.Range(0, 3).Select(_ => Fact("steal1", GameResult.WhiteWins, TerminationReason.Checkmate)));
        facts.AddRange(Enumerable.Range(0, 3).Select(_ => Fact("steal1", GameResult.Draw, TerminationReason.DrawByFiftyMove)));
        facts.Add(Fact("steal1", GameResult.BlackWins, TerminationReason.Flagfall));

        var summary = BalanceAnalytics.Summarize(facts, 90, false, false, GeneratedAt);
        var pool = Assert.Single(summary.Pools);

        // Checkmate and DrawByFiftyMove tie at 3; Checkmate has the lower enum ordinal, so it sorts first.
        Assert.Equal(
            new[] { nameof(TerminationReason.Checkmate), nameof(TerminationReason.DrawByFiftyMove), nameof(TerminationReason.Flagfall) },
            pool.Terminations.Select(t => t.Reason));
        Assert.Equal(new[] { 3, 3, 1 }, pool.Terminations.Select(t => t.Count));
    }

    [Fact]
    public void RatedOnly_sinceDays_and_truncated_are_echoed_back_unchanged()
    {
        var summary = BalanceAnalytics.Summarize(Array.Empty<GameFact>(), 30, true, true, GeneratedAt);

        Assert.Equal(30, summary.SinceDays);
        Assert.True(summary.RatedOnly);
        Assert.True(summary.Truncated);
        Assert.Equal(GeneratedAt, summary.GeneratedAt);
    }
}
