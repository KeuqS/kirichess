using KiriChess.Engine;
using KiriChess.Server.Data;
using KiriChess.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace KiriChess.Server.Games;

/// <summary>
/// Durable read of a finished game that does not put the row back into <see cref="GameRegistry"/>.
/// Live play stays registry-owned (Phase 8.4). A rematch mail lands on
/// <c>/game/{finishedGameId}</c> after <see cref="GameRegistry.PersistIfOverAsync"/> has already
/// evicted the session, so JoinGame and first-party GET have to read Postgres or the page 404s
/// (P-3-1a). Ongoing rows are ignored here: an in-progress game missing from this instance's
/// registry is an ownership/recovery problem, not a history read.
/// </summary>
public sealed class FinishedGameReader(KiriChessDbContext db, KiriChessEngine engine)
{
    public async Task<GameStateResponse?> TryReadParticipantStateAsync(
        Guid gameId, Guid userId, CancellationToken ct = default) =>
        (await TryReadParticipantSnapshotAsync(gameId, userId, ct))?.State;

    /// <summary>
    /// The same read as <see cref="TryReadParticipantStateAsync"/>, as the hub snapshot shape.
    ///
    /// The move log is C-1-2: JoinGame's live path now returns one, and the client rebuilds its move
    /// list, clock graph and captured material from it unconditionally on every join. Returning an
    /// empty log here would make that rebuild wipe the sidebar on the one path where it is most
    /// visible, a socket blip just after the game ended, since PersistIfOverAsync has already evicted
    /// the session and every rejoin lands here. It also means a cold load of a rematch mail link
    /// renders the finished game's history rather than an empty panel.
    ///
    /// No legal moves: the game is over, there is no move to make, which is the same reason
    /// <c>SpectatorSnapshotDto</c> carries none at all.
    /// </summary>
    public async Task<GameSnapshotDto?> TryReadParticipantSnapshotAsync(
        Guid gameId, Guid userId, CancellationToken ct = default)
    {
        var row = await db.Games.AsNoTracking().FirstOrDefaultAsync(g => g.Id == gameId, ct);
        if (row is null || row.Result == GameResult.Ongoing) return null;
        if (row.WhitePlayerId != userId && row.BlackPlayerId != userId) return null;
        return new GameSnapshotDto(
            GameMapping.FromPersisted(engine, row),
            Array.Empty<string>(),
            row.Moves.Select(m => m.ToSpectatorDto()).ToArray());
    }
}
