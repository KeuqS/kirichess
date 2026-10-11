namespace KiriChess.Simulator.Tests;

/// <summary>
/// Mirrors <c>ui/lib/botClock.test.ts</c>. If one of these fails after a TypeScript edit, the C#
/// port drifted and pool simulations no longer spend time the way live bots do.
/// </summary>
public class BotClockTests
{
    private static double NoJitter() => 0.5;

    [Theory]
    [InlineData(0, "novice")]
    [InlineData(-100, "novice")]
    [InlineData(899, "novice")]
    [InlineData(900, "casual")]
    [InlineData(1299, "casual")]
    [InlineData(1300, "club")]
    [InlineData(1699, "club")]
    [InlineData(1700, "expert")]
    [InlineData(2099, "expert")]
    [InlineData(2100, "master")]
    [InlineData(5000, "master")]
    public void TierForElo_matches_the_client_bands(int elo, string key) =>
        Assert.Equal(key, BotClock.TierForElo(elo).Key);

    [Fact]
    public void SimulatedThinkMs_never_exceeds_the_tier_max()
    {
        var ms = BotClock.SimulatedThinkMs(new BotClock.ThinkTimeInput(
            RemainingSeconds: 100000,
            LegalMoveCount: 60,
            Elo: 0,
            Random: NoJitter));
        Assert.True(ms <= 6000);
    }

    [Fact]
    public void SimulatedThinkMs_never_spends_past_the_reserve()
    {
        var policy = BotClock.TierForElo(2100).Clock;
        const int remainingSeconds = 20;
        var ms = BotClock.SimulatedThinkMs(new BotClock.ThinkTimeInput(
            RemainingSeconds: remainingSeconds,
            LegalMoveCount: 30,
            Elo: 2100,
            Random: NoJitter));
        var affordableMs = (remainingSeconds - policy.ReserveSeconds) * 1000;
        Assert.True(ms <= Math.Max(policy.MinThinkMs, affordableMs));
    }

    [Fact]
    public void SimulatedThinkMs_floors_at_minThinkMs_when_the_clock_cannot_afford_it()
    {
        var policy = BotClock.TierForElo(2100).Clock;
        var ms = BotClock.SimulatedThinkMs(new BotClock.ThinkTimeInput(
            RemainingSeconds: 1,
            LegalMoveCount: 30,
            Elo: 2100,
            Random: NoJitter));
        Assert.Equal(policy.MinThinkMs, ms);
    }

    [Fact]
    public void SimulatedThinkMs_gives_wider_positions_at_least_as_much_time()
    {
        var narrow = BotClock.SimulatedThinkMs(new BotClock.ThinkTimeInput(300, 5, 1300, NoJitter));
        var wide = BotClock.SimulatedThinkMs(new BotClock.ThinkTimeInput(300, 45, 1300, NoJitter));
        Assert.True(wide >= narrow);
    }

    [Fact]
    public void SimulatedThinkMs_is_deterministic_given_a_fixed_random_source()
    {
        var a = BotClock.SimulatedThinkMs(new BotClock.ThinkTimeInput(180, 30, 1500, () => 0.5));
        var b = BotClock.SimulatedThinkMs(new BotClock.ThinkTimeInput(180, 30, 1500, () => 0.5));
        Assert.Equal(a, b);
    }

    [Fact]
    public void SimulatedThinkMs_jitter_stays_in_the_documented_band_before_clamping()
    {
        const int remainingSeconds = 30;
        var policy = BotClock.TierForElo(1700).Clock;
        const int legalMoveCount = 30;
        var budgetMs = remainingSeconds * policy.BudgetFraction * 1.0 * 1000;
        Assert.True(budgetMs * 1.35 < policy.MaxThinkMs);
        Assert.True(budgetMs * 0.65 > policy.MinThinkMs);

        var low = BotClock.SimulatedThinkMs(new BotClock.ThinkTimeInput(remainingSeconds, legalMoveCount, 1700, () => 0));
        var high = BotClock.SimulatedThinkMs(new BotClock.ThinkTimeInput(remainingSeconds, legalMoveCount, 1700, () => 0.999999));

        Assert.True(low >= Math.Round(budgetMs * 0.65) - 1);
        Assert.True(high <= Math.Round(budgetMs * 1.35) + 1);
        Assert.True(high > low);
    }

    [Fact]
    public void Tiers_strengthen_by_spending_less_and_reserving_more()
    {
        for (var i = 1; i < BotClock.Tiers.Count; i++)
        {
            Assert.True(BotClock.Tiers[i].MinElo > BotClock.Tiers[i - 1].MinElo);
            Assert.True(BotClock.Tiers[i].Clock.BudgetFraction < BotClock.Tiers[i - 1].Clock.BudgetFraction);
            Assert.True(BotClock.Tiers[i].Clock.ReserveSeconds >= BotClock.Tiers[i - 1].Clock.ReserveSeconds);
        }
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(999, 0)]
    [InlineData(1000, 1)]
    [InlineData(2500, 2)]
    [InlineData(3500, 3)]
    // Named for what it actually pins: this harness floors each ply on its own. GameSession stopped
    // doing that at C-4-2, when it started carrying the remainder forward. See BotClock's remarks.
    public void ChargedThinkSeconds_floors_each_ply_independently(int thinkMs, int seconds) =>
        Assert.Equal(seconds, BotClock.ChargedThinkSeconds(thinkMs));
}

public class BotSkillTests
{
    [Fact]
    public void Below_800_blends_in_random_moves()
    {
        Assert.Equal(1.0, BotSkill.ForElo(400).RandomChance, 5);
        Assert.Equal(0.0, BotSkill.ForElo(800).RandomChance, 5);
        Assert.True(BotSkill.ForElo(600).RandomChance is > 0 and < 1);
    }

    [Fact]
    public void Maps_elo_to_the_client_skill_and_depth_table()
    {
        Assert.Equal(0, BotSkill.ForElo(800).SkillLevel);
        Assert.Equal(1, BotSkill.ForElo(900).Depth);
        Assert.Equal(8, BotSkill.ForElo(2100).Depth);
        Assert.Equal(15, BotSkill.ForElo(2700).Depth);
        Assert.Equal(20, BotSkill.ForElo(3000).SkillLevel);
        Assert.Equal(15, BotSkill.ForElo(3000).Depth);
    }
}
