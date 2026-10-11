namespace KiriChess.Server.Infrastructure.Redis;

/// <summary>
/// Extra work to run on each heartbeat tick, in the order registered. Phase 8.4 has exactly one
/// consumer (game-ownership lease renewal, Task B): lease renewal must share the instance heartbeat's
/// cadence rather than run on its own timer, because an instance whose heartbeat is late but whose
/// lease renewal is on time would look alive to the ownership check and dead to everything else.
/// A throwing hook is logged and skipped, one misbehaving subscriber must not stop the heartbeat,
/// because a missed heartbeat is what makes another instance take this one's games.
/// </summary>
public interface IInstanceHeartbeatHook
{
    Task OnHeartbeatAsync(CancellationToken ct);
}

/// <summary>
/// Work that must happen on a graceful stop <b>while this instance still looks alive to the rest of
/// the cluster</b>, i.e. before <see cref="InstanceRegistry.DeregisterAsync"/> deletes the heartbeat
/// key. Phase 8.4 has exactly one consumer (game-ownership lease release, C-5-3).
///
/// <b>The ordering is the whole point, and it is deliberately not a registration-order question.</b>
/// Hosted services stop in reverse registration order, which is easy to state and easy to break by
/// moving one line in Program.cs. These hooks instead run inside
/// <see cref="InstanceHeartbeat.StopAsync"/>, on the statement before the deregistration, so "released
/// before deregistered" is guaranteed by sequential control flow rather than by host wiring.
///
/// What that ordering buys: an instance that deletes its heartbeat key but leaves its
/// <c>{p}game:owner:*</c> leases behind looks provably dead to
/// <c>GameActionExecutor.ResolveOwnerOrTakeoverAsync</c> while its leases are still unclaimable, and
/// every player reconnecting to one of its games is told the game does not exist until the lease TTL
/// runs out.
///
/// A throwing hook is logged and skipped, for the same reason a throwing heartbeat hook is: shutdown
/// must still reach the deregistration.
/// </summary>
public interface IInstanceShutdownHook
{
    Task OnInstanceStoppingAsync(CancellationToken ct);
}

/// <summary>
/// Keeps this process's entry in <see cref="InstanceRegistry"/> alive for as long as it runs, and
/// runs every registered <see cref="IInstanceHeartbeatHook"/> on the same cadence. Registered as a
/// hosted service only when Redis is enabled (see <c>RedisRegistration.AddKiriChessRedis</c>), a
/// disabled deploy pays no timer, no Redis writes, nothing.
/// </summary>
public sealed class InstanceHeartbeat : BackgroundService
{
    private readonly InstanceRegistry _registry;
    private readonly RedisOptions _options;
    private readonly IEnumerable<IInstanceHeartbeatHook> _hooks;
    private readonly IEnumerable<IInstanceShutdownHook> _shutdownHooks;
    private readonly ILogger<InstanceHeartbeat> _logger;

    public InstanceHeartbeat(
        InstanceRegistry registry, RedisOptions options, IEnumerable<IInstanceHeartbeatHook> hooks,
        IEnumerable<IInstanceShutdownHook> shutdownHooks, ILogger<InstanceHeartbeat> logger)
    {
        _registry = registry;
        _options = options;
        _hooks = hooks;
        _shutdownHooks = shutdownHooks;
        _logger = logger;
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await _registry.RegisterAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.HeartbeatSeconds));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await _registry.HeartbeatAsync(stoppingToken);

            foreach (var hook in _hooks)
            {
                try
                {
                    await hook.OnHeartbeatAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Heartbeat hook {Hook} threw; skipping it for this tick.", hook.GetType().Name);
                }
            }
        }
    }

    /// <summary>
    /// Stops the timer, runs every <see cref="IInstanceShutdownHook"/>, and only then deregisters.
    /// The hooks run <b>before</b> <see cref="InstanceRegistry.DeregisterAsync"/> on purpose (C-5-3):
    /// deregistering first would publish "this instance is dead" to the cluster while its game leases
    /// were still held, and a takeover attempt against a dead-looking owner whose lease is still there
    /// fails, which is what turned a deploy into up to <c>InstanceTtlSeconds</c> of
    /// <c>"Game not found."</c> for every reconnecting player.
    /// </summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);

        foreach (var hook in _shutdownHooks)
        {
            try
            {
                await hook.OnInstanceStoppingAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                // Same rule as the heartbeat loop: one misbehaving hook must not stop the shutdown.
                // Deregistering is still strictly better than leaving a stale heartbeat key behind,
                // so this carries on rather than rethrowing.
                _logger.LogError(ex, "Shutdown hook {Hook} threw; continuing with deregistration.", hook.GetType().Name);
            }
        }

        await _registry.DeregisterAsync(cancellationToken);
    }
}
