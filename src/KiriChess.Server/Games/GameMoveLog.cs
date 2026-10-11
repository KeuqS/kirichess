using KiriChess.Server.Data.Entities;

namespace KiriChess.Server.Games;

/// <summary>
/// Joins the move list stored on the <see cref="Game"/> row with plies appended after it.
/// The row's list is whatever was durable before append-only snapshots (empty for a game
/// created under this scheme, or the last full rewrite from an older process). Appended
/// plies win when both name the same ply.
/// </summary>
public static class GameMoveLog
{
    public static List<MoveRecord> Merge(IReadOnlyList<MoveRecord> stored, IEnumerable<MoveRecord> appended)
    {
        var byPly = new SortedDictionary<int, MoveRecord>();
        foreach (var move in stored)
            byPly[move.Ply] = move;
        foreach (var move in appended)
            byPly[move.Ply] = move;
        return byPly.Values.ToList();
    }
}
