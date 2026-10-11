using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;

namespace KiriChess.Server.Games;

/// <summary>
/// Presence channel for the home screen: pushes the live "players online / in game" counts.
/// Deliberately *not* <c>[Authorize]</c>, a logged-out visitor deciding whether to sign up is exactly
/// who the counts are for. A bearer token is still honoured when present, and only then does the
/// visitor count towards <see cref="PresenceTracker.OnlineCount"/>.
/// </summary>
public sealed class PresenceHub : Hub
{
    private readonly PresenceTracker _presence;
    private readonly PresenceBroadcaster _broadcaster;

    public PresenceHub(PresenceTracker presence, PresenceBroadcaster broadcaster)
    {
        _presence = presence;
        _broadcaster = broadcaster;
    }

    public override async Task OnConnectedAsync()
    {
        var raw = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier) ?? Context.User?.FindFirstValue("sub");
        if (Guid.TryParse(raw, out var userId))
            _presence.Track(Context.ConnectionId, userId);

        // Send straight away so the client paints a number instead of waiting for the next sweep.
        await Clients.Caller.SendAsync("PresenceChanged", await _broadcaster.CurrentAsync(Context.ConnectionAborted));
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _presence.Remove(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }
}
