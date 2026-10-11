using KiriChess.Server.Data.Entities;
using KiriChess.Server.Moderation.Detection;
using Xunit;

namespace KiriChess.Server.Tests;

/// <summary>
/// Covers the Phase 8.5 Task B sandbagging maths from Phase 8.5:
/// the Insufficient gate, each of the four signals in isolation, and the score/caveat contract. Pure
/// and synchronous, so this needs no Docker and no database - the same split
/// <see cref="Analytics.BalanceAnalytics"/> and <see cref="BalanceAnalyticsTests"/> use.
/// </summary>
public sealed class SandbaggingAnalyzerTests
{
    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private const string DisplayName = "Test Player";
    private const string Pool = "steal1";
    private static readonly DateTimeOffset GeneratedAt = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset BaseDate = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static SandbaggingGameFact Fact(
        int dayOffset, bool won, double before, double after, double opponentBefore,
        int plyCount = 40, TerminationReason termination = TerminationReason.Checkmate) =>
        new(Guid.NewGuid(), Pool, BaseDate.AddDays(dayOffset), won, before, after, opponentBefore, plyCount, termination);

    private static DetectionReportDto Analyze(IReadOnlyList<SandbaggingGameFact> facts) =>
        SandbaggingAnalyzer.Analyze(UserId, DisplayName, Pool, facts, GeneratedAt);

    [Fact]
    public void Below_minimum_games_is_Insufficient_not_a_low_score()
    {
        // 19 games, blatant sandbagging shape: every loss to a much weaker opponent, every win over a
        // much stronger one. Still one below MinimumGames, so this must read as "we cannot say", not as
        // a low score - the whole point of the gate.
        var facts = new List<SandbaggingGameFact>();
        for (int i = 0; i < 10; i++)
            facts.Add(Fact(i, won: true, before: 1500, after: 1520, opponentBefore: 1700, plyCount: 60));
        for (int i = 10; i < 19; i++)
            facts.Add(Fact(i, won: false, before: 1500, after: 1480, opponentBefore: 1300, plyCount: 8, termination: TerminationReason.Resignation));

        Assert.Equal(19, facts.Count);
        var report = Analyze(facts);

        Assert.Equal(DetectionVerdict.Insufficient, report.Verdict);
        Assert.Equal(0, report.Score);
        Assert.Empty(report.Signals);
        Assert.Equal(19, report.SampleSize);
    }

    [Fact]
    public void A_consistent_player_scores_low()
    {
        // 40 games, opponent rating always close to the subject's own (never a 150+ gap either way),
        // flat rating (no drawdown), even ply counts, and a mix of terminations - the shape of an
        // ordinary player, not a sandbagger.
        var facts = new List<SandbaggingGameFact>();
        for (int i = 0; i < 40; i++)
        {
            bool won = i % 2 == 0;
            var termination = (i % 4) switch
            {
                0 => TerminationReason.Checkmate,
                1 => TerminationReason.Flagfall,
                2 => TerminationReason.Resignation,
                _ => TerminationReason.Checkmate,
            };
            int plies = 35 + (i % 5) * 3; // 35..47, both wins and losses draw from the same range
            facts.Add(Fact(i, won, before: 1500, after: 1500, opponentBefore: 1480 + (i % 3) * 10, plyCount: plies, termination: termination));
        }

        var report = Analyze(facts);

        Assert.Equal(DetectionVerdict.Unremarkable, report.Verdict);
        Assert.True(report.Score < 0.40, $"Expected a low score, got {report.Score}");
    }

    [Fact]
    public void Losing_to_weaker_while_beating_stronger_dominates_the_score()
    {
        // 21 losses (9 to opponents 150+ below), 24 wins (11 over opponents 150+ above) - the plan's
        // own worked example. Rating held flat so no drawdown fires, ply counts and terminations kept
        // identical between wins and losses so neither of the other two signals fires either.
        var facts = new List<SandbaggingGameFact>();
        int day = 0;
        for (int i = 0; i < 9; i++)
            facts.Add(Fact(day++, won: false, before: 1500, after: 1500, opponentBefore: 1300, plyCount: 40));
        for (int i = 0; i < 12; i++)
            facts.Add(Fact(day++, won: false, before: 1500, after: 1500, opponentBefore: 1500, plyCount: 40));
        for (int i = 0; i < 11; i++)
            facts.Add(Fact(day++, won: true, before: 1500, after: 1500, opponentBefore: 1700, plyCount: 40));
        for (int i = 0; i < 13; i++)
            facts.Add(Fact(day++, won: true, before: 1500, after: 1500, opponentBefore: 1500, plyCount: 40));

        Assert.Equal(21, facts.Count(f => !f.Won));
        Assert.Equal(24, facts.Count(f => f.Won));

        var report = Analyze(facts);

        var lossesToWeaker = Assert.Single(report.Signals, s => s.Name == "losses-to-weaker");
        var largest = report.Signals.OrderByDescending(s => s.Contribution).First();
        Assert.Equal("losses-to-weaker", largest.Name);
        Assert.True(lossesToWeaker.Contribution > 0, "losses-to-weaker must actually contribute here");
    }

