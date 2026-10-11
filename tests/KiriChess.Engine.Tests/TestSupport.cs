using KiriChess.Engine.Config;
using KiriChess.Engine.Core;

namespace KiriChess.Engine.Tests;

/// <summary>Shared fixtures for the engine tests.</summary>
internal static class TestSupport
{
    /// <summary>
    /// A config that isolates Rule 1 (capture economy): tolls off, checks off, promotion free.
    /// This is the "test Rule 1 in isolation first" configuration from the roadmap.
    /// </summary>
    public static KiriChessConfig CaptureOnly(bool gainEnabled, bool lossEnabled, int startSeconds = 180) => new()
    {
        StartTimeSeconds = startSeconds,
        BaseIncrement = 0,
        CaptureMult = 5,
        CaptureCaptorGainEnabled = gainEnabled,
        CaptureVictimLossEnabled = lossEnabled,
        MoveCostMode = MoveCostMode.None,
        CheckCheckerGain = 0,
        CheckVictimLoss = 0,
        PromotionCostMult = 0,
        CastleCostFlat = 0
    };

    /// <summary>A config that isolates Rule 3 (check chip): tolls off, captures off.</summary>
    public static KiriChessConfig CheckOnly(int gain, int loss, int startSeconds = 180) => new()
    {
        StartTimeSeconds = startSeconds,
        BaseIncrement = 0,
        CaptureMult = 0,
        CaptureCaptorGainEnabled = false,
        CaptureVictimLossEnabled = false,
        MoveCostMode = MoveCostMode.None,
        CheckCheckerGain = gain,
        CheckVictimLoss = loss,
        PromotionCostMult = 0,
        CastleCostFlat = 0
    };

    /// <summary>Everything zeroed: the engine should behave like ordinary no-increment chess.</summary>
    public static KiriChessConfig AllZeroed(int startSeconds = 180) => new()
    {
        StartTimeSeconds = startSeconds,
        BaseIncrement = 0,
        CaptureMult = 0,
        CaptureCaptorGainEnabled = false,
        CaptureVictimLossEnabled = false,
        MoveCostMode = MoveCostMode.None,
        CheckCheckerGain = 0,
        CheckVictimLoss = 0,
        PromotionCostMult = 0,
        CastleCostFlat = 0
    };

    public static int AmountOf(this ApplyResult r, ClockEventType type) =>
        r.Events.Where(e => e.Type == type).Sum(e => e.Amount);

    public static bool Has(this ApplyResult r, ClockEventType type) => r.Events.Any(e => e.Type == type);
}
