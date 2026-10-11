using KiriChess.Engine.Config;
using KiriChess.Engine.Core;
using Xunit.Abstractions;

namespace KiriChess.Engine.Tests.Generative;

/// <summary>
/// Ten invariants over the KiriChess clock economy - captures, tolls, check chips, repetition and
/// flag-fall - exercised by random legal self-play across all six shipped pools (Phase 10b.2, Task B).
///
/// <para><see cref="Rule1CaptureEconomyTests"/>, <see cref="Rule2MovementTollTests"/>,
/// <see cref="Rule3CheckChipTests"/> and <see cref="Rule4RepetitionGuardTests"/> each pin one rule at a
/// time, in an isolating config from <c>TestSupport</c>. Nothing before this file checked the rules
/// <b>together</b>, in the configs the site actually ships, over positions nobody chose by hand.</para>
///
/// <para>Every invariant below was validated by a throwaway probe across 27,313 plies over all six
/// pools before this file was written, and every one held - see
/// <c>Phase 10b.2</c> for the write-up. A failure here therefore means a real
/// regression in the economy or in this test's own modelling of it, not "the probe was lucky":
/// <b>do not weaken an assertion to make this pass.</b></para>
///
/// <para><b>Invariant 8, the victim-drain seam, needs particular care.</b> The engine only ever flags
/// the <i>mover's own</i> clock (<c>KiriChessEngine.ResolveStatus</c>'s <c>moverRawClock</c> check), so
/// a capture or check loss can legitimately drive the <i>non-moving</i> side's clock to 0 while the
/// result is still <c>Ongoing</c> - the drained side is by then on move, and
/// <c>GameSession.CheckFlagFall</c> plus the 1&nbsp;Hz <c>FlagFallMonitor</c> end the game on the very
/// next read. That is deliberate server/engine layering, not a bug, and this suite must not "fix" it:
/// the invariant below is deliberately phrased as "whichever side is at 0 is the side to move", which
/// is what actually always holds, rather than "no result is ever Ongoing with a 0 clock".</para>
/// </summary>
public sealed class EconomyInvariantTests
{
    private readonly ITestOutputHelper _output;

    public EconomyInvariantTests(ITestOutputHelper output) => _output = output;

    public static IEnumerable<object[]> Pools => SelfPlay.PoolKeys.Select(k => new object[] { k });

    /// <summary>
    /// Games per pool on a default run. A first pass at 10 games/pool measured 5.7s of the 6-pool
    /// theory's wall time (see the PR report), too much of the shared 25s Task A + Task B budget for
    /// what is meant to be a fast regression smoke test rather than a search - invariant 9's extra
    /// <see cref="KiriChessEngine.LegalMoves"/> call on almost every ply roughly doubles the walk's own
    /// per-ply cost. Halved to 5, which still exercises several thousand plies per pool.
    /// </summary>
    private const int GamesPerPool = 5;

    [Theory]
    [MemberData(nameof(Pools))]
    public void ClockEconomyInvariantsHoldAcrossRandomSelfPlay(string poolKey)
    {
        var engine = new KiriChessEngine();
        var config = SelfPlay.ConfigFor(poolKey);
        var ledger = new InvariantLedger();
        var games = GenerativeBudget.Games(GamesPerPool);

        long totalPlies = 0;
        var longestGame = 0;
        var outcomes = new Dictionary<GameStatus, int>();

        for (var g = 0; g < games; g++)
        {
            var seed = SelfPlay.SeedFor(poolKey, g);

            // Vary think time (0-3s) so flag-fall paths are exercised too, not just pure economy.
            var game = SelfPlay.Walk(engine, config, poolKey, seed, ply => CheckPly(engine, ply, ledger), rng => rng.Next(0, 4));

            totalPlies += game.Uci.Count;
            longestGame = Math.Max(longestGame, game.Uci.Count);
            outcomes[game.Status] = outcomes.GetValueOrDefault(game.Status) + 1;

            // Invariant 10: every game ends with a terminal status inside SelfPlay.MaxPlies, and the
            // engine never emits GameStatus.Aborted (ApplyResult's own doc comment: server-side only).
            if (game.Status == GameStatus.Ongoing)
                ledger.Violation("GameLevelTermination", $"pool={poolKey} seed={seed} hit the {SelfPlay.MaxPlies}-ply ceiling without a terminal status. moves={string.Join(" ", game.Uci)}");
            else
                ledger.Checked();

            if (game.Status == GameStatus.Aborted)
                ledger.Violation("NeverAborted", $"pool={poolKey} seed={seed} produced GameStatus.Aborted, which the engine must never emit.");
            else
                ledger.Checked();
        }

        _output.WriteLine(
            $"{poolKey}: games={games} plies={totalPlies} longestGame={longestGame} " +
            $"outcomes=[{string.Join(", ", outcomes.OrderBy(kv => kv.Key.ToString()).Select(kv => $"{kv.Key}={kv.Value}"))}]");

        ledger.AssertClean($"EconomyInvariants[{poolKey}]");
    }

