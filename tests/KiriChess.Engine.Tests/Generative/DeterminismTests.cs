using KiriChess.Engine.Config;
using KiriChess.Engine.Core;
using Xunit.Abstractions;

namespace KiriChess.Engine.Tests.Generative;

/// <summary>
/// Purity/reproducibility invariants for the engine (Phase 10b.2, Task B). <c>.claude/rules/engine.md</c>
/// states the engine must be pure and deterministic - no wall-clock reads, no randomness, no I/O - but
/// before this file the only coverage of that was <see cref="EngineThroughputTests"/>'s depth-3 set
/// comparison and <c>FullGameTests.TwoEngineInstances_AgreeOnTheSameScriptedGame</c>'s seven scripted
/// moves. This extends the same properties to whole random games, well past the ply 200 mark that
/// 9b.3's own load-testing caveat says nothing in the repo had explored ("no rung ran longer than
/// 180s").
///
/// <para>Every invariant here was validated by the same 27,313-ply probe as
/// <see cref="EconomyInvariantTests"/>; see that class's remarks and
/// <c>Phase 10b.2</c> for the write-up.</para>
/// </summary>
public sealed class DeterminismTests
{
    private readonly ITestOutputHelper _output;

    public DeterminismTests(ITestOutputHelper output) => _output = output;

    public static IEnumerable<object[]> Pools => SelfPlay.PoolKeys.Select(k => new object[] { k });

    /// <summary>
    /// Games per pool on a default run. Kept smaller than <see cref="EconomyInvariantTests"/>'s because
    /// two of these four tests double or triple the number of <c>Apply</c> calls per ply (a full replay
    /// through a second engine, or a repeated + cross-instance re-application), and the class's slice of
    /// the shared 25s budget has to cover all four theories across all six pools. A first pass at 6
    /// measured ~9s for all four theories combined (see the PR report); halved to 3, which still keeps
    /// <see cref="RepeatedAndCrossInstanceApplyAgreeOverFullGames"/> comfortably past ply 200 on the
    /// longer-clocked pools.
    /// </summary>
    private const int GamesPerPool = 3;

    // ------------------------------------------------------------------------------------------
    // 1. Apply does not mutate its input.
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// <see cref="GamePosition"/> is documented as immutable and <see cref="KiriChessEngine.Apply"/>
    /// copies the repetition-counts dictionary rather than writing through it - this pins that it stays
    /// that way. It matters because a caller holding a pre-move position (the server's "apply then
    /// inspect" path does) would otherwise see it change under it.
    ///
    /// <para>The shared driver hands <c>inspect</c> a ply strictly <i>after</i> its <c>Apply</c> call
    /// already ran, so there is no hook to snapshot "before" in the usual sense. Instead this exploits
    /// the fact that <c>ply.Result.NewPosition</c> for ply N is the exact same object <c>SelfPlay.Walk</c>
    /// then hands back as <c>ply.Before</c> for ply N+1: a snapshot taken the moment a position is
    /// produced (nothing has used it as input to anything yet) can be compared against that same
    /// position's fields the next time it is seen, which is exactly the mutation window this invariant
    /// cares about. The very first ply is checked against an independently-created
    /// <c>engine.NewGame(config)</c>, which is safe because <c>NewGame</c>'s own determinism is what
    /// <see cref="ReplayingAGamesUciListReproducesItExactly"/> and
    /// <see cref="RepeatedAndCrossInstanceApplyAgreeOverFullGames"/> establish.</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(Pools))]
    public void ApplyDoesNotMutateItsInput(string poolKey)
    {
        var engine = new KiriChessEngine();
        var config = SelfPlay.ConfigFor(poolKey);
        var ledger = new InvariantLedger();
        var games = GenerativeBudget.Games(GamesPerPool);

        for (var g = 0; g < games; g++)
        {
            var seed = SelfPlay.SeedFor(poolKey, g);
            PositionSnapshot? previous = Snapshot(engine.NewGame(config));

            SelfPlay.Walk(engine, config, poolKey, seed, ply =>
            {
                if (previous is { } prior)
                {
                    if (prior.MatchesCurrentValueOf(ply.Before)) ledger.Checked();
                    else ledger.Violation("ApplyDoesNotMutateItsInput", ply, "Before's Fen/clocks/RepetitionCounts changed between when it was produced and when it was next used as Apply's input.");
                }

                previous = Snapshot(ply.Result.NewPosition);
            });
        }

        ledger.AssertClean($"ApplyDoesNotMutateItsInput[{poolKey}]");
    }

    private readonly record struct PositionSnapshot(string Fen, int White, int Black, Dictionary<string, int> Counts)
    {
        public bool MatchesCurrentValueOf(GamePosition p) =>
            Fen == p.Fen && White == p.WhiteTimeSeconds && Black == p.BlackTimeSeconds &&
            Counts.Count == p.RepetitionCounts.Count &&
            Counts.All(kv => p.RepetitionCounts.TryGetValue(kv.Key, out var v) && v == kv.Value);
    }

