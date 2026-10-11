using System.Diagnostics.Metrics;
using System.Reflection;
using KiriChess.Engine;
using KiriChess.Engine.Config;
using KiriChess.Server.Games;
using KiriChess.Server.Games.Distributed;
using KiriChess.Server.Infrastructure;
using KiriChess.Server.Infrastructure.Redis;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace KiriChess.Server.Tests;

/// <summary>
/// Hand-rolled fakes for the Phase 8.4 game-ownership tests (<c>GameOwnershipTests</c>,
/// <c>GameCommandRoutingTests</c>). The test project has no mocking library, so
/// <see cref="IHubContext{GameHub}"/>, which <see cref="GameActionExecutor"/> and
/// <see cref="GameConnectionTracker"/> both take a dependency on to broadcast, needs a real, if
/// minimal, implementation rather than a mock.
/// </summary>
public sealed class FakeClientProxy : IClientProxy
{
    public List<(string Method, object?[] Args)> Sent { get; } = new();

    public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
    {
        Sent.Add((method, args));
        return Task.CompletedTask;
    }
}

public sealed class FakeSingleClientProxy : ISingleClientProxy
{
    public List<(string Method, object?[] Args)> Sent { get; } = new();

    public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
    {
        Sent.Add((method, args));
        return Task.CompletedTask;
    }

    public Task<T> InvokeCoreAsync<T>(string method, object?[] args, CancellationToken cancellationToken = default)
        => Task.FromResult(default(T)!);
}

/// <summary>
/// Every <see cref="Group"/>/<see cref="GroupExcept"/> call for the same group name returns the same
/// <see cref="FakeClientProxy"/> instance, so a test can assert on what a specific game's group
/// received. Everything else (<see cref="All"/>, <see cref="User"/>, ...) is not exercised by
/// <see cref="GameActionExecutor"/> or <see cref="GameConnectionTracker"/> and shares one catch-all proxy.
/// </summary>
public sealed class FakeHubClients : IHubClients
{
    private readonly FakeClientProxy _catchAll = new();
    public Dictionary<string, FakeClientProxy> GroupProxies { get; } = new();
    public Dictionary<string, FakeClientProxy> GroupExceptProxies { get; } = new();

    public IClientProxy All => _catchAll;
    public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => _catchAll;
    public ISingleClientProxy Client(string connectionId) => new FakeSingleClientProxy();
    IClientProxy IHubClients<IClientProxy>.Client(string connectionId) => Client(connectionId);
    public IClientProxy Clients(IReadOnlyList<string> connectionIds) => _catchAll;
    public IClientProxy Group(string groupName) => GetOrAdd(GroupProxies, groupName);
    public IClientProxy Groups(IReadOnlyList<string> groupNames) => _catchAll;
    public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => GetOrAdd(GroupExceptProxies, groupName);
    public IClientProxy User(string userId) => _catchAll;
    public IClientProxy Users(IReadOnlyList<string> userIds) => _catchAll;

    private static FakeClientProxy GetOrAdd(Dictionary<string, FakeClientProxy> dict, string key)
    {
        if (!dict.TryGetValue(key, out var proxy)) dict[key] = proxy = new FakeClientProxy();
        return proxy;
    }
}

public sealed class FakeHubContext : IHubContext<GameHub>
{
    public FakeHubClients Clients { get; } = new();
    IHubClients IHubContext<GameHub>.Clients => Clients;
    public IGroupManager Groups => throw new NotSupportedException("Not exercised by GameActionExecutor/GameConnectionTracker, they only ever use Clients.");
}

/// <summary>A router that fails the test the moment anything calls it, see <c>GameOwnershipTests.Local_path_is_used_when_the_session_is_in_the_registry</c>.</summary>
public sealed class ThrowingCommandRouter : IGameCommandRouter
{
    public Task<GameCommandReply> SendAsync(string ownerInstanceId, GameCommandEnvelope cmd, CancellationToken ct = default) =>
        throw new InvalidOperationException("The local path should never reach the command router.");
}

