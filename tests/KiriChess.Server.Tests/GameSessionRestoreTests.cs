using KiriChess.Engine;
using KiriChess.Engine.Config;
using KiriChess.Engine.Core;
using KiriChess.Server.Data.Entities;
using KiriChess.Server.Games;

namespace KiriChess.Server.Tests;

/// <summary>
/// Pure (no Docker) coverage of <see cref="GameSession.Restore"/> and <see cref="GameSession.Abort"/>,
/// the two mechanisms behind Phase 6c's boot-time recovery and honesty-path abort.
/// </summary>
public sealed class GameSessionRestoreTests
{
    private static KiriChessConfig CreateTestConfig() => new KiriChessConfig
    {
        MoveCostMode = MoveCostMode.None,
        CheckCheckerGain = 0,
        CheckVictimLoss = 0
    };

    private static (GameSession session, FakeGameClock clock) CreateSession(
        Guid whiteId, Guid blackId, KiriChessConfig? config = null, string? pool = "bounty1", bool rated = true)
    {
        var clock = new FakeGameClock();
        var cfg = config ?? CreateTestConfig();
        var session = new GameSession(new KiriChessEngine(), cfg, whiteId, blackId, pool, rated, clock);
        return (session, clock);
    }

    [Fact]
    public void Restore_reproduces_position_moves_and_clocks_after_round_trip()
    {
        var white = Guid.NewGuid();
        var black = Guid.NewGuid();
        var config = CreateTestConfig();
        var (session, clock) = CreateSession(white, black, config);

        clock.Advance(TimeSpan.FromSeconds(5));
        session.ApplyMove(white, Move.Parse("e2e4"));
        clock.Advance(TimeSpan.FromSeconds(5));
        session.ApplyMove(black, Move.Parse("d7d5"));
        clock.Advance(TimeSpan.FromSeconds(5));
        session.ApplyMove(white, Move.Parse("e4d5")); // White captures the d5 pawn.
        clock.Advance(TimeSpan.FromSeconds(5));
        session.ApplyMove(black, Move.Parse("d8d5")); // Black recaptures with the queen.

        var entity = session.ToEntity();
        var restored = GameSession.Restore(new KiriChessEngine(), entity);

        Assert.Equal(session.Position.Fen, restored.Position.Fen);
        Assert.Equal(session.Moves.Count, restored.Moves.Count);
        Assert.Equal(session.Moves, restored.Moves);
        Assert.Equal(GameStatus.Ongoing, restored.Status);
        Assert.Equal(session.Id, restored.Id);
        Assert.Equal(session.CreatedAt, restored.CreatedAt);

        var lastMove = session.Moves[^1];
        Assert.Equal(lastMove.WhiteTimeAfter, restored.LiveRemaining(Side.White));
        Assert.Equal(lastMove.BlackTimeAfter, restored.LiveRemaining(Side.Black));

        Assert.NotEmpty(restored.LegalMoves());
        var onTurn = restored.PlayerOnTurn;
        var followUp = restored.ApplyMove(onTurn, restored.LegalMoves()[0]);
        Assert.NotNull(followUp);
    }

    [Fact]
    public void Downtime_after_restore_is_not_charged_to_the_on_turn_player()
    {
        var white = Guid.NewGuid();
        var black = Guid.NewGuid();
        var config = CreateTestConfig();
        var (session, clock) = CreateSession(white, black, config);

        clock.Advance(TimeSpan.FromSeconds(3));
        session.ApplyMove(white, Move.Parse("e2e4")); // Black is on turn afterward.

        var entity = session.ToEntity();
        // A fresh instance: a restore genuinely happens in a new process with a new monotonic origin,
        // and reusing the same clock as `session` would hide a bug where Restore forgets to re-anchor.
        var restoreClock = new FakeGameClock();
        var restored = GameSession.Restore(new KiriChessEngine(), entity, restoreClock);

        var recorded = entity.Moves[^1].BlackTimeAfter;
        restoreClock.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal(recorded - 10, restored.LiveRemaining(Side.Black));
    }

    [Fact]
    public void Replay_that_ends_in_checkmate_marks_the_restored_session_over()
    {
        var white = Guid.NewGuid();
        var black = Guid.NewGuid();
        var config = CreateTestConfig();
        var (session, _) = CreateSession(white, black, config);

        session.ApplyMove(white, Move.Parse("f2f3"));
        session.ApplyMove(black, Move.Parse("e7e5"));
        session.ApplyMove(white, Move.Parse("g2g4"));
        session.ApplyMove(black, Move.Parse("d8h4")); // Fool's mate.
        Assert.True(session.IsOver);
        Assert.Equal(GameStatus.Checkmate, session.Status);

        var entity = session.ToEntity();
        entity.Result = GameResult.Ongoing; // The crash landed before the final write reached Postgres.

        var restored = GameSession.Restore(new KiriChessEngine(), entity);

        Assert.True(restored.IsOver);
        Assert.Equal(GameStatus.Checkmate, restored.Status);
        Assert.Equal(Side.Black, restored.Winner);
    }

    [Fact]
    public void Restore_with_no_moves_yields_a_fresh_ongoing_position()
    {
        var white = Guid.NewGuid();
        var black = Guid.NewGuid();
        var config = CreateTestConfig();
        var engine = new KiriChessEngine();

        var entity = new Game
        {
            Id = Guid.NewGuid(),
            WhitePlayerId = white,
            BlackPlayerId = black,
            RatingPool = "bounty1",
            IsRated = true,
            ConfigSnapshot = config,
            Moves = new List<MoveRecord>(),
            CreatedAt = DateTimeOffset.UtcNow
        };

        var restored = GameSession.Restore(engine, entity);

        Assert.False(restored.IsOver);
        Assert.Equal(GameStatus.Ongoing, restored.Status);
        Assert.Equal(engine.NewGame(config).Fen, restored.Position.Fen);
        Assert.Equal(config.StartTimeSeconds, restored.LiveRemaining(Side.White));
        Assert.Equal(config.StartTimeSeconds, restored.LiveRemaining(Side.Black));
    }