    private static PositionSnapshot Snapshot(GamePosition p) =>
        new(p.Fen, p.WhiteTimeSeconds, p.BlackTimeSeconds, new Dictionary<string, int>(p.RepetitionCounts));

    // ------------------------------------------------------------------------------------------
    // 2. Replaying a game reproduces it exactly.
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// Plays a game, keeps its UCI + per-ply think-time list, then replays that list move by move from
    /// a fresh <c>NewGame</c> through a <b>second</b> engine instance, asserting every ply agrees field
    /// by field. Think time must be replayed identically for the clocks to match, which is why this
    /// captures <see cref="Ply.ThinkSeconds"/> from the original walk rather than re-rolling it.
    /// </summary>
    [Theory]
    [MemberData(nameof(Pools))]
    public void ReplayingAGamesUciListReproducesItExactly(string poolKey)
    {
        var engineA = new KiriChessEngine();
        var config = SelfPlay.ConfigFor(poolKey);
        var ledger = new InvariantLedger();
        var games = GenerativeBudget.Games(GamesPerPool);

        for (var g = 0; g < games; g++)
        {
            var seed = SelfPlay.SeedFor(poolKey, g);
            var recorded = new List<(int Index, string Uci, int Think, ApplyResult Result)>();

            SelfPlay.Walk(engineA, config, poolKey, seed,
                ply => recorded.Add((ply.Index, ply.Move.ToString(), ply.ThinkSeconds, ply.Result)),
                rng => rng.Next(0, 4));

            var engineB = new KiriChessEngine();
            var position = engineB.NewGame(config);

            foreach (var (index, uci, think, expected) in recorded)
            {
                var actual = engineB.Apply(position, Move.Parse(uci), config, think);
                CompareApplyResults(expected, actual, "ReplayReproducesTheGameExactly", poolKey, seed, index, ledger);
                position = actual.NewPosition;
            }
        }

        ledger.AssertClean($"ReplayReproducesTheGameExactly[{poolKey}]");
    }

    // ------------------------------------------------------------------------------------------
    // 3. Two engine instances agree, and the same instance agrees with itself.
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// The same (position, move, config, think) applied repeatedly to the same engine instance, and
    /// applied once more to a second instance, must always yield an identical <see cref="ApplyResult"/>.
    /// <see cref="EngineThroughputTests.LegalMoveOrderIsNotStableButResultsAre"/> established this for a
    /// depth-3 tree; this runs it over full random games, well past the ply-200 mark nothing else in the
    /// repo explores (see the class remarks).
    /// </summary>
    [Theory]
    [MemberData(nameof(Pools))]
    public void RepeatedAndCrossInstanceApplyAgreeOverFullGames(string poolKey)
    {
        var primary = new KiriChessEngine();
        var secondary = new KiriChessEngine();
        var config = SelfPlay.ConfigFor(poolKey);
        var ledger = new InvariantLedger();
        var games = GenerativeBudget.Games(GamesPerPool);
        var deepestPlyChecked = -1;

        for (var g = 0; g < games; g++)
        {
            var seed = SelfPlay.SeedFor(poolKey, g);

            SelfPlay.Walk(primary, config, poolKey, seed, ply =>
            {
                deepestPlyChecked = Math.Max(deepestPlyChecked, ply.Index);

                var repeated = primary.Apply(ply.Before, ply.Move, ply.Config, ply.ThinkSeconds);
                CompareApplyResults(ply.Result, repeated, "SameInstanceRepeatsItself", poolKey, seed, ply.Index, ledger);

                var crossInstance = secondary.Apply(ply.Before, ply.Move, ply.Config, ply.ThinkSeconds);
                CompareApplyResults(ply.Result, crossInstance, "SecondInstanceAgrees", poolKey, seed, ply.Index, ledger);
            }, rng => rng.Next(0, 4));
        }

        _output.WriteLine($"{poolKey}: deepest ply index checked = {deepestPlyChecked}");
        ledger.AssertClean($"RepeatedAndCrossInstanceApplyAgree[{poolKey}]");
    }

