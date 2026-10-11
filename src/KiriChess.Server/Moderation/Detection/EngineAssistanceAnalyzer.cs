using System.Globalization;

namespace KiriChess.Server.Moderation.Detection;

/// <summary>
/// One ply of one game, reduced to what timing analysis needs. Flat and DB-free so the scoring can be
/// tested with a literal list - the same split BalanceAnalytics uses and for the same reason.
/// </summary>
public sealed record MoveTimingFact(
    int Ply,
    int ThinkMs,
    int LagMs,
    /// <summary>Null on rows written before 6e.2. Its presence is the marker that this ply is measurable at all.</summary>
    DateTimeOffset? ReceivedAtUtc,
    /// <summary>
    /// The mover's own remaining clock AFTER this move, in seconds.
    ///
    /// Deliberately NOT the clock-pressure axis of signal 3 any more (S-2-1). This value contains
    /// this ply's own think time with a minus sign, so correlating think against it carried a
    /// -Var(think) term that was pure arithmetic rather than behaviour. Signal 3 now reads the
    /// PREVIOUS ply's value instead, which is the clock the player actually decided under. This
    /// field is still what the time-trouble filter reads, where "how much clock is left after the
    /// move" is the question being asked.
    /// </summary>
    int MoverTimeAfterSeconds);

/// <summary>
/// One game's worth of the subject player's own plies. Opponent plies are not carried - a player is
/// scored on their own timing.
/// </summary>
public sealed record GameTimingFact(
    Guid GameId, string? Pool, bool IsRated, DateTimeOffset EndedAt,
    int TotalPliesInGame,
    IReadOnlyList<MoveTimingFact> SubjectPlies);

/// <summary>
/// Phase 8.5 Task C: a pure, DB-free statistical detector over the Phase 6e.2/6f.2 move-timing
/// telemetry. It never looks at a move's legality or quality - only at whether a player's cadence
/// behaves like a human's. See Phase 8.5 for the full
/// reasoning behind each signal and threshold; every number here is a documented, unvalidated guess,
/// not a tuned one, because no confirmed cheater has ever played on this site.
/// </summary>
public static class EngineAssistanceAnalyzer
{
    /// <summary>The stable detector name carried on <see cref="DetectionReportDto.Detector"/>.</summary>
    public const string DetectorName = "engine-assistance";

    /// <summary>
    /// Below this many scorable plies across all games in the window, nothing here means anything.
    /// A guess: what would tune it is the observed ply-level variance of real players, which needs real
    /// players. Deliberately high - a false positive on this detector accuses somebody of cheating.
    /// </summary>
    public const int MinimumPlies = 200;

    /// <summary>And across at least this many separate games - 200 plies from one game is one sitting, not a habit.</summary>
    public const int MinimumGames = 5;

    /// <summary>
    /// The first this-many plies of every game are dropped before scoring. They are book-ish and fast
    /// for everyone, honest or not, and including them drags every variance figure down regardless of
    /// who is playing.
    /// </summary>
    public const int OpeningPliesSkipped = 8;

    /// <summary>
    /// Plies where the mover has this many seconds or fewer left are dropped before scoring. Everybody's
    /// timing collapses under severe time pressure, including honest players; scoring those plies would
    /// flag exactly the players this variant puts under the most pressure, and it would also make signal
    /// 3 dishonest by mixing the floor effect into what should be a measurement of response to pressure.
    /// </summary>
    public const int TimeTroubleFloorSeconds = 10;

    /// <summary>A ply counts as a "long think" when its corrected think time is at least this multiple of the player's own median.</summary>
    private const int LongThinkMultiplier = 3;

    private const double WeightVarianceCollapse = 0.30;
    private const double WeightNoLongThinks = 0.25;
    private const double WeightClockPressure = 0.30;
    private const double WeightCrossGame = 0.15;

    private static readonly string[] BaseCaveats =
    [
        "Thresholds are unvalidated: no confirmed cheat has ever been observed on this site, so every cutoff here is a reasoned guess, not a tuned one.",
        "This detector analyses timing only. It never evaluates move quality and cannot tell a strong player from an assisted one on the basis of their moves.",
        "Think time is measured server-side (6e.2) with the applied lag credit netted off (6f.2); it remains an approximation of time the player actually spent thinking.",
        "Advisory only. This score has no automatic effect on the account."
    ];

    /// <summary>
    /// Not optional and not softenable (see the task plan): the sentence standing between an
    /// unvalidated heuristic and a banned innocent. Present on every Watch/Elevated report.
    /// </summary>
    private const string FalsePositiveCaveat =
        "A fast, consistent player with a stable connection can look like this. Timing alone has never been sufficient grounds anywhere.";

