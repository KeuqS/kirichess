using System.Collections.Concurrent;
using System.Security.Claims;
using System.Threading.RateLimiting;
using KiriChess.Server.Auth;
using Microsoft.AspNetCore.SignalR;

namespace KiriChess.Server.Infrastructure;

/// <summary>
/// Rate-limits SignalR hub method invocations (Phase 6e.1).
///
/// <b>Why this file has to exist:</b> ASP.NET's rate limiter (<see cref="RateLimitingSetup"/>) is
/// HTTP middleware. Over a WebSocket it sees the <c>/hubs/game/negotiate</c> request and the
/// upgrade, and then nothing, ever again. Every <c>SubmitMove</c>, <c>JoinQueue</c>,
/// <c>OfferDraw</c> for the life of that connection is completely invisible to it. Without this
/// filter, the primary real-time surface of the game server would be entirely unlimited, so this
/// re-implements the same idea, a token bucket per caller, as an <see cref="IHubFilter"/> instead.
///
/// One token bucket per method class, per caller: <c>SubmitMove</c> gets the loosest bucket (see
/// <see cref="RateLimitingSetup"/>'s header comment, a rejected move can cost a game), queue churn
/// (<c>JoinQueue</c>/<c>LeaveQueue</c>) gets the tightest, everything else in between. Buckets are
/// created lazily and released when the last connection using them goes away; without that cleanup
/// the dictionary would grow for the lifetime of the process, a slow leak a long-running game server
/// would actually hit.
///
/// <b>What "per caller" means, and why it is not always the connection (S-4-2).</b> For a human the
/// caller is the connection: a browser tab is a person, and a second tab is the same person doing
/// twice as little in each. For an API token it is the <em>account</em>. A bot may hold as many
/// sockets as <see cref="HubConnectionBudget"/> allows (20 per IP), and with a bucket per connection
/// its real throughput was that number times every threshold below - the socket-multiplication half
/// of S-4-2, whose HTTP half is <see cref="RateLimitPolicies.ApiAccount"/>. Note that no threshold in
/// this file changed: the fix is what a bucket is keyed by, not how big it is, so a bot on one socket
/// (which is every honest bot) sees exactly what it saw before.
///
/// <b>What that does not fix.</b> The subject key is still per instance, so a bot spreading sockets
/// across instances gets one bucket on each. That is the same limitation the per-connection key
/// always had, and it is bounded by <see cref="HubConnectionBudget"/> plus the per-account HTTP
/// ceiling; closing it properly means Redis-backed hub buckets, which Phase 8.4 (Task E) considered
/// and declined.
///
/// Phase 8.4 (Task E, distributed rate limiting) examined this file and deliberately left it
/// unchanged, because a connection id already partitions correctly across any number of instances,
/// there is nothing here for Redis to fix. That reasoning still holds for the connection-keyed half.
/// </summary>
public sealed class HubRateLimitFilter : IHubFilter, IDisposable
{
    private const string SubmitMoveMethod = "SubmitMove";
    private const string JoinQueueMethod = "JoinQueue";
    private const string LeaveQueueMethod = "LeaveQueue";

    private readonly RateLimitOptions _options;
    private readonly KiriChessMetrics _metrics;

    /// <summary>
    /// Guards <see cref="_buckets"/> and <see cref="_refs"/> together, and the acquire happens inside
    /// it too. That last part is the point: once a bucket can outlive a single connection it can also
    /// be disposed by a concurrent disconnect, and a lock-free read would race that disposal into an
    /// <see cref="ObjectDisposedException"/> on the hottest path in the process. Holding a lock across
    /// <c>AttemptAcquire</c> is cheap enough to be the right answer here - the bucket is a
    /// non-blocking in-memory token count with <c>QueueLimit = 0</c>, so nothing waits inside it, and
    /// <see cref="HubConnectionBudget"/> already takes a process-wide lock per connect.
    /// </summary>
    private readonly object _gate = new();
    private readonly Dictionary<string, ConnectionBuckets> _buckets = new(StringComparer.Ordinal);

    /// <summary>How many live connections share each bucket key. Always 1 for a connection-keyed entry.</summary>
    private readonly Dictionary<string, int> _refs = new(StringComparer.Ordinal);

    /// <summary>
    /// The key a connection was admitted under, so disconnect releases the same bucket connect
    /// reserved even if the principal somehow read differently later.
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _keyByConnection = new(StringComparer.Ordinal);

    public HubRateLimitFilter(IConfiguration configuration, KiriChessMetrics metrics)
    {
        // SignalR resolves hub filters lazily, on the first hub invocation that needs one, which is
        // always well after the host finishes Build(). Reading configuration here is therefore safe
        // by the same reasoning as RateLimitOptions.From's other callers: it sees the fully composed
        // configuration, not a builder-time snapshot. `AddSignalR(...).AddFilter<HubRateLimitFilter>()`
        // itself has to run at `builder.Services` time (before Build()), so unlike the HTTP
        // middleware toggle in Program.cs, there is no later point at which Program.cs could
        // conditionally omit this filter from the chain, instead it is always registered, and
        // `Enabled` is checked per invocation below. When false that is functionally identical to
        // not being in the pipeline: every call is simply forwarded to `next` with no bucket check.
        _options = RateLimitOptions.From(configuration);
        _metrics = metrics;
    }

