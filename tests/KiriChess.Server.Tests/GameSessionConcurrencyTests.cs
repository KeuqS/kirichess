using System.Collections.Concurrent;
using KiriChess.Engine;
using KiriChess.Engine.Config;
using KiriChess.Engine.Core;
using KiriChess.Server.Data.Entities;
using KiriChess.Server.Games;

namespace KiriChess.Server.Tests;

/// <summary>
/// The coverage <see cref="GameSession"/> never had: several tasks on one session at once. Nothing in
/// the suite used to hold two callers against a single session, which is why an unsynchronised move
/// log, an unsynchronised flag-fall check and an unsynchronised RTT estimate all survived. A session
/// is genuinely reached from five directions in production - the hub's move path (a second tab, or a
/// command forwarded from another instance), the HTTP move endpoint, the public bot API, the
/// one-second flag-fall sweep plus the lazy check on every registry read, the latency probe, and the
/// write-behind snapshot persister - and SignalR's per-client invocation limit serialises exactly one
/// of them.
///
/// These tests deliberately use a static <see cref="FakeGameClock"/>: they are about the interleaving,
/// not about time, and advancing a clock from a worker thread would be measuring the fake instead.
/// </summary>
public sealed class GameSessionConcurrencyTests
{
    private static KiriChessConfig CreateTestConfig() => new KiriChessConfig
    {
        MoveCostMode = MoveCostMode.None,
        CheckCheckerGain = 0,
        CheckVictimLoss = 0
    };

    private static GameSession CreateSession(Guid whiteId, Guid blackId, KiriChessConfig config, FakeGameClock clock) =>
        new(new KiriChessEngine(), config, whiteId, blackId, "bounty1", true, clock);

