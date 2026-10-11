using System.Diagnostics.Metrics;
using KiriChess.Server.Games;

namespace KiriChess.Server.Infrastructure;

/// <summary>
/// Registers the observable gauges, the "what is happening right now" half of 6d.3. These read
/// live singleton state on each scrape rather than being pushed to, so they cannot drift out of
/// sync with reality the way a hand-maintained counter can.
///
/// It is an <see cref="IHostedService"/> purely so the host constructs it eagerly; the gauges are
/// created in the constructor and the callbacks are what the exporter invokes. Registered with the
/// same singleton-plus-hosted-service double registration as <see cref="PresenceBroadcaster"/>.
/// </summary>
public sealed class GameMetricsObserver : IHostedService
{
    private readonly GameRegistry _registry;
    private readonly MatchmakingService _matchmaking;
    private readonly PresenceTracker _presence;
    private readonly LiveGamePersister _persister;
    private readonly ILogger<GameMetricsObserver> _logger;

    public GameMetricsObserver(
        IMeterFactory meterFactory,
        GameRegistry registry,
        MatchmakingService matchmaking,
        PresenceTracker presence,
        LiveGamePersister persister,
        ILogger<GameMetricsObserver> logger)
    {
        _registry = registry;
        _matchmaking = matchmaking;
        _presence = presence;
        _persister = persister;
        _logger = logger;

        var meter = meterFactory.Create(KiriChessMetrics.MeterName);

        meter.CreateObservableGauge(
            "kirichess.games.live", ObserveLiveGames, unit: "{game}",
            description: "Games currently in progress in memory. The headline capacity number for a single-instance server.");

        meter.CreateObservableGauge(
            "kirichess.matchmaking.queue.depth", ObserveQueueDepth, unit: "{player}",
            description: "Players waiting in each matchmaking pool. Persistent depth in one pool means nobody is being paired.");

        meter.CreateObservableGauge(
            "kirichess.presence.online_users", ObserveOnlineUsers, unit: "{user}",
            description: "Distinct authenticated users with at least one live presence connection.");

        meter.CreateObservableGauge(
            "kirichess.persistence.pending_snapshots", ObservePendingSnapshots, unit: "{game}",
            description: "Games with a coalesced move-snapshot write still queued. Sustained growth means Postgres is not keeping up and a crash would lose more than the tail move.");

        // --- Multi-instance game ownership (Phase 8.4) ---
        // Deliberately the same computation as kirichess.games.live, not a new count: after this
        // phase, every session in the local registry is one this instance currently holds the
        // ownership lease for (a lost lease evicts it immediately, see GameRegistry.Evict). The
        // separate name matters once this is actually deployed multi-instance: summing this gauge
        // across every instance should equal the cluster-wide total, which is the invariant an
        // operator actually wants to graph.
        meter.CreateObservableGauge(
            "kirichess.game.ownership.owned", ObserveLiveGames, unit: "{game}",
            description: "Games this instance currently owns. Equal to kirichess.games.live single-instance; summed across every instance it is the cluster-wide live-game total.");
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private int ObserveLiveGames() => _registry.ActiveSessions.Count(s => !s.IsOver);

    /// <summary>
    /// C-6-6: reads <see cref="MatchmakingService.QueueDepthsAsync"/> rather than the synchronous
    /// <see cref="MatchmakingService.QueueDepths"/>, which is the pre-8.4 in-memory dictionary nothing
    /// on the Redis path ever populates, so the whole series went dark the moment Redis was enabled
    /// while reading exactly like a healthy, idle lobby. <see cref="Meter.CreateObservableGauge"/> only
    /// accepts a synchronous callback, so this blocks on the async result rather than caching a stale
    /// one; on the disabled-Redis path this codebase runs today that costs nothing (the Task is already
    /// completed), and on the Redis path it costs one batched ZCARD round trip per scrape, which is the
    /// price of the gauge actually meaning something.
    /// </summary>
    private IEnumerable<Measurement<int>> ObserveQueueDepth()
    {
        IReadOnlyDictionary<string, int> depths;
        try
        {
            depths = _matchmaking.QueueDepthsAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            // MatchQueue.DepthsAsync already swallows a Redis outage and falls back internally; this
            // catch is only for something that got past that, and a scrape must never fail because one
            // gauge could not read its source.
            _logger.LogWarning(ex, "Failed to read matchmaking queue depths for the observable gauge; falling back to the local view.");
            depths = _matchmaking.QueueDepths;
        }

        return depths.Select(kv =>
            new Measurement<int>(kv.Value, new KeyValuePair<string, object?>("pool", kv.Key)));
    }

    private int ObserveOnlineUsers() => _presence.OnlineCount;

    private int ObservePendingSnapshots() => _persister.PendingSnapshotCount;
}