    /// <summary>
    /// Corrected think time for one ply: the wall-clock interval the server measured, net of the
    /// round-trip credit that was actually applied to the charged clock (6f.2). Clamped at zero because
    /// the lag credit is an EMA estimate and can exceed a very fast ply's measured interval - a negative
    /// think time is a measurement artefact, not a player moving before their opponent did.
    /// </summary>
    public static int CorrectedThinkMs(MoveTimingFact f) => Math.Max(0, f.ThinkMs - f.LagMs);

    /// <summary>
    /// A ply is scorable only when it carries the Phase 6e.2 telemetry and is not degenerate. Rows
    /// written before 6e.2 deserialize with ThinkMs == 0 and ReceivedAtUtc == null - that is "unknown",
    /// not "instant". Treating them as zero-think plies would make every pre-telemetry game look like
    /// perfect timing consistency and score as maximally suspicious. This is the single most important
    /// correctness requirement in this file.
    /// </summary>
    private static bool HasTelemetry(MoveTimingFact f) => f.ReceivedAtUtc is not null && f.ThinkMs > 0;

    public static DetectionReportDto Analyze(
        Guid userId, string displayName,
        IReadOnlyList<GameTimingFact> games,
        DateTimeOffset generatedAt)
    {
        int totalSubjectPlies = 0;
        int preTelemetryDropped = 0;
        int openingDropped = 0;
        int timeTroubleDropped = 0;

        var perGameCorrectedMs = new List<List<double>>();
        var scorableCorrectedMs = new List<double>();

        // S-2-1: signal 3's own paired axes, deliberately NOT the same lists the other signals use.
        // The pressure axis is the mover's clock ENTERING the decision, which does not exist for the
        // first of their plies in a game, so this pair is a subset of the scorable plies and has to
        // be built alongside rather than derived from `scorableCorrectedMs`. Keeping it separate is
        // what stops this fix changing what signals 1, 2 and 4 are computed over.
        var pressureEnteringSeconds = new List<double>();
        var pressureCorrectedMs = new List<double>();

        foreach (var game in games)
        {
            var gameCorrected = new List<double>();

            // The subject's own previous ply in this game. Captured for every ply, including plies
            // that are dropped below: a dropped ply still recorded a real clock reading, and it is
            // the reading the NEXT ply was decided under, which is the only thing it is used for.
            // Reset per game, so a correlation can never pair one game's clock with another's think.
            double? previousMoverTimeAfter = null;

            foreach (var ply in game.SubjectPlies)
            {
                totalSubjectPlies++;
                double? enteringSeconds = previousMoverTimeAfter;
                previousMoverTimeAfter = ply.MoverTimeAfterSeconds;

                // Applied in the order the plan pins: telemetry first (the trap), then opening, then
                // time trouble. Each ply is dropped for exactly one reason so the caveat counts sum to
                // the total rather than double-counting a ply that fails more than one check.
                if (!HasTelemetry(ply)) { preTelemetryDropped++; continue; }
                if (ply.Ply <= OpeningPliesSkipped) { openingDropped++; continue; }
                if (ply.MoverTimeAfterSeconds <= TimeTroubleFloorSeconds) { timeTroubleDropped++; continue; }

                double corrected = CorrectedThinkMs(ply);
                scorableCorrectedMs.Add(corrected);
                gameCorrected.Add(corrected);

                // Only when there is a preceding ply to read the entering clock from. The subject's
                // first scorable ply in each game is therefore absent from this correlation and
                // present in every other signal, which is correct: it has a think time but no
                // "clock they decided under" that is independent of that think.
                if (enteringSeconds is not null)
                {
                    pressureEnteringSeconds.Add(enteringSeconds.Value);
                    pressureCorrectedMs.Add(corrected);
                }
            }

            if (gameCorrected.Count > 0) perGameCorrectedMs.Add(gameCorrected);
        }

        int scorablePlies = scorableCorrectedMs.Count;
        int scorableGames = perGameCorrectedMs.Count;
        int gamesCount = games.Count;
        int unratedGamesCount = games.Count(g => !g.IsRated);

        var caveats = new List<string>(BaseCaveats);

        void AddConditionalCaveats()
        {
            if (preTelemetryDropped > 0)
                caveats.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{preTelemetryDropped} of {totalSubjectPlies} plies were dropped as pre-telemetry (roadmap 6e.2) or unmeasured."));
            if (openingDropped > 0)
                caveats.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{openingDropped} of {totalSubjectPlies} plies were dropped as opening moves (the first {OpeningPliesSkipped} plies of each game), which are book-fast for everyone."));
            if (timeTroubleDropped > 0)
                caveats.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{timeTroubleDropped} of {totalSubjectPlies} plies were dropped for severe time trouble ({TimeTroubleFloorSeconds}s or less remaining), where timing collapses for honest players too."));
            if (unratedGamesCount > 0)
                caveats.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{unratedGamesCount} of {gamesCount} games analysed were unrated. Engine assistance against a person is still prohibited in an unrated game, except one the site labelled as a bot because that opponent played through the public API."));
        }

        if (scorablePlies < MinimumPlies || scorableGames < MinimumGames)
        {
            AddConditionalCaveats();
            caveats.Add(string.Create(CultureInfo.InvariantCulture,
                $"Only {scorablePlies} scorable plies across {scorableGames} games; at least {MinimumPlies} plies across at least {MinimumGames} games are required before this detector says anything."));

            return new DetectionReportDto(
                userId, displayName, DetectorName, null, DetectionVerdict.Insufficient, 0, scorablePlies,
                [], [.. caveats], generatedAt);
        }

        var signals = new[]
        {
            VarianceCollapseSignal(scorableCorrectedMs),
            NoLongThinksSignal(scorableCorrectedMs),
            ClockPressureSignal(pressureEnteringSeconds, pressureCorrectedMs),
            CrossGameConsistencySignal(perGameCorrectedMs)
        };

        double score = Math.Clamp(signals.Sum(s => s.Contribution), 0, 1);
        string verdict = DetectionVerdict.For(score);

        AddConditionalCaveats();
        if (verdict is DetectionVerdict.Watch or DetectionVerdict.Elevated)
            caveats.Add(FalsePositiveCaveat);

        return new DetectionReportDto(
            userId, displayName, DetectorName, null, verdict, score, scorablePlies,
            signals, [.. caveats], generatedAt);
    }

