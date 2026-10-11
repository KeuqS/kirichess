using KiriChess.Engine.Config;
using KiriChess.Engine.Core;

namespace KiriChess.Engine.Tests;

/// <summary>
/// Phase 0 exit criterion: a full scripted game played through the engine, with both clocks
/// provably correct to the second at every step, ending in a decisive result.
/// </summary>
public class FullGameTests
{
    private readonly KiriChessEngine _engine = new();

    [Fact]
    public void ScholarsMate_UnderStandardConfig_ClocksAreCorrectToTheSecond()
    {
        var config = KiriChessConfig.CreateSteal1(); // PerPiece tolls, Steal economy, check chip on
        var start = config.StartTimeSeconds;

        var pos = _engine.NewGame(config);
        Assert.Equal(start, pos.WhiteTimeSeconds);
        Assert.Equal(start, pos.BlackTimeSeconds);

        // 1. e4        white pawn toll 1
        pos = Step(pos, config, "e2", "e4", expectWhite: start - 1, expectBlack: start);
        // 1... e5      black pawn toll 1
        pos = Step(pos, config, "e7", "e5", expectWhite: start - 1, expectBlack: start - 1);
        // 2. Bc4       white bishop toll 2
        pos = Step(pos, config, "f1", "c4", expectWhite: start - 3, expectBlack: start - 1);
        // 2... Nc6     black knight toll 2
        pos = Step(pos, config, "b8", "c6", expectWhite: start - 3, expectBlack: start - 3);
        // 3. Qh5       white queen toll 4
        pos = Step(pos, config, "d1", "h5", expectWhite: start - 7, expectBlack: start - 3);
        // 3... Nf6??   black knight toll 2
        pos = Step(pos, config, "g8", "f6", expectWhite: start - 7, expectBlack: start - 5);

        // 4. Qxf7#     capture pawn (+5) minus queen toll (4) = +1 for white; victim loses 5.
        var mate = _engine.Apply(pos, new Move("h5", "f7"), config);
        Assert.Equal(GameStatus.Checkmate, mate.Status);
        Assert.Equal(Side.White, mate.Winner);
        Assert.Equal(+1, mate.WhiteTimeDelta);
        Assert.Equal(-5, mate.BlackTimeDelta);
        Assert.Equal(start - 6, mate.NewPosition.WhiteTimeSeconds);
        Assert.Equal(start - 10, mate.NewPosition.BlackTimeSeconds);

        // The mating move is not treated as a "check chip": mate ends the game.
        Assert.False(mate.Has(ClockEventType.CheckGain));
    }

    [Fact]
    public void TwoEngineInstances_AgreeOnTheSameScriptedGame()
    {
        // "Two engine instances can play a full scripted game and both clocks are provably correct."
        var config = KiriChessConfig.CreateSteal1();
        var a = new KiriChessEngine();
        var b = new KiriChessEngine();

        var moves = new[]
        {
            new Move("e2", "e4"), new Move("e7", "e5"),
            new Move("f1", "c4"), new Move("b8", "c6"),
            new Move("d1", "h5"), new Move("g8", "f6"),
            new Move("h5", "f7")
        };

        var posA = a.NewGame(config);
        var posB = b.NewGame(config);
        GameStatus statusA = GameStatus.Ongoing, statusB = GameStatus.Ongoing;

        foreach (var mv in moves)
        {
            var ra = a.Apply(posA, mv, config);
            var rb = b.Apply(posB, mv, config);
            Assert.Equal(ra.NewPosition.Fen, rb.NewPosition.Fen);
            Assert.Equal(ra.NewPosition.WhiteTimeSeconds, rb.NewPosition.WhiteTimeSeconds);
            Assert.Equal(ra.NewPosition.BlackTimeSeconds, rb.NewPosition.BlackTimeSeconds);
            posA = ra.NewPosition; posB = rb.NewPosition;
            statusA = ra.Status; statusB = rb.Status;
        }

        Assert.Equal(statusA, statusB);
        Assert.Equal(GameStatus.Checkmate, statusA);
    }

    private GamePosition Step(GamePosition pos, KiriChessConfig config, string from, string to, int expectWhite, int expectBlack)
    {
        var r = _engine.Apply(pos, new Move(from, to), config);
        Assert.Equal(GameStatus.Ongoing, r.Status);
        Assert.Equal(expectWhite, r.NewPosition.WhiteTimeSeconds);
        Assert.Equal(expectBlack, r.NewPosition.BlackTimeSeconds);
        return r.NewPosition;
    }
}
