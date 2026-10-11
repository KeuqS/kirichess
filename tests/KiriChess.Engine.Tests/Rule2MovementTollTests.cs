using KiriChess.Engine.Config;
using KiriChess.Engine.Core;

namespace KiriChess.Engine.Tests;

public class Rule2MovementTollTests
{
    private readonly KiriChessEngine _engine = new();

    private KiriChessConfig BaseConfig(MoveCostMode mode) => new()
    {
        StartTimeSeconds = 180,
        MoveCostMode = mode,
        CaptureCaptorGainEnabled = false,
        CaptureVictimLossEnabled = false,
        CheckCheckerGain = 0,
        CheckVictimLoss = 0
    };

    [Fact]
    public void PerPiece_ChargesTheMovingPieceToll()
    {
        var config = BaseConfig(MoveCostMode.PerPiece); // knight toll = 2
        var pos = _engine.NewGame(config);

        var r = _engine.Apply(pos, new Move("g1", "f3"), config);
        Assert.Equal(-2, r.WhiteTimeDelta);
        Assert.Equal(178, r.NewPosition.WhiteTimeSeconds);
    }

    [Fact]
    public void Flat_ChargesTheSameRegardlessOfPiece()
    {
        var config = BaseConfig(MoveCostMode.Flat);
        config.MoveCostFlat = 1;
        var pos = _engine.NewGame(config);

        var r = _engine.Apply(pos, new Move("e2", "e4"), config);
        Assert.Equal(-1, r.WhiteTimeDelta);
    }

    [Fact]
    public void Distance_ChargesPerSquareTravelled()
    {
        var config = BaseConfig(MoveCostMode.Distance);
        config.MoveCostPerSquare = 1;
        var pos = _engine.FromFen(config, "4k3/8/8/8/8/8/8/R3K3 w - - 0 1");

        var r = _engine.Apply(pos, new Move("a1", "a5"), config); // 4 squares
        Assert.Equal(-4, r.WhiteTimeDelta);
    }

    [Fact]
    public void PerPieceXDistance_MultipliesTollByDistance()
    {
        var config = BaseConfig(MoveCostMode.PerPieceXDistance); // rook toll = 3
        var pos = _engine.FromFen(config, "4k3/8/8/8/8/8/8/R3K3 w - - 0 1");

        var r = _engine.Apply(pos, new Move("a1", "a5"), config); // 3 * 4 squares
        Assert.Equal(-12, r.WhiteTimeDelta);
    }

    [Fact]
    public void None_ChargesNothing()
    {
        var config = BaseConfig(MoveCostMode.None);
        var pos = _engine.NewGame(config);

        var r = _engine.Apply(pos, new Move("e2", "e4"), config);
        Assert.Equal(0, r.WhiteTimeDelta);
        Assert.False(r.Has(ClockEventType.MovementToll));
    }
}