    [Fact]
    public void A_strong_player_who_only_beats_weaker_opponents_is_not_flagged()
    {
        // The normal shape of a strong player: wins over weaker opponents, losses to stronger ones.
        // Neither half of losses-to-weaker's product can fire from this pattern.
        var facts = new List<SandbaggingGameFact>();
        int day = 0;
        for (int i = 0; i < 30; i++)
            facts.Add(Fact(day++, won: true, before: 1800, after: 1800, opponentBefore: 1550, plyCount: 45));
        for (int i = 0; i < 10; i++)
            facts.Add(Fact(day++, won: false, before: 1800, after: 1800, opponentBefore: 2000, plyCount: 45));

        var report = Analyze(facts);

        Assert.True(report.Score < 0.40, $"Expected a strong-player pattern to score low, got {report.Score}");
    }

    [Fact]
    public void One_deep_drawdown_does_not_trigger_the_cluster_signal()
    {
        // One drawdown of depth 500 that fully recovers on the very next game. A single instance is an
        // anecdote, not a pattern - the plan requires the contribution forced to zero below 2 deep
        // drawdowns, regardless of how clean the ratio looks.
        var facts = new List<SandbaggingGameFact>();
        int day = 0;

        for (int i = 0; i < 5; i++)
        {
            double before = 1000 + 100 * i;
            facts.Add(Fact(day++, won: true, before, after: before + 100, opponentBefore: before - 200, plyCount: 40));
        }
        // Peak is now 1500. Three losses drop it to 1200 (depth 300, deep).
        for (int i = 0; i < 3; i++)
        {
            double before = 1500 - 100 * i;
            facts.Add(Fact(day++, won: false, before, after: before - 100, opponentBefore: before + 200, plyCount: 40, termination: TerminationReason.Resignation));
        }
        // Immediate full recovery.
        facts.Add(Fact(day++, won: true, before: 1200, after: 1500, opponentBefore: 1000, plyCount: 40));
        // Pad well past MinimumGames with plain wins that never form a second drawdown.
        for (int i = 0; i < 13; i++)
            facts.Add(Fact(day++, won: true, before: 1500, after: 1500, opponentBefore: 1300, plyCount: 40));

        Assert.True(facts.Count >= SandbaggingAnalyzer.MinimumGames);

        var report = Analyze(facts);

        var cluster = Assert.Single(report.Signals, s => s.Name == "loss-cluster-after-peak");
        Assert.Equal(0, cluster.Contribution);
    }

    [Fact]
    public void Short_losses_signal_uses_medians_not_means()
    {
        // 12 losses at 20 plies plus one 400-ply outlier: the mean would be dragged past the 0.65
        // cutoff and read as "not short", but the median - 20 of 13 values - is untouched by the
        // outlier and still reads as a real signal.
        var facts = new List<SandbaggingGameFact>();
        int day = 0;
        for (int i = 0; i < 12; i++)
            facts.Add(Fact(day++, won: false, before: 1500, after: 1500, opponentBefore: 1500, plyCount: 20, termination: TerminationReason.Resignation));
        facts.Add(Fact(day++, won: false, before: 1500, after: 1500, opponentBefore: 1500, plyCount: 400));
        for (int i = 0; i < 15; i++)
            facts.Add(Fact(day++, won: true, before: 1500, after: 1500, opponentBefore: 1500, plyCount: 60));

        var report = Analyze(facts);

        var shortLosses = Assert.Single(report.Signals, s => s.Name == "short-losses");
        // Ratio of median loss plies (20, from the twelve 20-ply losses) to median win plies (60) - a
        // mean loss-ply figure would have been dragged to ~49.2 by the 400-ply outlier and produced a
        // ratio near 0.82, which would not register as short losses at all.
        Assert.Equal(20.0 / 60.0, shortLosses.Value, precision: 5);
        Assert.True(shortLosses.Contribution > 0, "median-based ratio must still register as short losses");
    }

    [Fact]
    public void Every_report_carries_the_unvalidated_thresholds_caveat()
    {
        const string expected =
            "Thresholds are unvalidated: no confirmed sandbagger has ever played on this site, so every cutoff here is a reasoned guess, not a tuned one.";

        var insufficientFacts = new List<SandbaggingGameFact>();
        for (int i = 0; i < 5; i++)
            insufficientFacts.Add(Fact(i, won: i % 2 == 0, before: 1500, after: 1500, opponentBefore: 1500, plyCount: 40));
        var insufficientReport = Analyze(insufficientFacts);
        Assert.Contains(expected, insufficientReport.Caveats);

        var normalFacts = new List<SandbaggingGameFact>();
        for (int i = 0; i < 40; i++)
            normalFacts.Add(Fact(i, won: i % 2 == 0, before: 1500, after: 1500, opponentBefore: 1500, plyCount: 40));
        var normalReport = Analyze(normalFacts);
        Assert.Contains(expected, normalReport.Caveats);
    }

