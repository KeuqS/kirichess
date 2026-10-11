using Microsoft.AspNetCore.SignalR;

namespace KiriChess.Server.Games;

/// <summary>
/// SignalR group names for a live game. Players and spectators must not share a group that
/// carries <see cref="MovePlayedDto.LegalMoves"/> (C-2-9 = C-8-7).
///
/// The player group is still <c>gameId.ToString()</c>, so every existing <c>JoinGame</c> and
/// <c>GroupExcept</c> site keeps its name. Spectators sit in <c>{gameId}:spectators</c>. Room-wide
/// events that a watcher already received because they used to share the player group
/// (<c>GameEnded</c>, <c>SpectatorsChanged</c>, <c>OpponentDisconnected</c>,
/// <c>OpponentReconnected</c>) fan out through <see cref="SendToRoomAsync"/> so the watch page
/// does not go dark. Player-only events (draw offer/decline, rematch) stay on the player group.
/// </summary>
internal static class GameHubGroups
{
    public static string Players(Guid gameId) => gameId.ToString();

    public static string Spectators(Guid gameId) => $"{gameId}:spectators";

    public static async Task SendToRoomAsync(
        IHubClients<IClientProxy> clients, Guid gameId, string method, object? arg, CancellationToken ct = default)
    {
        await clients.Group(Players(gameId)).SendAsync(method, arg, ct);
        await clients.Group(Spectators(gameId)).SendAsync(method, arg, ct);
    }

    public static async Task SendToRoomAsync(
        IHubClients<IClientProxy> clients, Guid gameId, string method, CancellationToken ct = default)
    {
        await clients.Group(Players(gameId)).SendAsync(method, ct);
        await clients.Group(Spectators(gameId)).SendAsync(method, ct);
    }
}
