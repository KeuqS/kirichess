using KiriChess.Engine.Config;
using KiriChess.Engine.Core;

namespace KiriChess.Engine.Tests;

public class Rule1CaptureEconomyTests
{
    private readonly KiriChessEngine _engine = new();

    // White e4 pawn and black c6/d5 pawns set up an equal pawn trade: 1.exd5 cxd5.
    private const string PawnTradeFen = "4k3/8/2p5/3p4/4P3/8/8/4K3 w - - 0 1";

    [Fact]
    public void Steal_EqualTrade_LeavesClockGapUnchanged()
    {
        var config = TestSupport.CaptureOnly(gainEnabled: true, lossEnabled: true);
        var pos = _engine.FromFen(config, PawnTradeFen);

        var r1 = _engine.Apply(pos, new Move("e4", "d5"), config); // white captures a pawn (value 1)
        Assert.Equal(+5, r1.WhiteTimeDelta);
        Assert.Equal(-5, r1.BlackTimeDelta);
        Assert.Equal(185, r1.NewPosition.WhiteTimeSeconds);
        Assert.Equal(175, r1.NewPosition.BlackTimeSeconds);

        var r2 = _engine.Apply(r1.NewPosition, new Move("c6", "d5"), config); // black recaptures
        Assert.Equal(-5, r2.WhiteTimeDelta);
        Assert.Equal(+5, r2.BlackTimeDelta);

        // Both clocks are back to the start: an equal trade under Steal leaves the gap untouched.
        Assert.Equal(180, r2.NewPosition.WhiteTimeSeconds);
        Assert.Equal(180, r2.NewPosition.BlackTimeSeconds);
        Assert.Equal(0, r2.NewPosition.WhiteTimeSeconds - r2.NewPosition.BlackTimeSeconds);
    }

    [Fact]
    public void Burn_EqualTrade_DrainsBothClocksButKeepsGap()
    {
        var config = TestSupport.CaptureOnly(gainEnabled: false, lossEnabled: true);
        var pos = _engine.FromFen(config, PawnTradeFen);

        var r1 = _engine.Apply(pos, new Move("e4", "d5"), config);
        var r2 = _engine.Apply(r1.NewPosition, new Move("c6", "d5"), config);

        // Captor gains nothing, victim loses: both sides bleed 5s, the gap is unchanged but pressure rises.
        Assert.Equal(175, r2.NewPosition.WhiteTimeSeconds);
        Assert.Equal(175, r2.NewPosition.BlackTimeSeconds);
    }

    [Fact]
    public void Bounty_Capture_OnlyRewardsCaptor()
    {
        var config = TestSupport.CaptureOnly(gainEnabled: true, lossEnabled: false);
        var pos = _engine.FromFen(config, PawnTradeFen);

        var r1 = _engine.Apply(pos, new Move("e4", "d5"), config);
        Assert.Equal(+5, r1.WhiteTimeDelta);
        Assert.Equal(0, r1.BlackTimeDelta);
        Assert.Equal(185, r1.NewPosition.WhiteTimeSeconds);
        Assert.Equal(180, r1.NewPosition.BlackTimeSeconds);
    }

    [Fact]
    public void CaptureValue_ScalesWithCapturedPiece()
    {
        // White knight on e5 captures the black queen on d7 (value 5).
        var config = TestSupport.CaptureOnly(gainEnabled: true, lossEnabled: true);
        var pos = _engine.FromFen(config, "4k3/3q4/8/4N3/8/8/8/4K3 w - - 0 1");

        var r = _engine.Apply(pos, new Move("e5", "d7"), config);
        Assert.Equal(+25, r.WhiteTimeDelta); // 5 * 5
        Assert.Equal(-25, r.BlackTimeDelta);
    }

    [Fact]
    public void Burn_Capture_MoverPaysOnlyTheToll_VictimLosesValue()
    {
        // Burn tier (Mainspring preset): captor gain OFF, victim loss ON. A capture must never
        // hand the mover a positive gain, the mover only ever pays the (negative) movement toll,
        // while the victim loses the captured piece's value.
        var config = KiriChessConfig.CreateBurn2();
        Assert.False(config.CaptureCaptorGainEnabled);
        Assert.True(config.CaptureVictimLossEnabled);

        // White knight on e5 captures the black queen on d7 (no check, no other economy in play).
        var pos = _engine.FromFen(config, "4k3/3q4/8/4N3/8/8/8/4K3 w - - 0 1");

        var r = _engine.Apply(pos, new Move("e5", "d7"), config);

        // Mover: only the knight's movement toll, negative, and NO capture gain event.
        int toll = config.MoveCostPerPiece[PieceType.Knight] * config.EconomyScale; // 2 * 2 = 4
        Assert.Equal(-toll, r.WhiteTimeDelta);
        Assert.False(r.Has(ClockEventType.CaptureGain));
        Assert.True(r.Has(ClockEventType.MovementToll));

        // Victim: loses value * CaptureMult * EconomyScale for the captured queen.
        int loss = config.PieceValues[PieceType.Queen] * config.CaptureMult * config.EconomyScale; // 5 * 5 * 2 = 50
        Assert.Equal(-loss, r.BlackTimeDelta);
        Assert.True(r.Has(ClockEventType.CaptureLoss));
    }

    [Fact]
    public void EnPassant_AppliesPawnValueEconomy()
    {
        // Black has just played d7-d5; white captures en passant with exd6.
        var config = TestSupport.CaptureOnly(gainEnabled: true, lossEnabled: true);
        var pos = _engine.FromFen(config, "4k3/8/8/3pP3/8/8/8/4K3 w - d6 0 1");

        var r = _engine.Apply(pos, new Move("e5", "d6"), config);
        Assert.Equal(+5, r.WhiteTimeDelta);  // captured pawn, value 1
        Assert.Equal(-5, r.BlackTimeDelta);
        Assert.True(r.Has(ClockEventType.CaptureGain));
    }
}
