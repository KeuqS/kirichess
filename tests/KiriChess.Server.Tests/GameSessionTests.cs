using KiriChess.Engine;
using KiriChess.Engine.Config;
using KiriChess.Engine.Core;
using KiriChess.Server.Data.Entities;
using KiriChess.Server.Games;

namespace KiriChess.Server.Tests;

public sealed class GameSessionTests
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
    public void ApplyMove_by_wrong_player_throws()
    {
        var white = Guid.NewGuid();
        var black = Guid.NewGuid();
        var (session, _) = CreateSession(white, black);

        var ex = Assert.Throws<InvalidGameActionException>(() => session.ApplyMove(black, Move.Parse("e2e4")));
        Assert.Equal("Not your turn.", ex.Message);
    }

    [Fact]
    public void Legal_move_sequence_advances_position_and_move_log()
    {
        var soloId = Guid.NewGuid();
        var (session, _) = CreateSession(soloId, soloId);

        var startFen = session.Position.Fen;

        session.ApplyMove(soloId, Move.Parse("e2e4"));
        session.ApplyMove(soloId, Move.Parse("e7e5"));
        session.ApplyMove(soloId, Move.Parse("g1f3"));
        session.ApplyMove(soloId, Move.Parse("b8c6"));

        Assert.Equal(4, session.Moves.Count);
        Assert.NotEqual(startFen, session.Position.Fen);
        Assert.Equal(GameStatus.Ongoing, session.Status);
        Assert.False(session.IsOver);
    }

    [Fact]
    public void Think_time_drains_the_movers_clock()
    {
        var soloId = Guid.NewGuid();
        var config = CreateTestConfig();
        config.StartTimeSeconds = 180;
        config.CheckVictimLoss = 0;
        var (session, clock) = CreateSession(soloId, soloId, config);

        clock.Advance(TimeSpan.FromSeconds(10));
        session.ApplyMove(soloId, Move.Parse("e2e4"));

        // Test economy => no gains/losses beyond think-time; White spent exactly 10s thinking.
        Assert.Equal(170, session.Position.WhiteTimeSeconds);
        Assert.Equal(180, session.Position.BlackTimeSeconds);
    }

    [Fact]
    public void CheckFlagFall_ends_game_when_live_time_hits_zero()
    {
        var white = Guid.NewGuid();
        var black = Guid.NewGuid();
        var config = CreateTestConfig();
        config.StartTimeSeconds = 2;
        var (session, clock) = CreateSession(white, black, config);

        clock.Advance(TimeSpan.FromSeconds(5));

        var flagged = session.CheckFlagFall();

        Assert.True(flagged);
        Assert.True(session.IsOver);
        Assert.Equal(TerminationReason.Flagfall, session.Termination);
        Assert.Equal(Side.Black, session.Winner); // White was to move and flagged.
    }

    [Fact]
    public void Resign_ends_game_with_opponent_as_winner()
    {
        var white = Guid.NewGuid();
        var black = Guid.NewGuid();
        var (session, _) = CreateSession(white, black);

        session.Resign(white);

        Assert.True(session.IsOver);
        Assert.Equal(Side.Black, session.Winner);
        Assert.Equal(TerminationReason.Resignation, session.Termination);
    }

    [Fact]
    public void Mainspring_session_does_not_credit_captor_on_capture()
    {
        // A Mainspring (Burn) game must never hand the captor time. This proves the tier's config
        // reaches the engine end-to-end through the server session, not just the raw engine.
        var soloId = Guid.NewGuid();
        var (session, _) = CreateSession(soloId, soloId, KiriChessConfig.CreateBurn2(), pool: "burn2");

        Assert.False(session.Config.CaptureCaptorGainEnabled);

        session.ApplyMove(soloId, Move.Parse("e2e4"));
        session.ApplyMove(soloId, Move.Parse("d7d5"));
        var capture = session.ApplyMove(soloId, Move.Parse("e4d5")); // White captures the d5 pawn.

        // Captor gets no positive credit: no CaptureGain event and a non-positive net delta.
        Assert.DoesNotContain(capture.Events, e => e.Type == ClockEventType.CaptureGain);
        Assert.True(capture.WhiteTimeDelta <= 0);
        // The victim still loses the captured piece's value (Burn keeps victim-loss on).
        Assert.Contains(capture.Events, e => e.Type == ClockEventType.CaptureLoss);
        Assert.True(capture.BlackTimeDelta < 0);
    }

    [Fact]
    public void AbortNoShow_voids_the_game_with_no_winner_and_maps_to_aborted()
    {
        var white = Guid.NewGuid();
        var black = Guid.NewGuid();
        var (session, _) = CreateSession(white, black);

        session.AbortNoShow(white);

        Assert.Equal(GameStatus.Aborted, session.Status);
        Assert.Null(session.Winner);
        Assert.Equal(TerminationReason.NoShow, session.Termination);
        var entity = session.ToEntity();
        Assert.Equal(GameResult.Aborted, entity.Result);
        Assert.Equal(white, entity.NoShowAtFaultPlayerId);
    }

    [Fact]
    public void AbortNoShow_with_null_at_fault_stores_null_on_the_entity()
    {
        var white = Guid.NewGuid();
        var black = Guid.NewGuid();
        var (session, _) = CreateSession(white, black);

        session.AbortNoShow(null);

        var entity = session.ToEntity();
        Assert.Null(entity.NoShowAtFaultPlayerId);
        Assert.Equal(GameResult.Aborted, entity.Result);
        Assert.Equal(TerminationReason.NoShow, entity.Termination);
        Assert.Null(session.Winner);
    }

    [Fact]
    public void AbortNoShow_on_an_already_finished_session_throws()
    {
        var white = Guid.NewGuid();
        var black = Guid.NewGuid();
        var (session, _) = CreateSession(white, black);

        session.Resign(white);

        Assert.Throws<InvalidGameActionException>(() => session.AbortNoShow(null));
    }

    [Fact]
    public void ToEntity_maps_resignation_correctly()
    {
        var white = Guid.NewGuid();
        var black = Guid.NewGuid();
        var config = CreateTestConfig();
        var (session, _) = CreateSession(white, black, config);

        session.ApplyMove(white, Move.Parse("e2e4"));
        session.Resign(black);

        var entity = session.ToEntity();

        Assert.Equal(GameResult.WhiteWins, entity.Result);
        Assert.Equal(TerminationReason.Resignation, entity.Termination);
        Assert.Single(entity.Moves);
        Assert.Same(config, entity.ConfigSnapshot);
    }

    [Fact]
    public void ApplyMove_of_well_formed_uci_off_an_empty_square_is_an_illegal_move()
    {
        var white = Guid.NewGuid();
        var black = Guid.NewGuid();
        var (session, _) = CreateSession(white, black);

        var ex = Assert.Throws<InvalidGameActionException>(() => session.ApplyMove(white, Move.Parse("e3e4")));
        Assert.Equal("Illegal move 'e3e4'.", ex.Message);
    }

    // ------------------------------------------------------------------------------
    // Flag vs. insufficient material on the *server* path (C-4-1, C-10-5).
    //
    // The rule had exactly one behavioural test and it was an engine test, so it only
    // ever covered the branch that almost never fires: the mover carrying their own clock
    // below zero on a move they actually submitted. The path that fires for an ordinary
    // think-time flag is CheckFlagFall, which GameRegistry.Get runs on every read, and it
    // used to hand the win to the opponent unconditionally. flagVsInsufficientMaterial-
    // IsDraw ships in all six rated pools and the result goes straight into Glicko-2, so
    // the two tests below are the server-side proof, and the second one exists so a rule
    // that simply draws everything cannot pass for a working one.
    // ------------------------------------------------------------------------------

    /// <summary>
    /// A session sitting at an arbitrary position. GameSession's public constructor always starts
    /// from the standard opening, and Restore with an empty move log is the supported way to seed a
    /// live session at a given FEN (it is what boot-time recovery does), so an endgame is reachable
    /// without playing forty plies to get to it.
    /// </summary>
    private static (GameSession session, FakeGameClock clock) SessionAt(string fen, int startSeconds)
    {
        var clock = new FakeGameClock();
        var config = CreateTestConfig();
        config.StartTimeSeconds = startSeconds;

        var entity = new Game
        {
            Id = Guid.NewGuid(),
            WhitePlayerId = Guid.NewGuid(),
            BlackPlayerId = Guid.NewGuid(),
            RatingPool = "bounty1",
            IsRated = true,
            StartFen = fen,
            ConfigSnapshot = config,
            Moves = new List<MoveRecord>(),
            CreatedAt = clock.UtcNow
        };

        return (GameSession.Restore(new KiriChessEngine(), entity, clock), clock);
    }

    [Fact]
    public void CheckFlagFall_against_an_opponent_who_cannot_mate_is_a_draw()
    {
        // White holds K+Q, Black a bare king. White simply stops moving and flags. Black cannot
        // mate with a lone king, so docs/RULES.md ("You run out of time: Loss, unless the opponent
        // has insufficient mating material, then a draw") makes this a draw, not a Black win.
        var (session, clock) = SessionAt("4k3/8/8/8/8/8/8/3QK3 w - - 0 1", startSeconds: 3);
        Assert.True(session.Config.FlagVsInsufficientMaterialIsDraw);
        Assert.Equal(Side.White, session.Position.SideToMove);

        clock.Advance(TimeSpan.FromSeconds(10));

        Assert.True(session.CheckFlagFall());
        Assert.True(session.IsOver);
        Assert.Null(session.Winner);
        Assert.Equal(GameStatus.DrawByInsufficientMaterial, session.Status);
        Assert.Equal(TerminationReason.DrawByInsufficientMaterial, session.Termination);
        // The rating path reads the entity, not the session, so the draw has to survive that hop.
        Assert.Equal(GameResult.Draw, session.ToEntity().Result);
    }

    [Fact]
    public void CheckFlagFall_against_an_opponent_who_can_mate_is_still_a_loss()
    {
        // Same shape, one piece different: Black now has a queen and can mate, so the flag decides
        // the game. Without this case the test above would also pass on a rule that drew everything.
        var (session, clock) = SessionAt("3qk3/8/8/8/8/8/8/3QK3 w - - 0 1", startSeconds: 3);

        clock.Advance(TimeSpan.FromSeconds(10));

        Assert.True(session.CheckFlagFall());
        Assert.True(session.IsOver);
        Assert.Equal(Side.Black, session.Winner);
        Assert.Equal(GameStatus.FlagFall, session.Status);
        Assert.Equal(TerminationReason.Flagfall, session.Termination);
        Assert.Equal(GameResult.BlackWins, session.ToEntity().Result);
    }

    // ------------------------------------------------------------------------------
    // C-4-2: think time floors to whole seconds and the anchor is then reset, so the
    // sub-second remainder used to be thrown away every ply. The shortfall accumulates
    // linearly with move count and is deterministically exploitable by a caller that
    // submits just under an integer boundary.
    // ------------------------------------------------------------------------------

    private static readonly string[] TwelveQuietPlies =
    {
        "e2e4", "e7e5", "g1f3", "b8c6", "f1c4", "g8f6",
        "d2d3", "f8c5", "b1c3", "d7d6", "c1g5", "c8g4"
    };

    [Fact]
    public void Sub_second_think_time_is_carried_forward_instead_of_discarded()
    {
        var soloId = Guid.NewGuid();
        var config = CreateTestConfig();
        config.StartTimeSeconds = 60;
        var (session, clock) = CreateSession(soloId, soloId, config);

        foreach (var uci in TwelveQuietPlies)
        {
            clock.Advance(TimeSpan.FromMilliseconds(1900));
            session.ApplyMove(soloId, Move.Parse(uci));
        }

        // Six plies a side at exactly 1.9s is 11.4 real seconds of thinking each. Flooring and
        // discarding the remainder charged 6, a 47% discount that is 0.9s x 6 moves, not an
        // artifact of the interval chosen.
        Assert.InRange(60 - session.Position.WhiteTimeSeconds, 11, 12);
        Assert.InRange(60 - session.Position.BlackTimeSeconds, 11, 12);
    }

    [Fact]
    public void The_carried_remainder_belongs_to_the_side_that_spent_it()
    {
        var soloId = Guid.NewGuid();
        var config = CreateTestConfig();
        config.StartTimeSeconds = 60;
        var (session, clock) = CreateSession(soloId, soloId, config);

        // 1.6 + 0.5 for each side, taken in turn. Per side that is 2.1s, charged 2. One shared
        // carry instead would hand Black the remainder White left and charge 1 and 3.
        foreach (var (uci, ms) in new[]
                 {
                     ("e2e4", 1600), ("e7e5", 1600), ("g1f3", 500), ("b8c6", 500)
                 })
        {
            clock.Advance(TimeSpan.FromMilliseconds(ms));

            // Reading the clock must never consume the carry: both of these run on every
            // registry read, and neither charges anything.
            session.LiveRemaining(Side.White);
            session.LiveRemaining(Side.Black);
            Assert.False(session.CheckFlagFall());

            session.ApplyMove(soloId, Move.Parse(uci));
        }

        Assert.Equal(58, session.Position.WhiteTimeSeconds);
        Assert.Equal(58, session.Position.BlackTimeSeconds);
    }

    // ------------------------------------------------------------------------------
    // The wire clock is whole seconds. The fraction already charged (this turn, plus carry
    // from the previous one) has to travel with it, or the client counts a full extra second
    // and can still show time when the flag falls.
    // ------------------------------------------------------------------------------

    [Fact]
    public void Live_snapshot_reports_the_fraction_already_charged_this_turn()
    {
        var white = Guid.NewGuid();
        var black = Guid.NewGuid();
        var config = CreateTestConfig();
        config.StartTimeSeconds = 60;
        var (session, clock) = CreateSession(white, black, config);

        clock.Advance(TimeSpan.FromMilliseconds(1600));

        var snap = session.LiveClockSnapshot();
        Assert.Equal(59, snap.WhiteSeconds);
        Assert.Equal(60, snap.BlackSeconds);
        Assert.Equal(600, snap.ActiveOffsetMs);
        Assert.Equal(snap.ActiveOffsetMs, session.ToState().ActiveClockOffsetMs);
    }

    [Fact]
    public void Live_snapshot_includes_carry_at_the_start_of_the_next_turn()
    {
        var soloId = Guid.NewGuid();
        var config = CreateTestConfig();
        config.StartTimeSeconds = 60;
        var (session, clock) = CreateSession(soloId, soloId, config);

        clock.Advance(TimeSpan.FromMilliseconds(1900));
        session.ApplyMove(soloId, Move.Parse("e2e4"));
        clock.Advance(TimeSpan.FromMilliseconds(50));
        session.ApplyMove(soloId, Move.Parse("e7e5"));

        // White thought 1.9s and was billed 1. The other 0.9s is due immediately on the next
        // turn, while the integer still reads 59. The face has to start at 58.1.
        var snap = session.LiveClockSnapshot();
        Assert.Equal(Side.White, session.Position.SideToMove);
        Assert.Equal(59, snap.WhiteSeconds);
        Assert.Equal(900, snap.ActiveOffsetMs);
        Assert.Equal(58.1, snap.WhiteSeconds - snap.ActiveOffsetMs / 1000d, precision: 3);
    }

    [Fact]
    public void Live_snapshot_adds_half_the_round_trip_only_after_the_lag_credit_is_spent()
    {
        var white = Guid.NewGuid();
        var black = Guid.NewGuid();
        var config = CreateTestConfig();
        config.StartTimeSeconds = 60;
        var (session, clock) = CreateSession(white, black, config);
        session.ReportLatency(white, TimeSpan.FromMilliseconds(200));

        clock.Advance(TimeSpan.FromMilliseconds(100));
        var duringCredit = session.LiveClockSnapshot();
        Assert.Equal(60, duringCredit.WhiteSeconds);
        Assert.Equal(0, duringCredit.ActiveOffsetMs);

        clock.Advance(TimeSpan.FromMilliseconds(1400));
        // 1500ms raw, 200ms credit: 1300ms charged. 300ms of that is past the whole second,
        // plus 100ms for the one-way flight the credit no longer covers.
        var afterCredit = session.LiveClockSnapshot();
        Assert.Equal(59, afterCredit.WhiteSeconds);
        Assert.Equal(400, afterCredit.ActiveOffsetMs);
    }

    [Fact]
    public void Live_snapshot_offset_is_zero_once_the_game_is_over()
    {
        var white = Guid.NewGuid();
        var black = Guid.NewGuid();
        var config = CreateTestConfig();
        config.StartTimeSeconds = 2;
        var (session, clock) = CreateSession(white, black, config);

        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.True(session.CheckFlagFall());

        var snap = session.LiveClockSnapshot();
        Assert.Equal(0, snap.ActiveOffsetMs);
        Assert.Equal(0, session.ToState().ActiveClockOffsetMs);
    }
}
