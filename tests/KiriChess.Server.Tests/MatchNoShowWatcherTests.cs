using KiriChess.Engine;
using KiriChess.Engine.Config;
using KiriChess.Server.Data.Entities;
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
/// C-10-4: <see cref="MatchNoShowWatcher"/> had no test at all. Covers the single-instance cases:
/// no abort when both matched players joined, an abort (with the correct at-fault player) when one
/// never did, and no at-fault player when neither did. Redis is disabled in
/// <see cref="Build"/>, so the watcher's <see cref="GameConnectionTracker.IsConnectedAsync"/> call
/// degrades to the same local <see cref="GameConnectionTracker.IsConnected"/> answer it always
/// gave here; the cross-instance case that call was made for lives in
/// <see cref="MatchNoShowWatcherCrossInstanceTests"/>, which needs a real Redis.
/// <c>Matchmaking:JoinDeadlineSeconds</c> is configured to the smallest value the
/// production code can actually observe: <see cref="MatchNoShowWatcher.Arm"/> reads it as an
/// <c>int</c> and feeds it straight to <see cref="Task.Delay(TimeSpan)"/>, so there is no sub-second
/// value to configure - 1 real second is the floor, not a shortcut this test is taking.
///
/// <see cref="GameRegistry.PersistIfOverAsync"/> is deliberately never reached in a way that
/// succeeds here: the harness's <see cref="ThrowingScopeFactory"/> (from
/// <c>GameOwnershipTestDoubles.cs</c>) has no real database behind it, matching the existing
/// no-Docker harnesses in this project (<c>GameOwnershipHeartbeatHarness</c>,
/// <c>GameActionExecutorHarness</c>). The watcher swallows that failure internally (its own
/// top-level catch exists exactly so a persistence fault never takes the process down) and the
/// assertions below read the in-memory <see cref="GameSession"/> the test itself created, not the
/// registry, so they see the abort decision regardless of whether the persist attempt after it
/// happened to succeed.
/// </summary>
public sealed class MatchNoShowWatcherTests
{
    private static (GameRegistry Registry, GameConnectionTracker Connections, FakeHubContext Hub, MatchNoShowWatcher Watcher)
        Build(int joinDeadlineSeconds)
    {
        var scopeFactory = new ThrowingScopeFactory();
        var identity = new InstanceIdentity("test-instance");
        var disabledOptions = RedisOptions.From(new ConfigurationBuilder().Build()); // Enabled == false
        var connection = new RedisConnection(disabledOptions, identity);
        var ownership = new GameOwnership(connection, identity, disabledOptions, NullLogger<GameOwnership>.Instance);
        var instances = new InstanceRegistry(connection, identity, disabledOptions, NullLogger<InstanceRegistry>.Instance);
        var liveIndex = new LiveGameIndex(connection, identity, NullLogger<LiveGameIndex>.Instance);
        var distribution = new GameDistribution(connection, ownership, liveIndex, scopeFactory, NullLogger<GameDistribution>.Instance);
        var meterFactory = new TestMeterFactory();
        var metrics = new KiriChessMetrics(meterFactory);
        var engine = new KiriChessEngine();
        var lagOptions = Options.Create(new LagCompensationOptions());
        var killSwitches = new KillSwitchService(scopeFactory, NullLogger<KillSwitchService>.Instance);
        var registry = new GameRegistry(engine, scopeFactory, metrics, lagOptions, distribution, killSwitches, NullLogger<GameRegistry>.Instance);

        var hub = new FakeHubContext();
        var connections = new GameConnectionTracker(
            hub, registry, new ThrowingActionExecutor(), connection, identity, instances, NullLogger<GameConnectionTracker>.Instance);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Matchmaking:JoinDeadlineSeconds"] = joinDeadlineSeconds.ToString(),
            })
            .Build();
        var watcher = new MatchNoShowWatcher(registry, connections, hub, configuration, NullLogger<MatchNoShowWatcher>.Instance);

        return (registry, connections, hub, watcher);
    }

    private static GameSession NewMatchedSession(GameRegistry registry, Guid whiteId, Guid blackId)
    {
        var engine = new KiriChessEngine();
        var config = new KiriChessConfig();
        var session = new GameSession(engine, config, whiteId, blackId, ratingPool: "bounty1", isRated: true);
        registry.Restore(session); // present in the registry, as GameHub.JoinQueue's CreateAsync would leave it
        return session;
    }

    /// <summary>Polls until <paramref name="isDone"/> is true or the deadline passes, never a fixed sleep-then-assert.</summary>
    private static async Task<bool> WaitUntilAsync(Func<bool> isDone, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (isDone()) return true;
            await Task.Delay(50);
        }
        return isDone();
    }

    [Fact]
    public async Task No_abort_when_both_players_joined_before_the_deadline()
    {
        var (registry, connections, _, watcher) = Build(joinDeadlineSeconds: 1);
        var whiteId = Guid.NewGuid();
        var blackId = Guid.NewGuid();
        var session = NewMatchedSession(registry, whiteId, blackId);

        connections.Track("white-conn", whiteId, session.Id);
        connections.Track("black-conn", blackId, session.Id);

        watcher.Arm(session.Id, whiteId, blackId);

        // The deadline is 1 second; give the watcher a further margin past it and confirm it never
        // aborts, rather than asserting immediately at a fixed instant.
        await Task.Delay(TimeSpan.FromSeconds(2));

        Assert.False(session.IsOver);
        Assert.Equal(TerminationReason.None, session.Termination);
        Assert.Null(session.NoShowAtFaultPlayerId);
    }

    [Fact]
    public async Task Aborts_as_no_show_when_one_player_never_joins()
    {
        var (registry, connections, _, watcher) = Build(joinDeadlineSeconds: 1);
        var whiteId = Guid.NewGuid();
        var blackId = Guid.NewGuid();
        var session = NewMatchedSession(registry, whiteId, blackId);

        // White joined (tracked); black never calls JoinGame.
        connections.Track("white-conn", whiteId, session.Id);

        watcher.Arm(session.Id, whiteId, blackId);

        var aborted = await WaitUntilAsync(() => session.IsOver, TimeSpan.FromSeconds(5));

        Assert.True(aborted, "the watcher should have aborted the game as a no-show within the deadline plus a margin");
        Assert.Equal(TerminationReason.NoShow, session.Termination);
        Assert.Null(session.Winner); // a no-show is void, never a forfeit win for the player who showed up
        Assert.Equal(blackId, session.NoShowAtFaultPlayerId); // black is the one who never joined
    }

    [Fact]
    public async Task Aborts_with_no_at_fault_player_when_neither_side_joins()
    {
        var (registry, _, _, watcher) = Build(joinDeadlineSeconds: 1);
        var whiteId = Guid.NewGuid();
        var blackId = Guid.NewGuid();
        var session = NewMatchedSession(registry, whiteId, blackId);

        // Neither player ever calls JoinGame - nobody is tracked.
        watcher.Arm(session.Id, whiteId, blackId);

        var aborted = await WaitUntilAsync(() => session.IsOver, TimeSpan.FromSeconds(5));

        Assert.True(aborted);
        Assert.Equal(TerminationReason.NoShow, session.Termination);
        Assert.Null(session.NoShowAtFaultPlayerId); // both missing charges neither player (C-6-2)
    }
}

