using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.SignalR;
using StackExchange.Redis;

namespace KiriChess.Server.Infrastructure.Redis;

/// <summary>
/// Wires the Phase 8.4 Redis foundation into DI. Kept as extension methods so
/// <c>Program.cs</c> stays readable, see the README's fail-closed rule before changing anything
/// here: an unconfigured <see cref="RedisOptions.Configuration"/> must add nothing beyond the three
/// always-safe singletons below.
/// </summary>
public static class RedisRegistration
{
    /// <summary>
    /// Registers the Redis foundation. Always registers <see cref="IRedisConnection"/>,
    /// <see cref="InstanceIdentity"/> and <see cref="InstanceRegistry"/>, they answer "disabled"
    /// harmlessly, and only registers the heartbeat hosted service and the shared Data Protection
    /// key ring when Redis is actually configured.
    /// <para>
    /// Since 10a.1 the key ring here is an <em>upgrade</em> rather than the only copy: Program.cs
    /// persists keys to Postgres unconditionally, and this overrides that when Redis is configured.
    /// Both are durable; Redis is the one that is also shared, which is what a second instance needs.
    /// </para>
    /// </summary>
    public static IServiceCollection AddKiriChessRedis(this IServiceCollection services, RedisOptions options)
    {
        services.AddSingleton(options);

        // One identity object for the process, constructed here and registered as that same object.
        // Registering the type instead left two of them alive: the one the container built (which
        // every ownership key, reply channel and presence entry is written under) and a second one
        // made here for the multiplexer's ClientName. Off Fly the two disagree, because the fallback
        // id carries a random suffix, so CLIENT LIST during an incident named a machine that appears
        // in no key. InstanceIdentity holds nothing but a string and is not disposable, so there is
        // nothing for the container to own here.
        var instance = new InstanceIdentity();
        services.AddSingleton(instance);

        // Constructed here, directly, rather than resolved through a service provider: it is safe
        // because the multiplexer inside it is lazy (nothing connects until first use), and doing it
        // here lets the very same instance be handed to Data Protection below without a second
        // ConnectionMultiplexer ever existing for one process.
        var connection = new RedisConnection(options, instance);

        // C-1-3: handed over through a factory, not as a pre-built implementation instance. The
        // container disposes only what it created, and a descriptor carrying an ImplementationInstance
        // is explicitly not that, so `AddSingleton<IRedisConnection>(connection)` left the
        // ConnectionMultiplexer open across every host shutdown: the old process of a rolling deploy
        // held its Redis connections (and its subscriptions) until the platform killed it. A factory
        // result IS created by the container and is tracked for disposal, and returning the captured
        // object keeps the single-multiplexer guarantee the comment above depends on.
        services.AddSingleton<IRedisConnection>(_ => connection);
        services.AddSingleton<InstanceRegistry>();

        if (options.Enabled)
        {
            services.AddHostedService<InstanceHeartbeat>();

            services.AddDataProtection()
                // Without a shared, named application, each instance derives different purpose
                // strings and a Google sign-in that starts on one machine and returns to another
                // fails "Correlation failed", the exact blocker the 6b.3 comment in Program.cs
                // parks on this phase.
                .SetApplicationName("KiriChess")
                // The Func overload, so the key ring is resolved lazily off the same lazy
                // multiplexer rather than forcing a connection during service registration.
                .PersistKeysToStackExchangeRedis(() => connection.Db, $"{options.KeyPrefix}dataprotection-keys");
        }

        return services;
    }

    /// <summary>
    /// Attaches the SignalR Redis backplane when configured. Without it, <c>Clients.Group(gameId)</c>
    /// only reaches connections on this process, which is the single-instance ceiling Phase 8.4
    /// exists to lift, and the reason every other piece of this phase is pointless on its own.
    /// </summary>
    public static ISignalRServerBuilder AddKiriChessBackplane(this ISignalRServerBuilder builder, RedisOptions options)
    {
        if (!options.Enabled) return builder;

        return builder.AddStackExchangeRedis(options.Configuration, o =>
        {
            // Namespaced so a shared Redis (or a staging and a production app pointed at one
            // instance by accident) cannot cross-deliver hub messages between deployments.
            o.Configuration.ChannelPrefix = RedisChannel.Literal($"{options.KeyPrefix}signalr");
            o.Configuration.AbortOnConnectFail = false;
        });
    }
}