/// <summary>A router whose owner never replies, for the "no retry on timeout" test.</summary>
public sealed class NeverRepliesCommandRouter : IGameCommandRouter
{
    public int CallCount { get; private set; }

    public async Task<GameCommandReply> SendAsync(string ownerInstanceId, GameCommandEnvelope cmd, CancellationToken ct = default)
    {
        CallCount++;
        await Task.Delay(Timeout.InfiniteTimeSpan, ct); // never completes on its own
        throw new InvalidOperationException("unreachable");
    }
}

/// <summary>A router that hands back a prepared reply, recording every envelope it was asked to send.</summary>
public sealed class RecordingCommandRouter : IGameCommandRouter
{
    public List<GameCommandEnvelope> Sent { get; } = new();
    public Func<GameCommandEnvelope, GameCommandReply>? ReplyFactory { get; set; }

    public Task<GameCommandReply> SendAsync(string ownerInstanceId, GameCommandEnvelope cmd, CancellationToken ct = default)
    {
        Sent.Add(cmd);
        var reply = ReplyFactory?.Invoke(cmd) ?? new GameCommandReply(cmd.CorrelationId, true, null, null);
        return Task.FromResult(reply);
    }
}

/// <summary>An <see cref="IGameActionExecutor"/> that fails the test the moment anything calls it, for tests that only need to prove an armed timer was cancelled before ever reaching the forfeit action.</summary>
public sealed class ThrowingActionExecutor : IGameActionExecutor
{
    private static Exception NotExpected() => new InvalidOperationException("GameActionExecutor should not have been called in this test.");

    public Task<GameSnapshotDto> JoinGameAsync(Guid gameId, Guid userId, string connectionId, CancellationToken ct = default) => throw NotExpected();
    public Task<MovePlayedDto> SubmitMoveAsync(Guid gameId, Guid userId, string connectionId, string uci, CancellationToken ct = default) => throw NotExpected();
    public Task<GameEndedDto> ResignAsync(Guid gameId, Guid userId, string connectionId, CancellationToken ct = default) => throw NotExpected();
    public Task OfferDrawAsync(Guid gameId, Guid userId, string connectionId, CancellationToken ct = default) => throw NotExpected();
    public Task RespondDrawAsync(Guid gameId, Guid userId, string connectionId, bool accept, CancellationToken ct = default) => throw NotExpected();
    public Task AbandonAsync(Guid gameId, Guid userId, CancellationToken ct = default) => throw NotExpected();
    public Task<SpectatorSnapshotDto> JoinAsSpectatorAsync(Guid gameId, string connectionId, CancellationToken ct = default) => throw NotExpected();
    public Task<GameCommandReply> ExecuteAsync(GameCommandEnvelope envelope, CancellationToken ct = default) => throw NotExpected();
    public Task<bool> TryTakeoverAsync(Guid gameId, string? fromDeadOwner = null, CancellationToken ct = default) => throw NotExpected();
}

/// <summary>
/// Counts the calls <see cref="GameConnectionTracker"/>'s grace timer makes rather than throwing on
/// them, for the tests that need to know how many timers actually reached the end of their window
/// (C-2-5). <see cref="ThrowingActionExecutor"/> cannot answer that: an exception thrown inside a
/// fire-and-forget grace task is never observed by the test that armed it.
/// </summary>
public sealed class RecordingActionExecutor : IGameActionExecutor
{
    private int _abandonCount;

    /// <summary>How many times a grace timer ran to the end of its window and asked for a forfeit.</summary>
    public int AbandonCount => Volatile.Read(ref _abandonCount);

    private static Exception NotExpected() => new InvalidOperationException("GameActionExecutor should not have been called in this test.");

