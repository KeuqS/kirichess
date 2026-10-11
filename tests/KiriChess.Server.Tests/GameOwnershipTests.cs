using KiriChess.Engine;
using KiriChess.Engine.Config;
using KiriChess.Server.Games;
using KiriChess.Server.Games.Distributed;
using KiriChess.Server.Infrastructure;
using KiriChess.Server.Infrastructure.Redis;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace KiriChess.Server.Tests;

/// <summary>
/// Phase 8.4 Task B: the ownership lease (<see cref="GameOwnership"/>), the single local/forwarded
/// code path (<see cref="GameActionExecutor"/>), and cross-instance connection tracking
/// (<see cref="GameConnectionTracker"/>). Pure tests below run everywhere; <see cref="GameOwnershipContainerTests"/>
/// needs a real Redis and skips cleanly without Docker.
/// </summary>
public sealed class GameOwnershipTests
{
    private static KiriChessConfig SimpleConfig() =>
        new() { MoveCostMode = MoveCostMode.None, CheckCheckerGain = 0, CheckVictimLoss = 0 };

    [Fact]
    public async Task Disabled_redis_owns_every_game_locally()
    {
        var options = RedisOptions.From(new ConfigurationBuilder().Build());
        Assert.False(options.Enabled);
        var identity = new InstanceIdentity("solo-instance");
        using var connection = new RedisConnection(options, identity);
        var ownership = new GameOwnership(connection, identity, options, NullLogger<GameOwnership>.Instance);

        var gameId = Guid.NewGuid();
        Assert.True(await ownership.TryClaimAsync(gameId));
        Assert.Equal("solo-instance", await ownership.GetOwnerAsync(gameId));
        Assert.True(await ownership.RenewAsync(gameId));
        await ownership.ReleaseAsync(gameId); // must not throw
    }

    [Fact]
    public async Task Local_path_is_used_when_the_session_is_in_the_registry()
    {
        // ThrowingCommandRouter fails the test outright if GameActionExecutor ever reaches the
        // forwarding path for a game whose session is right there in the local registry.
        var harness = GameActionExecutorHarness.Build(new ThrowingCommandRouter());
        var white = Guid.NewGuid();
        var black = Guid.NewGuid();
        var session = new GameSession(new KiriChessEngine(), SimpleConfig(), white, black, "bounty1", isRated: true);
        harness.Registry.Restore(session);

        await harness.Executor.SubmitMoveAsync(session.Id, white, "conn-white", "e2e4");

        Assert.Single(session.Moves);
        var players = harness.Hub.Clients.GroupProxies[GameHubGroups.Players(session.Id)];
        var playerSend = Assert.Single(players.Sent, s => s.Method == "MovePlayed");
        var playerPayload = Assert.IsType<MovePlayedDto>(playerSend.Args[0]);
        Assert.NotEmpty(playerPayload.LegalMoves);

        var spectators = harness.Hub.Clients.GroupProxies[GameHubGroups.Spectators(session.Id)];
        var spectatorSend = Assert.Single(spectators.Sent, s => s.Method == "MovePlayed");
        var spectatorPayload = Assert.IsType<MovePlayedDto>(spectatorSend.Args[0]);
        Assert.Empty(spectatorPayload.LegalMoves);
        Assert.Equal(playerPayload.Uci, spectatorPayload.Uci);
    }

    [Fact]
    public async Task Forwarded_errors_surface_with_the_same_message_as_local_ones()
    {
        // The README's rule 1: a player must not be able to tell from the exception message whether
        // their action executed locally or was forwarded. Drive "not your turn" both ways.
        var white = Guid.NewGuid();
        var black = Guid.NewGuid();
        var session = new GameSession(new KiriChessEngine(), SimpleConfig(), white, black, "bounty1", isRated: true);

        var localHarness = GameActionExecutorHarness.Build(new ThrowingCommandRouter());
        localHarness.Registry.Restore(session);

        // Local: it is White's turn, so Black submitting a move is rejected locally.
        var localEx = await Assert.ThrowsAsync<HubException>(
            () => localHarness.Executor.SubmitMoveAsync(session.Id, black, "conn-black", "e7e5"));

        // Forwarded: simulates a remote owner whose GameSession.ApplyMove threw the identical
        // InvalidGameActionException("Not your turn.") and whose GameActionExecutor.ExecuteAsync
        // translated it into a GameCommandReply, independently hardcoded here, not copied from
        // localEx, so this test actually proves the two strings agree rather than assuming it.
        // GameActionExecutor.ForwardAsync is `internal` purely for this seam.
        var router = new RecordingCommandRouter { ReplyFactory = env => new GameCommandReply(env.CorrelationId, false, "Not your turn.", null) };
        var remoteHarness = GameActionExecutorHarness.Build(router);
        var forwardedEx = await Assert.ThrowsAsync<HubException>(() => remoteHarness.Executor.ForwardAsync(
            "remote-instance", GameCommandKind.SubmitMove, session.Id, black, "conn-black", "e7e5", CancellationToken.None));

        Assert.Equal("Not your turn.", localEx.Message);
        Assert.Equal(localEx.Message, forwardedEx.Message);
    }

