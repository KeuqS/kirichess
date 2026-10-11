using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace KiriChess.Server.Games;

/// <summary>
/// Arms a one-shot join-deadline timer per match (Phase 6e.3), closing the hole where a matched
/// player who never calls <c>JoinGame</c> leaves the game sitting <c>Ongoing</c> forever: nobody is
/// on the clock in a meaningful sense, and the opponent, who did show up, has no exit. Not a
/// hosted service: there is no periodic sweep, each match arms its own timer, exactly like
/// <see cref="GameConnectionTracker.RunGraceAsync"/>.
/// </summary>
public sealed class MatchNoShowWatcher
{
    private readonly GameRegistry _registry;
    private readonly GameConnectionTracker _connections;
    private readonly IHubContext<GameHub> _hub;
    private readonly IConfiguration _configuration;
    private readonly ILogger<MatchNoShowWatcher> _logger;

    public MatchNoShowWatcher(
        GameRegistry registry,
        GameConnectionTracker connections,
        IHubContext<GameHub> hub,
        IConfiguration configuration,
        ILogger<MatchNoShowWatcher> logger)
    {
        _registry = registry;
        _connections = connections;
        _hub = hub;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Arms the join-deadline timer for a freshly matched game. Fire-and-forget by design, the
    /// caller (<c>GameHub.JoinQueue</c>) must not await this.
    /// </summary>
    public void Arm(Guid gameId, Guid whiteId, Guid blackId)
    {
        var joinDeadlineSeconds = _configuration.GetValue("Matchmaking:JoinDeadlineSeconds", 45);
        if (joinDeadlineSeconds <= 0) return; // 0 disables the watcher

        _ = RunAsync(gameId, whiteId, blackId, joinDeadlineSeconds);
    }

    private async Task RunAsync(Guid gameId, Guid whiteId, Guid blackId, int joinDeadlineSeconds)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(joinDeadlineSeconds));

            var session = _registry.Get(gameId);
            if (session is null || session.IsOver) return;

            // C-1-3: the cross-instance check, not the process-local one. A player who joined
            // through a second instance is tracked in {p}game:conns:{gameId}, not in this process's
            // dictionary, so the local-only IsConnected read them as absent and this watcher aborted
            // a game both players had actually shown up for. IsConnectedAsync degrades to the local
            // answer when Redis is disabled or unreachable, so the single-instance behaviour below is
            // byte-for-byte what it was; it is only the multi-instance answer that changes. That
            // degrade direction matters here more than anywhere: a false "not connected" is licence
            // to end somebody's game.
            var whiteConnected = await _connections.IsConnectedAsync(gameId, whiteId);
            var blackConnected = await _connections.IsConnectedAsync(gameId, blackId);
            if (whiteConnected && blackConnected) return;

            // C-6-2: NoShowAtFaultPlayerId on the game row is the record of who failed to join
            // (null when both seats were missing, so neither is charged). The log line stays for
            // operators watching the stream.
            Guid? atFault = !whiteConnected && !blackConnected
                ? null
                : !whiteConnected ? whiteId : blackId;
            var missing = atFault is null ? "both players" : atFault.Value.ToString();
            _logger.LogInformation(
                "Match {GameId} timed out after {JoinDeadlineSeconds}s with {Missing} never joined; aborting as NoShow.",
                gameId, joinDeadlineSeconds, missing);

            try { session.AbortNoShow(atFault); }
            catch (InvalidGameActionException) { return; } // finished between the check above and here

            await _registry.PersistIfOverAsync(session);
            await GameHubGroups.SendToRoomAsync(_hub.Clients, gameId, "GameEnded", new GameEndedDto(session.ToState()));
        }
        catch (Exception ex)
        {
            // A throw here must never take the process down, this runs detached from any request.
            _logger.LogError(ex, "MatchNoShowWatcher failed for game {GameId}.", gameId);
        }
    }
}
