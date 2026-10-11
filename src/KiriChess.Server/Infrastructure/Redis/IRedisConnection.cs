using StackExchange.Redis;

namespace KiriChess.Server.Infrastructure.Redis;

/// <summary>
/// The single abstraction every Phase 8.4 component takes a dependency on. Always registered, even
/// when Redis is disabled, callers check <see cref="Enabled"/> rather than checking whether the
/// service resolved, which keeps constructor injection uniform and makes the disabled path testable
/// without a DI dance.
/// </summary>
public interface IRedisConnection
{
    /// <summary>False when Redis:Configuration is unset. Every distributed path must check this first.</summary>
    bool Enabled { get; }

    /// <summary>The database. Throws <see cref="InvalidOperationException"/> when <see cref="Enabled"/> is false.</summary>
    IDatabase Db { get; }

    /// <summary>The pub/sub subscriber. Throws when <see cref="Enabled"/> is false.</summary>
    ISubscriber Subscriber { get; }

    /// <summary>Prefixes <paramref name="suffix"/> with the configured key prefix. Never build keys by hand.</summary>
    string Key(string suffix);

    /// <summary>A prefixed pub/sub channel. Same reasoning as <see cref="Key"/>.</summary>
    RedisChannel Channel(string suffix);

    /// <summary>True when the multiplexer currently has a live connection, for /health and the gauges.</summary>
    bool IsConnected { get; }
}
