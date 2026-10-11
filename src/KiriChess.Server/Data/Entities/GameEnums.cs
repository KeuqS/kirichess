namespace KiriChess.Server.Data.Entities;

/// <summary>Final outcome of a game from White's frame.</summary>
public enum GameResult { Ongoing, WhiteWins, BlackWins, Draw, Aborted }

/// <summary>Why a game ended. Superset of the engine's <c>GameStatus</c> plus server-only reasons.</summary>
public enum TerminationReason
{
    None,
    Checkmate,
    Flagfall,
    Stalemate,
    Resignation,
    DrawByRepetition,
    DrawByInsufficientMaterial,
    DrawByFiftyMove,
    Abandoned,
    DrawByAgreement,
    ServerRestart,
    /// <summary>A matched player never joined the game; it is aborted, not forfeited (Phase 6e.3).</summary>
    NoShow
}
