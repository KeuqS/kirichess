using KiriChess.Server.Moderation.Detection;
using Xunit;

namespace KiriChess.Server.Tests;

/// <summary>
/// Covers the S-2-4 pair-concentration maths: the Insufficient gate, each of the two signals in
/// isolation, and the score/caveat contract. Pure and synchronous, same split as
/// <see cref="SandbaggingAnalyzerTests"/> - no Docker, no database.
/// </summary>
public sealed class PairConcentrationAnalyzerTests
{
    private static readonly Guid UserId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private const string DisplayName = "Test Player";
    private const string Pool = "steal1";
    private static readonly DateTimeOffset GeneratedAt = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset BaseDate = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static PairConcentrationGameFact Fact(
        int dayOffset, Guid opponentId, double before, double after) =>
        new(Guid.NewGuid(), Pool, BaseDate.AddDays(dayOffset), opponentId, before, after);

    private static DetectionReportDto Analyze(IReadOnlyList<PairConcentrationGameFact> facts) =>
        PairConcentrationAnalyzer.Analyze(UserId, DisplayName, Pool, facts, GeneratedAt);

    [Fact]
    public void Below_minimum_games_is_Insufficient_not_a_low_score()
    {
        var opponent = Guid.NewGuid();
        var facts = new List<PairConcentrationGameFact>();
        for (int i = 0; i < 19; i++)
            facts.Add(Fact(i, opponent, before: 1500, after: 1520)); // every game against the same opponent, all gains

        var report = Analyze(facts);

        Assert.Equal(DetectionVerdict.Insufficient, report.Verdict);
        Assert.Equal(0, report.Score);
        Assert.Empty(report.Signals);
        Assert.Equal(19, report.SampleSize);
    }

    [Fact]
    public void Opponents_spread_evenly_with_no_net_flow_scores_low()
    {
        // 40 games against 20 different opponents (2 each), rating held flat - an ordinary player's
        // pattern, not a feeder's.
        var facts = new List<PairConcentrationGameFact>();
        for (int i = 0; i < 40; i++)
        {
            var opponent = Guid.Parse($"000000{i % 20:D2}-0000-0000-0000-000000000000");
            facts.Add(Fact(i, opponent, before: 1500, after: 1500));
        }

        var report = Analyze(facts);

        Assert.Equal(DetectionVerdict.Unremarkable, report.Verdict);
        Assert.True(report.Score < 0.40, $"Expected a low score, got {report.Score}");
    }

    [Fact]
    public void Heavy_concentration_on_one_opponent_with_no_net_flow_is_not_full_score()
    {
        // 30 of 40 games (75%) against one opponent, but the subject wins and loses to them evenly -
        // a genuine rivalry, not feeding. Concentration alone should raise the score but net flow
        // should not add much, since the two directions cancel out.
        var facts = new List<PairConcentrationGameFact>();
        var rival = Guid.NewGuid();
        for (int i = 0; i < 30; i++)
        {
            bool up = i % 2 == 0;
            facts.Add(Fact(i, rival, before: 1500, after: up ? 1516 : 1484));
        }
        for (int i = 30; i < 40; i++)
            facts.Add(Fact(i, Guid.NewGuid(), before: 1500, after: 1500));

        var report = Analyze(facts);

        var concentration = Assert.Single(report.Signals, s => s.Name == "opponent-concentration");
        Assert.True(concentration.Contribution > 0, "75% concentration on one opponent must contribute.");

        var netFlow = Assert.Single(report.Signals, s => s.Name == "net-rating-flow");
        Assert.Equal(0, netFlow.Contribution, precision: 5);
    }

    [Fact]
    public void Heavy_concentration_with_one_sided_flow_reaches_Elevated()
    {
        // 35 of 40 games against one opponent, and the subject gains rating from every single one of
        // them - the feeding shape the finding describes: one account consistently taking rating from
        // one other account.
        var facts = new List<PairConcentrationGameFact>();
        var fedFrom = Guid.NewGuid();
        for (int i = 0; i < 35; i++)
            facts.Add(Fact(i, fedFrom, before: 1500 + i, after: 1500 + i + 16));
        for (int i = 35; i < 40; i++)
            facts.Add(Fact(i, Guid.NewGuid(), before: 1500, after: 1490));

        var report = Analyze(facts);

        Assert.Equal(DetectionVerdict.Elevated, report.Verdict);
        Assert.True(report.Score >= 0.70, $"Expected an elevated score, got {report.Score}");
        Assert.Contains(report.Caveats, c => c.Contains("same-person account linkage is not detectable by design", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Caveats_always_include_the_advisory_and_no_linkage_disclaimers()
    {
        var opponent = Guid.NewGuid();
        var facts = new List<PairConcentrationGameFact>();
        for (int i = 0; i < 25; i++)
            facts.Add(Fact(i, i % 5 == 0 ? opponent : Guid.NewGuid(), before: 1500, after: 1500));

        var report = Analyze(facts);

        Assert.Contains(report.Caveats, c => c.Contains("Advisory only", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(report.Caveats, c => c.Contains("not detectable by design", StringComparison.OrdinalIgnoreCase));
    }
}
