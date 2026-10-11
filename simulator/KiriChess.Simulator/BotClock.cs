namespace KiriChess.Simulator;

/// <summary>
/// Port of <c>ui/lib/botClock.ts</c>. Keep the numbers and clamp order in lockstep: the simulator
/// exists to spend think time the way live bots do, and a drift here would make pool stats describe
/// a different opponent than the one on the site.
/// </summary>
public sealed record ClockPolicy(
    double BudgetFraction,
    int MaxThinkMs,
    int MinThinkMs,
    int ReserveSeconds);

public sealed record BotTier(string Key, int MinElo, ClockPolicy Clock);

public static class BotClock
{
    public static readonly IReadOnlyList<BotTier> Tiers =
    [
        new("novice", 0, new ClockPolicy(0.12, 6000, 600, 0)),
        new("casual", 900, new ClockPolicy(0.08, 5000, 500, 2)),
        new("club", 1300, new ClockPolicy(0.055, 4000, 400, 5)),
        new("expert", 1700, new ClockPolicy(0.04, 3000, 350, 10)),
        new("master", 2100, new ClockPolicy(0.03, 2500, 300, 15)),
    ];

    public static BotTier TierForElo(int elo)
    {
        var match = Tiers[0];
        foreach (var tier in Tiers)
        {
            if (elo >= tier.MinElo) match = tier;
        }
        return match;
    }

    public readonly record struct ThinkTimeInput(
        int RemainingSeconds,
        int LegalMoveCount,
        int Elo,
        Func<double>? Random = null);

    /// <summary>
    /// How long the bot should appear to think, in milliseconds. Same shape as
    /// <c>simulatedThinkMs</c>: a fraction of remaining clock, scaled by complexity, jittered, then
    /// clamped by the tier and by what the clock can afford.
    /// </summary>
    public static int SimulatedThinkMs(ThinkTimeInput input)
    {
        var rand = input.Random ?? Random.Shared.NextDouble;
        var policy = TierForElo(input.Elo).Clock;

        var complexity = Clamp(input.LegalMoveCount / 30.0, 0.45, 1.6);
        var budgetMs = Math.Max(0, input.RemainingSeconds) * policy.BudgetFraction * complexity * 1000;
        var jittered = budgetMs * (0.65 + rand() * 0.7);
        var clamped = Clamp(jittered, policy.MinThinkMs, policy.MaxThinkMs);

        var spendableSeconds = Math.Max(0, input.RemainingSeconds - policy.ReserveSeconds);
        var affordableMs = Math.Max(0, spendableSeconds * 1000);

        // JS Math.round is half-away-from-zero; .NET's default MidpointRounding.ToEven is not.
        return (int)Math.Round(
            Clamp(clamped, 0, Math.Max(policy.MinThinkMs, affordableMs)),
            MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Whole seconds this harness charges for a wait of <paramref name="thinkMs"/>: floor, never
    /// negative.
    /// </summary>
    /// <remarks>
    /// This used to claim it matched <c>GameSession.ElapsedSeconds</c>, and since C-4-2 it no longer
    /// does. The live server floors the same way but keeps the sub-second remainder in a per-side
    /// carry and bills it on that side's next turn, so a run of sub-second replies costs a real bot
    /// time and costs a simulated one nothing. The simulator is therefore the conservative side of
    /// the difference: it understates think time, so it understates flag rate and overstates game
    /// duration, by up to a second per ply. Deliberately left alone here rather than fixed in
    /// passing, because carrying a remainder through <c>GameRunner</c> would move every published
    /// pool statistic, including the thinking-budget tables generated from
    /// <c>config/kirichess.rules.json</c>.
    /// </remarks>
    public static int ChargedThinkSeconds(int thinkMs) => Math.Max(0, thinkMs / 1000);

    private static double Clamp(double value, double min, double max) =>
        Math.Min(Math.Max(value, min), max);
}
