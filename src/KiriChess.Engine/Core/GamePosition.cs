using System.Collections.Generic;

namespace KiriChess.Engine.Core;

/// <summary>
/// A complete, immutable snapshot of a KiriChess game: the board (as FEN), both clocks, whose
/// turn it is, and the repetition history needed by Rule 4. Produced by
/// <see cref="KiriChessEngine.NewGame"/> and by every <see cref="KiriChessEngine.Apply"/>.
/// </summary>
public sealed class GamePosition
{
    /// <summary>Full FEN of the board (all six fields), as understood by the underlying chess engine.</summary>
    public string Fen { get; }

    /// <summary>Side to move in this position.</summary>
    public Side SideToMove { get; }

    public int WhiteTimeSeconds { get; }
    public int BlackTimeSeconds { get; }

    /// <summary>
    /// How many times each position-key (FEN minus the move counters) has occurred so far, this
    /// position included. Drives Rule 4 ("once seen, always seen") and threefold-repetition draws.
    /// </summary>
    public IReadOnlyDictionary<string, int> RepetitionCounts { get; }

    public GamePosition(
        string fen,
        Side sideToMove,
        int whiteTimeSeconds,
        int blackTimeSeconds,
        IReadOnlyDictionary<string, int> repetitionCounts)
    {
        Fen = fen;
        SideToMove = sideToMove;
        WhiteTimeSeconds = whiteTimeSeconds;
        BlackTimeSeconds = blackTimeSeconds;
        RepetitionCounts = repetitionCounts;
    }

    public int TimeFor(Side side) => side == Side.White ? WhiteTimeSeconds : BlackTimeSeconds;
}