    public Task<GameSnapshotDto> JoinGameAsync(Guid gameId, Guid userId, string connectionId, CancellationToken ct = default) => throw NotExpected();
    public Task<MovePlayedDto> SubmitMoveAsync(Guid gameId, Guid userId, string connectionId, string uci, CancellationToken ct = default) => throw NotExpected();
    public Task<GameEndedDto> ResignAsync(Guid gameId, Guid userId, string connectionId, CancellationToken ct = default) => throw NotExpected();
    public Task OfferDrawAsync(Guid gameId, Guid userId, string connectionId, CancellationToken ct = default) => throw NotExpected();
    public Task RespondDrawAsync(Guid gameId, Guid userId, string connectionId, bool accept, CancellationToken ct = default) => throw NotExpected();
    public Task<SpectatorSnapshotDto> JoinAsSpectatorAsync(Guid gameId, string connectionId, CancellationToken ct = default) => throw NotExpected();
    public Task<GameCommandReply> ExecuteAsync(GameCommandEnvelope envelope, CancellationToken ct = default) => throw NotExpected();
    public Task<bool> TryTakeoverAsync(Guid gameId, string? fromDeadOwner = null, CancellationToken ct = default) => throw NotExpected();

    public Task AbandonAsync(Guid gameId, Guid userId, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _abandonCount);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Records whether this instance still looked alive to the rest of the cluster at the moment its
/// shutdown hooks ran. That is the whole ordering guarantee C-5-3 turns on: leases must be handed
/// back <i>before</i> <see cref="InstanceRegistry.DeregisterAsync"/> publishes "this instance is
/// dead", never after.
/// </summary>
public sealed class LivenessProbingShutdownHook : IInstanceShutdownHook
{
    private readonly InstanceRegistry _instances;
    private readonly string _instanceId;

    public LivenessProbingShutdownHook(InstanceRegistry instances, string instanceId)
    {
        _instances = instances;
        _instanceId = instanceId;
    }

    public bool Ran { get; private set; }
    public bool InstanceWasStillLive { get; private set; }

    public async Task OnInstanceStoppingAsync(CancellationToken ct)
    {
        Ran = true;
        InstanceWasStillLive = await _instances.IsLiveAsync(_instanceId, ct);
    }
}

/// <summary>Fails loudly rather than silently touching a real database, most tests never need a scope at all.</summary>
public sealed class ThrowingScopeFactory : IServiceScopeFactory
{
    public IServiceScope CreateScope() => throw new NotSupportedException("This test never expected a DB scope to be created.");
}

/// <summary>Minimal <see cref="IMeterFactory"/> so <see cref="KiriChess.Server.Infrastructure.KiriChessMetrics"/> can be constructed without a DI container.</summary>
public sealed class TestMeterFactory : IMeterFactory
{
    private readonly List<Meter> _meters = new();
    public Meter Create(MeterOptions options)
    {
        var meter = new Meter(options);
        _meters.Add(meter);
        return meter;
    }
    public void Dispose()
    {
        foreach (var meter in _meters) meter.Dispose();
    }
}

/// <summary>
/// A fake <see cref="IRedisConnection"/> for tests that need <see cref="IRedisConnection.Enabled"/> to
/// be true (so ownership/forwarding code takes its Redis branch) without a real multiplexer. Only
/// <see cref="Subscriber"/> is ever populated, the tests that use this never touch <see cref="Db"/>.
/// </summary>
public sealed class FakeRedisConnection : IRedisConnection
{
    public bool Enabled { get; init; } = true;
    public IDatabase Db => throw new NotSupportedException("This fake only supports Subscriber; no test using it should touch Db.");
    public required ISubscriber Subscriber { get; init; }
    public string Key(string suffix) => $"test:{suffix}";
    public RedisChannel Channel(string suffix) => RedisChannel.Literal(Key(suffix));
    public bool IsConnected => Enabled;
}

/// <summary>
/// A dynamic proxy standing in for <see cref="IDatabase"/> in the pure (no-Docker)
/// <see cref="GameOwnershipHeartbeat"/> tests that need to control exactly what
/// <see cref="GameOwnership.TryClaimAsync"/> and <see cref="GameOwnership.RenewAsync"/> see.
/// <see cref="IDatabase"/> is StackExchange's interface, not ours, with well over six hundred members,
/// hand-stubbing all of them the way <see cref="FakeSubscriber"/> does for the much smaller
/// <see cref="ISubscriber"/> is not practical, and the test project has no mocking library (see this
/// file's header). <see cref="DispatchProxy"/> is the BCL's own answer to that gap: it answers exactly
/// the two calls <see cref="GameOwnership"/> makes: <c>StringSetAsync</c> (the SET NX behind
/// <see cref="GameOwnership.TryClaimAsync"/>) and <c>ScriptEvaluateAsync</c> (the compare-and-renew
/// script behind <see cref="GameOwnership.RenewAsync"/>), with a caller-supplied result, and throws on
/// anything else, so a test that reaches an unexpected call fails loudly instead of silently returning a
/// default.
/// </summary>
public class FakeOwnershipDatabase : DispatchProxy // DispatchProxy generates a subclass at runtime, so this cannot be sealed
{
    public bool ClaimSucceeds { get; set; }
    public bool RenewSucceeds { get; set; }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
    {
        nameof(IDatabase.StringSetAsync) => Task.FromResult(ClaimSucceeds),
        nameof(IDatabase.ScriptEvaluateAsync) => Task.FromResult(RedisResult.Create(RenewSucceeds ? 1L : 0L)),
        _ => throw new NotSupportedException(
            $"FakeOwnershipDatabase does not support {targetMethod?.Name}; GameOwnership should not be calling it here."),
    };