    [Fact]
    public async Task Two_connections_submitting_the_same_move_at_once_apply_exactly_one_ply()
    {
        // The concrete C-5-2 failure: two callers both pass the IsOver / PlayerOnTurn / IsLegal checks
        // before either writes, so both broadcast, both take ply number 1, and one anchor's think-time
        // is charged twice.
        var solo = Guid.NewGuid();
        var session = CreateSession(solo, solo, CreateTestConfig(), new FakeGameClock());

        const int contenders = 32;
        using var start = new ManualResetEventSlim(false);
        var applied = 0;
        var rejected = 0;
        var failures = new ConcurrentBag<Exception>();

        var racers = Enumerable.Range(0, contenders).Select(_ => Task.Run(() =>
        {
            start.Wait();
            try
            {
                session.ApplyMove(solo, Move.Parse("e2e4"));
                Interlocked.Increment(ref applied);
            }
            catch (InvalidGameActionException)
            {
                Interlocked.Increment(ref rejected); // lost the race, which is the correct outcome
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        })).ToArray();

        start.Set();
        await Task.WhenAll(racers);

        Assert.True(failures.IsEmpty, string.Join("\n", failures.Select(e => e.ToString())));
        Assert.Equal(1, applied);
        Assert.Equal(contenders - 1, rejected);
        Assert.Equal(1, session.MoveCount);
        Assert.Equal(1, session.Moves[0].Ply);
        Assert.Equal("e2e4", session.Moves[0].Uci);
    }

    [Fact]
    public async Task Hammering_one_session_from_many_tasks_keeps_the_move_log_consistent()
    {
        var solo = Guid.NewGuid();
        var session = CreateSession(solo, solo, CreateTestConfig(), new FakeGameClock());

        var failures = new ConcurrentBag<Exception>();
        using var readersStop = new CancellationTokenSource();

        // Four movers, each picking a legal move at random from a seeded RNG. A move that has become
        // illegal, or a game that ended under them, is an InvalidGameActionException and is expected;
        // anything else is the bug this test exists for.
        var movers = Enumerable.Range(0, 4).Select(seed => Task.Run(() =>
        {
            var rng = new Random(seed);
            for (var i = 0; i < 500 && !session.IsOver; i++)
            {
                try
                {
                    var legal = session.LegalMoves();
                    if (legal.Count == 0) break;
                    session.ApplyMove(solo, legal[rng.Next(legal.Count)]);
                }
                catch (InvalidGameActionException)
                {
                    // Lost the race for this ply, or the game ended: both correct.
                }
                catch (Exception ex)
                {
                    failures.Add(ex);
                    return;
                }
            }
        })).ToArray();

        // Four readers standing in for the sweep, the registry's lazy check, a spectator join, the
        // snapshot persister and the latency probe. The snapshot each one takes must be internally
        // consistent even though moves are landing throughout.
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            while (!readersStop.IsCancellationRequested)
            {
                try
                {
                    var snapshot = session.Moves;
                    for (var p = 0; p < snapshot.Count; p++)
                        if (snapshot[p].Ply != p + 1)
                            failures.Add(new InvalidOperationException(
                                $"Move log snapshot had ply {snapshot[p].Ply} at index {p}."));

                    var entity = session.ToEntity();
                    if (entity.Moves.Count != entity.PlyCount)
                        failures.Add(new InvalidOperationException(
                            $"Entity had {entity.Moves.Count} moves but PlyCount {entity.PlyCount}."));

                    _ = session.MoveCount;
                    _ = session.LiveRemaining(Side.White);
                    session.CheckFlagFall();
                    session.ReportLatency(solo, TimeSpan.FromMilliseconds(30));
                }
                catch (Exception ex)
                {
                    failures.Add(ex);
                    return;
                }
            }
        })).ToArray();

        await Task.WhenAll(movers);
        await readersStop.CancelAsync();
        await Task.WhenAll(readers);

        Assert.True(failures.IsEmpty, string.Join("\n", failures.Select(e => e.ToString())));

        var moves = session.Moves;
        Assert.NotEmpty(moves);
        for (var i = 0; i < moves.Count; i++)
            Assert.Equal(i + 1, moves[i].Ply);

        Assert.Equal(moves.Count, session.MoveCount);
        var final = session.ToEntity();
        Assert.Equal(moves.Count, final.PlyCount);
        Assert.Equal(moves.Count, final.Moves.Count);
    }

    [Fact]
    public async Task A_move_racing_the_flag_fall_check_leaves_one_coherent_ending()
    {
        // Finish() writing Status/EndedAt while ApplyMove overwrote Status with the move's own result
        // is how a player wins on time and then does not. With both under one gate the loser of the
        // race sees IsOver and stops, whichever way round it went.
        var white = Guid.NewGuid();
        var black = Guid.NewGuid();
        var config = CreateTestConfig();
        config.StartTimeSeconds = 2;
        var clock = new FakeGameClock();
        var session = CreateSession(white, black, config, clock);

        clock.Advance(TimeSpan.FromSeconds(5)); // White is past their flag before anyone touches the session.

        using var start = new ManualResetEventSlim(false);
        var endings = 0;
        var failures = new ConcurrentBag<Exception>();

        var racers = Enumerable.Range(0, 16).Select(i => Task.Run(() =>
        {
            start.Wait();
            try
            {
                if (i % 2 == 0)
                {
                    if (session.CheckFlagFall()) Interlocked.Increment(ref endings);
                }
                else
                {
                    session.ApplyMove(white, Move.Parse("e2e4"));
                    Interlocked.Increment(ref endings);
                }
            }
            catch (InvalidGameActionException)
            {
                // The other side of the race got there first.
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        })).ToArray();

        start.Set();
        await Task.WhenAll(racers);

        Assert.True(failures.IsEmpty, string.Join("\n", failures.Select(e => e.ToString())));

        // Exactly one caller ended the game, and both possible winners of the race agree on the
        // outcome: White was out of time, so Black wins on the flag either way. The only difference is
        // whether the losing move made it into the log first.
        Assert.Equal(1, endings);
        Assert.True(session.IsOver);
        Assert.Equal(GameStatus.FlagFall, session.Status);
        Assert.Equal(TerminationReason.Flagfall, session.Termination);
        Assert.Equal(Side.Black, session.Winner);
        Assert.NotNull(session.EndedAt);
        Assert.InRange(session.MoveCount, 0, 1);
    }

    [Fact]
    public void Moves_hands_out_a_snapshot_not_the_live_list()
    {
        // The persister, the spectator join and the sweep all enumerate this while a hub thread may be
        // inside _moves.Add. A snapshot taken before a move must not grow underneath its reader.
        var solo = Guid.NewGuid();
        var session = CreateSession(solo, solo, CreateTestConfig(), new FakeGameClock());

        session.ApplyMove(solo, Move.Parse("e2e4"));
        var snapshot = session.Moves;

        session.ApplyMove(solo, Move.Parse("e7e5"));

        Assert.Single(snapshot);
        Assert.Equal(2, session.MoveCount);
        Assert.NotSame(snapshot, session.Moves);
    }
}