    public async ValueTask<object?> InvokeMethodAsync(
        HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        if (_options.Enabled && !TryAcquire(BucketKey(invocationContext.Context), invocationContext.HubMethodName))
        {
            _metrics.RateLimitRejected("hub", MethodClass(invocationContext.HubMethodName));
            // HubObservabilityFilter is registered before this filter (AddFilter order == outer-to-
            // inner), so it is the outer filter and logs this HubException at Information as normal
            // control flow, the same treatment "Game not found"/"Not your turn" already get. Do not
            // log it again here.
            throw new HubException("Too many requests. Slow down.");
        }

        return await next(invocationContext);
    }

    /// <summary>
    /// Claims a share of the caller's bucket. The key is decided once, here, and remembered: an
    /// api-token connection and a human one on the same process must not race over which key a
    /// disconnect releases.
    /// </summary>
    public Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
    {
        var key = BucketKey(context.Context);
        _keyByConnection[context.Context.ConnectionId] = key;
        lock (_gate)
            _refs[key] = _refs.TryGetValue(key, out var count) ? count + 1 : 1;
        return next(context);
    }

    public Task OnDisconnectedAsync(
        HubLifetimeContext context, Exception? exception, Func<HubLifetimeContext, Exception?, Task> next)
    {
        // A connection refused by an outer filter never reached OnConnectedAsync, so there is nothing
        // recorded and nothing to release.
        if (_keyByConnection.TryRemove(context.Context.ConnectionId, out var key))
        {
            lock (_gate)
            {
                var remaining = _refs.TryGetValue(key, out var count) ? count - 1 : 0;
                if (remaining > 0)
                {
                    _refs[key] = remaining;
                }
                else
                {
                    _refs.Remove(key);
                    // Only the last connection on a key disposes it. For a human that is the same
                    // connection that created it, so this is byte-for-byte the pre-S-4-2 behaviour;
                    // for a bot's second socket it is what stops one closing tab resetting the
                    // account's budget for the others.
                    if (_buckets.Remove(key, out var buckets)) buckets.Dispose();
                }
            }
        }

        return next(context, exception);
    }

    /// <summary>
    /// What a bucket is keyed by: the account for an API token, the connection for everyone else. See
    /// the class doc for why those two are different callers.
    /// </summary>
    private static string BucketKey(HubCallerContext context)
    {
        var user = context.User;
        if (user?.FindFirst(ApiTokenClaims.TokenUse)?.Value != ApiTokenClaims.Api)
            return context.ConnectionId;

        var subject = user.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? user.FindFirst("sub")?.Value;
        // No usable subject means nothing to aggregate on, and every player method throws on that
        // principal anyway; fall back rather than collapsing such callers into one shared bucket.
        return subject is null ? context.ConnectionId : $"api:{subject}";
    }

    /// <summary>
    /// The bucket-acquire decision, extracted so it can be unit-tested directly without constructing
    /// a full <see cref="HubInvocationContext"/> (its constructor wants a <see cref="HubCallerContext"/>,
    /// the hub type, a service provider, method info and an argument list, heavier than the bucket
    /// arithmetic actually being tested is worth). <paramref name="key"/> is what
    /// <see cref="BucketKey"/> returned: a connection id, or <c>api:{userId}</c> for an API token.
    /// </summary>
    internal bool TryAcquire(string key, string methodName)
    {
        lock (_gate)
        {
            if (!_buckets.TryGetValue(key, out var buckets))
                _buckets[key] = buckets = new ConnectionBuckets(_options);
            using var lease = buckets.For(methodName).AttemptAcquire();
            return lease.IsAcquired;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var buckets in _buckets.Values) buckets.Dispose();
            _buckets.Clear();
            _refs.Clear();
        }
        _keyByConnection.Clear();
    }

    private static string MethodClass(string methodName) => methodName switch
    {
        SubmitMoveMethod => "hub-move",
        JoinQueueMethod or LeaveQueueMethod => "hub-queue",
        _ => "hub-default"
    };

    /// <summary>One token bucket per method class, lazily created per connection and disposed together.</summary>
    private sealed class ConnectionBuckets : IDisposable
    {
        private readonly TokenBucketRateLimiter _move;
        private readonly TokenBucketRateLimiter _queue;
        private readonly TokenBucketRateLimiter _default;

        public ConnectionBuckets(RateLimitOptions options)
        {
            _move = Create(options.HubMoveBurst, options.HubMoveTokensPerSecond);
            _queue = Create(options.HubQueueBurst, options.HubQueueTokensPerSecond);
            _default = Create(options.HubDefaultBurst, options.HubDefaultTokensPerSecond);
        }

        public TokenBucketRateLimiter For(string methodName) => methodName switch
        {
            SubmitMoveMethod => _move,
            JoinQueueMethod or LeaveQueueMethod => _queue,
            _ => _default
        };

        private static TokenBucketRateLimiter Create(int burst, int tokensPerSecond) =>
            new(new TokenBucketRateLimiterOptions
            {
                TokenLimit = burst,
                TokensPerPeriod = tokensPerSecond,
                ReplenishmentPeriod = TimeSpan.FromSeconds(1),
                QueueLimit = 0,
                AutoReplenishment = true
            });

        public void Dispose()
        {
            _move.Dispose();
            _queue.Dispose();
            _default.Dispose();
        }
    }
}