    /// <summary>
    /// A human's think time is heavy-tailed - mostly fast, occasionally long. A player reading moves
    /// off a screen has a cadence set by the relay, and their coefficient of variation collapses toward
    /// zero. CV at or above 0.80 is healthy human play and contributes nothing; CV at or below 0.25
    /// contributes the full weight.
    /// </summary>
    private static DetectionSignalDto VarianceCollapseSignal(IReadOnlyList<double> correctedMs)
    {
        double mean = Mean(correctedMs);
        double sd = PopulationStdDev(correctedMs);
        double cv = mean == 0 ? 0 : sd / mean;
        double contribution = Math.Clamp((0.80 - cv) / 0.55, 0, 1) * WeightVarianceCollapse;

        string evidence = string.Create(CultureInfo.InvariantCulture,
            $"Corrected think time has a coefficient of variation of {cv:0.00} across {correctedMs.Count} plies (mean {mean / 1000.0:0.0}s, sd {sd / 1000.0:0.0}s).");

        return new DetectionSignalDto("timing-variance-collapse", "Timing variance collapse", cv, contribution, evidence);
    }

    /// <summary>
    /// A human game contains a few plies where the player stopped and worked something out. A relayed
    /// game rarely does. The threshold is the player's own median, not a global constant, so six pools
    /// with wildly different time budgets are normalised away. 6%+ long thinks contributes nothing; none
    /// contributes the full weight.
    /// </summary>
    private static DetectionSignalDto NoLongThinksSignal(IReadOnlyList<double> correctedMs)
    {
        double median = Median(correctedMs);
        double threshold = median * LongThinkMultiplier;
        int longCount = correctedMs.Count(v => v >= threshold);
        double fraction = correctedMs.Count == 0 ? 0 : (double)longCount / correctedMs.Count;
        double contribution = Math.Clamp((0.06 - fraction) / 0.06, 0, 1) * WeightNoLongThinks;

        string evidence = string.Create(CultureInfo.InvariantCulture,
            $"{longCount} of {correctedMs.Count} plies ({fraction * 100:0.0}%) had a corrected think time at least {LongThinkMultiplier}x the player's own median of {median / 1000.0:0.0}s; human play typically clears 6%.");

        return new DetectionSignalDto("no-long-thinks", "No long thinks", fraction, contribution, evidence);
    }

