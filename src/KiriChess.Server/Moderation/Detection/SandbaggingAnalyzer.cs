using KiriChess.Server.Data.Entities;
using KiriChess.Server.Ratings;

namespace KiriChess.Server.Moderation.Detection;

/// <summary>
/// One rated, decisive game as the sandbagging analyzer sees it (Phase 8.5 Task B). Flat, engine-free
/// and DB-free so the scoring can be tested with a literal list - the same split
/// <see cref="Analytics.BalanceAnalytics"/> uses (Phase 8.3) and for the same reason: the maths is the
/// part worth testing, and keeping it out of the query means it can be tested without a Postgres
/// container.
/// </summary>
/// <remarks>
/// <see cref="SandbaggingEndpoints"/> excludes draws, unrated/custom games, solo games and the same
/// infrastructure terminations <see cref="Analytics.BalanceAnalytics.IsExcluded"/> excludes (NoShow,
/// ServerRestart) before this record is ever built. A draw has no "loser", so the drawdown and
/// short-loss maths below both need every fact to be decisive.
/// </remarks>
public sealed record SandbaggingGameFact(
    Guid GameId,
    string Pool,
    DateTimeOffset EndedAt,
    /// <summary>True when the subject won, false when they lost. Draws are excluded before this record is built.</summary>
    bool Won,
    double SubjectRatingBefore,
    double SubjectRatingAfter,
    double OpponentRatingBefore,
    int PlyCount,
    TerminationReason Termination);

/// <summary>
/// Phase 8.5 Task B: scores an account for the sandbagging shape - inconsistency, not excellence. See
/// Phase 8.5 for the reasoning behind every threshold below;
/// each one is a named, unvalidated guess rather than a tuned constant, because no confirmed
/// sandbagger has ever played on this site. Advisory only - see Phase 8.5 scope
/// decision 1. Nothing here writes a UserSanction, changes a rating, or blocks a queue join.
///
/// Pure and synchronous by design, matching <see cref="Analytics.BalanceAnalytics"/>: no DbContext, no
/// IServiceProvider, no clock service, no I/O. <paramref name="facts"/> must arrive sorted oldest
/// first - the drawdown-recovery walk depends on chronological order and does not re-sort, the same
/// way BalanceAnalytics.Summarize trusts its caller's projection.
/// </summary>
public static class SandbaggingAnalyzer
{
    /// <summary>
    /// Below this many decisive rated games in a pool, no signal here means anything: a 6-game player
    /// who lost 4 looks identical to a sandbagger and is almost certainly just new. Returning
    /// Insufficient is deliberately different from returning a low score - "we cannot say" is not "they
    /// are clean". This number is a guess. What would tune it: the observed variance of real players'
    /// first 50 games, which needs real players.
    /// </summary>
    public const int MinimumGames = 20;

    private const string UnvalidatedThresholdsCaveat =
        "Thresholds are unvalidated: no confirmed sandbagger has ever played on this site, so every cutoff here is a reasoned guess, not a tuned one.";

    private const string AdvisoryOnlyCaveat =
        "Advisory only. This score has no automatic effect on the account.";

