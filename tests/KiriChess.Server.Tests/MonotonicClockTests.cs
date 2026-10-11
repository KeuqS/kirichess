using KiriChess.Engine;
using KiriChess.Engine.Config;
using KiriChess.Engine.Core;
using KiriChess.Server.Data.Entities;
using KiriChess.Server.Games;

namespace KiriChess.Server.Tests;

/// <summary>
/// Phase 6f.1: proves <see cref="GameSession"/> measures every interval off the monotonic side of
/// <see cref="IGameClock"/> and is immune to a wall-clock step, forward or backward, while the
/// wall-clock instants (<c>ReceivedAtUtc</c>) still follow the wall clock, including a step, because
/// that is what makes them comparable to every other timestamp in the database.
/// </summary>
public sealed class MonotonicClockTests
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
    public void Wall_clock_step_forward_does_not_charge_the_player_on_move()
    {
        var white = Guid.NewGuid();
        var black = Guid.NewGuid();
        var config = CreateTestConfig();
        var (session, clock) = CreateSession(white, black, config);

        clock.Advance(TimeSpan.FromSeconds(3));
        clock.StepWallClock(TimeSpan.FromMinutes(2));

        Assert.Equal(config.StartTimeSeconds - 3, session.LiveRemaining(Side.White));
    }

    [Fact]
    public void Wall_clock_step_backward_does_not_hand_the_player_free_time()
    {
        var white = Guid.NewGuid();
        var black = Guid.NewGuid();
        var config = CreateTestConfig();
        var (session, clock) = CreateSession(white, black, config);

        clock.Advance(TimeSpan.FromSeconds(10));
        clock.StepWallClock(TimeSpan.FromMinutes(-1));

        // Under the old code (_clock() - LastMoveAtUtc) this subtraction would go negative and
        // Math.Max(0, ...) would clamp it to zero elapsed, free time for the player on move.
        Assert.Equal(config.StartTimeSeconds - 10, session.LiveRemaining(Side.White));
    }

    [Fact]
    public void Wall_clock_step_does_not_change_the_think_time_charged_to_the_engine()
    {
        var white = Guid.NewGuid();
        var black = Guid.NewGuid();
        var config = CreateTestConfig();
        config.StartTimeSeconds = 180;
        var (session, clock) = CreateSession(white, black, config);

        clock.Advance(TimeSpan.FromSeconds(10));
        clock.StepWallClock(TimeSpan.FromSeconds(30));
        session.ApplyMove(white, Move.Parse("e2e4"));

        Assert.Equal(170, session.Position.WhiteTimeSeconds);
    }

    [Fact]
    public void Wall_clock_step_forward_cannot_flag_a_player()
    {
        var white = Guid.NewGuid();
        var black = Guid.NewGuid();
        var config = CreateTestConfig();
        config.StartTimeSeconds = 10;
        var (session, clock) = CreateSession(white, black, config);

        clock.Advance(TimeSpan.FromSeconds(2));
        clock.StepWallClock(TimeSpan.FromHours(1));

        Assert.False(session.CheckFlagFall());
        Assert.False(session.IsOver);
    }

    [Fact]
    public void ThinkMs_telemetry_is_measured_monotonically()
    {
        var white = Guid.NewGuid();
        var black = Guid.NewGuid();
        var config = CreateTestConfig();
        var (session, clock) = CreateSession(white, black, config);

        clock.Advance(TimeSpan.FromMilliseconds(2500));
        clock.StepWallClock(TimeSpan.FromSeconds(5));
        session.ApplyMove(white, Move.Parse("e2e4"));

        Assert.Equal(2500, session.Moves[0].ThinkMs);
    }

    [Fact]
    public void ReceivedAtUtc_follows_the_wall_clock_including_a_step()
    {
        // The deliberate other half of this file: a persisted instant *should* reflect a wall-clock
        // correction, because that is what makes it comparable to every other timestamp in the
        // database. This is the test that stops a later reader from "fixing" ReceivedAtUtc onto the
        // monotonic source and destroying its meaning.
        var white = Guid.NewGuid();
        var black = Guid.NewGuid();
        var config = CreateTestConfig();
        var (session, clock) = CreateSession(white, black, config);

        clock.Advance(TimeSpan.FromSeconds(1));
        clock.StepWallClock(TimeSpan.FromSeconds(5));
        session.ApplyMove(white, Move.Parse("e2e4"));

        Assert.Equal(clock.UtcNow, session.Moves[0].ReceivedAtUtc);
    }

    [Fact]
    public void Restore_re_anchors_the_monotonic_source()
    {
        var white = Guid.NewGuid();
        var black = Guid.NewGuid();
        var config = CreateTestConfig();
        var (session, clock) = CreateSession(white, black, config);

        clock.Advance(TimeSpan.FromSeconds(5));
        session.ApplyMove(white, Move.Parse("e2e4"));

        var entity = session.ToEntity();
        // A fresh instance: a restore genuinely happens in a new process with a new monotonic origin,
        // and reusing the same clock as `session` would hide a bug where Restore forgets to re-anchor.
        var restoreClock = new FakeGameClock();
        var restored = GameSession.Restore(new KiriChessEngine(), entity, restoreClock);

        var lastMove = entity.Moves[^1];
        Assert.Equal(lastMove.WhiteTimeAfter, restored.LiveRemaining(Side.White));
        Assert.Equal(lastMove.BlackTimeAfter, restored.LiveRemaining(Side.Black));

        restoreClock.Advance(TimeSpan.FromSeconds(4));

        Assert.Equal(lastMove.BlackTimeAfter - 4, restored.LiveRemaining(Side.Black));
    }
}