    [Fact]
    public void Evidence_strings_contain_the_measured_numbers()
    {
        // A combined pattern that lights up three of the four signals at once, each with a distinct,
        // known count, so each signal's Evidence can be checked against its own numbers rather than a
        // generic sentence.
        var facts = new List<SandbaggingGameFact>();
        int day = 0;
        for (int i = 0; i < 6; i++) // losses to weaker, resigned - 6 of the 9 thrown losses
            facts.Add(Fact(day++, won: false, before: 1500, after: 1500, opponentBefore: 1300, plyCount: 15, termination: TerminationReason.Resignation));
        for (int i = 0; i < 6; i++) // other losses, played out - not thrown
            facts.Add(Fact(day++, won: false, before: 1500, after: 1500, opponentBefore: 1500, plyCount: 15, termination: TerminationReason.Checkmate));
        for (int i = 0; i < 3; i++) // other losses, resigned - the remaining 3 of the 9 thrown losses
            facts.Add(Fact(day++, won: false, before: 1500, after: 1500, opponentBefore: 1500, plyCount: 15, termination: TerminationReason.Resignation));
        for (int i = 0; i < 5; i++) // wins over stronger
            facts.Add(Fact(day++, won: true, before: 1500, after: 1500, opponentBefore: 1700, plyCount: 50));
        for (int i = 0; i < 10; i++) // other wins
            facts.Add(Fact(day++, won: true, before: 1500, after: 1500, opponentBefore: 1500, plyCount: 50));

        Assert.Equal(15, facts.Count(f => !f.Won));
        Assert.Equal(15, facts.Count(f => f.Won));

        var report = Analyze(facts);

        var lossesToWeaker = Assert.Single(report.Signals, s => s.Name == "losses-to-weaker");
        Assert.True(lossesToWeaker.Contribution > 0);
        Assert.Contains("6", lossesToWeaker.Evidence);
        Assert.Contains("15", lossesToWeaker.Evidence);
        Assert.Contains("5", lossesToWeaker.Evidence);

        var shortLosses = Assert.Single(report.Signals, s => s.Name == "short-losses");
        Assert.True(shortLosses.Contribution > 0);
        Assert.Contains("15", shortLosses.Evidence);
        Assert.Contains("50", shortLosses.Evidence);

        var terminationSkew = Assert.Single(report.Signals, s => s.Name == "loss-termination-skew");
        Assert.True(terminationSkew.Contribution > 0);
        Assert.Contains("9", terminationSkew.Evidence);
        Assert.Contains("15", terminationSkew.Evidence);
    }

    [Fact]
    public void Score_is_clamped_to_one()
    {
        // Every signal pushed to its maximum simultaneously: all losses to opponents 150+ weaker, all
        // wins over opponents 150+ stronger, two deep drawdowns that both fully recover, losses far
        // shorter than wins, and every loss thrown by resignation. The four weights sum to exactly 1.0,
        // so this is the ceiling case for the final Math.Clamp(score, 0, 1).
        var facts = new List<SandbaggingGameFact>();
        int day = 0;

        for (int i = 0; i < 10; i++) // climb to a peak of 2000, all wins over stronger opponents
        {
            double before = 1000 + 100 * i;
            facts.Add(Fact(day++, won: true, before, after: before + 100, opponentBefore: before + 200, plyCount: 60));
        }
        for (int i = 0; i < 5; i++) // first deep drawdown: 2000 -> 1500, depth 500
        {
            double before = 2000 - 100 * i;
            facts.Add(Fact(day++, won: false, before, after: before - 100, opponentBefore: before - 200, plyCount: 5, termination: TerminationReason.Resignation));
        }
        facts.Add(Fact(day++, won: true, before: 1500, after: 2000, opponentBefore: 1700, plyCount: 60)); // immediate recovery
        for (int i = 0; i < 5; i++) // second deep drawdown: 2000 -> 1500, depth 500
        {
            double before = 2000 - 100 * i;
            facts.Add(Fact(day++, won: false, before, after: before - 100, opponentBefore: before - 200, plyCount: 5, termination: TerminationReason.Resignation));
        }
        facts.Add(Fact(day++, won: true, before: 1500, after: 2000, opponentBefore: 1700, plyCount: 60)); // immediate recovery

        Assert.True(facts.Count >= SandbaggingAnalyzer.MinimumGames);

        var report = Analyze(facts);

        Assert.True(report.Score <= 1.0, $"Score must never exceed 1.0, got {report.Score}");
        Assert.Equal(DetectionVerdict.Elevated, report.Verdict);
    }
}
