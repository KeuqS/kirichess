namespace KiriChess.Server.Moderation.Detection;

/// <summary>
/// One decisive, rated game as the pair-concentration analyzer sees it (S-2-4). Flat and DB-free for
/// the same reason <see cref="SandbaggingGameFact"/> is: the maths is what needs testing without a
/// Postgres container. Unlike <see cref="SandbaggingGameFact"/> this does not need <c>Won</c> - a
/// drawn game still moves rating and still counts toward opponent concentration, so draws are not
/// excluded here the way they are for the drawdown/short-loss signals.
/// </summary>
public sealed record PairConcentrationGameFact(
    Guid GameId,
    string Pool,
    DateTimeOffset EndedAt,
    Guid OpponentId,
    double SubjectRatingBefore,
    double SubjectRatingAfter);

/// <summary>
/// S-2-4: two accounts run by one person, one feeding rating to the other, is the multi-account abuse
/// shape that actually pays on a rated ladder - and it is invisible to <see cref="SandbaggingAnalyzer"/>,
/// whose whole premise is inconsistency WITHIN one account. A dedicated feeder is not inconsistent: the
/// feeder is a consistently bad player against everyone, the fed account is a consistently good one.
/// What both accounts share instead is an unusual concentration of games against, and rating flow with,
/// one single opponent - which is computable from data this site already keeps (<c>Games</c>,
/// <c>RatingHistory</c>) with no IP address, device id or other new personal data at all. See
/// <c>docs/audits/S.md</c> S-2-4: same-person linkage itself is not detectable by design and this
/// analyzer does not claim to detect it - it only measures how lopsided one account's own rated
/// history is toward a single opponent, same advisory contract as the other two detectors.
///
/// Pure and synchronous, matching <see cref="SandbaggingAnalyzer"/>: no DbContext, no I/O.
/// <paramref name="facts"/> need not be pre-sorted - unlike the sandbagging drawdown walk, both
/// signals here are order-independent aggregates.
/// </summary>
public static class PairConcentrationAnalyzer
{
    /// <summary>
    /// Below this many decisive rated games, concentration on one opponent is just "a new player who
    /// has not met many opponents yet", not a signal. Matches <see cref="SandbaggingAnalyzer.MinimumGames"/>
    /// for the same unvalidated-guess reason - no confirmed pair-feeding case has ever been seen here.
    /// </summary>
    public const int MinimumGames = 20;

    private const string UnvalidatedThresholdsCaveat =
        "Thresholds are unvalidated: no confirmed pair-feeding case has ever been observed on this site, so every cutoff here is a reasoned guess, not a tuned one.";

    private const string AdvisoryOnlyCaveat =
        "Advisory only. This score has no automatic effect on the account.";

    private const string NoLinkageCaveat =
        "This detector cannot confirm two accounts share an owner - same-person account linkage is not detectable by design (S-2-4). It only measures how concentrated one account's own rated history is on a single opponent.";

    public static DetectionReportDto Analyze(
        Guid userId, string displayName, string pool,
        IReadOnlyList<PairConcentrationGameFact> facts,
        DateTimeOffset generatedAt)
    {
        if (facts.Count < MinimumGames)
        {
            return new DetectionReportDto(
                userId, displayName, "pair-concentration", pool,
                DetectionVerdict.Insufficient, 0, facts.Count,
                Array.Empty<DetectionSignalDto>(),
                new[]
                {
                    $"Only {facts.Count} decisive rated games in this window; {MinimumGames} are needed before any signal here means anything.",
                    UnvalidatedThresholdsCaveat,
                },
                generatedAt);
        }

        var byOpponent = facts
            .GroupBy(f => f.OpponentId)
            .OrderByDescending(g => g.Count())
            .First();

        var concentration = ConcentrationSignal(facts, byOpponent);
        var netFlow = NetFlowSignal(facts, byOpponent);
        var signals = new[] { concentration, netFlow };
        double score = Math.Clamp(signals.Sum(s => s.Contribution), 0, 1);

        var caveats = new List<string> { UnvalidatedThresholdsCaveat, AdvisoryOnlyCaveat, NoLinkageCaveat };
        if (facts.Count < 40)
            caveats.Add($"Only {facts.Count} games. Signals at this sample size move a long way on one game.");

        return new DetectionReportDto(
            userId, displayName, "pair-concentration", pool,
            DetectionVerdict.For(score), score, facts.Count,
            signals, caveats.ToArray(), generatedAt);
    }

    /// <summary>
    /// Weight 0.50: the share of this account's decisive rated games played against its single most
    /// frequent opponent. A normal player's opponents are spread across matchmaking; a pair farming
    /// rating between two accounts is disproportionately one relationship. Starts contributing above
    /// 25% concentration (a real player can easily see one rival 1-in-5 games in a small pool) and
    /// reaches full weight at 60%.
    /// </summary>
    private static DetectionSignalDto ConcentrationSignal(
        IReadOnlyList<PairConcentrationGameFact> facts, IGrouping<Guid, PairConcentrationGameFact> topOpponent)
    {
        double share = (double)topOpponent.Count() / facts.Count;
        double contribution = Math.Clamp((share - 0.25) / 0.35, 0, 1) * 0.50;

        string evidence =
            $"{topOpponent.Count()} of {facts.Count} decisive rated games ({share:P0}) were played against a single opponent.";

        return new DetectionSignalDto("opponent-concentration", "Share of games against most frequent opponent", share, contribution, evidence);
    }

    /// <summary>
    /// Weight 0.50: the net rating this account gained or lost against its most frequent opponent, as
    /// a share of the account's total rating movement (sum of absolute per-game changes) across every
    /// opponent in the window. A player who beats and loses to the same rival roughly evenly nets
    /// close to zero even at high concentration - that is normal rivalry, not feeding. A one-sided,
    /// consistently-signed flow concentrated in one relationship is the shape this looks for. Starts
    /// contributing above 30% of total movement, full weight at 70%.
    /// </summary>
    private static DetectionSignalDto NetFlowSignal(
        IReadOnlyList<PairConcentrationGameFact> facts, IGrouping<Guid, PairConcentrationGameFact> topOpponent)
    {
        double netFlow = topOpponent.Sum(f => f.SubjectRatingAfter - f.SubjectRatingBefore);
        double totalMovement = facts.Sum(f => Math.Abs(f.SubjectRatingAfter - f.SubjectRatingBefore));
        double value = totalMovement <= 0 ? 0 : Math.Abs(netFlow) / totalMovement;

        double contribution = Math.Clamp((value - 0.30) / 0.40, 0, 1) * 0.50;

        string direction = netFlow >= 0 ? "gained from" : "lost to";
        string evidence =
            $"{Math.Abs(netFlow):0.#} net rating points {direction} that one opponent - {value:P0} of this account's total rating movement in the window.";

        return new DetectionSignalDto("net-rating-flow", "Net rating flow with most frequent opponent", value, contribution, evidence);
    }
}