    [Fact]
    public async Task A_rejected_forward_is_reported_verbatim()
    {
        // A second, independent case: the router replies with an arbitrary domain rejection, and
        // ForwardAsync must rethrow exactly that text, not a generic "could not complete" message.
        var router = new RecordingCommandRouter
        {
            ReplyFactory = env => new GameCommandReply(env.CorrelationId, false, "Game is already over.", null)
        };
        var harness = GameActionExecutorHarness.Build(router);

        var ex = await Assert.ThrowsAsync<HubException>(() => harness.Executor.ForwardAsync(
            "remote-instance", GameCommandKind.Resign, Guid.NewGuid(), Guid.NewGuid(), "conn-1", null, CancellationToken.None));

        Assert.Equal("Game is already over.", ex.Message);
        Assert.Single(router.Sent);
    }

    [Fact]
    public async Task A_lapsed_but_unclaimed_lease_is_reclaimed_and_the_game_keeps_playing()
    {
        // Task B2 Step 1: RenewAsync false does not by itself prove a takeover, the owner key can be
        // gone with nobody having claimed it (Redis restart without persistence, a flush, or a
        // maxmemory eviction). The heartbeat must try to reclaim before it evicts.
        var harness = GameOwnershipHeartbeatHarness.Build(claimSucceeds: true, renewSucceeds: false);

        await harness.Heartbeat.OnHeartbeatAsync(CancellationToken.None);

        // Still in the registry: the reclaim succeeded, so the live session was kept, not dropped.
        Assert.NotNull(harness.Registry.Get(harness.Session.Id));
    }

    [Fact]
    public async Task A_lease_taken_over_by_another_instance_still_evicts()
    {
        // The behaviour that must not regress while fixing the case above: when the claim attempt
        // also fails, another instance genuinely holds the lease, and the local (now stale) replica
        // must still be evicted and never persisted.
        var harness = GameOwnershipHeartbeatHarness.Build(claimSucceeds: false, renewSucceeds: false);

        await harness.Heartbeat.OnHeartbeatAsync(CancellationToken.None);

        Assert.Null(harness.Registry.Get(harness.Session.Id));
    }

