using KiriChess.Engine.Config;
using KiriChess.Engine.Core;

namespace KiriChess.Engine.Tests;

public class GameEndTests
{
    private readonly KiriChessEngine _engine = new();

    [Fact]
    public void Checkmate_WinsRegardlessOfFlag()
    {
        // Back-rank mate: 1.Ra8#. White is almost out of time and "thinks" far past the flag,
        // but delivering mate must end the game as a win regardless (FIDE-style).
        var config = TestSupport.AllZeroed(startSeconds: 5);
        var pos = _engine.FromFen(config, "6k1/5ppp/8/8/8/8/8/R5K1 w - - 0 1");

        var r = _engine.Apply(pos, new Move("a1", "a8"), config, thinkTimeSeconds: 100);

        Assert.Equal(GameStatus.Checkmate, r.Status);
        Assert.Equal(Side.White, r.Winner);
    }

    [Fact]
    public void FlagFall_MidThink_IsDetected_OpponentWins()
    {
        var config = TestSupport.AllZeroed(startSeconds: 10);
        var pos = _engine.NewGame(config);

        // White spends 20s on a quiet move with only 10s on the clock.
        var r = _engine.Apply(pos, new Move("e2", "e4"), config, thinkTimeSeconds: 20);

        Assert.Equal(GameStatus.FlagFall, r.Status);
        Assert.Equal(Side.Black, r.Winner);
        Assert.Equal(0, r.NewPosition.WhiteTimeSeconds); // clamped, never negative
        Assert.True(r.Has(ClockEventType.FlagFall));
    }

    [Fact]
    public void FlagFall_VsLoneKing_IsDraw()
    {
        var config = TestSupport.AllZeroed(startSeconds: 5);
        config.FlagVsInsufficientMaterialIsDraw = true;
        // Black has only a king; if white flags, black cannot mate -> draw.
        var pos = _engine.FromFen(config, "k7/8/8/8/8/8/8/R3K3 w - - 0 1");

        var r = _engine.Apply(pos, new Move("a1", "b1"), config, thinkTimeSeconds: 50);

        Assert.Equal(GameStatus.DrawByInsufficientMaterial, r.Status);
        Assert.Null(r.Winner);
    }

    // ------------------------------------------------------------------------------
    // Flag precedence (C-3-1). docs/RULES.md's "How games end" table gives checkmate,
    // stalemate and threefold their outcomes unconditionally, and ResolveStatus used to
    // honour that for checkmate only: the other three sat below the flag branch, so the
    // mover lost on time on the very ply that ended the game in a draw. These three are
    // fixed positions on purpose. The generative suite cannot be relied on to find them
    // (a 25x soak over all six pools never sampled a threefold and a flag on one ply),
    // because the counterexample needs both to happen on the same move.
    //
    // Each position deliberately leaves the flagging side's opponent holding mating
    // material, so FlagVsInsufficientMaterialIsDraw cannot return the right outcome under
    // the wrong name and hide a still-wrong ordering.
    // ------------------------------------------------------------------------------

    [Fact]
    public void Stalemate_BeatsTheFlag()
    {
        var config = TestSupport.AllZeroed(startSeconds: 5);
        // 1.Qf7 stalemates: the black king on h8 has g7 and g8 covered by the queen, h7 is its own
        // pawn, and that pawn is blocked by White's on h6. Black keeps a pawn, so Black can mate.
        var pos = _engine.FromFen(config, "7k/7p/7P/8/8/8/8/5QK1 w - - 0 1");

        // White stalemates while thinking ten times their remaining clock away.
        var r = _engine.Apply(pos, new Move("f1", "f7"), config, thinkTimeSeconds: 50);

        Assert.Equal(GameStatus.Stalemate, r.Status);
        Assert.Null(r.Winner);
        // The position ended the game, not the clock, so no FlagFall event is itemised.
        Assert.False(r.Has(ClockEventType.FlagFall));
    }

    [Fact]
    public void ThreefoldRepetition_BeatsTheFlag()
    {
        var config = TestSupport.AllZeroed(startSeconds: 30);
        // The knight shuffle returns to this position after every four plies, so the eighth ply
        // (Black's) is the third occurrence. Both sides keep a pawn: mating material on both sides,
        // and no fifty-move interference at eight halfmoves.
        var pos = _engine.FromFen(config, "4k3/7p/8/8/8/8/7P/4K1N1 w - - 0 1");

        foreach (var uci in new[] { "g1f3", "e8d8", "f3g1", "d8e8", "g1f3", "e8d8", "f3g1" })
        {
            var step = _engine.Apply(pos, Move.Parse(uci), config);
            Assert.Equal(GameStatus.Ongoing, step.Status);
            pos = step.NewPosition;
        }

        // Black completes the third occurrence and runs their own clock out doing it.
        var r = _engine.Apply(pos, new Move("d8", "e8"), config, thinkTimeSeconds: 100);

        Assert.Equal(GameStatus.DrawByRepetition, r.Status);
        Assert.Null(r.Winner);
        Assert.False(r.Has(ClockEventType.FlagFall));
    }

