using System.Collections.Concurrent;
using System.Text.Json;
using KiriChess.Server.Infrastructure.Redis;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace KiriChess.Server.Games.Distributed;

/// <summary>
/// The one method of <see cref="GameCommandRouter"/> that <see cref="GameActionExecutor"/> calls.
/// Pulled out purely as a unit-test seam (<c>GameOwnershipTests</c> substitutes a fake that fails a
/// test if it is ever invoked, to prove the local path never forwards), every real caller still
/// resolves the concrete <see cref="GameCommandRouter"/> singleton; there is no second implementation
/// in production.
/// </summary>
public interface IGameCommandRouter
{
    Task<GameCommandReply> SendAsync(string ownerInstanceId, GameCommandEnvelope cmd, CancellationToken ct = default);
}

/// <summary>
/// The forwarding RPC transport (Phase 8.4): publishes a <see cref="GameCommandEnvelope"/> to whichever
/// instance owns a game and awaits its <see cref="GameCommandReply"/>, and, the mirror direction, is
/// the one subscriber that turns an inbound envelope into a call on <see cref="GameActionExecutor"/>
/// when this instance is the owner. Registered both as a singleton and as the hosted service that runs
/// its subscriptions (one instance, two roles, cf. <c>PresenceBroadcaster</c>), and registered
/// unconditionally: <see cref="ExecuteAsync"/> checks <see cref="IRedisConnection.Enabled"/> first and
/// returns immediately when Redis is disabled, so a disabled deploy never subscribes and never touches
/// the multiplexer through this type.
///
/// Takes <see cref="IServiceProvider"/> rather than <see cref="GameActionExecutor"/> or
/// <see cref="GameConnectionTracker"/> directly: both of those depend back on this router (the executor
/// to forward, the tracker to react to a reconnect notice going out), so a constructor-injected direct
/// reference in either direction would be a circular dependency the DI container cannot construct.
/// Resolving them lazily, on first use, breaks the cycle without changing the object graph's shape,
/// all three are singletons, so "resolve once, on demand" behaves identically to "resolve at startup".
/// </summary>
public sealed class GameCommandRouter : BackgroundService, IGameCommandRouter
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly IRedisConnection _connection;
    private readonly InstanceIdentity _instance;
    private readonly RedisOptions _options;
    private readonly IServiceProvider _services;
    private readonly ILogger<GameCommandRouter> _logger;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<GameCommandReply>> _pending = new();

    private readonly Lazy<IGameActionExecutor> _actions;
    private readonly Lazy<GameConnectionTracker> _connections;

    public GameCommandRouter(
        IRedisConnection connection, InstanceIdentity instance, RedisOptions options,
        IServiceProvider services, ILogger<GameCommandRouter> logger)
    {
        _connection = connection;
        _instance = instance;
        _options = options;
        _services = services;
        _logger = logger;

        _actions = new Lazy<IGameActionExecutor>(() => _services.GetRequiredService<IGameActionExecutor>());
        _connections = new Lazy<GameConnectionTracker>(() => _services.GetRequiredService<GameConnectionTracker>());
    }

    /// <summary>
    /// Sends a command to the owning instance and awaits its reply, or throws
    /// <c>HubException("The game server did not respond. Try again.")</c> after
    /// <see cref="RedisOptions.CommandTimeoutSeconds"/>. <b>Never retries</b>, a retried
    /// <c>SubmitMove</c> where both attempts land is a double move, which is strictly worse than
    /// surfacing "try again" to the player and letting them decide.
    /// </summary>
    public async Task<GameCommandReply> SendAsync(string ownerInstanceId, GameCommandEnvelope cmd, CancellationToken ct = default)
    {
        // RunContinuationsAsynchronously: without it, a continuation chained onto tcs.Task would run
        // synchronously on the thread that completes it, the Redis subscriber's dispatch thread, for
        // a reply that arrives normally, and a continuation that itself awaits Redis again could
        // deadlock the subscription.
        var tcs = new TaskCompletionSource<GameCommandReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(cmd.CorrelationId, tcs))
            throw new InvalidOperationException($"Duplicate command correlation id '{cmd.CorrelationId}'.");

        try
        {
            var json = JsonSerializer.Serialize(cmd, JsonOptions);
            await _connection.Subscriber.PublishAsync(_connection.Channel($"game:cmd:{ownerInstanceId}"), json).WaitAsync(ct);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(_options.CommandTimeoutSeconds));
            try
            {
                return await tcs.Task.WaitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new HubException("The game server did not respond. Try again.");
            }
        }
        finally
        {
            // Always removed, success or timeout, otherwise a timed-out entry leaks forever, since
            // nothing else will ever complete or remove it.
            _pending.TryRemove(cmd.CorrelationId, out _);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_connection.Enabled) return; // never subscribes, never touches the multiplexer, when Redis is off

        var subscriber = _connection.Subscriber;
        await subscriber.SubscribeAsync(
            _connection.Channel($"game:cmd:{_instance.Id}"),
            (channel, message) => _ = HandleCommandAsync(message, stoppingToken));
        await subscriber.SubscribeAsync(
            _connection.Channel($"game:reply:{_instance.Id}"),
            (channel, message) => HandleReply(message));
        await subscriber.SubscribeAsync(
            _connection.Channel("game:reconnect"),
            (channel, message) => _ = HandleReconnectAsync(message, stoppingToken));

        // The subscriptions above are callback-driven; this hosted service just needs to stay alive to
        // keep them registered until shutdown.
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    /// <summary>A subscriber callback must never throw, an exception here would tear down the subscription for every future message, not just this one.</summary>
    private async Task HandleCommandAsync(RedisValue message, CancellationToken ct)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<GameCommandEnvelope>((string)message!, JsonOptions);
            if (envelope is null) return;

            var reply = await _actions.Value.ExecuteAsync(envelope, ct);
            var json = JsonSerializer.Serialize(reply, JsonOptions);
            await _connection.Subscriber.PublishAsync(_connection.Channel($"game:reply:{envelope.FromInstanceId}"), json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to handle an inbound forwarded game command.");
        }
    }

    private void HandleReply(RedisValue message)
    {
        try
        {
            var reply = JsonSerializer.Deserialize<GameCommandReply>((string)message!, JsonOptions);
            if (reply is null) return;
            if (_pending.TryGetValue(reply.CorrelationId, out var tcs))
                tcs.TrySetResult(reply);
            // No pending entry: the wait already timed out and gave up, or this reply is a duplicate.
            // Either way there is nobody left to deliver it to.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to handle an inbound game command reply.");
        }
    }

    private async Task HandleReconnectAsync(RedisValue message, CancellationToken ct)
    {
        try
        {
            var notice = JsonSerializer.Deserialize<GameReconnectNotice>((string)message!, JsonOptions);
            if (notice is null) return;
            await _connections.Value.HandleReconnectAsync(notice.GameId, notice.UserId, notice.ConnectionId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to handle an inbound reconnect notice.");
        }
    }
}
