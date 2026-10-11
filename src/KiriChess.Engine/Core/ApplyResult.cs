using System.Collections.Generic;

namespace KiriChess.Engine.Core;

/// <summary>How a game is standing after a move.</summary>
public enum GameStatus
{
    Ongoing,
    Checkmate,
    Stalemate,
    FlagFall,
    DrawByRepetition,
    DrawByInsufficientMaterial,
    DrawByFiftyMove,

    /// <summary>
    /// Server-side only: the game was voided (e.g. restart recovery found nobody to resume it).
    /// The engine never produces this status.
    /// </summary>
    Aborted
}

/// <summary>
/// The result of <see cref="KiriChessEngine.Apply"/>: the resulting position, the net clock
/// change for each side (the "+15s / −15s" the UI animates), the itemised events behind those
/// numbers, and the game status.
/// </summary>
/// <param name="WhiteTimeDelta">
/// The nominal economy amount applied to White by this move (captures, movement toll, check chip,
/// base increment), not clamped to what White's clock could actually absorb. <see
/// cref="GamePosition.WhiteTimeSeconds"/> on <paramref name="NewPosition"/> is clamped to zero and
/// is the authoritative clock; this delta can therefore be a larger negative number than the clock
/// actually had left to give when a charge would have driven it below zero. See docs/game/PGN.md's
/// `[%kc]` section, which persists this same unclamped value as `dw`/`db`.
/// </param>
/// <param name="BlackTimeDelta">The same, for Black.</param>
/// <param name="San">
/// Standard algebraic notation for the move just applied ("e4", "Nxd5", "O-O", "e8=Q+", "Rae1"),
/// produced by the underlying move generator rather than derived here, because disambiguation
/// needs the position before the move and the check/mate suffix needs the position after it.
/// Empty string only if the library declined to produce one, which nothing in this codebase has
/// ever been observed to hit; callers that put SAN on a wire contract should treat empty as
/// "fall back to UCI", never as an error.
/// </param>
public sealed record ApplyResult(
    GamePosition NewPosition,
    int WhiteTimeDelta,
    int BlackTimeDelta,
    IReadOnlyList<ClockEvent> Events,
    GameStatus Status,
    Side? Winner,
    string San = "")
{
    public int DeltaFor(Side side) => side == Side.White ? WhiteTimeDelta : BlackTimeDelta;
    public bool IsGameOver => Status != GameStatus.Ongoing;
}
