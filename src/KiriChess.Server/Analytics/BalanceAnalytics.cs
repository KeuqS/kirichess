using KiriChess.Server.Data.Entities;
using KiriChess.Server.Ratings;

namespace KiriChess.Server.Analytics;

/// <summary>
/// Turns a flat list of finished games into the Phase 8.3 balance summary. Pure and synchronous by
/// design: the balance maths is the part worth testing, and keeping it out of the query means it can
/// be tested with a literal list instead of a Postgres container.
/// </summary>
public static class BalanceAnalytics
{
    /// <summary>Bucket key for games played on a custom/unrated config (<c>RatingPool == null</c>).</summary>
    public const string CustomPool = "custom";

    /// <summary>Pool key of the all-pools roll-up.</summary>
    public const string AllPools = "all";

    /// <summary>
    /// Terminations that are counted but never enter a percentage. An abort, a no-show (6e.3) and a
    /// server restart (6c.3) are infrastructure and griefing artefacts, they say nothing about
    /// whether the variant is balanced, and letting them into the denominator means every ratio
    /// quietly sags whenever the server is redeployed.
    /// </summary>
    public static bool IsExcluded(GameFact f) =>
        f.Result is GameResult.Ongoing or GameResult.Aborted
        || f.Termination is TerminationReason.None or TerminationReason.NoShow or TerminationReason.ServerRestart
        || f.EndedAt is null;

    public static BalanceSummaryDto Summarize(
        IReadOnlyList<GameFact> facts,
        int sinceDays,
        bool ratedOnly,
        bool truncated,
        DateTimeOffset generatedAt)
    {
        // Overall is recomputed from every row rather than averaged from the per-pool DTOs below, a
        // weighted average of percentages would silently drift from the row-level truth as pool sizes
        // diverge, and this is the number the dashboard leads with.
        var overall = BuildPool(AllPools, facts);

        // Bucketed by the same key the game-creation path already writes (lowercase, or null for a
        // custom/unrated config), nothing here re-derives or normalises it.
        var byPool = facts.ToLookup(f => f.RatingPool ?? CustomPool);

        // Official pools in roadmap/RatingConstants order, custom last, and only pools that actually
        // have a row, an empty pool is noise on a tuning dashboard, not a data point.
        var poolKeys = RatingConstants.OfficialPools.Where(byPool.Contains)
            .Concat(byPool.Contains(CustomPool) ? [CustomPool] : Array.Empty<string>());

        var pools = poolKeys.Select(key => BuildPool(key, byPool[key].ToList())).ToList();

        return new BalanceSummaryDto(
            generatedAt, sinceDays, ratedOnly, overall.Games, overall.ExcludedGames, truncated, overall, pools);
    }

    private static BalancePoolDto BuildPool(string poolKey, IReadOnlyList<GameFact> bucketFacts)
    {
        int excludedGames = bucketFacts.Count(IsExcluded);
        var counted = bucketFacts.Where(f => !IsExcluded(f)).ToList();
        int games = counted.Count;

        // No counted games: every percentage/average is a defined 0, never NaN, a pool nobody has
        // played on a fresh variant must render as "no data yet", not crash the dashboard's JSON parse.
        if (games == 0)
        {
            return new BalancePoolDto(
                poolKey, 0, excludedGames, 0, 0, 0, 0, 0, 0, 0, 0, 0, Array.Empty<TerminationSliceDto>());
        }

        var plies = counted.Select(f => (double)f.PlyCount).ToList();
        // A restored game can in principle end up with EndedAt earlier than CreatedAt (a corrected
        // clock, a replayed recovery); a negative game length is nonsense, not a signal, so it clamps.
        var durations = counted
            .Select(f => Math.Max(0, (f.EndedAt!.Value - f.CreatedAt).TotalSeconds))
            .ToList();

        // Grouped on the enum (not the string) so "descending count, then reason ordinal" is a plain
        // int comparison, the string form only appears once, at the final projection.
        var terminations = counted
            .GroupBy(f => f.Termination)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => (int)g.Key)
            .Select(g => new TerminationSliceDto(g.Key.ToString(), g.Count(), Percent(g.Count(), games)))
            .ToList();

        return new BalancePoolDto(
            Pool: poolKey,
            Games: games,
            ExcludedGames: excludedGames,
            WhiteWinPercent: Percent(counted.Count(f => f.Result == GameResult.WhiteWins), games),
            BlackWinPercent: Percent(counted.Count(f => f.Result == GameResult.BlackWins), games),
            DrawPercent: Percent(counted.Count(f => f.Result == GameResult.Draw), games),
            CheckmatePercent: Percent(counted.Count(f => f.Termination == TerminationReason.Checkmate), games),
            FlagfallPercent: Percent(counted.Count(f => f.Termination == TerminationReason.Flagfall), games),
            AvgPlies: Math.Round(plies.Average(), 1),
            MedianPlies: Math.Round(Median(plies), 1),
            AvgDurationSeconds: Math.Round(durations.Average(), 1),
            MedianDurationSeconds: Math.Round(Median(durations), 1),
            Terminations: terminations);
    }

    private static double Percent(int count, int total) =>
        total == 0 ? 0 : Math.Round(100.0 * count / total, 1);

    /// <summary>Even-sized sets average the two middle values, per the pinned contract.</summary>
    private static double Median(List<double> values)
    {
        if (values.Count == 0) return 0;
        var sorted = values.OrderBy(v => v).ToList();
        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 1
            ? sorted[mid]
            : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }
}