    [Fact]
    public void FiftyMoveRule_BeatsTheFlag()
    {
        var config = TestSupport.AllZeroed(startSeconds: 30);
        config.ThreefoldRepetitionIsDraw = false; // isolate the 50-move rule
        // Halfmove clock at 98, knights shuffling. The two pawns are there only so both sides hold
        // mating material; neither can move, so they never reset the halfmove clock.
        var pos = _engine.FromFen(config, "4k1n1/7p/8/8/8/8/7P/4K1N1 w - - 98 60");

        var r1 = _engine.Apply(pos, new Move("g1", "f3"), config); // halfmove 98 -> 99
        Assert.Equal(GameStatus.Ongoing, r1.Status);

        // Black crosses 100 halfmoves and flags on the same ply.
        var r2 = _engine.Apply(r1.NewPosition, new Move("g8", "f6"), config, thinkTimeSeconds: 100);

        Assert.Equal(GameStatus.DrawByFiftyMove, r2.Status);
        Assert.Null(r2.Winner);
        Assert.False(r2.Has(ClockEventType.FlagFall));
    }

    [Fact]
    public void Stalemate_IsDetected()
    {
        var config = TestSupport.AllZeroed();
        // 1.Qf7 leaves the black king on h8 with no legal move and not in check.
        var pos = _engine.FromFen(config, "7k/8/6K1/8/8/8/8/5Q2 w - - 0 1");

        var r = _engine.Apply(pos, new Move("f1", "f7"), config);
        Assert.Equal(GameStatus.Stalemate, r.Status);
        Assert.Null(r.Winner);
    }

    [Fact]
    public void FiftyMoveRule_FiresThroughPerMoveFenRebuild()
    {
        var config = TestSupport.AllZeroed();
        config.ThreefoldRepetitionIsDraw = false; // isolate the 50-move rule

        // Kings + knights only, halfmove clock already at 98. Knights shuffle with no captures and
        // no pawn moves, so crossing 100 halfmoves trips the 50-move rule. Because the engine rebuilds
        // the board from FEN every move, this proves the halfmove clock survives LoadFromFen/ToFen.
        var pos = _engine.FromFen(config, "4k1n1/8/8/8/8/8/8/4K1N1 w - - 98 60");

        var r1 = _engine.Apply(pos, new Move("g1", "f3"), config); // halfmove 98 -> 99
        Assert.Equal(GameStatus.Ongoing, r1.Status);

        var r2 = _engine.Apply(r1.NewPosition, new Move("g8", "f6"), config); // 99 -> 100
        Assert.Equal(GameStatus.DrawByFiftyMove, r2.Status);
        Assert.Null(r2.Winner);
    }

    [Fact]
    public void AllZeroedConfig_BehavesLikeOrdinaryChess()
    {
        var config = TestSupport.AllZeroed();
        var pos = _engine.NewGame(config);

        // A capture and a check produce no clock movement whatsoever when every dial is zero.
        foreach (var mv in new[]
                 {
                     new Move("e2", "e4"), new Move("e7", "e5"),
                     new Move("d1", "h5"), new Move("b8", "c6"),
                     new Move("f1", "c4"), new Move("g8", "f6"),
                     new Move("h5", "e5") // queen grabs a pawn, gives no bonus
                 })
        {
            var r = _engine.Apply(pos, mv, config);
            Assert.Equal(0, r.WhiteTimeDelta);
            Assert.Equal(0, r.BlackTimeDelta);
            pos = r.NewPosition;
        }

        Assert.Equal(180, pos.WhiteTimeSeconds);
        Assert.Equal(180, pos.BlackTimeSeconds);
    }

    // ------------------------------------------------------------------------------
    // C-3-3: FromFen loads a position the underlying library already considers finished
    // (AutoRules auto-detects insufficient material and the fifty-move rule at load time).
    // LegalMoves still lists moves and IsLegal still says yes, so Apply used to reach
    // board.Move and let Chess.ChessGameEndedException out of the engine. No library
    // exception type may cross the engine boundary.
    // ------------------------------------------------------------------------------

    [Theory]
    [InlineData("4k3/8/8/8/8/8/8/4KN2 w - - 0 1", "f1", "e3")]   // K+N vs K
    [InlineData("4k3/8/8/8/8/8/8/4KB2 w - - 0 1", "f1", "e2")]   // K+B vs K
    [InlineData("4k3/8/8/8/8/8/8/R3K3 w - - 100 1", "a1", "a2")] // halfmove clock at 100
    public void Apply_IntoAnAlreadyFinishedPosition_ThrowsTheEnginesOwnException(
        string fen, string from, string to)
    {
        var config = TestSupport.AllZeroed();
        var pos = _engine.FromFen(config, fen);
        var move = new Move(from, to);

        // The leak's precondition: the engine still offers the move, so a caller has every
        // reason to submit it.
        Assert.NotEmpty(_engine.LegalMoves(pos));
        Assert.True(_engine.IsLegal(pos, move));

        var ex = Assert.Throws<ArgumentException>(() => _engine.Apply(pos, move, config));
        Assert.Contains("already finished", ex.Message);
    }
}
