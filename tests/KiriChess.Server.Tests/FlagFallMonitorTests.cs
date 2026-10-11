using KiriChess.Engine;
using KiriChess.Engine.Config;
using KiriChess.Server.Games;
using KiriChess.Server.Games.Distributed;
using KiriChess.Server.Infrastructure;
using KiriChess.Server.Infrastructure.Redis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace KiriChess.Server.Tests;

/// <summary>
/// I-1-2: the flag-fall tick must complete even when the registry is empty or a persist throws.
/// </summary>
public sealed class FlagFallMonitorTests
{
    private static (FlagFallMonitor Monitor, GameRegistry Registry) Build()
    {
        var scopeFactory = new ThrowingScopeFactory();
        var identity = new InstanceIdentity("test-instance");
        var disabledOptions = RedisOptions.From(new ConfigurationBuilder().Build());
        var connection = new RedisConnection(disabledOptions, identity);
        var ownership = new GameOwnership(connection, identity, disabledOptions, NullLogger<GameOwnership>.Instance);
        var liveIndex = new LiveGameIndex(connection, identity, NullLogger<LiveGameIndex>.Instance);
        var distribution = new GameDistribution(connection, ownership, liveIndex, scopeFactory, NullLogger<GameDistribution>.Instance);
        var meterFactory = new TestMeterFactory();
        var metrics = new KiriChessMetrics(meterFactory);
        var registry = new GameRegistry(
            new KiriChessEngine(), scopeFactory, metrics, Options.Create(new LagCompensationOptions()),
            distribution, new KillSwitchService(scopeFactory, NullLogger<KillSwitchService>.Instance),
            NullLogger<GameRegistry>.Instance);
        var hub = new FakeHubContext();
        var monitor = new FlagFallMonitor(registry, hub, metrics, NullLogger<FlagFallMonitor>.Instance);
        return (monitor, registry);
    }

    [Fact]
    public async Task Sweep_on_an_empty_registry_completes()
    {
        var (monitor, _) = Build();
        await monitor.SweepOnceAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_persist_fault_on_one_game_does_not_abort_the_tick()
    {
        var (monitor, registry) = Build();
        var white = Guid.NewGuid();
        var black = Guid.NewGuid();
        var config = new KiriChessConfig { MoveCostMode = MoveCostMode.None, CheckCheckerGain = 0, CheckVictimLoss = 0 };
        var session = new GameSession(new KiriChessEngine(), config, white, black, "bounty1", isRated: true);
        session.Resign(white);
        registry.Restore(session);

        await monitor.SweepOnceAsync(CancellationToken.None);
    }
}
