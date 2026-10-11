using KiriChess.Server.Games.Distributed;
using KiriChess.Server.Infrastructure.Redis;
using Microsoft.AspNetCore.SignalR;

namespace KiriChess.Server.Games;

/// <summary>
/// Pushes the presence counts to every <see cref="PresenceHub"/> client whenever they change.
/// Polls on a coarse timer rather than hooking each event source, because "in game" moves for reasons
/// the presence hub never sees (flag-fall, abandon, a game created over REST), one cheap tick covers
/// all of them, and nothing is sent while the numbers sit still.
///
/// Phase 8.4: with N instances, every one of them ticks independently and each broadcasts the same
/// change to its own connected clients, that is correct (each instance owns its own SignalR
/// connections and cannot reach anyone else's without the backplane) and is not a fan-out multiplier,
/// but the <see cref="_last"/> comparison below is now per-instance, so a client that reconnects to a
/// different machine mid-session can receive the same numbers twice. Harmless, the client just
/// re-renders the same counts, and not worth coordinating away.
/// </summary>
public sealed class PresenceBroadcaster : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(3);

    private readonly IHubContext<PresenceHub> _hub;
    private readonly PresenceTracker _presence;
    private readonly GameRegistry _registry;
    private readonly LiveGameIndex _liveGames;
    private readonly IRedisConnection _connection;
    private readonly ILogger<PresenceBroadcaster> _logger;
    private PresenceDto _last = new(0, 0);

    public PresenceBroadcaster(
        IHubContext<PresenceHub> hub, PresenceTracker presence, GameRegistry registry,
        LiveGameIndex liveGames, IRedisConnection connection, ILogger<PresenceBroadcaster> logger)
    {
        _hub = hub;
        _presence = presence;
        _registry = registry;
        _liveGames = liveGames;
        _connection = connection;
        _logger = logger;
    }

    /// <summary>
    /// The counts as of right now (also what a freshly connected client is sent). Players in a live
    /// game are unioned into "online" rather than assumed to be there: someone mid-game whose
    /// presence connection dropped is still online, and the readout would read as broken if the
    /// in-game count ever exceeded it. "Playing" is cluster-wide via
    /// <see cref="LiveGameIndex.PlayersInGameAsync"/> when Redis is enabled (the union still needs
    /// the id set; callers that only want a count use <see cref="LiveGameIndex.PlayersInGameCountAsync"/>),
    /// else the per-process <see cref="GameRegistry.PlayersInGame"/>; "online" is always
    /// <see cref="PresenceTracker.OnlineUsersAsync"/>.
    /// </summary>
    public async Task<PresenceDto> CurrentAsync(CancellationToken ct = default)
    {
        IReadOnlySet<Guid> playing = _connection.Enabled
            ? await _liveGames.PlayersInGameAsync(ct)
            : _registry.PlayersInGame;
        var online = await _presence.OnlineUsersAsync(ct);
        online.UnionWith(playing);
        return new PresenceDto(online.Count, playing.Count);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await TickAsync(stoppingToken);
    }

    /// <summary>
    /// One tick, guarded the way <c>RetentionSweeper.RunOnceAsync</c> is. Both readings in
    /// <see cref="CurrentAsync"/> are Redis round trips and Redis is optional-at-runtime here, so a
    /// timeout is a normal event, not an impossible one. Unguarded it was fatal: HostOptions is left
    /// at its StopHost default, so one bad presence tick stopped the application and ended every live
    /// game on the instance. A count that is three seconds stale is not worth a process for.
    /// </summary>
    private async Task TickAsync(CancellationToken ct)
    {
        try
        {
            var current = await CurrentAsync(ct);
            if (current == _last) return;
            // Sent before _last moves, so a failed send is re-attempted on the next tick rather than
            // suppressed for as long as the numbers happen to sit still. Re-sending identical counts
            // is already documented above as harmless.
            await _hub.Clients.All.SendAsync("PresenceChanged", current, ct);
            _last = current;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // shutdown, not a failed tick - let it propagate out of the loop
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Presence broadcast tick failed; will try again next tick.");
        }
    }
}
