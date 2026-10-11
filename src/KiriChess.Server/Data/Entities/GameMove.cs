namespace KiriChess.Server.Data.Entities;

/// <summary>
/// One ply appended while a game is still live. The finished <see cref="Game.Moves"/> list is written
/// once, when the game ends. Until then recovery replays these rows, so a snapshot does not rewrite
/// the whole move list on every ply.
/// </summary>
public sealed class GameMove
{
    public Guid GameId { get; set; }
    public Game? Game { get; set; }

    /// <summary>1-based ply, matching <see cref="MoveRecord.Ply"/>. Unique per game.</summary>
    public int Ply { get; set; }

    public MoveRecord Move { get; set; } = null!;
}