    [Fact]
    public async Task Reconnect_notice_cancels_a_pending_abandon_and_broadcasts_exactly_once()
    {
        // Single-instance (Redis disabled): GameConnectionTracker.Track's cancel-and-broadcast runs
        // synchronously and directly, the same observable behaviour as before Phase 8.4.
        var hub = new FakeHubContext();
        var identity = new InstanceIdentity("solo-instance");
        var options = RedisOptions.From(new ConfigurationBuilder().Build());
        using var connection = new RedisConnection(options, identity);
        var instances = new InstanceRegistry(connection, identity, options, NullLogger<InstanceRegistry>.Instance);
        var harness = GameActionExecutorHarness.Build(new ThrowingCommandRouter());
        var tracker = new GameConnectionTracker(
            hub, harness.Registry, new ThrowingActionExecutor(), connection, identity, instances, NullLogger<GameConnectionTracker>.Instance);

        var gameId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        tracker.Track("conn-1", userId, gameId);
        tracker.OnDisconnected("conn-1"); // arms the grace timer (fire-and-forget)

        // RunGraceAsync's OpponentDisconnected broadcast happens synchronously up to its own first
        // await in these tests (FakeClientProxy never actually suspends), but poll briefly instead of
        // depending on that, this only needs to wait out real scheduling, never the 30s grace window.
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!hub.Clients.GroupProxies.TryGetValue(gameId.ToString(), out var g) || g.Sent.Count == 0)
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("OpponentDisconnected was never broadcast.");
            await Task.Delay(10);
        }

        tracker.Track("conn-2", userId, gameId); // reconnect, cancels the armed timer, broadcasts once

        var groupExcept = hub.Clients.GroupExceptProxies[gameId.ToString()];
        Assert.Single(groupExcept.Sent);
        Assert.Equal("OpponentReconnected", groupExcept.Sent[0].Method);

        // Nothing left armed: a second reconnect notice for the same pair is a silent no-op.
        Assert.False(tracker.CancelPendingAbandon(gameId, userId));
    }

    [Fact]
    public async Task A_second_disconnect_in_one_grace_window_does_not_leave_the_first_timer_running()
    {
        // C-2-5 / C-5-9. _pending is keyed by (game, user), not by connection, so a player with two
        // tabs open (or one tab that drops twice inside the window) arms the grace timer twice. The
        // arming used to be a bare indexer assignment, which dropped the first CancellationTokenSource
        // without cancelling it: both timers then ran to the end of their windows and both asked for a
        // forfeit. Exactly one timer may survive a second disconnect.
        var abandons = new RecordingActionExecutor();
        var (tracker, _, _) = BuildTracker(abandons, TimeSpan.FromMilliseconds(250));

        var gameId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        tracker.Track("conn-1", userId, gameId);
        tracker.Track("conn-2", userId, gameId); // same player, second tab

        // FakeClientProxy completes SendAsync synchronously, so RunGraceAsync runs straight through
        // its broadcast to the arming AddOrUpdate before it suspends on the delay. Each call therefore
        // returns with its timer genuinely armed, no polling needed.
        tracker.OnDisconnected("conn-1");
        tracker.OnDisconnected("conn-2");

        // Comfortably past a 250ms window: both timers, if both were still running, would have fired.
        await Task.Delay(1500);

        Assert.Equal(1, abandons.AbandonCount);
    }

    [Fact]
    public async Task A_reconnect_after_a_second_disconnect_still_tells_the_opponent()
    {
        // The player-visible half of C-2-5, and the reason it is not merely a leaked timer. The
        // orphaned first timer removed the *newer* timer's _pending entry when its own window elapsed,
        // so the player's reconnect found nothing armed, CancelPendingAbandon returned false, and
        // HandleReconnectAsync returned before broadcasting. The opponent's client sets presenceNote on
        // OpponentDisconnected and clears it only on OpponentReconnected, so it spent the rest of the
        // game claiming the opponent had 30 seconds to return.
        //
        // The two disconnects are spaced so that the first timer's window closes while the second's is
        // still open: the reconnect below lands in exactly that gap, which is the only interval in
        // which the two behaviours differ. The gap's width is the spacing between the two
        // disconnects (both timers share the same grace, so that spacing is exactly how far apart
        // the two windows close), not the grace value itself. B12-2 found this flaking under a
        // loaded machine with the previous 1s-wide gap and a 0.5s margin on each side of the landing
        // point; widened to a 3s-wide gap with a 1.5s margin on each side so ordinary scheduler
        // jitter under load cannot push the reconnect outside the gap. The assertion is unchanged.
        var grace = TimeSpan.FromSeconds(2);
        var (tracker, hub, _) = BuildTracker(new RecordingActionExecutor(), grace);

        var gameId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        tracker.Track("conn-1", userId, gameId);
        tracker.Track("conn-2", userId, gameId);

        tracker.OnDisconnected("conn-1");              // window closes at ~2.0s
        await Task.Delay(TimeSpan.FromSeconds(3));
        tracker.OnDisconnected("conn-2");              // window closes at ~5.0s (at ~3.0s + grace)
        await Task.Delay(TimeSpan.FromSeconds(0.5));   // ~3.5s: past the first window, inside the second

        tracker.Track("conn-3", userId, gameId);       // the player comes back, still inside their grace

        Assert.True(
            hub.Clients.GroupExceptProxies.TryGetValue(gameId.ToString(), out var groupExcept)
            && groupExcept.Sent.Any(m => m.Method == "OpponentReconnected"),
            "The opponent was never told the player came back, so their client is still showing the disconnect notice.");
    }

    [Fact]
    public async Task A_player_who_never_left_has_their_disconnect_notice_retracted()
    {
        // The other half of C-2-5's stuck banner, reached without a second disconnect at all. A player
        // with two tabs open closes one: the grace timer arms and broadcasts, the surviving tab means
        // IsConnectedAsync is true when the window closes so there is correctly no forfeit, and there
        // is no reconnect either, because the player never left. Nothing used to clear the notice, and
        // the client clears it on OpponentReconnected and on nothing else, so the opponent spent the
        // rest of the game being told their opponent had 30 seconds to return.
        var abandons = new RecordingActionExecutor();
        var (tracker, hub, _) = BuildTracker(abandons, TimeSpan.FromMilliseconds(250));

        var gameId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        tracker.Track("conn-1", userId, gameId);
        tracker.Track("conn-2", userId, gameId); // the tab that stays open

        tracker.OnDisconnected("conn-1");

        await Task.Delay(1500); // well past the window, so the timer has run and decided not to forfeit

        Assert.Equal(0, abandons.AbandonCount); // the surviving connection still saves the game

        // Sent to the whole group, not GroupExcept: there is no reconnecting connection to spare, and
        // the player's own surviving tab was shown the notice too.
        Assert.True(
            hub.Clients.GroupProxies.TryGetValue(gameId.ToString(), out var group)
            && group.Sent.Any(m => m.Method == "OpponentReconnected"),
            "The disconnect notice was never retracted, so the opponent's banner stays up for the rest of the game.");
    }

    [Fact]
    public void A_full_disconnect_is_recorded_and_a_spare_tab_is_not()
    {
        // S-2-6. The count is the arm, taken while the player has no connection left. Closing one
        // of two tabs still arms the timer (C-2-5) but the player never left, so it is not a flap.
        var recorded = new RecordingGraceDisconnects();
        var (tracker, _, _) = BuildTracker(new RecordingActionExecutor(), TimeSpan.FromSeconds(30), recorded);
        var gameId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        tracker.Track("conn-1", userId, gameId);
        tracker.Track("conn-2", userId, gameId);
        tracker.OnDisconnected("conn-1");
        Assert.Empty(recorded.Calls);

        tracker.OnDisconnected("conn-2");
        Assert.Equal([(gameId, userId)], recorded.Calls);
    }

    [Fact]
    public void A_disconnect_that_returns_inside_the_window_is_still_recorded()
    {
        // The case AbandonmentPolicy cannot see: they come back before the timer fires, the game
        // does not end Abandoned, and the arm has to remain.
        var recorded = new RecordingGraceDisconnects();
        var (tracker, _, _) = BuildTracker(new RecordingActionExecutor(), TimeSpan.FromSeconds(30), recorded);
        var gameId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        tracker.Track("conn-1", userId, gameId);
        tracker.OnDisconnected("conn-1");
        tracker.Track("conn-2", userId, gameId);
        tracker.OnDisconnected("conn-2");

        Assert.Equal(2, recorded.Calls.Count);
        Assert.All(recorded.Calls, call => Assert.Equal((gameId, userId), call));
    }

    [Fact]
    public void A_disconnect_after_the_game_ended_is_not_recorded()
    {
        var recorded = new RecordingGraceDisconnects();
        var (tracker, _, registry) = BuildTracker(new RecordingActionExecutor(), TimeSpan.FromSeconds(30), recorded);
        var white = Guid.NewGuid();
        var black = Guid.NewGuid();
        var session = new GameSession(new KiriChessEngine(), SimpleConfig(), white, black, "bounty1", isRated: false);
        session.Abandon(white);
        registry.Restore(session);

        tracker.Track("conn-1", white, session.Id);
        tracker.OnDisconnected("conn-1");

        Assert.Empty(recorded.Calls);
    }

    /// <summary>
    /// A tracker on the single-instance (Redis disabled) path, whose grace window can be shortened so a
    /// test can reach the far side of it. Everything else matches the two reconnect tests above.
    /// </summary>
    private static (GameConnectionTracker Tracker, FakeHubContext Hub, GameRegistry Registry) BuildTracker(
        IGameActionExecutor actions, TimeSpan grace, IGraceDisconnectStore? graceDisconnects = null)
    {
        var hub = new FakeHubContext();
        var identity = new InstanceIdentity("solo-instance");
        var options = RedisOptions.From(new ConfigurationBuilder().Build());
        var connection = new RedisConnection(options, identity);
        var instances = new InstanceRegistry(connection, identity, options, NullLogger<InstanceRegistry>.Instance);
        var harness = GameActionExecutorHarness.Build(new ThrowingCommandRouter());
        var tracker = new GameConnectionTracker(
            hub, harness.Registry, actions, connection, identity, instances,
            NullLogger<GameConnectionTracker>.Instance, grace, graceDisconnects);
        return (tracker, hub, harness.Registry);
    }

    private sealed class RecordingGraceDisconnects : IGraceDisconnectStore
    {
        public List<(Guid GameId, Guid UserId)> Calls { get; } = [];

        public void RecordArmed(Guid gameId, Guid userId) => Calls.Add((gameId, userId));
    }

    [Fact]
    public async Task Solo_disconnect_does_not_broadcast_opponent_disconnected()
    {
        var hub = new FakeHubContext();
        var identity = new InstanceIdentity("solo-instance");
        var options = RedisOptions.From(new ConfigurationBuilder().Build());
        using var connection = new RedisConnection(options, identity);
        var instances = new InstanceRegistry(connection, identity, options, NullLogger<InstanceRegistry>.Instance);
        var harness = GameActionExecutorHarness.Build(new ThrowingCommandRouter());
        var tracker = new GameConnectionTracker(
            hub, harness.Registry, new ThrowingActionExecutor(), connection, identity, instances, NullLogger<GameConnectionTracker>.Instance);

        var userId = Guid.NewGuid();
        var session = new GameSession(new KiriChessEngine(), SimpleConfig(), userId, userId, "bounty1", isRated: false);
        harness.Registry.Restore(session);

        tracker.Track("conn-1", userId, session.Id);
        tracker.OnDisconnected("conn-1");

        // Give the fire-and-forget grace task the same scheduling window the human-vs-human test uses.
        await Task.Delay(50);

        Assert.False(
            hub.Clients.GroupProxies.TryGetValue(session.Id.ToString(), out var group)
            && group.Sent.Any(s => s.Method == "OpponentDisconnected"));
    }
}

