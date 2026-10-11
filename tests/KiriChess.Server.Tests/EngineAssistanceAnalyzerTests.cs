using System.Text.Json;
using KiriChess.Server.Moderation.Detection;
using Xunit;

namespace KiriChess.Server.Tests;

/// <summary>
/// Covers the Phase 8.5 Task C engine-assistance maths from
/// Phase 8.5: the pre-telemetry trap, the sufficiency
/// gate, the four signals, and the caveat contract. Pure and synchronous, so this needs no Docker and
/// no database - the same split BalanceAnalyticsTests uses and for the same reason.
/// </summary>
public sealed class EngineAssistanceAnalyzerTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private const string DisplayName = "Test Subject";
    private static readonly DateTimeOffset GeneratedAt = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Received = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);

    private static MoveTimingFact Ply(int ply, int thinkMs, int moverTimeAfterSeconds, int lagMs = 0) =>
        new(ply, thinkMs, lagMs, Received, moverTimeAfterSeconds);

    private static MoveTimingFact PreTelemetryPly(int ply, int moverTimeAfterSeconds) =>
        new(ply, 0, 0, null, moverTimeAfterSeconds);

    private static GameTimingFact Game(IReadOnlyList<MoveTimingFact> plies, bool isRated = true, string? pool = "steal1") =>
        new(Guid.NewGuid(), pool, isRated, GeneratedAt, plies.Count, plies);

    /// <summary>
    /// steal1 queen capture: <c>pieceValues.queen * captureMult</c> = 5 * 5 = 25 seconds. Used as the
    /// mid-game economy delta S-2-2 asks the fixture to actually apply.
    /// </summary>
    private const int QueenCaptureGainSeconds = 25;

    /// <summary>
    /// The clock GameSession writes after a ply: <c>TimeAfter = TimeBefore - floor(thinkMs/1000) +
    /// delta</c>, floored at zero the way the engine clamps each side. Whole seconds only, matching
    /// <c>GameSession.WholeSeconds</c>. This is the identity the old fixture inverted.
    /// </summary>
    private static int ChargeClock(int enteringSeconds, int thinkMs, int deltaSeconds = 0)
    {
        int thinkSeconds = Math.Max(0, thinkMs / 1000);
        return Math.Max(0, enteringSeconds - thinkSeconds + deltaSeconds);
    }

    /// <summary>
    /// A heavy-tailed, clock-aware timing pattern for one game: think time is derived from the clock
    /// ENTERING the ply (the S-2-1 pressure axis), then that think is subtracted and an optional
    /// economy delta is applied so a long think costs time on the next observation. Per-game pace
    /// varies via <paramref name="paceScale"/> so cross-game consistency does not collapse to zero by
    /// construction.
    /// </summary>
    private static GameTimingFact HumanLikeGame(
        int plyCount, double paceScale, int? captureAtIndex = null, int captureGainSeconds = 0)
    {
        var plies = new List<MoveTimingFact>();
        int remaining = 300;
        for (int j = 0; j < plyCount; j++)
        {
            int ply = 9 + j; // past the opening
            int entering = remaining;

            int baseThink = (int)(entering * 12 * paceScale);
            int wobble = ((j % 7) - 3) * 150; // -450..450, breaks a perfectly linear correlation
            int thinkMs = Math.Max(300, baseThink + wobble);
            if (j % 9 == 4) thinkMs += 9000; // an occasional long think, now charged below

            int delta = captureAtIndex == j ? captureGainSeconds : 0;
            remaining = ChargeClock(entering, thinkMs, delta);
            plies.Add(Ply(ply, thinkMs, remaining));
        }
        return Game(plies);
    }

    /// <summary>Near-constant think time, no long thinks, and a clock that actually declines by that think.</summary>
    private static GameTimingFact MetronomicGame(int plyCount)
    {
        var plies = new List<MoveTimingFact>();
        int remaining = 300;
        for (int j = 0; j < plyCount; j++)
        {
            int ply = 9 + j;
            int thinkMs = 3000 + (j % 3); // 3000/3001/3002ms - negligible, non-zero variance
            remaining = ChargeClock(remaining, thinkMs);
            plies.Add(Ply(ply, thinkMs, remaining));
        }
        return Game(plies);
    }

    /// <summary>
    /// S-2-1. The regression test for the accounting identity: signal 3 must not read think time back
    /// out of the axis it is correlated against.
    ///
    /// HumanLikeGame / MetronomicGame now charge think (S-2-2). This case still builds its own clock
    /// so the think pattern has no relationship to remaining time: that is the whole point. The bug
    /// only exists when the axis actually contains the quantity being correlated, and a clock-aware
    /// human fixture would hide it behind a real positive r.
    ///
    /// The player here is honest and unremarkable. Their think times cycle through a fixed pattern
    /// with a few long thinks, and that pattern has no relationship to how much clock they have left,
    /// so the true correlation is around zero. Against the post-move clock, though, the arithmetic
    /// alone dragged r to roughly -0.2, which is the full-weight accusatory end of a signal carrying
    /// 0.30 of a 1.0 score. Reading the clock they entered the move on removes it.
    /// </summary>
    [Fact]
    public void Clock_pressure_signal_is_not_dragged_negative_by_the_think_time_it_measures()
    {
        // Real variance, a few long thinks, and no relationship to the remaining clock.
        int[] thinkCycle = [1000, 1000, 30000, 1000, 1000];

        var games = Enumerable.Range(0, 10).Select(g =>
        {
            var plies = new List<MoveTimingFact>();
            int remaining = 300;
            for (int j = 0; j < 25; j++)
            {
                int thinkMs = thinkCycle[(j + g) % thinkCycle.Length];
                remaining = ChargeClock(remaining, thinkMs);
                plies.Add(Ply(9 + j, thinkMs, remaining));
            }
            return Game(plies);
        }).ToList();

        var report = EngineAssistanceAnalyzer.Analyze(UserId, DisplayName, games, GeneratedAt);
        var clockPressure = report.Signals[2];

        // Thresholds sit between the two measured values rather than being picked for comfort. On this
        // exact fixture the old post-move axis yields r = -0.115 and a contribution of 0.243; the
        // entering-clock axis yields r = +0.124 and 0.084. Both assertions therefore fail if the axis
        // is ever moved back, which is the only reason they are worth writing.
        Assert.True(
            clockPressure.Value > -0.05,
            $"Clock-pressure r came out at {clockPressure.Value:0.000} for a player whose think time is "
            + "uncorrelated with their clock. A negative r here is the accounting identity S-2-1 "
            + "describes, not evidence about the player.");

        // And the consequence that actually reaches a moderator: this player must not be handed most
        // of the signal's accusatory weight by arithmetic alone.
        Assert.True(
            clockPressure.Contribution < 0.15,
            $"An honest player drew {clockPressure.Contribution:0.000} of the 0.30 clock-pressure weight.");
    }

    [Fact]
    public void Pre_telemetry_plies_are_dropped_not_treated_as_instant_moves()
    {
        // Rows written before roadmap 6e.2 deserialize with ThinkMs == 0 and ReceivedAtUtc == null.
        // If those were scored as zero-think plies instead of being dropped, every legacy game in the
        // database would show perfect timing consistency (zero variance) and score as maximally
        // suspicious. This game has 60 such plies and must contribute nothing: SampleSize == 0 proves
        // they were dropped, not silently counted as instant moves.
        var plies = Enumerable.Range(1, 60).Select(p => PreTelemetryPly(p, 200)).ToList();
        var report = EngineAssistanceAnalyzer.Analyze(UserId, DisplayName, [Game(plies)], GeneratedAt);

        Assert.Equal(DetectionVerdict.Insufficient, report.Verdict);
        Assert.Equal(0, report.Score);
        Assert.Equal(0, report.SampleSize);
        Assert.Empty(report.Signals);
        Assert.Contains(report.Caveats, c => c.Contains("60 of 60 plies were dropped as pre-telemetry"));
    }

    [Fact]
    public void Below_minimum_plies_is_Insufficient()
    {
        // 5 games (clears MinimumGames) but only 20 scorable plies each - 100 total, well under
        // MinimumPlies (200).
        var games = Enumerable.Range(0, 5).Select(_ => HumanLikeGame(20, 1.0)).ToList();
        var report = EngineAssistanceAnalyzer.Analyze(UserId, DisplayName, games, GeneratedAt);

        Assert.Equal(DetectionVerdict.Insufficient, report.Verdict);
        Assert.Equal(0, report.Score);
        Assert.Empty(report.Signals);
    }

    [Fact]
    public void Below_minimum_games_is_Insufficient()
    {
        // 300 total scorable plies (clears MinimumPlies) but only 2 games - under MinimumGames.
        var games = Enumerable.Range(0, 2).Select(_ => HumanLikeGame(150, 1.0)).ToList();
        var report = EngineAssistanceAnalyzer.Analyze(UserId, DisplayName, games, GeneratedAt);

        Assert.Equal(DetectionVerdict.Insufficient, report.Verdict);
        Assert.Equal(0, report.Score);
        Assert.Empty(report.Signals);
    }

    [Fact]
    public void Fixture_clock_charges_the_plys_own_think()
    {
        var game = HumanLikeGame(50, 1.0);
        Assert.Equal(50, game.SubjectPlies.Count);

        int entering = 300;
        for (int j = 0; j < game.SubjectPlies.Count; j++)
        {
            var ply = game.SubjectPlies[j];
            int expected = ChargeClock(entering, ply.ThinkMs);
            Assert.Equal(expected, ply.MoverTimeAfterSeconds);
            entering = ply.MoverTimeAfterSeconds;
        }

        var firstLong = game.SubjectPlies[4];
        int longEntering = game.SubjectPlies[3].MoverTimeAfterSeconds;
        Assert.True(firstLong.ThinkMs >= 9000, "the j%9==4 ply must still be the long think");
        int remainingWithoutLong = ChargeClock(longEntering, firstLong.ThinkMs - 9000);
        Assert.True(
            firstLong.MoverTimeAfterSeconds <= remainingWithoutLong - 8,
            $"the 9-second long think left {firstLong.MoverTimeAfterSeconds}s; without it the clock would be {remainingWithoutLong}s. A fixture that deducts a flat 5s per ply cannot produce this gap.");
    }

    [Fact]
    public void A_large_mid_game_capture_gain_is_visible_on_the_clock()
    {
        const int captureAt = 24;
        var withCapture = HumanLikeGame(50, 1.0, captureAt, QueenCaptureGainSeconds);
        var without = HumanLikeGame(50, 1.0);

        int entering = withCapture.SubjectPlies[captureAt - 1].MoverTimeAfterSeconds;
        var capturePly = withCapture.SubjectPlies[captureAt];
        Assert.Equal(without.SubjectPlies[captureAt].ThinkMs, capturePly.ThinkMs);
        Assert.Equal(
            ChargeClock(entering, capturePly.ThinkMs, QueenCaptureGainSeconds),
            capturePly.MoverTimeAfterSeconds);
        Assert.Equal(
            without.SubjectPlies[captureAt].MoverTimeAfterSeconds + QueenCaptureGainSeconds,
            capturePly.MoverTimeAfterSeconds);

        double[] paceScales = [0.6, 1.6, 0.8, 1.4, 1.0, 1.2];
        var games = paceScales.Select((scale, i) =>
            i == 0 ? HumanLikeGame(50, scale, captureAt, QueenCaptureGainSeconds) : HumanLikeGame(50, scale)).ToList();
        var report = EngineAssistanceAnalyzer.Analyze(UserId, DisplayName, games, GeneratedAt);

        Assert.True(report.SampleSize >= EngineAssistanceAnalyzer.MinimumPlies);
        Assert.Equal(DetectionVerdict.Unremarkable, report.Verdict);
        Assert.True(report.Score < 0.40, $"expected score < 0.40, was {report.Score}");
    }

    [Fact]
    public void Human_like_timing_scores_low()
    {
        double[] paceScales = [0.6, 1.6, 0.8, 1.4, 1.0, 1.2];
        var games = paceScales.Select(scale => HumanLikeGame(50, scale)).ToList();
        var report = EngineAssistanceAnalyzer.Analyze(UserId, DisplayName, games, GeneratedAt);

        Assert.True(report.SampleSize >= EngineAssistanceAnalyzer.MinimumPlies);
        Assert.Equal(DetectionVerdict.Unremarkable, report.Verdict);
        Assert.True(report.Score < 0.40, $"expected score < 0.40, was {report.Score}");
    }

    [Fact]
    public void Metronomic_timing_scores_high()
    {
        var games = Enumerable.Range(0, 6).Select(_ => MetronomicGame(50)).ToList();
        var report = EngineAssistanceAnalyzer.Analyze(UserId, DisplayName, games, GeneratedAt);

        Assert.True(report.SampleSize >= EngineAssistanceAnalyzer.MinimumPlies);
        Assert.Equal(DetectionVerdict.Elevated, report.Verdict);
        Assert.True(report.Score >= 0.70, $"expected score >= 0.70, was {report.Score}");
    }

    [Fact]
    public void A_fast_consistent_player_still_gets_the_false_positive_caveat()
    {
        var games = Enumerable.Range(0, 6).Select(_ => MetronomicGame(50)).ToList();
        var report = EngineAssistanceAnalyzer.Analyze(UserId, DisplayName, games, GeneratedAt);

        Assert.True(report.Verdict is DetectionVerdict.Watch or DetectionVerdict.Elevated);
        Assert.Contains(report.Caveats,
            c => c.Contains("A fast, consistent player with a stable connection can look like this"));
    }

    [Fact]
    public void Opening_plies_are_excluded_from_scoring()
    {
        var baseline = Enumerable.Range(0, 6).Select(_ => MetronomicGame(50)).ToList();
        var withOpening = baseline.Select(g =>
        {
            // 8 identical, suspiciously fast opening plies (Ply 1..8) prepended to each game.
            var opening = Enumerable.Range(1, 8).Select(p => Ply(p, 50, 300)).ToList();
            var plies = opening.Concat(g.SubjectPlies).ToList();
            return g with { SubjectPlies = plies };
        }).ToList();

        var baselineReport = EngineAssistanceAnalyzer.Analyze(UserId, DisplayName, baseline, GeneratedAt);
        var withOpeningReport = EngineAssistanceAnalyzer.Analyze(UserId, DisplayName, withOpening, GeneratedAt);

        // The invariant this test is named for: no opening ply's THINK TIME is scored. Sample size is
        // unchanged, and signals 0, 1 and 3 are computed purely from the scorable think times, so they
        // must still match to the last decimal place. This is the assertion that catches an opening
        // ply leaking into scoring.
        Assert.Equal(baselineReport.SampleSize, withOpeningReport.SampleSize);
        Assert.Equal(baselineReport.Signals[0].Value, withOpeningReport.Signals[0].Value, 6);
        Assert.Equal(baselineReport.Signals[1].Value, withOpeningReport.Signals[1].Value, 6);
        Assert.Equal(baselineReport.Signals[3].Value, withOpeningReport.Signals[3].Value, 6);

        // Signal 2 (clock pressure) is deliberately NOT asserted equal, and this is the S-2-1 change
        // rather than a regression. Its x-axis is the clock the player entered the move on, which is
        // the previous ply's remaining time. A game whose recorded history starts at ply 9 has no
        // predecessor for that first scored ply, so it contributes no pressure observation; prepending
        // real opening plies gives it one. The opening ply supplies a clock READING, never a think
        // time, so it moves this signal without being scored.
        //
        // Bounded rather than left free: one extra paired observation per game across six games of
        // fifty plies must not move the correlation far. A large move here would mean the pairing is
        // wrong, not that the fix is working.
        double baselineR = baselineReport.Signals[2].Value;
        double withOpeningR = withOpeningReport.Signals[2].Value;
        Assert.True(
            Math.Abs(baselineR - withOpeningR) < 0.10,
            $"One extra pressure observation per game moved r from {baselineR:0.000} to {withOpeningR:0.000}, "
            + "which is too large to be that one observation. Check the entering-clock pairing.");
    }

    [Fact]
    public void Plies_in_severe_time_trouble_are_excluded()
    {
        var baseline = Enumerable.Range(0, 6).Select(_ => MetronomicGame(50)).ToList();
        var withFlagging = baseline.Select(g =>
        {
            // A handful of erratic, near-zero-clock plies appended - a player who flags does not
            // become a suspect.
            int nextPly = g.SubjectPlies[^1].Ply + 1;
            var flagging = new[]
            {
                Ply(nextPly, 50, 5),
                Ply(nextPly + 1, 9000, 3),
                Ply(nextPly + 2, 10, 10),
            };
            var plies = g.SubjectPlies.Concat(flagging).ToList();
            return g with { SubjectPlies = plies };
        }).ToList();

        var baselineReport = EngineAssistanceAnalyzer.Analyze(UserId, DisplayName, baseline, GeneratedAt);
        var withFlaggingReport = EngineAssistanceAnalyzer.Analyze(UserId, DisplayName, withFlagging, GeneratedAt);

        Assert.Equal(baselineReport.SampleSize, withFlaggingReport.SampleSize);
        Assert.Equal(baselineReport.Score, withFlaggingReport.Score, 6);
    }

    [Fact]
    public void Zero_variance_input_does_not_produce_NaN()
    {
        // Every scorable ply identical in every axis: think time, remaining clock, per-game mean.
        // This is the degenerate input that would divide by zero inside a naive correlation/CV
        // calculation.
        var plies = Enumerable.Range(0, 50).Select(j => Ply(9 + j, 3000, 200)).ToList();
        var games = Enumerable.Range(0, 5).Select(_ => Game(plies.ToList())).ToList();
        var report = EngineAssistanceAnalyzer.Analyze(UserId, DisplayName, games, GeneratedAt);

        Assert.True(double.IsFinite(report.Score));
        foreach (var signal in report.Signals)
        {
            Assert.True(double.IsFinite(signal.Value), $"{signal.Name}.Value was not finite");
            Assert.True(double.IsFinite(signal.Contribution), $"{signal.Name}.Contribution was not finite");
        }

        var json = JsonSerializer.Serialize(report);
        Assert.False(string.IsNullOrEmpty(json));
    }

    [Fact]
    public void Long_think_threshold_is_relative_to_the_players_own_median()
    {
        // The exact same pattern, scaled 4x. Coefficient of variation, the long-think fraction and the
        // clock-pressure correlation are all scale-invariant under a uniform positive multiplier, so
        // every signal's Value should be unchanged - proving the time control is normalised away rather
        // than compared to a fixed constant.
        var unscaled = Enumerable.Range(0, 6).Select(_ => HumanLikeGame(50, 1.0)).ToList();
        var scaled = unscaled.Select(g => g with
        {
            SubjectPlies = g.SubjectPlies.Select(p => p with { ThinkMs = p.ThinkMs * 4 }).ToList()
        }).ToList();

        var unscaledReport = EngineAssistanceAnalyzer.Analyze(UserId, DisplayName, unscaled, GeneratedAt);
        var scaledReport = EngineAssistanceAnalyzer.Analyze(UserId, DisplayName, scaled, GeneratedAt);

        var unscaledNoLongThinks = Assert.Single(unscaledReport.Signals, s => s.Name == "no-long-thinks");
        var scaledNoLongThinks = Assert.Single(scaledReport.Signals, s => s.Name == "no-long-thinks");
        Assert.Equal(unscaledNoLongThinks.Value, scaledNoLongThinks.Value, 6);
    }

    [Fact]
    public void Every_report_carries_the_timing_only_caveat()
    {
        const string timingOnly = "This detector analyses timing only.";

        var insufficient = EngineAssistanceAnalyzer.Analyze(
            UserId, DisplayName, [Game([Ply(9, 3000, 200)])], GeneratedAt);
        Assert.Contains(insufficient.Caveats, c => c.Contains(timingOnly));

        var sufficientGames = Enumerable.Range(0, 6).Select(_ => MetronomicGame(50)).ToList();
        var sufficient = EngineAssistanceAnalyzer.Analyze(UserId, DisplayName, sufficientGames, GeneratedAt);
        Assert.Contains(sufficient.Caveats, c => c.Contains(timingOnly));
    }

    [Fact]
    public void Evidence_strings_contain_the_measured_numbers()
    {
        var games = Enumerable.Range(0, 6).Select(_ => HumanLikeGame(50, 1.0)).ToList();
        var report = EngineAssistanceAnalyzer.Analyze(UserId, DisplayName, games, GeneratedAt);

        Assert.Equal(4, report.Signals.Length);
        foreach (var signal in report.Signals)
            Assert.Contains(signal.Evidence, ch => char.IsDigit(ch));
    }
}