    public static DetectionReportDto Analyze(
        Guid userId, string displayName, string pool,
        IReadOnlyList<SandbaggingGameFact> facts,
        DateTimeOffset generatedAt)
    {
        if (facts.Count < MinimumGames)
        {
            return new DetectionReportDto(
                userId, displayName, "sandbagging", pool,
                DetectionVerdict.Insufficient, 0, facts.Count,
                Array.Empty<DetectionSignalDto>(),
                new[]
                {
                    $"Only {facts.Count} decisive rated games in this window; {MinimumGames} are needed before any signal here means anything.",
                    UnvalidatedThresholdsCaveat,
                },
                generatedAt);
        }

        var lossesToWeaker = LossesToWeaker(facts);
        var lossCluster = LossClusterAfterPeak(facts);
        var shortLosses = ShortLosses(facts);
        var terminationSkew = LossTerminationSkew(facts);

        var signals = new[] { lossesToWeaker, lossCluster, shortLosses, terminationSkew };
        double score = Math.Clamp(signals.Sum(s => s.Contribution), 0, 1);

        var caveats = new List<string> { UnvalidatedThresholdsCaveat, AdvisoryOnlyCaveat };

        // Fallback for "any rating in the window is provisional": SandbaggingGameFact does not carry
        // the subject's RD (carrying it was the alternative the plan offers; this repo chose not to,
        // so the fact record stays exactly the shape the query naturally projects). A rated game's
        // ValueBefore lands on exactly RatingConstants.DefaultRating only on that player's first-ever
        // rated game in the pool - nothing else can coincidentally hit that value once ratings start
        // moving - so its presence in the earliest slice of the window is a defensible proxy for "this
        // window includes early, still-settling ratings".
        if (facts.Take(Math.Min(30, facts.Count)).Any(f => f.SubjectRatingBefore == RatingConstants.DefaultRating))
            caveats.Add("Part of this window covers provisional ratings, where large swings are normal.");

        if (facts.Count < 40)
            caveats.Add($"Only {facts.Count} games. Signals at this sample size move a long way on one game.");

        double spanDays = (facts[^1].EndedAt - facts[0].EndedAt).TotalDays;
        if (spanDays < 14)
            caveats.Add(
                $"All {facts.Count} games fall inside a {spanDays:0.#}-day window; a burst of games is a weaker basis than the same count over months.");

        return new DetectionReportDto(
            userId, displayName, "sandbagging", pool,
            DetectionVerdict.For(score), score, facts.Count,
            signals, caveats.ToArray(), generatedAt);
    }

    /// <summary>
    /// The strongest signal (weight 0.40): losing to opponents rated well below the subject while
    /// beating opponents rated well above them, in the same window. Either half alone is just a normal
    /// player - a bad one, or an underrated one - the product of both rates is what makes this two
    /// different strengths in one account, which a real player does not have.
    /// </summary>
    private static DetectionSignalDto LossesToWeaker(IReadOnlyList<SandbaggingGameFact> facts)
    {
        int totalLosses = facts.Count(f => !f.Won);
        int totalWins = facts.Count(f => f.Won);
        int lossesToWeakerCount = facts.Count(f => !f.Won && f.SubjectRatingBefore - f.OpponentRatingBefore >= 150);
        int winsOverStrongerCount = facts.Count(f => f.Won && f.SubjectRatingBefore - f.OpponentRatingBefore <= -150);

        double lossRate = totalLosses == 0 ? 0 : (double)lossesToWeakerCount / totalLosses;
        double winRate = totalWins == 0 ? 0 : (double)winsOverStrongerCount / totalWins;
        double value = lossRate * winRate;

        double contribution = Math.Clamp(value / 0.15, 0, 1) * 0.40;

        string evidence =
            $"Lost {lossesToWeakerCount} of {totalLosses} games to opponents rated 150+ below them, " +
            $"while winning {winsOverStrongerCount} of {totalWins} against opponents rated 150+ above.";

        return new DetectionSignalDto("losses-to-weaker", "Losses to weaker opponents", value, contribution, evidence);
    }

