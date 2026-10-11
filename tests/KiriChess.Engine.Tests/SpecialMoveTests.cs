using KiriChess.Engine.Config;
using KiriChess.Engine.Core;

namespace KiriChess.Engine.Tests;

public class SpecialMoveTests
{
    private readonly KiriChessEngine _engine = new();

    [Fact]
    public void Promotion_ChargesCost_UnderPromotionCheaperThanQueen()
    {
        var config = TestSupport.AllZeroed();
        config.PromotionCostMult = 2; // cost = mult * value of promoted piece

        var pos = _engine.FromFen(config, "4k3/P7/8/8/8/8/8/4K3 w - - 0 1");

        var queen = _engine.Apply(pos, new Move("a7", "a8", PieceType.Queen), config);
        Assert.Equal(-10, queen.WhiteTimeDelta); // 2 * 5
        Assert.True(queen.Has(ClockEventType.PromotionCost));

        var knight = _engine.Apply(pos, new Move("a7", "a8", PieceType.Knight), config);
        Assert.Equal(-4, knight.WhiteTimeDelta); // 2 * 2

        Assert.True(knight.WhiteTimeDelta > queen.WhiteTimeDelta, "under-promotion should be cheaper");
    }

    [Fact]
    public void Castling_ChargesFlatCost_NotKingAndRookTolls()
    {
        var config = new KiriChessConfig
        {
            StartTimeSeconds = 180,
            MoveCostMode = MoveCostMode.PerPiece, // king toll would otherwise be 1
            CastleCostFlat = 3,
            CaptureCaptorGainEnabled = false,
            CaptureVictimLossEnabled = false,
            CheckCheckerGain = 0,
            CheckVictimLoss = 0
        };
        var pos = _engine.FromFen(config, "r3k2r/8/8/8/8/8/8/R3K2R w KQkq - 0 1");

        // Short castling (O-O)
        var shortCastle = _engine.Apply(pos, new Move("e1", "g1"), config);
        Assert.Equal(-3, shortCastle.WhiteTimeDelta);
        Assert.True(shortCastle.Has(ClockEventType.CastleCost));
        Assert.False(shortCastle.Has(ClockEventType.MovementToll));

        // Long castling (O-O-O)
        var longCastle = _engine.Apply(pos, new Move("e1", "c1"), config);
        Assert.Equal(-3, longCastle.WhiteTimeDelta);
        Assert.True(longCastle.Has(ClockEventType.CastleCost));
        Assert.False(longCastle.Has(ClockEventType.MovementToll));
    }

    [Fact]
    public void Castling_WithTollsOff_IsFree()
    {
        var config = TestSupport.AllZeroed();
        config.CastleCostFlat = 3; // ignored while MoveCostMode is None
        var pos = _engine.FromFen(config, "r3k2r/8/8/8/8/8/8/R3K2R w KQkq - 0 1");

        var r = _engine.Apply(pos, new Move("e1", "g1"), config);
        Assert.Equal(0, r.WhiteTimeDelta);
    }
}