    /// <summary>
    /// The domain-specific signal. On a variant whose entire economy is time, a human under clock
    /// pressure moves faster: the correlation between remaining clock and think time is strongly
    /// positive. A player whose cadence is set by an external engine shows a correlation near zero -
    /// their timing does not know their clock is running out. r &gt;= 0.25 contributes nothing; r &lt;=
    /// -0.20 contributes the full weight.
    ///
    /// <para>
    /// S-2-1: <paramref name="enteringSeconds"/> is the mover's clock ENTERING each decision (their
    /// previous ply's remaining time), not the clock after the move. Regressing think time against
    /// the post-move clock was an accounting identity: that clock is
    /// <c>before - think + economyDelta</c>, so the covariance carried a <c>-Var(think)</c> term
    /// that no behaviour produced. The bias ran the wrong way for the wrong person, since a player
    /// who takes a few genuinely long thinks raises sd(think), which signal 1 scores as innocent
    /// while the artifact pushed this signal toward the full accusatory weight for the same
    /// behaviour.
    /// </para>
    /// <para>
    /// The 0.25 / -0.20 cutoffs below are UNCHANGED and were chosen against the biased quantity, so
    /// they are now applied to a different distribution than the one they were guessed from. They
    /// are not re-derived here because that needs real scored population data, not a code change.
    /// Treat them as provisional until somebody reads them off actual reports. This is advisory-only
    /// output, so a provisional cutoff misranks a report, it does not sanction anybody.
    /// </para>
    /// </summary>
    private static DetectionSignalDto ClockPressureSignal(IReadOnlyList<double> enteringSeconds, IReadOnlyList<double> correctedMs)
    {
        double sdX = PopulationStdDev(enteringSeconds);
        double sdY = PopulationStdDev(correctedMs);

        // Guarded rather than left to divide-by-zero: a NaN that reaches JsonSerializer throws, and a
        // NaN that reaches the score silently poisons it. No variance in either axis means the
        // correlation is undefined, not "engine-flat" - report it as no signal, not as evidence.
        if (sdX == 0 || sdY == 0)
        {
            const string evidence = "The clock entering the move, or think time, showed no variance across scorable plies, so the clock-pressure correlation is undefined and this signal contributes nothing.";
            return new DetectionSignalDto("clock-pressure-insensitivity", "Clock-pressure insensitivity", 0, 0, evidence);
        }

        double meanX = Mean(enteringSeconds);
        double meanY = Mean(correctedMs);
        double covariance = 0;
        for (int i = 0; i < enteringSeconds.Count; i++)
            covariance += (enteringSeconds[i] - meanX) * (correctedMs[i] - meanY);
        covariance /= enteringSeconds.Count;

        double r = covariance / (sdX * sdY);
        double contribution = Math.Clamp((0.25 - r) / 0.45, 0, 1) * WeightClockPressure;

        string evidenceText = string.Create(CultureInfo.InvariantCulture,
            $"Think time correlates with the clock entering the move at r = {r:0.00}; human play on a time-economy variant is expected to be strongly positive.");

        return new DetectionSignalDto("clock-pressure-insensitivity", "Clock-pressure insensitivity", r, contribution, evidenceText);
    }

    /// <summary>
    /// A human's average pace varies with mood, opponent and time of day; a relay's does not. Requires
    /// at least MinimumGames games with scorable plies - always true once the overall sufficiency gate
    /// has passed, since that gate uses the same threshold, but the guard is kept here too because
    /// nothing in this method's own contract depends on that gate having run first.
    /// </summary>
    private static DetectionSignalDto CrossGameConsistencySignal(IReadOnlyList<List<double>> perGameCorrectedMs)
    {
        if (perGameCorrectedMs.Count < MinimumGames)
        {
            string evidence = string.Create(CultureInfo.InvariantCulture,
                $"Only {perGameCorrectedMs.Count} games had scorable plies; at least {MinimumGames} are required for this signal, so it contributes nothing.");
            return new DetectionSignalDto("cross-game-consistency", "Cross-game consistency", 0, 0, evidence);
        }

        var perGameMeans = perGameCorrectedMs.Select(Mean).ToList();
        double meanOfMeans = Mean(perGameMeans);
        double sdOfMeans = PopulationStdDev(perGameMeans);
        double cv = meanOfMeans == 0 ? 0 : sdOfMeans / meanOfMeans;
        double contribution = Math.Clamp((0.35 - cv) / 0.35, 0, 1) * WeightCrossGame;

        string evidenceText = string.Create(CultureInfo.InvariantCulture,
            $"Per-game mean think time has a coefficient of variation of {cv:0.00} across {perGameCorrectedMs.Count} games.");

        return new DetectionSignalDto("cross-game-consistency", "Cross-game consistency", cv, contribution, evidenceText);
    }

    private static double Mean(IReadOnlyList<double> xs) => xs.Count == 0 ? 0 : xs.Sum() / xs.Count;

    private static double PopulationStdDev(IReadOnlyList<double> xs)
    {
        if (xs.Count == 0) return 0;
        double mean = Mean(xs);
        double sumSq = xs.Sum(x => (x - mean) * (x - mean));
        return Math.Sqrt(sumSq / xs.Count);
    }

    /// <summary>Even-sized sets average the two middle values, matching BalanceAnalytics' contract.</summary>
    private static double Median(IReadOnlyList<double> xs)
    {
        if (xs.Count == 0) return 0;
        var sorted = xs.OrderBy(v => v).ToList();
        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }
}