    /// <summary>
    /// Weight 0.30. Walks the rating timeline in order, tracking the running peak. A drawdown is a
    /// maximal run of consecutive losses; it is "deep" at 100+ points and "recovered" if the rating
    /// returns to within 30 points of the pre-run peak inside the next 10 games. A deep drop that
    /// reliably bounces back on schedule is the sandbagging shape; a deep drop that never recovers is
    /// just a player who got worse or quit, which is a different claim.
    /// </summary>
    private static DetectionSignalDto LossClusterAfterPeak(IReadOnlyList<SandbaggingGameFact> facts)
    {
        var drawdownDepths = new List<double>();
        var drawdownRecovered = new List<bool>();

        double peak = facts[0].SubjectRatingBefore;
        int i = 0;
        while (i < facts.Count)
        {
            if (facts[i].Won)
            {
                peak = Math.Max(peak, facts[i].SubjectRatingAfter);
                i++;
                continue;
            }

            double peakBeforeRun = peak;
            int runEnd = i;
            while (runEnd < facts.Count && !facts[runEnd].Won) runEnd++;
            runEnd--; // last loss index in the run

            double ratingAfterRun = facts[runEnd].SubjectRatingAfter;
            double depth = peakBeforeRun - ratingAfterRun;

            bool recovered = false;
            for (int j = runEnd + 1; j < Math.Min(facts.Count, runEnd + 1 + 10); j++)
            {
                if (facts[j].SubjectRatingAfter >= peakBeforeRun - 30) { recovered = true; break; }
            }

            drawdownDepths.Add(depth);
            drawdownRecovered.Add(recovered);
            peak = Math.Max(peak, ratingAfterRun);
            i = runEnd + 1;
        }

        int deepCount = 0, deepRecoveredCount = 0;
        for (int d = 0; d < drawdownDepths.Count; d++)
        {
            if (drawdownDepths[d] < 100) continue;
            deepCount++;
            if (drawdownRecovered[d]) deepRecoveredCount++;
        }

        double value = deepCount == 0 ? 0 : (double)deepRecoveredCount / deepCount;

        // A single deep drawdown is an anecdote, not a pattern - the ratio would read 1.00 off one
        // event, so the contribution is forced to zero below 2 deep drawdowns regardless of value.
        double contribution = deepCount < 2 ? 0 : Math.Clamp(value, 0, 1) * 0.30;

        string evidence = deepCount == 0
            ? "No drawdown of 100+ points was found in this window."
            : $"{deepRecoveredCount} of {deepCount} drawdowns of 100+ points recovered to within 30 points of the prior peak within 10 games.";

        return new DetectionSignalDto("loss-cluster-after-peak", "Loss cluster after rating peak", value, contribution, evidence);
    }

    /// <summary>
    /// Weight 0.20. Median, not mean, for the reason 8.3's BalanceAnalytics already documents: a
    /// handful of long outlier games drags an average and tells you nothing. A deliberate loss is a
    /// fast loss - resign early, or let the clock run - so a low ratio of loss-plies to win-plies is
    /// the signature this looks for.
    /// </summary>
    private static DetectionSignalDto ShortLosses(IReadOnlyList<SandbaggingGameFact> facts)
    {
        var lossPlies = facts.Where(f => !f.Won).Select(f => (double)f.PlyCount).ToList();
        var winPlies = facts.Where(f => f.Won).Select(f => (double)f.PlyCount).ToList();

        double medianLoss = Median(lossPlies);
        double medianWin = Median(winPlies);
        double value = (lossPlies.Count == 0 || winPlies.Count == 0 || medianWin == 0) ? 0 : medianLoss / medianWin;

        double contribution = Math.Clamp((0.65 - value) / 0.35, 0, 1) * 0.20;

        string evidence = $"Losses end at a median of {medianLoss:0.#} plies against {medianWin:0.#} for wins (ratio {value:0.00}).";

        return new DetectionSignalDto("short-losses", "Short losses relative to wins", value, contribution, evidence);
    }

    /// <summary>
    /// Weight 0.10, the lowest of the four on purpose: resignation is a normal thing losing players do,
    /// and Abandoned is confounded by ordinary disconnects - 6e.3 built a whole cooldown around that
    /// fact without ever calling it cheating. This signal only nudges the score when a loss set is
    /// unusually thrown-away rather than played out.
    /// </summary>
    private static DetectionSignalDto LossTerminationSkew(IReadOnlyList<SandbaggingGameFact> facts)
    {
        var losses = facts.Where(f => !f.Won).ToList();
        int thrown = losses.Count(f => f.Termination is TerminationReason.Resignation or TerminationReason.Abandoned);
        double value = losses.Count == 0 ? 0 : (double)thrown / losses.Count;

        double contribution = Math.Clamp((value - 0.50) / 0.40, 0, 1) * 0.10;

        string evidence = $"{thrown} of {losses.Count} losses ended in resignation or abandonment.";

        return new DetectionSignalDto("loss-termination-skew", "Losses end by resignation or abandonment", value, contribution, evidence);
    }

    /// <summary>Even-sized sets average the two middle values, matching BalanceAnalytics's convention.</summary>
    private static double Median(List<double> values)
    {
        if (values.Count == 0) return 0;
        var sorted = values.OrderBy(v => v).ToList();
        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }
}
