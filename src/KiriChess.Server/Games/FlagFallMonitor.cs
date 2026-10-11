using System.Diagnostics;
using KiriChess.Server.Infrastructure;
using Microsoft.AspNetCore.SignalR;

namespace KiriChess.Server.Games;

/// <summary>
/// Coarse safety-net sweep: once a second, flags any live game whose on-turn player has run out of
/// time even though no move was submitted, persists it, and broadcasts <c>GameEnded</c>. This is the
/// "coarse server timer, not per-second DB ticks" from the Phase 3 plan, clocks are still computed
/// on demand; this only detects the idle-flag transition.
/// </summary>
public sealed class FlagFallMonitor : BackgroundService
{
    // I-1-2 / AS-5: persist + rating + GameEnded used to run one game at a time inside the 1s tick.
    // Four at once is enough to drain a bullet-pool flag wave without opening a connection storm.
    private const int SweepConcurrency = 4;

    private readonly GameRegistry _registry;
    private readonly IHubContext<GameHub> _hub;
    private readonly KiriChessMetrics _metrics;
    private readonly ILogger<FlagFallMonitor> _logger;

    public FlagFallMonitor(
        GameRegistry registry, IHubContext<GameHub> hub, KiriChessMetrics metrics, ILogger<FlagFallMonitor> logger)
    {
        _registry = registry;
        _hub = hub;
        _metrics = metrics;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await SweepOnceAsync(stoppingToken);
    }

    /// <summary>
    /// One tick, guarded the way <c>RetentionSweeper.RunOnceAsync</c> is. The loop body reaches
    /// Postgres through <see cref="GameRegistry.PersistIfOverAsync"/> and the backplane through the
    /// hub, and neither used to be inside a try: with HostOptions left at its StopHost default, one
    /// Neon connection blip during a flag-fall persist stopped the whole application and took every
    /// live game on the instance with it. A sweep that logs and retries in a second is strictly
    /// better than a sweep that ends the process. Per-game work is bounded-parallel so one slow
    /// persist does not hold every later flag in the same tick (I-1-2).
    /// </summary>
    internal async Task SweepOnceAsync(CancellationToken ct)
    {
        try
        {
            var sweepStart = Stopwatch.GetTimestamp();
            await Parallel.ForEachAsync(
                _registry.EnumerateSessions(),
                new ParallelOptions { MaxDegreeOfParallelism = SweepConcurrency, CancellationToken = ct },
                async (session, token) =>
                {
                    try
                    {
                        // A session can already be over here (flagged lazily by GameRegistry.Get on a
                        // read), it still needs persisting and announcing, which is why this doesn't skip IsOver.
                        if (!session.IsOver && !session.CheckFlagFall()) return;

                        var result = await _registry.PersistIfOverAsync(session);
                        if (result.Persisted)
                        {
                            _metrics.FlagFallDetected();
                            await GameHubGroups.SendToRoomAsync(
                                _hub.Clients, session.Id, "GameEnded", new GameEndedDto(session.ToState(), result.RatingChange), token);
                        }
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Flag-fall persist failed for game {GameId}; other games in this tick continue.", session.Id);
                    }
                });
            _metrics.FlagFallSweepCompleted(Stopwatch.GetElapsedTime(sweepStart).TotalMilliseconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // shutdown, not a failed sweep - let it propagate out of the loop
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Flag-fall sweep failed; will try again next tick.");
        }
    }
}