/// <summary>
/// C-10-4's third case, made live by C-1-3: a player who is genuinely connected, but to a
/// <i>different</i> instance than the one running the watcher, must not read as a no-show.
/// <see cref="MatchNoShowWatcher"/> now calls the cross-instance
/// <see cref="GameConnectionTracker.IsConnectedAsync"/> rather than the synchronous, local-only
/// <see cref="GameConnectionTracker.IsConnected"/>, which is what
/// <see cref="GameConnectionTracker.OnDisconnected"/>'s own grace timer already did. This test was
/// written skipped against the old code and the <c>Skip</c> is now removed unchanged: the
/// assertions below are exactly the ones that were red, so putting <c>IsConnected</c> back turns
/// this red again.
///
/// Requires Docker (a real Redis, because cross-instance connectivity is read from
/// <c>{p}game:conns:{gameId}</c> and <c>{p}instances</c>, and <c>.claude/rules/testing.md</c> is
/// explicit that this class of behaviour is not to be proven against a hand-rolled fake).
/// </summary>
public sealed class MatchNoShowWatcherCrossInstanceTests : IClassFixture<RedisFixture>
{
    private readonly RedisFixture _fixture;

    public MatchNoShowWatcherCrossInstanceTests(RedisFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task No_abort_when_the_missing_player_is_connected_to_a_second_instance()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var options = new RedisOptions(_fixture.Container!.GetConnectionString(), $"test-{Guid.NewGuid():N}:",
            InstanceTtlSeconds: 30, HeartbeatSeconds: 10, CommandTimeoutSeconds: 5);

        var identityA = new InstanceIdentity("instance-a"); // runs the watcher
        var identityB = new InstanceIdentity("instance-b"); // holds the actual connection

        var connectionA = new RedisConnection(options, identityA);
        var connectionB = new RedisConnection(options, identityB);

        var instancesA = new InstanceRegistry(connectionA, identityA, options, NullLogger<InstanceRegistry>.Instance);
        var instancesB = new InstanceRegistry(connectionB, identityB, options, NullLogger<InstanceRegistry>.Instance);
        await instancesA.RegisterAsync();
        await instancesB.RegisterAsync();

        var scopeFactory = new ThrowingScopeFactory();
        var ownershipA = new GameOwnership(connectionA, identityA, options, NullLogger<GameOwnership>.Instance);
        var liveIndexA = new LiveGameIndex(connectionA, identityA, NullLogger<LiveGameIndex>.Instance);
        var distributionA = new GameDistribution(connectionA, ownershipA, liveIndexA, scopeFactory, NullLogger<GameDistribution>.Instance);
        var meterFactory = new TestMeterFactory();
        var metrics = new KiriChessMetrics(meterFactory);
        var engine = new KiriChessEngine();
        var lagOptions = Options.Create(new LagCompensationOptions());
        var killSwitches = new KillSwitchService(scopeFactory, NullLogger<KillSwitchService>.Instance);
        var registry = new GameRegistry(engine, scopeFactory, metrics, lagOptions, distributionA, killSwitches, NullLogger<GameRegistry>.Instance);

        var hub = new FakeHubContext();
        // The watcher's own tracker (instance A): nobody is ever Track()-ed on it locally.
        var connectionsA = new GameConnectionTracker(
            hub, registry, new ThrowingActionExecutor(), connectionA, identityA, instancesA, NullLogger<GameConnectionTracker>.Instance);
        // A second tracker standing in for instance B, where the "missing" player actually is.
        var connectionsB = new GameConnectionTracker(
            hub, registry, new ThrowingActionExecutor(), connectionB, identityB, instancesB, NullLogger<GameConnectionTracker>.Instance);

        var whiteId = Guid.NewGuid();
        var blackId = Guid.NewGuid();
        var config = new KiriChessConfig();
        var session = new GameSession(engine, config, whiteId, blackId, ratingPool: "bounty1", isRated: true);
        registry.Restore(session);

        connectionsA.Track("white-conn", whiteId, session.Id); // white is local to instance A
        connectionsB.Track("black-conn", blackId, session.Id); // black is connected, but to instance B

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Matchmaking:JoinDeadlineSeconds"] = "1" })
            .Build();
        var watcher = new MatchNoShowWatcher(registry, connectionsA, hub, configuration, NullLogger<MatchNoShowWatcher>.Instance);

        watcher.Arm(session.Id, whiteId, blackId);
        await Task.Delay(TimeSpan.FromSeconds(3));

        Assert.False(session.IsOver); // black is present, just on a different instance
    }
}