/// <summary>
/// Container-backed half of the ownership tests: real Redis, real TTL expiry. Requires Docker; skips
/// cleanly via <see cref="SkippableFactAttribute"/> when no Docker daemon is reachable. Each test uses
/// its own key prefix (shared *within* the test, across its simulated instances) so tests never see
/// each other's leases despite sharing one container (see <see cref="RedisFixture"/>).
/// </summary>
public sealed class GameOwnershipContainerTests : IClassFixture<RedisFixture>
{
    private readonly RedisFixture _fixture;

    public GameOwnershipContainerTests(RedisFixture fixture) => _fixture = fixture;

    private (RedisConnection Connection, GameOwnership Ownership) CreateOwnership(string prefix, string instanceId, int ttlSeconds = 30)
    {
        var options = new RedisOptions(_fixture.Container!.GetConnectionString(), prefix, ttlSeconds, HeartbeatSeconds: 10, CommandTimeoutSeconds: 5);
        var identity = new InstanceIdentity(instanceId);
        var connection = new RedisConnection(options, identity);
        var ownership = new GameOwnership(connection, identity, options, NullLogger<GameOwnership>.Instance);
        return (connection, ownership);
    }

    [SkippableFact]
    public async Task A_claimed_game_cannot_be_claimed_by_another_instance()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");
        var prefix = $"test-{Guid.NewGuid():N}:";
        var gameId = Guid.NewGuid();
        var (connA, ownA) = CreateOwnership(prefix, "instance-a");
        var (connB, ownB) = CreateOwnership(prefix, "instance-b");