    [Fact]
    public void Restore_replays_from_the_stored_StartFen_not_the_standard_start()
    {
        // GameReplayBuilder and PgnBuilder both root their replay at entity.StartFen; Restore used to
        // call NewGame unconditionally, so the one reconstruction path whose output resumes as a live
        // rated game was the one that would have replayed a custom start from the wrong position.
        var white = Guid.NewGuid();
        var black = Guid.NewGuid();
        var config = CreateTestConfig();
        var engine = new KiriChessEngine();
        const string startFen = "r3k2r/pppppppp/8/8/8/8/PPPPPPPP/R3K2R w KQkq - 0 1";

        var entity = new Game
        {
            Id = Guid.NewGuid(),
            WhitePlayerId = white,
            BlackPlayerId = black,
            RatingPool = "bounty1",
            IsRated = true,
            StartFen = startFen,
            ConfigSnapshot = config,
            Moves = new List<MoveRecord>
            {
                new(1, "a2a4", Side.White, config.StartTimeSeconds, config.StartTimeSeconds, 0, 0,
                    GameStatus.Ongoing.ToString())
            },
            CreatedAt = DateTimeOffset.UtcNow
        };

        var restored = GameSession.Restore(engine, entity);

        var expected = engine.Apply(
            engine.FromFen(config, startFen), Move.Parse("a2a4"), config, 0).NewPosition;

        Assert.Equal(expected.Fen, restored.Position.Fen);
        Assert.NotEqual(engine.NewGame(config).Fen, restored.Position.Fen);
        Assert.Equal(Side.Black, restored.Position.SideToMove);
        Assert.False(restored.IsOver);
    }

    [Fact]
    public void Restore_with_a_blank_StartFen_still_replays_from_the_standard_start()
    {
        // The same fallback the two builders spell out: rows written before StartFen was populated,
        // and any row hand-seeded without one, must keep working.
        var white = Guid.NewGuid();
        var black = Guid.NewGuid();
        var config = CreateTestConfig();
        var engine = new KiriChessEngine();

        var entity = new Game
        {
            Id = Guid.NewGuid(),
            WhitePlayerId = white,
            BlackPlayerId = black,
            RatingPool = "bounty1",
            IsRated = true,
            StartFen = "   ",
            ConfigSnapshot = config,
            Moves = new List<MoveRecord>(),
            CreatedAt = DateTimeOffset.UtcNow
        };

        var restored = GameSession.Restore(engine, entity);

        Assert.Equal(engine.NewGame(config).Fen, restored.Position.Fen);
    }

    [Fact]
    public void Abort_voids_the_game_with_no_winner_and_marks_it_a_server_restart()
    {
        var white = Guid.NewGuid();
        var black = Guid.NewGuid();
        var (session, _) = CreateSession(white, black);

        session.Abort();

        Assert.True(session.IsOver);
        Assert.Equal(GameStatus.Aborted, session.Status);
        Assert.Null(session.Winner);
        Assert.Equal(TerminationReason.ServerRestart, session.Termination);
        Assert.Equal(GameResult.Aborted, session.ToEntity().Result);
    }

    [Fact]
    public void Abort_on_a_finished_session_throws()
    {
        var white = Guid.NewGuid();
        var black = Guid.NewGuid();
        var (session, _) = CreateSession(white, black);

        session.Resign(white);

        Assert.Throws<InvalidGameActionException>(() => session.Abort());
    }

    [Fact]
    public void Restored_session_preserves_repetition_history_across_the_cut()
    {
        var white = Guid.NewGuid();
        var black = Guid.NewGuid();
        var config = new KiriChessConfig(); // Defaults include RepeatedPositionNullifiesTime = true.
        var engine = new KiriChessEngine();

        // Ground truth: play the whole repeating knight shuffle on one unrestored session.
        var (baseline, _) = CreateSession(white, black, config);
        baseline.ApplyMove(white, Move.Parse("g1f3"));
        baseline.ApplyMove(black, Move.Parse("g8f6"));
        baseline.ApplyMove(white, Move.Parse("f3g1"));
        var baselineRepeat = baseline.ApplyMove(black, Move.Parse("f6g8")); // Back to the start position.

        Assert.Contains(baselineRepeat.Events, e => e.Type == ClockEventType.RepetitionNullified);
        Assert.Equal(0, baselineRepeat.BlackTimeDelta);

        // Restore mid-sequence: replay only the first two plies, then continue past the cut.
        var (before, _) = CreateSession(white, black, config);
        before.ApplyMove(white, Move.Parse("g1f3"));
        before.ApplyMove(black, Move.Parse("g8f6"));
        var entity = before.ToEntity();

        var restored = GameSession.Restore(engine, entity);
        restored.ApplyMove(white, Move.Parse("f3g1"));
        var restoredRepeat = restored.ApplyMove(black, Move.Parse("f6g8"));

        Assert.Contains(restoredRepeat.Events, e => e.Type == ClockEventType.RepetitionNullified);
        Assert.Equal(0, restoredRepeat.BlackTimeDelta);
        Assert.Equal(baselineRepeat.BlackTimeDelta, restoredRepeat.BlackTimeDelta);
    }
}