    public static IDatabase Create(bool claimSucceeds, bool renewSucceeds)
    {
        var proxy = DispatchProxy.Create<IDatabase, FakeOwnershipDatabase>();
        var fake = (FakeOwnershipDatabase)(object)proxy;
        fake.ClaimSucceeds = claimSucceeds;
        fake.RenewSucceeds = renewSucceeds;
        return proxy;
    }
}

/// <summary>
/// An <see cref="IRedisConnection"/> whose <see cref="Db"/> is a caller-supplied
/// <see cref="FakeOwnershipDatabase"/>, always <see cref="Enabled"/> so <see cref="GameOwnership"/> takes
/// its Redis branch. <see cref="Subscriber"/> is never touched by <c>TryClaimAsync</c>/<c>RenewAsync</c>,
/// so it stays a throwing stub like <see cref="FakeRedisConnection"/>'s <see cref="Db"/>.
/// </summary>
public sealed class FakeOwnershipRedisConnection : IRedisConnection
{
    public bool Enabled => true;
    public required IDatabase Db { get; init; }
    public ISubscriber Subscriber => throw new NotSupportedException("Not touched by GameOwnership.TryClaimAsync/RenewAsync.");
    public string Key(string suffix) => $"test:{suffix}";
    public RedisChannel Channel(string suffix) => RedisChannel.Literal(Key(suffix));
    public bool IsConnected => true;
}

/// <summary>
/// A fake <see cref="ISubscriber"/> that only implements <see cref="PublishAsync"/> (recording every
/// publish), enough to drive <c>GameCommandRouter.SendAsync</c>'s real timeout logic
/// (<c>GameCommandRoutingTests.A_timed_out_command_does_not_retry</c>) without a real Redis server:
/// nothing ever calls back with a reply, so the router's own timeout fires exactly as it would if the
/// owning instance genuinely never answered.
/// </summary>
public sealed class FakeSubscriber : ISubscriber
{
    public List<(RedisChannel Channel, RedisValue Message)> Published { get; } = new();

    public Task<long> PublishAsync(RedisChannel channel, RedisValue message, CommandFlags flags = CommandFlags.None)
    {
        Published.Add((channel, message));
        return Task.FromResult(1L);
    }

