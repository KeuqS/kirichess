using KiriChess.Server.Infrastructure.Redis;

namespace KiriChess.Server.Games;

/// <summary>
/// Refreshes the TTLs Task C owns on the shared <see cref="InstanceHeartbeat"/> cadence (Phase 8.4):
/// <see cref="PresenceTracker"/>'s <c>{p}presence:{instanceId}</c> set and every game
/// <see cref="SpectatorTracker"/> currently has a local spectator connection in. Sharing the
/// heartbeat's timer rather than starting a second one keeps every TTL on this instance moving in
/// lockstep, see <see cref="IInstanceHeartbeatHook"/>'s doc comment for why a hook running on its own
/// schedule is exactly the split-brain risk this design avoids. A no-op on either tracker when Redis
/// is disabled.
/// </summary>
public sealed class PresenceHeartbeat : IInstanceHeartbeatHook
{
    private readonly PresenceTracker _presence;
    private readonly SpectatorTracker _spectators;

    public PresenceHeartbeat(PresenceTracker presence, SpectatorTracker spectators)
    {
        _presence = presence;
        _spectators = spectators;
    }

    public async Task OnHeartbeatAsync(CancellationToken ct)
    {
        await _presence.RefreshTtlAsync(ct);
        await _spectators.RefreshTtlAsync(ct);
    }
}
