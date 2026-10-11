using System.Diagnostics;
using KiriChess.Engine;
using KiriChess.Engine.Config;
using KiriChess.Engine.Core;
using Xunit.Abstractions;

namespace KiriChess.Engine.Tests;

/// <summary>
/// Engine invariants around move generation and application, plus a throughput measurement.
///
/// <para>These tests outlived Phase 7.4, which is <b>closed and will not be retried</b>: the item
/// existed only to feed a server-side KiriChess search, and 7.3 decided bots run client-side, so
/// nothing in the server process searches. Two optimisations were tried against KiriChessEngine and
/// both were reverted:</para>
///
/// <list type="bullet">
/// <item>A bounded FEN→move-list cache plus a thread-local board cache: measured <b>0.98x</b>. This
/// workload makes ~421 LegalMoves calls against 9,322 Apply calls, and Apply rebuilds its own board
/// regardless, so caching the other two paths can only ever reach ~4% of the work.</item>
/// <item>Passing <c>generateSan: false</c> to move generation: no measurable gain either, so the
/// engine was restored to exactly its pre-7.4 state rather than carrying a change that bought
/// nothing.</item>
/// </list>
///
/// <para>Do not re-attempt either, they are measured, not guessed. Should a server-side search ever
/// be reinstated, the only remaining win is inside Apply: make/unmake against a single board, not
/// out here.</para>
///
/// <para><b>Finding, unrelated to 7.4 and pre-existing:</b> <c>LegalMoves</c> returns moves in a
/// <b>non-deterministic order between runs</b>, Gera.Chess evidently parallelises generation. This
/// reproduces on the original, untouched engine code, so it has been true since Phase 0. It is
/// benign today because every consumer treats the list as a set (UI hint dots, the bot's random
/// fallback, the legal-moves endpoint), but it means no future search can assume reproducible move
/// ordering without sorting first. <see cref="LegalMoveOrderIsNotStableButResultsAre"/> pins the
/// invariant that actually holds.</para>
/// </summary>
public class EngineThroughputTests
{
    private readonly ITestOutputHelper _output;

    public EngineThroughputTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// The reference workload: an exhaustive depth-3 expansion from the start position under the
    /// steal1 preset. Every Apply is one node. Returns every observable about every node, so two
    /// runs can be compared field by field.
    /// </summary>
    private static List<string> RunWorkload(KiriChessEngine engine, int depth, out long nodes)
    {
        var config = KiriChessConfig.CreateSteal1();
        var log = new List<string>();
        long counted = 0;

        void Expand(GamePosition position, int remaining)
        {
            if (remaining == 0) return;

            foreach (var move in engine.LegalMoves(position))
            {
                var result = engine.Apply(position, move, config, thinkTimeSeconds: 0);
                counted++;

                var events = string.Join(
                    "|",
                    result.Events.Select(e => $"{e.Type}:{e.Side}:{e.Amount}:{e.Detail ?? "-"}"));
                log.Add(
                    $"{move}>{result.NewPosition.Fen}>{result.WhiteTimeDelta}>{result.BlackTimeDelta}>" +
                    $"{result.Status}>{result.Winner?.ToString() ?? "-"}>{events}");

                if (!result.IsGameOver) Expand(result.NewPosition, remaining - 1);
            }
        }

        Expand(engine.NewGame(config), depth);
        nodes = counted;
        return log;
    }

    /// <summary>
    /// The engine does <b>not</b> promise a stable move order, Gera.Chess returns legal moves in a
    /// different order on every run (see the note on this class). What it must promise, and what
    /// everything downstream actually relies on, is that the same tree yields the same *set* of
    /// nodes with the same per-move results. So this compares sorted multisets, and would fail if
    /// move generation ever started producing genuinely different moves or different economics.
    /// </summary>
    [Fact]
    public void LegalMoveOrderIsNotStableButResultsAre()
    {
        var first = RunWorkload(new KiriChessEngine(), depth: 3, out var firstNodes);
        var second = RunWorkload(new KiriChessEngine(), depth: 3, out var secondNodes);

        Assert.Equal(firstNodes, secondNodes);
        Assert.Equal(first.Count, second.Count);

        first.Sort(StringComparer.Ordinal);
        second.Sort(StringComparer.Ordinal);
        Assert.Equal(first, second);
    }

    /// <summary>
    /// Repeatedly visiting the same position must keep returning the same legal moves, including
    /// while Apply is being called against that position in between.
    /// </summary>
    [Fact]
    public void RepeatedVisitsToTheSamePositionAreStable()
    {
        var engine = new KiriChessEngine();
        var config = KiriChessConfig.CreateSteal1();
        var position = engine.NewGame(config);

        var first = engine.LegalMoves(position).Select(m => m.ToString()).OrderBy(s => s).ToList();

        for (int i = 0; i < 50; i++)
        {
            var again = engine.LegalMoves(position).Select(m => m.ToString()).OrderBy(s => s).ToList();
            Assert.Equal(first, again);

            var result = engine.Apply(position, Move.Parse("e2e4"), config);
            Assert.Equal(GameStatus.Ongoing, result.Status);
        }

        Assert.Equal(first, engine.LegalMoves(position).Select(m => m.ToString()).OrderBy(s => s).ToList());
    }

    /// <summary>
    /// IsLegal and LegalMoves must agree. They are separate code paths against the same board, and
    /// skipping SAN generation on one of them must not have changed which moves exist.
    /// </summary>
    [Fact]
    public void IsLegalAgreesWithLegalMoves()
    {
        var engine = new KiriChessEngine();
        var config = KiriChessConfig.CreateSteal1();
        var position = engine.NewGame(config);

        foreach (var move in engine.LegalMoves(position))
            Assert.True(engine.IsLegal(position, move), $"{move} was enumerated but rejected.");

        Assert.False(engine.IsLegal(position, Move.Parse("e2e5")));
        Assert.False(engine.IsLegal(position, Move.Parse("a1a8")));
    }

    /// <summary>
    /// The measurement, kept so the next throughput attempt has a baseline to beat. Asserts only a
    /// very loose bound, this runs on every PR and a tight timing assertion would flake on a shared
    /// CI runner. The number is for the log, not for the gate.
    /// </summary>
    [Fact]
    public void MeasureThroughput()
    {
        RunWorkload(new KiriChessEngine(), depth: 2, out _);   // warm the JIT

        var sw = Stopwatch.StartNew();
        RunWorkload(new KiriChessEngine(), depth: 3, out var nodes);
        sw.Stop();

        var rate = nodes / Math.Max(sw.Elapsed.TotalSeconds, 0.0001);
        _output.WriteLine($"nodes : {nodes:N0}");
        _output.WriteLine($"time  : {sw.ElapsedMilliseconds:N0} ms");
        _output.WriteLine($"rate  : {rate:N0} nodes/sec");

        Assert.True(sw.Elapsed.TotalSeconds < 60, "Workload should complete well inside a minute.");
    }
}
