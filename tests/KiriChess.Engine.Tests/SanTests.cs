using KiriChess.Engine.Config;
using KiriChess.Engine.Core;

namespace KiriChess.Engine.Tests;

/// <summary>
/// Pins the empirical question Phase 9e.2 (Phase 9e.2) opens Step 1 with: does Gera.Chess
/// 1.2.0 stamp <c>Move.San</c> onto the move object passed to <c>ChessBoard.Move</c> as a side effect
/// of executing it, or only onto the board's own executed-move list. Confirmed here: <c>chessMove.San</c>
/// is populated after <c>board.Move(chessMove)</c> returns, so <see cref="KiriChessEngine.Apply"/>
/// reads it straight off the move it just executed rather than falling back to
/// <c>ChessBoard.ExecutedMoves.LastOrDefault()</c>.
/// </summary>
public class SanTests
{
    private readonly KiriChessEngine _engine = new();

    [Fact]
    public void Quiet_pawn_move_from_start_gives_e4()
    {
        var config = TestSupport.AllZeroed();
        var pos = _engine.NewGame(config);

        var result = _engine.Apply(pos, new Move("e2", "e4"), config);

        Assert.Equal("e4", result.San);
    }

    [Fact]
    public void Knight_move_gives_Nf3()
    {
        var config = TestSupport.AllZeroed();
        var pos = _engine.NewGame(config);

        var result = _engine.Apply(pos, new Move("g1", "f3"), config);

        Assert.Equal("Nf3", result.San);
    }

    [Fact]
    public void Capture_gives_the_x_form()
    {
        var config = TestSupport.AllZeroed();
        var pos = _engine.NewGame(config);
        var afterE4 = _engine.Apply(pos, new Move("e2", "e4"), config);
        var afterD5 = _engine.Apply(afterE4.NewPosition, new Move("d7", "d5"), config);

        var result = _engine.Apply(afterD5.NewPosition, new Move("e4", "d5"), config);

        Assert.Equal("exd5", result.San);
    }

    [Fact]
    public void Castling_gives_O_O()
    {
        var config = TestSupport.AllZeroed();
        var pos = _engine.FromFen(config, "r3k2r/8/8/8/8/8/8/R3K2R w KQkq - 0 1");

        var shortCastle = _engine.Apply(pos, new Move("e1", "g1"), config);
        var longCastle = _engine.Apply(pos, new Move("e1", "c1"), config);

        // Assert what the library actually returns, capital letter O versus digit 0: this is
        // load-bearing for the format's interop story (docs/game/PGN.md), not a stylistic pick.
        Assert.Equal("O-O", shortCastle.San);
        Assert.Equal("O-O-O", longCastle.San);
    }

    [Fact]
    public void Promotion_gives_the_equals_form()
    {
        var config = TestSupport.AllZeroed();
        var pos = _engine.FromFen(config, "4k3/P7/8/8/8/8/8/4K3 w - - 0 1");

        var result = _engine.Apply(pos, new Move("a7", "a8", PieceType.Queen), config);

        // The promoted queen also checks the king on e8 along rank 8 from this exact FEN, so the
        // suffix belongs in the assertion rather than being a surprise: it is the same +/# logic
        // Check_gives_a_plus_suffix pins, just incidentally triggered here too.
        Assert.Equal("a8=Q+", result.San);
    }

    [Fact]
    public void Check_gives_a_plus_suffix()
    {
        var config = TestSupport.AllZeroed();
        // White queen delivers check, no mate: kings far apart, nothing blocks the escape squares.
        var pos = _engine.FromFen(config, "4k3/8/8/8/8/8/8/Q3K3 w - - 0 1");

        var result = _engine.Apply(pos, new Move("a1", "a8"), config);

        Assert.EndsWith("+", result.San);
    }

    [Fact]
    public void Mate_gives_a_hash_suffix()
    {
        var config = TestSupport.AllZeroed();
        // Fool's mate, one move from completion: White has played 1. f3 2. g4, Black mates with
        // Qd8-h4 along the now-open diagonal.
        var pos = _engine.FromFen(config, "rnbqkbnr/pppp1ppp/8/4p3/6P1/5P2/PPPPP2P/RNBQKBNR b KQkq - 0 2");

        var result = _engine.Apply(pos, new Move("d8", "h4"), config);

        Assert.EndsWith("#", result.San);
    }

    [Fact]
    public void Two_knights_able_to_reach_the_same_square_produce_a_disambiguated_san()
    {
        var config = TestSupport.AllZeroed();
        // Knights on b1 and f1 can both reach d2; a cleared board keeps every other square out of
        // the way so the only ambiguity is exactly the one this test wants to pin. The white pawn on
        // h2 is otherwise irrelevant to the move; it exists only so the library's insufficient-material
        // auto-detection does not treat two bare knights as an already-drawn, unplayable position.
        var pos = _engine.FromFen(config, "4k3/8/8/8/8/8/7P/1N3NK1 w - - 0 1");

        var result = _engine.Apply(pos, new Move("f1", "d2"), config);

        // Both N-on-b1 and N-on-f1 can reach d2; disambiguation must name the file.
        Assert.Equal("Nfd2", result.San);
    }

    [Fact]
    public void Stalemating_move_has_no_suffix()
    {
        var config = TestSupport.AllZeroed();
        var pos = _engine.FromFen(config, "7k/8/8/8/3PQ3/8/7K/8 w - - 15 151");

        var result = _engine.Apply(pos, new Move("e4", "g6"), config);

        Assert.Equal(GameStatus.Stalemate, result.Status);
        Assert.Equal("Qg6", result.San);
    }
}
