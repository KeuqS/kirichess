namespace KiriChess.Engine.Core;

/// <summary>The kind of clock change produced by applying a move, one per popup the UI would show.</summary>
public enum ClockEventType
{
    /// <summary>Captor gained time for taking a piece (Rule 1).</summary>
    CaptureGain,
    /// <summary>Victim lost time because a piece was taken (Rule 1).</summary>
    CaptureLoss,
    /// <summary>Mover paid the movement toll (Rule 2).</summary>
    MovementToll,
    /// <summary>Checking side gained time (Rule 3).</summary>
    CheckGain,
    /// <summary>Checked side lost time (Rule 3).</summary>
    CheckLoss,
    /// <summary>Mover paid the promotion cost.</summary>
    PromotionCost,
    /// <summary>Mover paid the castling cost (replaces the king's movement toll).</summary>
    CastleCost,
    /// <summary>Base time-control increment credited to the mover.</summary>
    Increment,
    /// <summary>All KiriChess rule deltas were cancelled because the position had been seen before (Rule 4).</summary>
    RepetitionNullified,
    /// <summary>A player's clock reached zero.</summary>
    FlagFall
}

/// <summary>
/// A single clock change caused by applying a move. <see cref="Amount"/> is signed in seconds
/// (positive = time added, negative = time removed) and applies to <see cref="Side"/>.
/// </summary>
public sealed record ClockEvent(ClockEventType Type, Side Side, int Amount, string? Detail = null);