    private static void CheckPly(KiriChessEngine engine, Ply ply, InvariantLedger ledger)
    {
        var mover = ply.Before.SideToMove;
        var result = ply.Result;
        var config = ply.Config;

        // 1. Clock arithmetic, including the clamp at zero.
        foreach (var side in Sides)
        {
            var think = side == mover ? ply.ThinkSeconds : 0;
            var expected = Math.Max(0, ply.Before.TimeFor(side) + result.DeltaFor(side) - think);
            var actual = result.NewPosition.TimeFor(side);
            if (actual == expected) ledger.Checked();
            else ledger.Violation("ClockArithmetic", ply, $"{side}: expected {expected} (before={ply.Before.TimeFor(side)}, delta={result.DeltaFor(side)}, think={think}), got {actual}.");
        }

        // 2. Events sum to the reported delta, per side.
        foreach (var side in Sides)
        {
            var sum = result.Events.Where(e => e.Side == side).Sum(e => e.Amount);
            if (sum == result.DeltaFor(side)) ledger.Checked();
            else ledger.Violation("EventsSumToDelta", ply, $"{side}: events sum to {sum}, reported delta is {result.DeltaFor(side)}.");
        }

        // 3. Fixed sign per event type.
        foreach (var e in result.Events)
        {
            var ok = e.Type switch
            {
                ClockEventType.CaptureGain or ClockEventType.CheckGain or ClockEventType.Increment => e.Amount >= 0,
                ClockEventType.CaptureLoss or ClockEventType.CheckLoss or ClockEventType.MovementToll
                    or ClockEventType.CastleCost or ClockEventType.PromotionCost => e.Amount <= 0,
                _ => true
            };
            if (ok) ledger.Checked();
            else ledger.Violation("FixedSignPerEventType", ply, $"{e.Type} carried amount {e.Amount}, which has the wrong sign for that event type.");
        }

        // 4. Only RepetitionNullified and FlagFall may carry a zero amount; Apply's local Add() drops
        // every other zero-amount event before it ever reaches the list.
        foreach (var e in result.Events)
        {
            var ok = e.Amount != 0 || e.Type is ClockEventType.RepetitionNullified or ClockEventType.FlagFall;
            if (ok) ledger.Checked();
            else ledger.Violation("OnlyMarkersAreZero", ply, $"{e.Type} carried a zero amount; only RepetitionNullified/FlagFall are allowed to.");
        }

        // 5. Rule 4 nullification is total: once RepetitionNullified fires, nothing but the base
        // increment (never a KiriChess rule) and a flag-fall marker may also appear.
        if (result.Events.Any(e => e.Type == ClockEventType.RepetitionNullified))
        {
            var stray = result.Events
                .Select(e => e.Type)
                .Where(t => t is not (ClockEventType.RepetitionNullified or ClockEventType.Increment or ClockEventType.FlagFall))
                .Distinct()
                .ToList();
            if (stray.Count == 0) ledger.Checked();
            else ledger.Violation("NullificationIsTotal", ply, $"RepetitionNullified fired alongside {string.Join(",", stray)}.");
        }

        // 6. EconomyScale is linear. Sampled every 37th ply (see class remarks): the probe found this
        // ratio enough to run 811 comparisons across the full validation and it keeps this test's cost
        // (two extra Apply calls per hit) a small fraction of the walk itself.
        if (ply.Index % 37 == 0)
            CheckEconomyScaleLinearity(engine, ply, ledger);

        // 7. Repetition bookkeeping (Rule 4's own history, not the underlying board's).
        CheckRepetitionBookkeeping(ply, ledger);

        // 8. A zero clock in an Ongoing result belongs to the side to move (the victim-drain seam
        // documented in the class remarks - pinned, not "fixed").
        if (config.FlagFallLoses && result.Status == GameStatus.Ongoing)
        {
            foreach (var side in Sides)
            {
                if (result.NewPosition.TimeFor(side) != 0) { ledger.Checked(); continue; }
                if (result.NewPosition.SideToMove == side) ledger.Checked();
                else ledger.Violation("ZeroClockBelongsToSideToMove", ply, $"{side}'s clock is 0 but {result.NewPosition.SideToMove} is to move, and the result is still Ongoing.");
            }
        }

        // 9. Ongoing implies a move exists; a position with none is checkmate or stalemate.
        if (result.Status == GameStatus.Ongoing)
        {
            if (engine.LegalMoves(result.NewPosition).Count > 0) ledger.Checked();
            else ledger.Violation("OngoingImpliesAMoveExists", ply, "Status is Ongoing but LegalMoves(NewPosition) is empty.");
        }
    }