        Assert.True(await ownA.TryClaimAsync(gameId));
        Assert.False(await ownB.TryClaimAsync(gameId));
        Assert.Equal("instance-a", await ownA.GetOwnerAsync(gameId));

        connA.Dispose();
        connB.Dispose();
    }

    [SkippableFact]
    public async Task An_expired_lease_can_be_claimed_by_another_instance()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");
        var prefix = $"test-{Guid.NewGuid():N}:";
        var gameId = Guid.NewGuid();
        var (connA, ownA) = CreateOwnership(prefix, "instance-a", ttlSeconds: 1);
        var (connB, ownB) = CreateOwnership(prefix, "instance-b", ttlSeconds: 30);

        Assert.True(await ownA.TryClaimAsync(gameId));
        await Task.Delay(TimeSpan.FromSeconds(1.5));

        Assert.True(await ownB.TryClaimAsync(gameId));
        Assert.Equal("instance-b", await ownB.GetOwnerAsync(gameId));

        connA.Dispose();
        connB.Dispose();
    }

    [SkippableFact]
    public async Task Renew_fails_once_another_instance_has_taken_over()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");
        var prefix = $"test-{Guid.NewGuid():N}:";
        var gameId = Guid.NewGuid();
        var (connA, ownA) = CreateOwnership(prefix, "instance-a", ttlSeconds: 1);
        var (connB, ownB) = CreateOwnership(prefix, "instance-b", ttlSeconds: 30);

        Assert.True(await ownA.TryClaimAsync(gameId));
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        Assert.True(await ownB.TryClaimAsync(gameId)); // B takes over after A's lease lapses

        // Redis explicitly answers "you no longer own this", the one failure mode RenewAsync must
        // believe (see its doc comment).
        Assert.False(await ownA.RenewAsync(gameId));

        connA.Dispose();
        connB.Dispose();
    }

    [SkippableFact]
    public async Task Release_does_not_delete_another_instances_lease()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");
        var prefix = $"test-{Guid.NewGuid():N}:";
        var gameId = Guid.NewGuid();
        var (connA, ownA) = CreateOwnership(prefix, "instance-a");
        var (connB, ownB) = CreateOwnership(prefix, "instance-b");

        Assert.True(await ownA.TryClaimAsync(gameId));
        await ownB.ReleaseAsync(gameId); // B never held it, compare-and-delete must refuse
        Assert.Equal("instance-a", await ownA.GetOwnerAsync(gameId));

        connA.Dispose();
        connB.Dispose();
    }

    [SkippableFact]
    public async Task A_dead_owners_lease_can_be_taken_over_by_name_but_nobody_elses_can()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");
        var prefix = $"test-{Guid.NewGuid():N}:";
        var gameId = Guid.NewGuid();
        var (connA, ownA) = CreateOwnership(prefix, "instance-a");
        var (connB, ownB) = CreateOwnership(prefix, "instance-b");
        var (connC, ownC) = CreateOwnership(prefix, "instance-c");

        Assert.True(await ownA.TryClaimAsync(gameId));

        // The state C-5-3 is about: A is gone but its lease has not expired, and the plain SET NX claim
        // that the dead-owner branch used to make cannot get past it.
        Assert.False(await ownB.TryClaimAsync(gameId));

        // Naming the wrong holder is refused, so a caller cannot displace a lease by guessing.
        Assert.False(await ownB.TryTakeOverFromAsync(gameId, "instance-z"));
        Assert.Equal("instance-a", await ownA.GetOwnerAsync(gameId));

        // Naming the actual holder, which the caller has proved dead, wins.
        Assert.True(await ownB.TryTakeOverFromAsync(gameId, "instance-a"));
        Assert.Equal("instance-b", await ownB.GetOwnerAsync(gameId));

        // A third instance racing on the same stale reading loses: the script is atomic, so it reads
        // B's id, not A's.
        Assert.False(await ownC.TryTakeOverFromAsync(gameId, "instance-a"));
        Assert.Equal("instance-b", await ownB.GetOwnerAsync(gameId));

        // The new holder holds a real lease, TTL and all, not a bare key.
        Assert.True(await ownB.RenewAsync(gameId));
        Assert.False(await ownA.RenewAsync(gameId));

        connA.Dispose();
        connB.Dispose();
        connC.Dispose();
    }

    [SkippableFact]
    public async Task A_graceful_stop_releases_every_lease_this_instance_holds()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");
        var prefix = $"test-{Guid.NewGuid():N}:";
        var (connection, options, identity) = CreateInstance(prefix, "instance-a");
        using var _ = connection;

        var ownership = new GameOwnership(connection, identity, options, NullLogger<GameOwnership>.Instance);
        var (registry, heartbeat) = BuildOwnershipHeartbeat(connection, identity, ownership);

        var engine = new KiriChessEngine();
        var sessions = new List<GameSession>();
        for (var i = 0; i < 3; i++)
        {
            var session = new GameSession(
                engine,
                new KiriChessConfig { MoveCostMode = MoveCostMode.None, CheckCheckerGain = 0, CheckVictimLoss = 0 },
                Guid.NewGuid(), Guid.NewGuid(), "bounty1", isRated: true);
            registry.Restore(session);
            sessions.Add(session);
            Assert.True(await ownership.TryClaimAsync(session.Id));
        }

        await heartbeat.OnInstanceStoppingAsync(CancellationToken.None);

        // C-5-3: a graceful stop must leave no {p}game:owner:* key behind. Before this, they sat there
        // for the whole InstanceTtlSeconds while the heartbeat key was already gone, and that pairing
        // is the one state the takeover path cannot resolve: the owner reads as provably dead and its
        // lease is still unclaimable, so every reconnecting player gets "Game not found."
        foreach (var session in sessions)
            Assert.Null(await ownership.GetOwnerAsync(session.Id));
    }

    [SkippableFact]
    public async Task Shutdown_hooks_run_while_the_instance_is_still_registered()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");
        var prefix = $"test-{Guid.NewGuid():N}:";
        // A heartbeat cadence far longer than the test, so no tick fires between start and stop and the
        // only Redis writes are the registration and the deregistration this test is about.
        var (connection, options, identity) = CreateInstance(prefix, "instance-a", heartbeatSeconds: 600);
        using var _ = connection;

        var instances = new InstanceRegistry(connection, identity, options, NullLogger<InstanceRegistry>.Instance);
        var hook = new LivenessProbingShutdownHook(instances, identity.Id);
        var heartbeat = new InstanceHeartbeat(
            instances, options, Array.Empty<IInstanceHeartbeatHook>(),
            new IInstanceShutdownHook[] { hook }, NullLogger<InstanceHeartbeat>.Instance);

        await heartbeat.StartAsync(CancellationToken.None);
        Assert.True(await instances.IsLiveAsync(identity.Id));

        await heartbeat.StopAsync(CancellationToken.None);

        // The ordering is the fix, not the release itself: releasing after the deregistration would
        // still leave a window in which this instance reads as dead while its leases are unclaimable.
        Assert.True(hook.Ran);
        Assert.True(hook.InstanceWasStillLive, "A shutdown hook ran after the instance had already been deregistered.");
        Assert.False(await instances.IsLiveAsync(identity.Id));
    }

    private (RedisConnection Connection, RedisOptions Options, InstanceIdentity Identity) CreateInstance(
        string prefix, string instanceId, int ttlSeconds = 30, int heartbeatSeconds = 10)
    {
        var options = new RedisOptions(_fixture.Container!.GetConnectionString(), prefix, ttlSeconds, heartbeatSeconds, CommandTimeoutSeconds: 5);
        var identity = new InstanceIdentity(instanceId);
        return (new RedisConnection(options, identity), options, identity);
    }

    /// <summary>
    /// A real <see cref="GameOwnershipHeartbeat"/> over a real Redis-backed <see cref="GameOwnership"/>.
    /// Only the lease side needs to be real here: the shutdown path releases leases and touches neither
    /// the live-state publish nor the connections hash, so those collaborators take their genuine
    /// disabled-Redis no-op path, exactly as <c>GameOwnershipHeartbeatHarness</c> does.
    /// </summary>
    private static (GameRegistry Registry, GameOwnershipHeartbeat Heartbeat) BuildOwnershipHeartbeat(
        IRedisConnection connection, InstanceIdentity identity, GameOwnership ownership)
    {
        var disabledOptions = RedisOptions.From(new ConfigurationBuilder().Build());
        var disabledConnection = new RedisConnection(disabledOptions, identity);
        var liveIndex = new LiveGameIndex(disabledConnection, identity, NullLogger<LiveGameIndex>.Instance);
        var scopeFactory = new ThrowingScopeFactory();
        var distribution = new GameDistribution(disabledConnection, ownership, liveIndex, scopeFactory, NullLogger<GameDistribution>.Instance);
        var instances = new InstanceRegistry(disabledConnection, identity, disabledOptions, NullLogger<InstanceRegistry>.Instance);

        using var meterFactory = new TestMeterFactory();
        var metrics = new KiriChessMetrics(meterFactory);
        var registry = new GameRegistry(new KiriChessEngine(), scopeFactory, metrics, Options.Create(new LagCompensationOptions()), distribution, new KillSwitchService(scopeFactory, NullLogger<KillSwitchService>.Instance), NullLogger<GameRegistry>.Instance);

        var connections = new GameConnectionTracker(
            new FakeHubContext(), registry, new ThrowingActionExecutor(), disabledConnection, identity, instances,
            NullLogger<GameConnectionTracker>.Instance);

        var heartbeat = new GameOwnershipHeartbeat(
            registry, ownership, distribution, connections, connection, NullLogger<GameOwnershipHeartbeat>.Instance);
        return (registry, heartbeat);
    }

    [SkippableFact]
    public async Task The_live_index_drops_games_whose_state_key_expired()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");
        var prefix = $"test-{Guid.NewGuid():N}:";
        var options = new RedisOptions(_fixture.Container!.GetConnectionString(), prefix, InstanceTtlSeconds: 30, HeartbeatSeconds: 10, CommandTimeoutSeconds: 5);
        var identity = new InstanceIdentity("instance-a");
        using var connection = new RedisConnection(options, identity);
        var index = new LiveGameIndex(connection, identity, NullLogger<LiveGameIndex>.Instance);

        var session = new GameSession(
            new KiriChessEngine(),
            new KiriChessConfig { MoveCostMode = MoveCostMode.None, CheckCheckerGain = 0, CheckVictimLoss = 0 },
            Guid.NewGuid(), Guid.NewGuid(), "bounty1", isRated: true);

        await index.PublishAsync(session, "Alice", "Bob", "None", "None");

        // Force the state key to expire immediately rather than waiting out its real 1-hour TTL.
        await connection.Db.KeyExpireAsync(connection.Key($"game:state:{session.Id}"), TimeSpan.FromMilliseconds(1));
        await Task.Delay(TimeSpan.FromMilliseconds(200));

        var live = await index.LiveGamesAsync(limit: 50);
        Assert.DoesNotContain(live, g => g.GameId == session.Id);

        // Self-healing: the dangling ZSET entry is pruned, not just skipped.
        var score = await connection.Db.SortedSetScoreAsync(connection.Key("games:live"), session.Id.ToString());
        Assert.Null(score);
    }

    [SkippableFact]
    public async Task A_flushed_owner_key_does_not_cost_the_game()
    {
        // Task B2 Step 1, against real Redis: a DEL'd owner key is exactly what a restart without
        // persistence (or a flush) looks like. The heartbeat must reclaim, not evict.
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");
        var prefix = $"test-{Guid.NewGuid():N}:";
        var (connection, ownership) = CreateOwnership(prefix, "instance-a");

        var engine = new KiriChessEngine();
        var config = new KiriChessConfig { MoveCostMode = MoveCostMode.None, CheckCheckerGain = 0, CheckVictimLoss = 0 };
        var session = new GameSession(engine, config, Guid.NewGuid(), Guid.NewGuid(), "bounty1", isRated: true);

        Assert.True(await ownership.TryClaimAsync(session.Id));
        await connection.Db.KeyDeleteAsync(connection.Key($"game:owner:{session.Id}"));

        // Everything downstream of the renew/claim branch (live-state publish, connections TTL
        // refresh) takes the disabled-Redis no-op path -- this test is only about the lease itself.
        var disabledOptions = RedisOptions.From(new ConfigurationBuilder().Build());
        var identity = new InstanceIdentity("instance-a");
        using var disabledConnection = new RedisConnection(disabledOptions, identity);
        var liveIndex = new LiveGameIndex(disabledConnection, identity, NullLogger<LiveGameIndex>.Instance);
        var scopeFactory = new ThrowingScopeFactory();
        var distribution = new GameDistribution(disabledConnection, ownership, liveIndex, scopeFactory, NullLogger<GameDistribution>.Instance);
        var instances = new InstanceRegistry(disabledConnection, identity, disabledOptions, NullLogger<InstanceRegistry>.Instance);
        using var meterFactory = new TestMeterFactory();
        var metrics = new KiriChessMetrics(meterFactory);
        var lagOptions = Options.Create(new LagCompensationOptions());
        var registry = new GameRegistry(engine, scopeFactory, metrics, lagOptions, distribution, new KillSwitchService(scopeFactory, NullLogger<KillSwitchService>.Instance), NullLogger<GameRegistry>.Instance);
        registry.Restore(session);
        var hub = new FakeHubContext();
        var connections = new GameConnectionTracker(
            hub, registry, new ThrowingActionExecutor(), disabledConnection, identity, instances, NullLogger<GameConnectionTracker>.Instance);

        var heartbeat = new GameOwnershipHeartbeat(
            registry, ownership, distribution, connections, connection, NullLogger<GameOwnershipHeartbeat>.Instance);

        await heartbeat.OnHeartbeatAsync(CancellationToken.None);

        Assert.NotNull(registry.Get(session.Id));
        Assert.Equal("instance-a", await ownership.GetOwnerAsync(session.Id));

        connection.Dispose();
    }
}
