using KiriChess.Engine.Core;

namespace KiriChess.Engine.Tests;

public class Rule3CheckChipTests
{
    private readonly KiriChessEngine _engine = new();

    [Fact]
    public void GivingCheck_ChecksTheChip_BothWays()
    {
        var config = TestSupport.CheckOnly(gain: 3, loss: 2);
        // White rook on a1 delivers Ra8+ against the black king on e8.
        var pos = _engine.FromFen(config, "4k3/8/8/8/8/8/8/R3K3 w - - 0 1");

        var r = _engine.Apply(pos, new Move("a1", "a8"), config);
        Assert.Equal(+3, r.WhiteTimeDelta);
        Assert.Equal(-2, r.BlackTimeDelta);
        Assert.True(r.Has(ClockEventType.CheckGain));
        Assert.True(r.Has(ClockEventType.CheckLoss));
    }

    [Fact]
    public void QuietMove_NoCheckChip()
    {
        var config = TestSupport.CheckOnly(gain: 3, loss: 2);
        var pos = _engine.FromFen(config, "4k3/8/8/8/8/8/8/R3K3 w - - 0 1");

        var r = _engine.Apply(pos, new Move("a1", "b1"), config);
        Assert.Equal(0, r.WhiteTimeDelta);
        Assert.False(r.Has(ClockEventType.CheckGain));
    }

    // ------------------------------------------------------------------------------
    // C-2-8: doubleCheckChipMultiplier ships as 2 on the default rules and nothing
    // exercised it. The chip is outside every cost vector by construction
    // (CostVectorGenerator sums CaptureGain/MovementToll/CastleCost/PromotionCost only),
    // so these C# tests are the whole guard for the lever.
    // ------------------------------------------------------------------------------

    /// <summary>
    /// Ne4-d6 gives knight check and discovers the e1 rook down the e-file: two attackers of e8
    /// on one move. Nothing else in the repository priced a double check before this test.
    /// </summary>
    [Fact]
    public void DoubleCheck_MultipliesBothHalvesOfTheChip()
    {
        var config = TestSupport.CheckOnly(gain: 3, loss: 2);
        config.DoubleCheckChipMultiplier = 2;
        var pos = _engine.FromFen(config, "4k3/8/8/8/4N3/8/8/K3R3 w - - 0 1");

        var r = _engine.Apply(pos, new Move("e4", "d6"), config);

        Assert.Equal(GameStatus.Ongoing, r.Status); // a mate is not a chip
        Assert.Equal(+6, r.WhiteTimeDelta);
        Assert.Equal(-4, r.BlackTimeDelta);
    }

    [Fact]
    public void SingleCheck_IsNotDoubled_EvenWhenTheMultiplierIsSet()
    {
        var config = TestSupport.CheckOnly(gain: 3, loss: 2);
        config.DoubleCheckChipMultiplier = 2;
        var pos = _engine.FromFen(config, "4k3/8/8/8/8/8/8/R3K3 w - - 0 1");

        var r = _engine.Apply(pos, new Move("a1", "a8"), config);

        Assert.Equal(+3, r.WhiteTimeDelta);
        Assert.Equal(-2, r.BlackTimeDelta);
    }

    /// <summary>
    /// The case that decides what <c>ChessBoard.GeneratePositions</c> actually returns. The white
    /// rook on e1 is pinned by the black rook on a1 against the white king on h1, and it is still
    /// one of the two pieces checking e8 after Ne4-d6. FIDE agrees: a pin constrains where a piece
    /// may move, never what it attacks, so this must count as a double check. It does, because
    /// GeneratePositions is documented pseudo-legal ("doesn't take in account validation for
    /// king"), which is the right semantics for attack detection. If that generator ever became
    /// legality-filtered this test goes red and the chip silently halves.
    /// </summary>
    [Fact]
    public void DoubleCheck_CountsACheckerThatIsPinnedAgainstItsOwnKing()
    {
        var config = TestSupport.CheckOnly(gain: 3, loss: 2);
        config.DoubleCheckChipMultiplier = 2;
        var pos = _engine.FromFen(config, "4k3/8/8/8/4N3/8/8/r3R2K w - - 0 1");

        var r = _engine.Apply(pos, new Move("e4", "d6"), config);

        Assert.Equal(GameStatus.Ongoing, r.Status);
        Assert.Equal(+6, r.WhiteTimeDelta);
        Assert.Equal(-4, r.BlackTimeDelta);
    }

    /// <summary>
    /// The short-circuit added alongside these tests skips the 64-square scan when the multiplier
    /// is 1 or both chip amounts are zero. Neither can change an amount, and this pins the first:
    /// a genuine double check on a multiplier of 1 still pays exactly the single-check chip.
    /// </summary>
    [Fact]
    public void DoubleCheck_WithAMultiplierOfOne_PaysTheSingleCheckChip()
    {
        var config = TestSupport.CheckOnly(gain: 3, loss: 2);
        config.DoubleCheckChipMultiplier = 1;
        var pos = _engine.FromFen(config, "4k3/8/8/8/4N3/8/8/K3R3 w - - 0 1");

        var r = _engine.Apply(pos, new Move("e4", "d6"), config);

        Assert.Equal(+3, r.WhiteTimeDelta);
        Assert.Equal(-2, r.BlackTimeDelta);
    }
}
