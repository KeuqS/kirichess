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
/// I-1-6 / AS-8: seated-player membership is incremental, and the flag-fall sweep can iterate
/// sessions without <c>ToArray()</c>.
/// </summary>
public sealed class LivePlayerSetTests
{
    [Fact]
    public void Add_and_remove_are_counted_once_per_distinct_player()
    {
        var set = new LivePlayerSet();
        var white = Guid.NewGuid();
        var black = Guid.NewGuid();
        set.Add(white, black);
        Assert.Equal(2, set.Count);
        Assert.True(set.Contains(white));
        Assert.True(set.Contains(black));

        set.Add(white, black);
        Assert.Equal(2, set.Count);

        set.Remove(white, black);
        Assert.Equal(2, set.Count);
        set.Remove(white, black);
        Assert.Empty(set);
        Assert.False(set.Contains(white));
    }

    [Fact]
    public void A_solo_seat_is_one_player()
    {
        var set = new LivePlayerSet();
        var me = Guid.NewGuid();
        set.Add(me, me);
        Assert.Single(set);
        set.Remove(me, me);
        Assert.Empty(set);
    }

    [Fact]
    public void Restore_then_evict_keeps_PlayersInGame_in_step()
    {
        var registry = BuildRegistry();
        var white = Guid.NewGuid();
        var black = Guid.NewGuid();
        var config = new KiriChessConfig { MoveCostMode = MoveCostMode.None, CheckCheckerGain = 0, CheckVictimLoss = 0 };
        var session = new GameSession(new KiriChessEngine(), config, white, black, "bounty1", isRated: true);
        registry.Restore(session);

        Assert.Contains(white, registry.PlayersInGame);
        Assert.Contains(black, registry.PlayersInGame);
        Assert.Equal(2, registry.PlayersInGame.Count);
        Assert.Contains(session, registry.EnumerateSessions());

        registry.Evict(session.Id);
        Assert.Empty(registry.PlayersInGame);
        Assert.Empty(registry.EnumerateSessions());
    }

    private static GameRegistry BuildRegistry()
    {
        var scopeFactory = new ThrowingScopeFactory();
        var identity = new InstanceIdentity("test-instance");
        var disabledOptions = RedisOptions.From(new ConfigurationBuilder().Build());
        var connection = new RedisConnection(disabledOptions, identity);
        var ownership = new GameOwnership(connection, identity, disabledOptions, NullLogger<GameOwnership>.Instance);
        var liveIndex = new LiveGameIndex(connection, identity, NullLogger<LiveGameIndex>.Instance);
        var distribution = new GameDistribution(connection, ownership, liveIndex, scopeFactory, NullLogger<GameDistribution>.Instance);
        var metrics = new KiriChessMetrics(new TestMeterFactory());
        return new GameRegistry(
            new KiriChessEngine(), scopeFactory, metrics, Options.Create(new LagCompensationOptions()),
            distribution, new KillSwitchService(scopeFactory, NullLogger<KillSwitchService>.Instance),
            NullLogger<GameRegistry>.Instance);
    }
}
