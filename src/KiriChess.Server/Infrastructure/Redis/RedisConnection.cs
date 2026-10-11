using StackExchange.Redis;

namespace KiriChess.Server.Infrastructure.Redis;

/// <summary>
/// Default <see cref="IRedisConnection"/>. Owns the one <see cref="ConnectionMultiplexer"/> for the
/// process; every distributed component in Phase 8.4 goes through this rather than opening its own.
/// </summary>
public sealed class RedisConnection : IRedisConnection, IDisposable, IAsyncDisposable
{
    private readonly RedisOptions _options;
    private readonly InstanceIdentity _instance;

    // Lazy, not eager: a disabled or unreachable Redis must never block startup. The server boots
    // with Redis down and simply runs single-instance until Redis comes back, the multiplexer is
    // only ever touched by a caller that already checked Enabled and is about to use it for real.
    private readonly Lazy<ConnectionMultiplexer> _multiplexer;

    public RedisConnection(RedisOptions options, InstanceIdentity instance)
    {
        _options = options;
        _instance = instance;
        _multiplexer = new Lazy<ConnectionMultiplexer>(CreateMultiplexer, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public bool Enabled => _options.Enabled;

    public IDatabase Db => Enabled
        ? _multiplexer.Value.GetDatabase()
        : throw new InvalidOperationException("Redis is not configured; check IRedisConnection.Enabled before using Db.");

    public ISubscriber Subscriber => Enabled
        ? _multiplexer.Value.GetSubscriber()
        : throw new InvalidOperationException("Redis is not configured; check IRedisConnection.Enabled before using Subscriber.");

    public string Key(string suffix) => $"{_options.KeyPrefix}{suffix}";

    public RedisChannel Channel(string suffix) => RedisChannel.Literal(Key(suffix));

    // Must not force a connection just to answer a health probe: if the lazy value was never
    // created (Redis disabled, or nothing has used it yet), there is nothing to be connected to.
    public bool IsConnected => Enabled && _multiplexer.IsValueCreated && _multiplexer.Value.IsConnected;

    private ConnectionMultiplexer CreateMultiplexer()
    {
        var configurationOptions = ConfigurationOptions.Parse(_options.Configuration);
        // The single most important line in this file: the default (true) turns a transient Redis
        // blip at boot into a permanently dead multiplexer. With it false, StackExchange.Redis keeps
        // retrying in the background and reconnects on its own once Redis comes back.
        configurationOptions.AbortOnConnectFail = false;
        configurationOptions.ConnectTimeout = 5000;
        configurationOptions.SyncTimeout = 5000;
        configurationOptions.ClientName = $"kirichess-{_instance.Id}"; // readable in CLIENT LIST during an incident
        return ConnectionMultiplexer.Connect(configurationOptions);
    }

    public void Dispose()
    {
        // Touching _multiplexer.Value here would connect to Redis on shutdown, which is absurd,
        // only close it if it was ever actually created.
        if (_multiplexer.IsValueCreated)
            _multiplexer.Value.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_multiplexer.IsValueCreated)
            await _multiplexer.Value.DisposeAsync();
    }
}
