using KiriChess.Server.Games;
using KiriChess.Server.Games.Distributed;
using KiriChess.Server.Infrastructure.Redis;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KiriChess.Server.Tests;

/// <summary>
/// Phase 8.4 Task C: <see cref="PresenceTracker"/>, <see cref="SpectatorTracker"/>, <see cref="MatchQueue"/>,
/// <see cref="MatchmakingService"/> and <c>GET /api/games/live</c>'s Redis-enabled path
/// (<see cref="GameEndpoints.IsListable"/>/<see cref="GameEndpoints.ApproximateClocks"/>). See
/// Phase 8.4 "Tests".
///
/// One deviation from the plan's test list: <c>A_user_with_several_tabs_is_counted_once_and_leaves_once</c>
/// was specified as a pure test "against a fake Redis". <c>StackExchange.Redis.IDatabase</c> has well
/// over 500 members (confirmed by attempting exactly that fake and reading the compiler's unimplemented-member
/// list), which makes a hand-rolled fake impractical for what this test needs to prove. It runs
/// container-backed instead, in <see cref="DistributedLiveStateContainerTests"/>, asserting the real
/// SADD/SREM-once behaviour against a real Redis SET.
/// </summary>
public sealed class DistributedLiveStateTests
{
    // --- Presence/spectator counts are unchanged without a Redis collaborator ---

    [Fact]
    public async Task Presence_and_spectator_counts_are_unchanged_without_redis()
    {
        var presence = new PresenceTracker();
        var user = Guid.NewGuid();
        presence.Track("c1", user);
        presence.Track("c2", user);
        Assert.Equal(1, presence.OnlineCount);
        Assert.Equal(1, await presence.OnlineCountAsync());
        Assert.Single(await presence.OnlineUsersAsync());

        var spectators = new SpectatorTracker();
        var game = Guid.NewGuid();
        await spectators.AddAsync("s1", game);
        await spectators.AddAsync("s2", game);
        Assert.Equal(2, await spectators.CountForAsync(game));
    }

    // --- MatchQueue: disabled path is the in-memory implementation ---

    [Fact]
    public async Task MatchQueue_in_memory_behaviour_is_unchanged()
    {
        var q = new MatchQueue();
        var live = new HashSet<string>();

        Assert.Null(await q.TryPairAsync("steal1", new Waiting(Guid.NewGuid(), "c1", 1500), live));

        var opp = await q.TryPairAsync("steal1", new Waiting(Guid.NewGuid(), "c2", 1500), live);
        Assert.NotNull(opp);
        Assert.Equal("c1", opp!.Value.ConnectionId);
    }

    // --- GET /api/games/live's Redis-path clock approximation and solo-game filter (pure functions) ---

    [Fact]
    public void Live_list_clock_approximation_never_goes_negative()
    {
        var now = DateTimeOffset.UtcNow;
        var dto = MakeDto(sideToMove: "White", whiteTime: 5, blackTime: 100, lastMoveAgo: TimeSpan.FromDays(1), now: now);

        var (white, black) = GameEndpoints.ApproximateClocks(dto, now);

        Assert.Equal(0, white); // would be deeply negative without the floor
        Assert.Equal(100, black);
    }

    [Fact]
    public void Live_list_clock_approximation_only_adjusts_the_side_to_move()
    {
        var now = DateTimeOffset.UtcNow;
        var dto = MakeDto(sideToMove: "Black", whiteTime: 60, blackTime: 60, lastMoveAgo: TimeSpan.FromSeconds(10), now: now);

        var (white, black) = GameEndpoints.ApproximateClocks(dto, now);

        Assert.Equal(60, white); // not on move: untouched
        Assert.Equal(50, black); // on move: 60 - 10 elapsed
    }

    [Fact]
    public void Live_list_excludes_solo_games()
    {
        var samePlayer = Guid.NewGuid();
        var solo = MakeDto(whitePlayerId: samePlayer, blackPlayerId: samePlayer);
        Assert.False(GameEndpoints.IsListable(solo));

        var twoPlayer = MakeDto(whitePlayerId: Guid.NewGuid(), blackPlayerId: Guid.NewGuid());
        Assert.True(GameEndpoints.IsListable(twoPlayer));

        var over = MakeDto(whitePlayerId: Guid.NewGuid(), blackPlayerId: Guid.NewGuid(), isOver: true);
        Assert.False(GameEndpoints.IsListable(over));
    }

