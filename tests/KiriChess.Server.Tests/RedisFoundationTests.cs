using KiriChess.Server.Infrastructure.Redis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KiriChess.Server.Tests;

/// <summary>
/// Covers the Phase 8.4 fail-closed contract from Phase 8.4: with
/// <c>Redis:Configuration</c> unset, nothing here ever opens a connection, and every method answers
/// harmlessly instead of throwing. These run everywhere, no Docker required,
/// <see cref="RedisFoundationContainerTests"/> below covers the real-Redis paths.
/// </summary>
public sealed class RedisFoundationTests
{
    private static RedisOptions EmptyOptions() => RedisOptions.From(new ConfigurationBuilder().Build());

    [Fact]
    public void Redis_is_disabled_when_configuration_is_empty()
    {
        var options = EmptyOptions();
        Assert.False(options.Enabled);

        using var connection = new RedisConnection(options, new InstanceIdentity("test-instance"));
        Assert.False(connection.Enabled);
        Assert.False(connection.IsConnected);
        Assert.Throws<InvalidOperationException>(() => connection.Db);
        Assert.Throws<InvalidOperationException>(() => connection.Subscriber);
    }

    [Fact]
    public void Key_prefixes_with_the_configured_prefix()
    {
        var defaultOptions = EmptyOptions();
        using var defaultConnection = new RedisConnection(defaultOptions, new InstanceIdentity("test-instance"));
        Assert.Equal("kirichess:game:owner:x", defaultConnection.Key("game:owner:x"));

        var customOptions = defaultOptions with { KeyPrefix = "custom:" };
        using var customConnection = new RedisConnection(customOptions, new InstanceIdentity("test-instance"));
        Assert.Equal("custom:game:owner:x", customConnection.Key("game:owner:x"));
    }

    [Fact]
    public void Instance_identity_prefers_the_fly_machine_id()
    {
        var original = Environment.GetEnvironmentVariable("FLY_MACHINE_ID");
        try
        {
            Environment.SetEnvironmentVariable("FLY_MACHINE_ID", "fly-abc123");
            var identity = new InstanceIdentity();
            Assert.Equal("fly-abc123", identity.Id);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FLY_MACHINE_ID", original);
        }
    }

    [Fact]
    public void Instance_identities_are_distinct_without_a_fly_machine_id()
    {
        var original = Environment.GetEnvironmentVariable("FLY_MACHINE_ID");
        try
        {
            Environment.SetEnvironmentVariable("FLY_MACHINE_ID", null);
            var a = new InstanceIdentity();
            var b = new InstanceIdentity();
            Assert.NotEqual(a.Id, b.Id);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FLY_MACHINE_ID", original);
        }
    }

    [Fact]
    public async Task Instance_registry_is_a_no_op_when_redis_is_disabled()
    {
        var options = EmptyOptions();
        var identity = new InstanceIdentity("test-instance");
        using var connection = new RedisConnection(options, identity);
        var registry = new InstanceRegistry(connection, identity, options, NullLogger<InstanceRegistry>.Instance);

        // None of these may throw, and none of them may report anything as live, the disabled path
        // is a hot path in single-instance mode and must be silent.
        await registry.RegisterAsync();
        await registry.HeartbeatAsync();
        await registry.DeregisterAsync();
        var live = await registry.LiveInstancesAsync();

        Assert.Empty(live);
        Assert.False(await registry.IsLiveAsync("test-instance"));
    }

    [Fact]
    public void Options_defaults_match_appsettings()
    {
        var path = AppSettingsPath();
        Assert.True(File.Exists(path), $"Expected appsettings.json at {path}");

        var fromFile = RedisOptions.From(new ConfigurationBuilder().AddJsonFile(path).Build());
        var fromDefaults = EmptyOptions();

        // If these ever drift, RedisOptions.From's hard-coded defaults no longer match what
        // appsettings.json documents as the shipped configuration.
        Assert.Equal(fromDefaults, fromFile);
    }

    // The compiled path of this very file, resolved at compile time via CallerFilePath, stable
    // across whatever directory the test runner's working directory happens to be.
    private static string AppSettingsPath([System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "src", "KiriChess.Server", "appsettings.json"));
}

/// <summary>
/// Container-backed half of the Phase 8.4 foundation tests: real Redis, real TTL expiry. Requires
/// Docker; skips cleanly (via <see cref="SkippableFactAttribute"/>) when no Docker daemon is
/// reachable. See <see cref="RedisFixture"/> for the shared container and why every test below uses
/// its own key prefix.
/// </summary>
public sealed class RedisFoundationContainerTests : IClassFixture<RedisFixture>
{
    private readonly RedisFixture _fixture;