    private static void CompareApplyResults(ApplyResult expected, ApplyResult actual, string invariant, string poolKey, int seed, int plyIndex, InvariantLedger ledger)
    {
        void Check(bool ok, string what)
        {
            if (ok) ledger.Checked();
            else ledger.Violation(invariant, $"pool={poolKey} seed={seed} ply={plyIndex}: {what}");
        }

        Check(expected.NewPosition.Fen == actual.NewPosition.Fen, $"Fen differs: '{expected.NewPosition.Fen}' vs '{actual.NewPosition.Fen}'.");
        Check(expected.NewPosition.WhiteTimeSeconds == actual.NewPosition.WhiteTimeSeconds, $"WhiteTimeSeconds differs: {expected.NewPosition.WhiteTimeSeconds} vs {actual.NewPosition.WhiteTimeSeconds}.");
        Check(expected.NewPosition.BlackTimeSeconds == actual.NewPosition.BlackTimeSeconds, $"BlackTimeSeconds differs: {expected.NewPosition.BlackTimeSeconds} vs {actual.NewPosition.BlackTimeSeconds}.");
        Check(expected.WhiteTimeDelta == actual.WhiteTimeDelta, $"WhiteTimeDelta differs: {expected.WhiteTimeDelta} vs {actual.WhiteTimeDelta}.");
        Check(expected.BlackTimeDelta == actual.BlackTimeDelta, $"BlackTimeDelta differs: {expected.BlackTimeDelta} vs {actual.BlackTimeDelta}.");
        Check(expected.Status == actual.Status, $"Status differs: {expected.Status} vs {actual.Status}.");
        Check(expected.Winner == actual.Winner, $"Winner differs: {expected.Winner} vs {actual.Winner}.");

        Check(expected.Events.Count == actual.Events.Count, $"Events count differs: {expected.Events.Count} vs {actual.Events.Count}.");
        var n = Math.Min(expected.Events.Count, actual.Events.Count);
        for (var i = 0; i < n; i++)
        {
            var e = expected.Events[i];
            var a = actual.Events[i];
            Check(e.Type == a.Type && e.Side == a.Side && e.Amount == a.Amount && e.Detail == a.Detail,
                $"event[{i}] differs: {e.Type}:{e.Side}:{e.Amount}:{e.Detail ?? "-"} vs {a.Type}:{a.Side}:{a.Amount}:{a.Detail ?? "-"}.");
        }
    }

    // ------------------------------------------------------------------------------------------
    // 4. FromFen round-trips a mid-game position, but deliberately not its clocks or history.
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// For sampled mid-game positions, <c>FromFen(config, position.Fen)</c> reproduces the FEN, the
    /// side to move and the exact legal-move set - but starts both clocks at
    /// <c>config.StartTimeSeconds</c> and seeds a fresh occurrence count, which it deliberately does
    /// <b>not</b> preserve. That limitation is asserted here too, rather than left assumed, because
    /// <c>GameReplayBuilder</c> and <c>PgnBuilder</c> both start from <c>FromFen</c> and then re-drive
    /// the clocks themselves - silently starting to preserve clocks here would break that re-drive.
    /// Sampled every 15th ply: each hit does a second board rebuild via <c>FromFen</c> plus two
    /// <see cref="KiriChessEngine.LegalMoves"/> calls.
    /// </summary>
    [Theory]
    [MemberData(nameof(Pools))]
    public void FromFenReproducesAMidGamePositionButNotItsClocksOrHistory(string poolKey)
    {
        var engine = new KiriChessEngine();
        var config = SelfPlay.ConfigFor(poolKey);
        var ledger = new InvariantLedger();
        var games = GenerativeBudget.Games(GamesPerPool);

        for (var g = 0; g < games; g++)
        {
            var seed = SelfPlay.SeedFor(poolKey, g);

            SelfPlay.Walk(engine, config, poolKey, seed, ply =>
            {
                if (ply.Index % 15 != 0) return;

                var position = ply.Result.NewPosition;
                var reconstructed = engine.FromFen(config, position.Fen);

                void Check(bool ok, string what)
                {
                    if (ok) ledger.Checked();
                    else ledger.Violation("FromFenRoundTrip", ply, $"{what} (fen={position.Fen}).");
                }

                Check(reconstructed.Fen == position.Fen, "Fen was not reproduced");
                Check(reconstructed.SideToMove == position.SideToMove, "SideToMove was not reproduced");

                var expectedMoves = engine.LegalMoves(position).Select(m => m.ToString()).OrderBy(s => s, StringComparer.Ordinal).ToList();
                var actualMoves = engine.LegalMoves(reconstructed).Select(m => m.ToString()).OrderBy(s => s, StringComparer.Ordinal).ToList();
                Check(expectedMoves.SequenceEqual(actualMoves), "LegalMoves set differs between the original and the FromFen reconstruction");

                // Deliberately not preserved.
                Check(reconstructed.WhiteTimeSeconds == config.StartTimeSeconds, "WhiteTimeSeconds was not reset to config.StartTimeSeconds");
                Check(reconstructed.BlackTimeSeconds == config.StartTimeSeconds, "BlackTimeSeconds was not reset to config.StartTimeSeconds");
                Check(reconstructed.RepetitionCounts.Count == 1, "RepetitionCounts was not reseeded to a single fresh entry");
            });
        }

        ledger.AssertClean($"FromFenRoundTrip[{poolKey}]");
    }
}