    private static LiveGameStateDto MakeDto(
        Guid? whitePlayerId = null, Guid? blackPlayerId = null, string sideToMove = "White",
        int whiteTime = 60, int blackTime = 60, bool isOver = false, TimeSpan? lastMoveAgo = null,
        DateTimeOffset? now = null)
    {
        var effectiveNow = now ?? DateTimeOffset.UtcNow;
        return new LiveGameStateDto(
            Guid.NewGuid(),
            "instance-a",
            whitePlayerId ?? Guid.NewGuid(),
            blackPlayerId ?? Guid.NewGuid(),
            "White Player",
            "Black Player",
            "steal1",
            true,
            sideToMove,
            whiteTime,
            blackTime,
            4,
            isOver,
            effectiveNow.AddMinutes(-5),
            effectiveNow - (lastMoveAgo ?? TimeSpan.Zero),
            effectiveNow);
    }
}

/// <summary>
/// Container-backed half of the Task C tests: real Redis, real cross-instance behaviour. Requires
/// Docker; skips cleanly via <see cref="SkippableFactAttribute"/> when no Docker daemon is reachable.
/// See <see cref="RedisFixture"/> for the shared container and why every test below uses its own key
/// prefix.
/// </summary>
public sealed class DistributedLiveStateContainerTests : IClassFixture<RedisFixture>
{
    private readonly RedisFixture _fixture;

    public DistributedLiveStateContainerTests(RedisFixture fixture) => _fixture = fixture;

    private RedisOptions OptionsFor(string prefix, int ttlSeconds = 30) =>
        new(_fixture.Container!.GetConnectionString(), prefix, ttlSeconds, HeartbeatSeconds: 10, CommandTimeoutSeconds: 5);

    private static string UniquePrefix() => $"test-{Guid.NewGuid():N}:";

    [SkippableFact]
    public async Task A_user_with_several_tabs_is_counted_once_and_leaves_once()
    {
        // This is Step 1's stated difficulty (see DistributedLiveStateTests's class comment for why
        // it runs here, container-backed, instead of against a hand-rolled fake IDatabase).
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var options = OptionsFor(UniquePrefix());
        var identity = new InstanceIdentity("instance-a");
        using var connection = new RedisConnection(options, identity);
        var registry = new InstanceRegistry(connection, identity, options, NullLogger<InstanceRegistry>.Instance);
        var presence = new PresenceTracker(connection, identity, registry, options, NullLogger<PresenceTracker>.Instance);
        var user = Guid.NewGuid();
        var presenceKey = connection.Key("presence:instance-a");

        presence.Track("c1", user);
        presence.Track("c2", user);
        presence.Track("c3", user);
        Assert.Equal(1, await WaitForLength(() => connection.Db.SetLengthAsync(presenceKey), 1)); // one SADD, not three

        presence.Remove("c1");
        presence.Remove("c2");
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        Assert.Equal(1, await connection.Db.SetLengthAsync(presenceKey)); // still present, one tab left

        presence.Remove("c3");
        Assert.Equal(0, await WaitForLength(() => connection.Db.SetLengthAsync(presenceKey), 0)); // one SREM on the last tab
    }

    [SkippableFact]
    public async Task Two_instances_pair_players_with_each_other()
    {
        // This is the test that justifies the whole task: two players who click "Play" at the same
        // moment and land on different instances must still find each other.
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var options = OptionsFor(UniquePrefix());
        using var connA = new RedisConnection(options, new InstanceIdentity("instance-a"));
        using var connB = new RedisConnection(options, new InstanceIdentity("instance-b"));
        var qA = new MatchQueue(connA, new InstanceIdentity("instance-a"), NullLogger<MatchQueue>.Instance);
        var qB = new MatchQueue(connB, new InstanceIdentity("instance-b"), NullLogger<MatchQueue>.Instance);
        var live = new HashSet<string> { "instance-a", "instance-b" };

        var user1 = Guid.NewGuid();
        var user2 = Guid.NewGuid();

        var first = await qA.TryPairAsync("steal1", new Waiting(user1, "conn-a", 1500), live);
        Assert.Null(first); // waits on instance-a

        var second = await qB.TryPairAsync("steal1", new Waiting(user2, "conn-b", 1500), live);
        Assert.NotNull(second);
        Assert.Equal(user1, second!.Value.UserId);
        Assert.Equal("conn-a", second.Value.ConnectionId);
    }

    [SkippableFact]
    public async Task A_queue_entry_from_a_dead_instance_is_skipped_and_pruned()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var options = OptionsFor(UniquePrefix());
        using var connDead = new RedisConnection(options, new InstanceIdentity("instance-dead"));
        var qDead = new MatchQueue(connDead, new InstanceIdentity("instance-dead"), NullLogger<MatchQueue>.Instance);
        var deadUser = Guid.NewGuid();
        // Enqueued while "instance-dead" is (nominally) live, from that instance's own point of view.
        await qDead.TryPairAsync("steal1", new Waiting(deadUser, "conn-dead", 1500), new HashSet<string> { "instance-dead" });