    public RedisFoundationContainerTests(RedisFixture fixture) => _fixture = fixture;

    private (RedisConnection Connection, InstanceRegistry Registry) CreateRegistry(string instanceId, int ttlSeconds = 30)
    {
        var options = new RedisOptions(
            _fixture.Container!.GetConnectionString(),
            $"test-{Guid.NewGuid():N}:",
            ttlSeconds,
            HeartbeatSeconds: 10,
            CommandTimeoutSeconds: 5);
        var identity = new InstanceIdentity(instanceId);
        var connection = new RedisConnection(options, identity);
        var registry = new InstanceRegistry(connection, identity, options, NullLogger<InstanceRegistry>.Instance);
        return (connection, registry);
    }

    [SkippableFact]
    public async Task Heartbeat_makes_this_instance_live()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var (connection, registry) = CreateRegistry("instance-a");
        await registry.RegisterAsync();

        Assert.True(await registry.IsLiveAsync("instance-a"));

        connection.Dispose();
    }

    [SkippableFact]
    public async Task An_instance_whose_heartbeat_expired_is_not_live()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var (connection, registry) = CreateRegistry("instance-b", ttlSeconds: 1);
        await registry.RegisterAsync();
        Assert.True(await registry.IsLiveAsync("instance-b"));

        await Task.Delay(TimeSpan.FromSeconds(1.5));

        var live = await registry.LiveInstancesAsync();
        Assert.DoesNotContain("instance-b", live);

        // Pruned, not just excluded: the set itself must no longer carry the dead id.
        var stillMember = await connection.Db.SetContainsAsync(connection.Key("instances"), "instance-b");
        Assert.False(stillMember);

        connection.Dispose();
    }

    [SkippableFact]
    public async Task Live_instances_survive_a_dead_sibling()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var prefix = $"test-{Guid.NewGuid():N}:";
        var liveOptions = new RedisOptions(_fixture.Container!.GetConnectionString(), prefix, InstanceTtlSeconds: 30, HeartbeatSeconds: 10, CommandTimeoutSeconds: 5);
        var deadOptions = liveOptions with { InstanceTtlSeconds = 1 };

        var liveIdentity = new InstanceIdentity("instance-live");
        var deadIdentity = new InstanceIdentity("instance-dead");
        using var liveConnection = new RedisConnection(liveOptions, liveIdentity);
        using var deadConnection = new RedisConnection(deadOptions, deadIdentity);
        var liveRegistry = new InstanceRegistry(liveConnection, liveIdentity, liveOptions, NullLogger<InstanceRegistry>.Instance);
        var deadRegistry = new InstanceRegistry(deadConnection, deadIdentity, deadOptions, NullLogger<InstanceRegistry>.Instance);

        await liveRegistry.RegisterAsync();
        await deadRegistry.RegisterAsync();

        await Task.Delay(TimeSpan.FromSeconds(1.5));

        var live = await liveRegistry.LiveInstancesAsync();
        Assert.Contains("instance-live", live);
        Assert.DoesNotContain("instance-dead", live);
    }

    /// <summary>
    /// C-1-3: the container has to close the multiplexer when the host shuts down.
    /// <c>AddKiriChessRedis</c> used to hand the already-built <see cref="RedisConnection"/> over as
    /// a service descriptor's ImplementationInstance, and the DI container disposes only the
    /// services it created itself, so nothing ever called <c>Dispose</c> on it: the old process of a
    /// rolling deploy kept its Redis connections and subscriptions open until the platform killed
    /// it. Asserted through <see cref="RedisConnection.IsConnected"/> rather than by inspecting the
    /// descriptor, because what matters is the socket being shut, not the shape of the registration.
    /// </summary>
    [SkippableFact]
    public async Task Disposing_the_container_closes_the_redis_connection()
    {
        Skip.IfNot(DockerAvailability.IsAvailable, "Docker not available");

        var options = new RedisOptions(
            _fixture.Container!.GetConnectionString(),
            $"test-{Guid.NewGuid():N}:",
            InstanceTtlSeconds: 30,
            HeartbeatSeconds: 10,
            CommandTimeoutSeconds: 5);

        var services = new ServiceCollection();
        services.AddKiriChessRedis(options);
        var provider = services.BuildServiceProvider();

        var connection = provider.GetRequiredService<IRedisConnection>();
        // The multiplexer is lazy, so force it into existence and prove it is actually up before
        // asserting anything about it being taken down.
        await connection.Db.PingAsync();
        Assert.True(connection.IsConnected);

        await provider.DisposeAsync();

        Assert.False(connection.IsConnected);
    }
}
