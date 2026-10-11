using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace KiriChess.Server.Games;

/// <summary>
/// Measures each live player's round-trip time so <see cref="GameSession"/> can stop charging them
/// for it (Phase 6f.2). The measurement is **server-initiated**: the server calls the client's
/// <c>Ping</c> handler and times the answer against its own monotonic clock. A client-reported
/// figure would be trivially forgeable, claim 900ms every move and collect nearly a free second per
/// ply. Server-initiated is not unforgeable either (a client can stall its reply), which is what
/// <see cref="LagCompensationOptions.MaxCompensationMs"/> bounds.
///
/// A probe that fails, times out, or hits a client with no <c>Ping</c> handler records **nothing**.
/// Treating an unanswered probe as "enormous latency" would hand out the largest possible credit to
/// exactly the clients that answered least, the opposite of what this is for.
/// </summary>
public sealed class LatencyProbe : BackgroundService
{
    private readonly GameRegistry _registry;
    private readonly GameConnectionTracker _connections;
    private readonly IHubContext<GameHub> _hub;
    private readonly LagCompensationOptions _options;
    private readonly ILogger<LatencyProbe> _logger;
    private readonly IGameClock _clock = SystemGameClock.Instance;

    public LatencyProbe(
        GameRegistry registry,
        GameConnectionTracker connections,
        IHubContext<GameHub> hub,
        IOptions<LagCompensationOptions> options,
        ILogger<LatencyProbe> logger)
    {
        _registry = registry;
        _connections = connections;
        _hub = hub;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.LagCompensationEnabled)
        {
            _logger.LogInformation("Lag compensation is disabled; the latency probe will not run.");
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(1, _options.ProbeIntervalSeconds));
        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            foreach (var session in _registry.ActiveSessions)
            {
                if (session.IsOver) continue;
                foreach (var (connectionId, userId) in _connections.ConnectionsIn(session.Id))
                {
                    // Fire-and-forget on purpose: one unresponsive client must not delay the probes
                    // of every other live game behind it. ProbeAsync swallows everything.
                    _ = ProbeAsync(session, connectionId, userId, stoppingToken);
                }
            }
        }
    }

    private async Task ProbeAsync(GameSession session, string connectionId, Guid userId, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.ProbeTimeoutSeconds)));

        var started = _clock.Timestamp;
        try
        {
            await _hub.Clients.Client(connectionId).InvokeAsync<long>("Ping", timeout.Token);
        }
        catch (Exception)
        {
            // Disconnected, timed out, or a client with no Ping handler. No measurement, no log:
            // this is normal churn and logging it per connection per interval would be noise.
            return;
        }

        session.ReportLatency(userId, _clock.Elapsed(started));
    }
}