    public long Publish(RedisChannel channel, RedisValue message, CommandFlags flags = CommandFlags.None) => throw new NotSupportedException();
    public System.Net.EndPoint? IdentifyEndpoint(RedisChannel channel, CommandFlags flags = CommandFlags.None) => throw new NotSupportedException();
    public Task<System.Net.EndPoint?> IdentifyEndpointAsync(RedisChannel channel, CommandFlags flags = CommandFlags.None) => throw new NotSupportedException();
    public bool IsConnected(RedisChannel channel = default) => throw new NotSupportedException();
    public void Subscribe(RedisChannel channel, Action<RedisChannel, RedisValue> handler, CommandFlags flags = CommandFlags.None) => throw new NotSupportedException();
    public Task SubscribeAsync(RedisChannel channel, Action<RedisChannel, RedisValue> handler, CommandFlags flags = CommandFlags.None) => throw new NotSupportedException();
    public ChannelMessageQueue Subscribe(RedisChannel channel, CommandFlags flags = CommandFlags.None) => throw new NotSupportedException();
    public Task<ChannelMessageQueue> SubscribeAsync(RedisChannel channel, CommandFlags flags = CommandFlags.None) => throw new NotSupportedException();
    public System.Net.EndPoint SubscribedEndpoint(RedisChannel channel) => throw new NotSupportedException();
    public void Unsubscribe(RedisChannel channel, Action<RedisChannel, RedisValue>? handler = null, CommandFlags flags = CommandFlags.None) => throw new NotSupportedException();
    public Task UnsubscribeAsync(RedisChannel channel, Action<RedisChannel, RedisValue>? handler = null, CommandFlags flags = CommandFlags.None) => throw new NotSupportedException();
    public void UnsubscribeAll(CommandFlags flags = CommandFlags.None) => throw new NotSupportedException();
    public Task UnsubscribeAllAsync(CommandFlags flags = CommandFlags.None) => throw new NotSupportedException();

    // IRedis / IRedisAsync base members, not exercised by GameCommandRouter.SendAsync.
    public TimeSpan Ping(CommandFlags flags = CommandFlags.None) => throw new NotSupportedException();
    public Task<TimeSpan> PingAsync(CommandFlags flags = CommandFlags.None) => throw new NotSupportedException();
    public bool TryWait(Task task) => throw new NotSupportedException();
    public void Wait(Task task) => throw new NotSupportedException();
    public T Wait<T>(Task<T> task) => throw new NotSupportedException();
    public void WaitAll(params Task[] tasks) => throw new NotSupportedException();
    public IConnectionMultiplexer Multiplexer => throw new NotSupportedException();
}

/// <summary>
/// Assembles a real <see cref="GameActionExecutor"/> with Redis disabled (single-instance path) and a
/// caller-supplied <see cref="IGameCommandRouter"/>, everything a pure, no-Docker test needs to drive
/// the local-execution and forwarding-translation logic without a real cluster. Every collaborator
/// except the router is the genuine production type: only the router is a seam (see
/// <see cref="IGameCommandRouter"/>'s doc comment for why), so a bug in any other type here would be a
/// bug in the executor's own dependencies, not in the harness.
/// </summary>
public static class GameActionExecutorHarness
{
    public sealed record Built(
        GameActionExecutor Executor, GameRegistry Registry, FakeHubContext Hub, InstanceIdentity Instance);