    private static readonly Side[] Sides = { Side.White, Side.Black };

    private static void CheckEconomyScaleLinearity(KiriChessEngine engine, Ply ply, InvariantLedger ledger)
    {
        // Large StartTimeSeconds + FlagFallLoses=false so a flag never truncates the comparison; the
        // move's economy, not its clock outcome, is what this invariant is about.
        KiriChessConfig Comparison(int scale)
        {
            var c = ply.Config.Clone();
            c.EconomyScale = scale;
            c.StartTimeSeconds = 100_000;
            c.FlagFallLoses = false;
            return c;
        }

        var atScale1 = engine.Apply(ply.Before, ply.Move, Comparison(1), thinkTimeSeconds: 0);
        var atScale3 = engine.Apply(ply.Before, ply.Move, Comparison(3), thinkTimeSeconds: 0);

        if (atScale1.Events.Count != atScale3.Events.Count)
        {
            ledger.Violation("EconomyScaleIsLinear", ply, $"scale=1 produced {atScale1.Events.Count} events, scale=3 produced {atScale3.Events.Count}.");
            return;
        }

        for (var i = 0; i < atScale1.Events.Count; i++)
        {
            var e1 = atScale1.Events[i];
            var e3 = atScale3.Events[i];
            var amountOk = e1.Type == ClockEventType.Increment ? e3.Amount == e1.Amount : e3.Amount == e1.Amount * 3;
            var ok = e1.Type == e3.Type && e1.Side == e3.Side && amountOk;
            if (ok) ledger.Checked();
            else ledger.Violation("EconomyScaleIsLinear", ply, $"event[{i}]: scale=1 {e1.Type}:{e1.Side}:{e1.Amount}, scale=3 {e3.Type}:{e3.Side}:{e3.Amount}.");
        }
    }

    private static void CheckRepetitionBookkeeping(Ply ply, InvariantLedger ledger)
    {
        var before = ply.Before.RepetitionCounts;
        var after = ply.Result.NewPosition.RepetitionCounts;

        var neverDecreases = before.All(kv => after.GetValueOrDefault(kv.Key) >= kv.Value);
        if (neverDecreases) ledger.Checked();
        else ledger.Violation("RepetitionCountsNeverDecrease", ply, "some position key's occurrence count decreased across a single Apply.");

        string? increasedKey = null;
        var otherChange = false;
        foreach (var key in before.Keys.Union(after.Keys))
        {
            var diff = after.GetValueOrDefault(key) - before.GetValueOrDefault(key);
            if (diff == 1)
            {
                if (increasedKey is null) increasedKey = key; else otherChange = true;
            }
            else if (diff != 0) otherChange = true;
        }

        if (increasedKey is not null && !otherChange) ledger.Checked();
        else ledger.Violation("ExactlyOneKeyIncrementsPerPly", ply, $"expected exactly one key to increase by 1; found={(increasedKey is null ? "none" : "one")}, otherChange={otherChange}.");

        // The start position is counted at creation (+1), so after N plies the sum of every key's
        // occurrence count is N+1.
        var expectedSum = ply.Index + 2;
        var actualSum = after.Values.Sum();
        if (actualSum == expectedSum) ledger.Checked();
        else ledger.Violation("RepetitionCountsSumToPliesPlusOne", ply, $"expected sum {expectedSum} after {ply.Index + 1} plies, got {actualSum}.");

        if (ply.Config.ThreefoldRepetitionIsDraw && increasedKey is not null && after[increasedKey] >= 3)
        {
            if (ply.Result.Status == GameStatus.DrawByRepetition) ledger.Checked();
            else ledger.Violation("ThreefoldEndsTheGame", ply, $"key reached occurrence count {after[increasedKey]} under ThreefoldRepetitionIsDraw, but Status is {ply.Result.Status}, expected DrawByRepetition.");
        }
    }
}