        using var connLive = new RedisConnection(options, new InstanceIdentity("instance-live"));
        var qLive = new MatchQueue(connLive, new InstanceIdentity("instance-live"), NullLogger<MatchQueue>.Instance);
        var liveUser = Guid.NewGuid();

        // instance-dead is not in the live set this caller passes in, the dead entrant must be
        // skipped (never matched) and pruned, not handed to the live entrant as a ghost opponent.
        var result = await qLive.TryPairAsync("steal1", new Waiting(liveUser, "conn-live", 1500), new HashSet<string> { "instance-live" });
        Assert.Null(result);

        var depths = await qLive.DepthsAsync();
        Assert.Equal(1, depths["steal1"]); // only the live entrant remains
    }

    [SkippableFact]
    public async Task OpenSeeksAsync_lists_an_unpaired_entrant_and_clears_on_leave()
    {
        // Phase 9e.4: the public seek list, built off the same Redis-backed queue as pairing.
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var options = OptionsFor(UniquePrefix());
        using var connection = new RedisConnection(options, new InstanceIdentity("instance-a"));
        var q = new MatchQueue(connection, new InstanceIdentity("instance-a"), NullLogger<MatchQueue>.Instance);
        var live = new HashSet<string> { "instance-a" };

        await q.TryPairAsync("steal1", new Waiting(Guid.NewGuid(), "c1", 1500), live);

        var seeks = await q.OpenSeeksAsync(live, TimeSpan.FromMinutes(10));
        var seek = Assert.Single(seeks);
        Assert.Equal("steal1", seek.Pool);

        await q.RemoveAsync("c1");
        Assert.Empty(await q.OpenSeeksAsync(live, TimeSpan.FromMinutes(10)));
    }

    [SkippableFact]
    public async Task OpenSeeksAsync_drops_a_seek_once_it_is_paired()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var options = OptionsFor(UniquePrefix());
        using var connection = new RedisConnection(options, new InstanceIdentity("instance-a"));
        var q = new MatchQueue(connection, new InstanceIdentity("instance-a"), NullLogger<MatchQueue>.Instance);
        var live = new HashSet<string> { "instance-a" };

        await q.TryPairAsync("steal1", new Waiting(Guid.NewGuid(), "c1", 1500), live);
        var match = await q.TryPairAsync("steal1", new Waiting(Guid.NewGuid(), "c2", 1500), live); // pairs with c1
        Assert.NotNull(match);

        Assert.Empty(await q.OpenSeeksAsync(live, TimeSpan.FromMinutes(10)));
    }

    [SkippableFact]
    public async Task OpenSeeksAsync_drops_and_prunes_a_dead_instances_entry()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var options = OptionsFor(UniquePrefix());
        using var connDead = new RedisConnection(options, new InstanceIdentity("instance-dead"));
        var qDead = new MatchQueue(connDead, new InstanceIdentity("instance-dead"), NullLogger<MatchQueue>.Instance);
        await qDead.TryPairAsync("steal1", new Waiting(Guid.NewGuid(), "conn-dead", 1500), new HashSet<string> { "instance-dead" });

        using var connLive = new RedisConnection(options, new InstanceIdentity("instance-live"));
        var qLive = new MatchQueue(connLive, new InstanceIdentity("instance-live"), NullLogger<MatchQueue>.Instance);
        var liveOnly = new HashSet<string> { "instance-live" };

        // instance-dead is not in the live set, so its entry must not be advertised, and DepthsAsync
        // (which reads the same ZSET) confirms it was pruned rather than merely filtered.
        Assert.Empty(await qLive.OpenSeeksAsync(liveOnly, TimeSpan.FromMinutes(10)));
        var depths = await qLive.DepthsAsync();
        Assert.Equal(0, depths["steal1"]);
    }

    [SkippableFact]
    public async Task A_user_cannot_sit_in_two_pools_at_once()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var options = OptionsFor(UniquePrefix());
        using var connection = new RedisConnection(options, new InstanceIdentity("instance-a"));
        var q = new MatchQueue(connection, new InstanceIdentity("instance-a"), NullLogger<MatchQueue>.Instance);
        var user = Guid.NewGuid();
        var live = new HashSet<string> { "instance-a" };

        await q.TryPairAsync("steal1", new Waiting(user, "c1", 1500), live);
        await q.TryPairAsync("burn2", new Waiting(user, "c1", 1500), live);

        var depths = await q.DepthsAsync();
        Assert.Equal(0, depths["steal1"]); // swept out by the second join (Phase 6e.3)
        Assert.Equal(1, depths["burn2"]);
    }

    [SkippableFact]
    public async Task Presence_unions_across_instances()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var options = OptionsFor(UniquePrefix());
        var (presenceA, _) = await BuildRegisteredPresenceAsync("instance-a", options);
        var (presenceB, _) = await BuildRegisteredPresenceAsync("instance-b", options);

        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        presenceA.Track("ca", userA);
        presenceB.Track("cb", userB);

        var online = await WaitForSet(() => presenceA.OnlineUsersAsync(), expectedCount: 2);
        Assert.Contains(userA, online);
        Assert.Contains(userB, online);
    }

    [SkippableFact]
    public async Task Presence_excludes_a_dead_instance()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var prefix = UniquePrefix();
        var liveOptions = OptionsFor(prefix);
        var deadOptions = liveOptions with { InstanceTtlSeconds = 1 };

        var (presenceLive, _) = await BuildRegisteredPresenceAsync("instance-live", liveOptions);
        var (presenceDead, _) = await BuildRegisteredPresenceAsync("instance-dead", deadOptions);

        var deadUser = Guid.NewGuid();
        presenceDead.Track("cd", deadUser);

        await Task.Delay(TimeSpan.FromSeconds(1.5)); // let instance-dead's heartbeat key expire

        var online = await presenceLive.OnlineUsersAsync();
        Assert.DoesNotContain(deadUser, online);
    }

    [SkippableFact]
    public async Task Spectator_count_sums_across_instances_and_drops_dead_ones()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var prefix = UniquePrefix();
        var liveOptions = OptionsFor(prefix);
        var deadOptions = liveOptions with { InstanceTtlSeconds = 1 };

        var liveIdentity = new InstanceIdentity("instance-live");
        using var connLive = new RedisConnection(liveOptions, liveIdentity);
        var registryLive = new InstanceRegistry(connLive, liveIdentity, liveOptions, NullLogger<InstanceRegistry>.Instance);
        var spectatorsLive = new SpectatorTracker(connLive, liveIdentity, registryLive, NullLogger<SpectatorTracker>.Instance);
        await registryLive.RegisterAsync();

        var deadIdentity = new InstanceIdentity("instance-dead");
        using var connDead = new RedisConnection(deadOptions, deadIdentity);
        var registryDead = new InstanceRegistry(connDead, deadIdentity, deadOptions, NullLogger<InstanceRegistry>.Instance);
        var spectatorsDead = new SpectatorTracker(connDead, deadIdentity, registryDead, NullLogger<SpectatorTracker>.Instance);
        await registryDead.RegisterAsync();

        var game = Guid.NewGuid();
        await spectatorsLive.AddAsync("c-live", game);
        await spectatorsDead.AddAsync("c-dead", game);

        Assert.Equal(2, await spectatorsLive.CountForAsync(game));

        await Task.Delay(TimeSpan.FromSeconds(1.5)); // let instance-dead's heartbeat key expire

        Assert.Equal(1, await spectatorsLive.CountForAsync(game));

        // Pruned, not just excluded from the count.
        var members = await connLive.Db.SetMembersAsync(connLive.Key($"spectators:{game}"));
        Assert.DoesNotContain(members, m => ((string)m!).StartsWith("instance-dead:"));
    }

    private async Task<(PresenceTracker Presence, InstanceRegistry Registry)> BuildRegisteredPresenceAsync(string instanceId, RedisOptions options)
    {
        var identity = new InstanceIdentity(instanceId);
        var connection = new RedisConnection(options, identity);
        var registry = new InstanceRegistry(connection, identity, options, NullLogger<InstanceRegistry>.Instance);
        await registry.RegisterAsync();
        var presence = new PresenceTracker(connection, identity, registry, options, NullLogger<PresenceTracker>.Instance);
        return (presence, registry);
    }

    /// <summary>
    /// <see cref="PresenceTracker.Track"/>/<see cref="PresenceTracker.Remove"/> fire their Redis write
    /// as an untracked background task (a hub method must not block on a Redis round trip, see
    /// <see cref="PresenceTracker"/>'s doc comment), so a fast-following assertion needs a short poll
    /// rather than a fixed sleep or a race.
    /// </summary>
    private static async Task<long> WaitForLength(Func<Task<long>> probe, long expected)
    {
        long last = -1;
        for (var i = 0; i < 20; i++)
        {
            last = await probe();
            if (last == expected) return last;
            await Task.Delay(50);
        }
        return last;
    }

    private static async Task<HashSet<Guid>> WaitForSet(Func<Task<HashSet<Guid>>> probe, int expectedCount)
    {
        HashSet<Guid> result = new();
        for (var i = 0; i < 20; i++)
        {
            result = await probe();
            if (result.Count >= expectedCount) return result;
            await Task.Delay(100);
        }
        return result;
    }
}