    public static Built Build(IGameCommandRouter router, string instanceId = "test-instance")
    {
        var options = RedisOptions.From(new ConfigurationBuilder().Build()); // Configuration unset: Enabled == false
        var identity = new InstanceIdentity(instanceId);
        var connection = new RedisConnection(options, identity);
        var ownership = new GameOwnership(connection, identity, options, NullLogger<GameOwnership>.Instance);
        var instances = new InstanceRegistry(connection, identity, options, NullLogger<InstanceRegistry>.Instance);
        var liveIndex = new LiveGameIndex(connection, identity, NullLogger<LiveGameIndex>.Instance);
        var scopeFactory = new ThrowingScopeFactory();
        var distribution = new GameDistribution(connection, ownership, liveIndex, scopeFactory, NullLogger<GameDistribution>.Instance);
        var meterFactory = new TestMeterFactory();
        var metrics = new KiriChessMetrics(meterFactory);
        var engine = new KiriChessEngine();
        var lagOptions = Options.Create(new LagCompensationOptions());
        var registry = new GameRegistry(engine, scopeFactory, metrics, lagOptions, distribution, new KillSwitchService(scopeFactory, NullLogger<KillSwitchService>.Instance), NullLogger<GameRegistry>.Instance);
        var snapshotOptions = Options.Create(new SnapshotPersistenceOptions());
        var persister = new LiveGamePersister(
            registry, scopeFactory, NullLogger<LiveGamePersister>.Instance, metrics, snapshotOptions);
        var hub = new FakeHubContext();

        var executor = new GameActionExecutor(
            registry, ownership, instances, identity, connection, router, distribution,
            hub, scopeFactory, engine, lagOptions, persister, metrics, NullLogger<GameActionExecutor>.Instance);

        return new Built(executor, registry, hub, identity);
    }
}

/// <summary>
/// Assembles a real <see cref="GameOwnershipHeartbeat"/> for Task B2 Step 1's pure tests, whose
/// <see cref="GameOwnership"/> is backed by <see cref="FakeOwnershipDatabase"/> so a test can dictate
/// exactly what <c>RenewAsync</c>/<c>TryClaimAsync</c> answer. Everything downstream of a successful
/// renew or reclaim (the live-state publish, the connections-TTL refresh) takes the genuine
/// disabled-Redis no-op path, Step 1's fix lives entirely in the renew/claim branch itself, so nothing
/// else here needs to be real.
/// </summary>
public static class GameOwnershipHeartbeatHarness
{
    public sealed record Built(GameOwnershipHeartbeat Heartbeat, GameRegistry Registry, GameSession Session);

    public static Built Build(bool claimSucceeds, bool renewSucceeds)
    {
        var identity = new InstanceIdentity("test-instance");

        var ownershipOptions = new RedisOptions("fake:6379", "test:", InstanceTtlSeconds: 30, HeartbeatSeconds: 10, CommandTimeoutSeconds: 5);
        var ownershipConnection = new FakeOwnershipRedisConnection { Db = FakeOwnershipDatabase.Create(claimSucceeds, renewSucceeds) };
        var ownership = new GameOwnership(ownershipConnection, identity, ownershipOptions, NullLogger<GameOwnership>.Instance);

        var disabledOptions = RedisOptions.From(new ConfigurationBuilder().Build()); // Configuration unset: Enabled == false
        var disabledConnection = new RedisConnection(disabledOptions, identity);
        var liveIndex = new LiveGameIndex(disabledConnection, identity, NullLogger<LiveGameIndex>.Instance);
        var scopeFactory = new ThrowingScopeFactory();
        var distribution = new GameDistribution(disabledConnection, ownership, liveIndex, scopeFactory, NullLogger<GameDistribution>.Instance);
        var instances = new InstanceRegistry(disabledConnection, identity, disabledOptions, NullLogger<InstanceRegistry>.Instance);

        var meterFactory = new TestMeterFactory();
        var metrics = new KiriChessMetrics(meterFactory);
        var engine = new KiriChessEngine();
        var lagOptions = Options.Create(new LagCompensationOptions());
        var registry = new GameRegistry(engine, scopeFactory, metrics, lagOptions, distribution, new KillSwitchService(scopeFactory, NullLogger<KillSwitchService>.Instance), NullLogger<GameRegistry>.Instance);

        var hub = new FakeHubContext();
        var connections = new GameConnectionTracker(
            hub, registry, new ThrowingActionExecutor(), disabledConnection, identity, instances, NullLogger<GameConnectionTracker>.Instance);

        var config = new KiriChessConfig { MoveCostMode = MoveCostMode.None, CheckCheckerGain = 0, CheckVictimLoss = 0 };
        var session = new GameSession(engine, config, Guid.NewGuid(), Guid.NewGuid(), "bounty1", isRated: true);
        registry.Restore(session); // present locally, as boot recovery or an earlier takeover would leave it before this heartbeat tick runs

        // ownershipConnection.Enabled is always true, so OnHeartbeatAsync's early-return guard is
        // bypassed and the renew/claim branch under test actually runs.
        var heartbeat = new GameOwnershipHeartbeat(
            registry, ownership, distribution, connections, ownershipConnection, NullLogger<GameOwnershipHeartbeat>.Instance);

        return new Built(heartbeat, registry, session);
    }
}
