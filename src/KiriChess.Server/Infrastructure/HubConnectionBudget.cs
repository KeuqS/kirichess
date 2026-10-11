using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;

namespace KiriChess.Server.Infrastructure;

/// <summary>
/// Every tunable for the Phase 10a.2 connection budget, read from configuration in one place for the
/// same reason <see cref="RateLimitOptions"/> is: a deploy can loosen any of it without a rebuild.
/// </summary>
/// <param name="Enabled">
/// Off means no counting at all, not counting-without-enforcing: a disabled budget must cost a live
/// connection nothing, which is the same rule <c>UseKiriChessRateLimiter</c> follows.
/// </param>
/// <param name="MaxConnectionsPerIp">
/// Live hub connections one client IP may hold across both hubs. <b>20 is derived, not a default.</b>
/// A player holds two (<c>/hubs/game</c> + <c>/hubs/presence</c>) per tab, so 20 is ten tabs, or a
/// household/NAT of several people playing at once, while capping any single address at 2% of
/// <c>hard_limit</c>. The number that matters is the second one: a script from one machine can no
/// longer take the site's whole budget, which is the failure this exists to stop.
///
/// <b>C-5-10: this holds on one machine only.</b> <c>_perIp</c> below is an in-memory
/// <see cref="Dictionary{TKey,TValue}"/>, per process, not shared across instances. With N instances
/// behind Fly's proxy one address effectively gets N x 20 sockets, and a client that reconnects until
/// it lands on a fresh instance gets there deliberately. The anonymous ceiling below has the same
/// per-process shape but is harmless there, because <c>hard_limit</c> and the reserve arithmetic are
/// both per machine by construction; this cap's whole point is being cross-machine, so it is the one
/// that actually needs fixing once multi-instance lands - onto the <c>RedisRateLimiter</c> that
/// already exists, not a second distributed mechanism.
/// </param>
/// <param name="MaxAnonymousConnections">
/// Live hub connections held by callers with no user id, i.e. 8.1's anonymous spectators and
/// logged-out lobby visitors. <b>200 is the reserve, stated as its complement:</b> it leaves 400
/// sockets that only authenticated callers can ever occupy, and 400 is 100 concurrent games at
/// 9c.7's four-sockets-per-game. So at the worst moment the launch post can produce - the ceiling
/// full of onlookers - a hundred games can still start, provided the 400 authenticated sockets are
/// actually held by players. The judgement inside that is which side gets the slack, and it goes to
/// players deliberately: a refused spectator retries and loses a page view, a refused player loses
/// the reason they came.
///
/// <b>C-5-6: the partition this reserve enforces is authenticated-vs-anonymous, not player-vs-
/// spectator.</b> It is decided at connect time by <see cref="HubConnectionBudget.IsAuthenticated"/>,
/// before any hub method has run, so a signed-in visitor watching <c>/watch/[id]</c> - an ordinary
/// case on a launch thread, not an edge one - spends the players' reserve exactly like a player
/// would. The reserve holds against a logged-out onlooker only; it does nothing against the same
/// crowd once they have accounts, which is the direction 8.7 is trying to push them. Making it a real
/// players-only cap would mean counting a socket in only once <c>JoinGame</c> has succeeded on it
/// (the hub already knows), which this class does not do today.
/// </param>
public sealed record HubConnectionBudgetOptions(
    bool Enabled,
    int MaxConnectionsPerIp,
    int MaxAnonymousConnections)
{
    public static HubConnectionBudgetOptions From(IConfiguration configuration) => new(
        configuration.GetValue("Hub:ConnectionBudget:Enabled", true),
        configuration.GetValue("Hub:ConnectionBudget:MaxConnectionsPerIp", 20),
        configuration.GetValue("Hub:ConnectionBudget:MaxAnonymousConnections", 200));
}

/// <summary>
/// Counts live SignalR connections per client IP and, separately, in total for anonymous callers
/// (Phase 10a.2).
///
/// <b>Why a connection counter and not another rate limiter.</b> <see cref="HubRateLimitFilter"/> is
/// a per-connection token bucket: it limits how fast a connection may <em>invoke</em> and says
/// nothing about how many connections exist, so a thousand idle spectator sockets violate no rate
/// limit at all. <c>fly.backend.toml</c>'s <c>hard_limit = 600</c> does count the right unit, but
/// it counts it globally and for the wrong population: <c>JoinAsSpectator</c> is anonymous on
/// purpose (8.1), and past <c>hard_limit</c> this machine refuses new connections while a second
/// stopped machine, if Redis is up, takes them. The traffic 8.7 is designed to produce is mostly people who watch one
/// game and leave. The failure that combination produces is not a server falling over, which would
/// at least be legible: it is <em>players being refused while onlookers hold the capacity</em>.
///
/// <b>Do not solve this by raising <c>hard_limit</c></b>: 9b.3 measured the ceiling and 9c.7 derated
/// it deliberately, and the toml file argues that at length.
///
/// The IP is trustworthy here for the reason <see cref="RateLimitingSetup.IpKey"/> gives:
/// <c>UseForwardedHeaders()</c> runs first with the known-network allowlist cleared, so
/// RemoteIpAddress is the last trusted hop (ForwardLimit = 1). OriginHostLock 404s API traffic
/// that presents the public Fly hostname; it does not change how X-Forwarded-For is read.
/// <c>RemoteIpAddress</c> is the caller rather than Fly's or Cloudflare's edge.
/// </summary>
public sealed class HubConnectionBudget
{
    /// <summary>Refusal reasons, also the metric tag. Fixed strings, never an IP - see KiriChessMetrics' cardinality rule.</summary>
    public const string PerIpPolicy = "hub-connections-per-ip";
    public const string AnonymousPolicy = "hub-connections-anonymous";

    /// <summary>Partition key for a caller with no remote address. See <see cref="IsKnownAddress"/> for what it costs.</summary>
    public const string UnknownAddress = "anonymous";

    /// <summary>
    /// What a refused caller is told. Deliberately says nothing about which ceiling was hit or how
    /// high it is, the same rule the HTTP limiter's rejection follows, and deliberately reads as
    /// temporary, because it is: the budget frees as connections close.
    /// </summary>
    public const string RefusalMessage = "Busy right now, try again shortly.";

    private readonly object _gate = new();
    // C-5-10: in-memory and per-process. See MaxConnectionsPerIp's doc comment - this is the reason
    // the per-IP cap's cross-machine guarantee does not hold once a second instance exists.
    private readonly Dictionary<string, int> _perIp = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Reservation> _reservations = new(StringComparer.Ordinal);
    private int _anonymous;

    private readonly HubConnectionBudgetOptions _options;
    private readonly KiriChessMetrics _metrics;

    public HubConnectionBudget(IConfiguration configuration, KiriChessMetrics metrics)
    {
        // Same reasoning as HubRateLimitFilter's constructor: hub filters and their dependencies are
        // resolved on the first connection, long after Build(), so this sees the fully composed
        // configuration rather than a builder-time snapshot.
        _options = HubConnectionBudgetOptions.From(configuration);
        _metrics = metrics;
    }

    public HubConnectionBudgetOptions Options => _options;

    /// <summary>Live connections currently counted against <paramref name="ipKey"/>. Test/diagnostic only.</summary>
    internal int LiveForIp(string ipKey)
    {
        lock (_gate) return _perIp.TryGetValue(ipKey, out var count) ? count : 0;
    }

    /// <summary>Live anonymous connections across both hubs. Test/diagnostic only.</summary>
    internal int LiveAnonymous
    {
        get { lock (_gate) return _anonymous; }
    }

    /// <summary>
    /// Takes a slot for a connection that is being established, or returns the policy that refused
    /// it. Check-and-increment happen under one lock: two sockets arriving on the same millisecond
    /// from the same address must not both read a ceiling that only one of them can have.
    /// </summary>
    public string? TryReserve(string connectionId, string ipKey, bool authenticated)
    {
        if (!_options.Enabled) return null;

        lock (_gate)
        {
            var forIp = _perIp.TryGetValue(ipKey, out var existing) ? existing : 0;
            if (IsKnownAddress(ipKey) && forIp >= _options.MaxConnectionsPerIp)
            {
                _metrics.RateLimitRejected("hub-connect", PerIpPolicy);
                return PerIpPolicy;
            }

            if (!authenticated && _anonymous >= _options.MaxAnonymousConnections)
            {
                _metrics.RateLimitRejected("hub-connect", AnonymousPolicy);
                return AnonymousPolicy;
            }

            // Recorded before the counters move so Release cannot decrement a bucket this connection
            // was never charged to; TryAdd rather than [] because a duplicated connection id would
            // otherwise silently overwrite a reservation and leak the first one's slot.
            if (!_reservations.TryAdd(connectionId, new Reservation(ipKey, authenticated))) return null;

            _perIp[ipKey] = forIp + 1;
            if (!authenticated) _anonymous++;
            return null;
        }
    }

    /// <summary>Gives back whatever <see cref="TryReserve"/> took. Safe to call for a connection that never reserved.</summary>
    public void Release(string connectionId)
    {
        if (!_reservations.TryRemove(connectionId, out var reservation)) return;

        lock (_gate)
        {
            if (_perIp.TryGetValue(reservation.IpKey, out var count))
            {
                if (count <= 1) _perIp.Remove(reservation.IpKey);   // never leave zero-rows behind: this dictionary is unbounded in keys
                else _perIp[reservation.IpKey] = count - 1;
            }

            if (!reservation.Authenticated && _anonymous > 0) _anonymous--;
        }
    }

    /// <summary>
    /// Would a connection from this caller be admitted right now? Used by the negotiate probe, which
    /// answers before a connection exists and therefore must not reserve anything. Advisory by
    /// construction: <see cref="TryReserve"/> is the decision, this only lets the client be told
    /// clearly instead of watching a socket die.
    /// </summary>
    public bool HasHeadroom(string ipKey, bool authenticated)
    {
        if (!_options.Enabled) return true;

        lock (_gate)
        {
            var forIp = _perIp.TryGetValue(ipKey, out var existing) ? existing : 0;
            if (IsKnownAddress(ipKey) && forIp >= _options.MaxConnectionsPerIp) return false;
            return authenticated || _anonymous < _options.MaxAnonymousConnections;
        }
    }

    /// <summary>
    /// The per-IP cap only applies when there is an IP to apply it to.
    ///
    /// <b>This is a deliberate fail-open, and it is the safer direction for a ceiling.</b> If
    /// <c>RemoteIpAddress</c> is ever null - forwarded headers misconfigured, a transport that has no
    /// remote address - then every caller in the world keys to <see cref="UnknownAddress"/>, and
    /// enforcing the per-IP cap on that bucket would not partition anything: it would cap the entire
    /// site at twenty connections, from a misconfiguration, in the same deploy that was meant to
    /// protect capacity. Refusing to partition is the pre-10a.2 state, which was survivable; refusing
    /// everyone is not. The anonymous ceiling is unaffected and still holds the players' reserve.
    /// </summary>
    private static bool IsKnownAddress(string ipKey) => !string.Equals(ipKey, UnknownAddress, StringComparison.Ordinal);

    /// <summary>True when the caller carries a usable user id, which is what "player" means for the reserve.</summary>
    public static bool IsAuthenticated(ClaimsPrincipal? user)
    {
        var raw = user?.FindFirstValue(ClaimTypes.NameIdentifier) ?? user?.FindFirstValue("sub");
        return Guid.TryParse(raw, out _);
    }

    private readonly record struct Reservation(string IpKey, bool Authenticated);
}

/// <summary>
/// Applies <see cref="HubConnectionBudget"/> at the one place a connection actually begins existing
/// (Phase 10a.2). Both hubs get it: a spectator holds a game socket and a logged-out lobby visitor
/// holds a presence socket, and both spend the same <c>hard_limit</c>.
///
/// <b>The rejection is a <see cref="HubException"/> on purpose.</b> ASP.NET only forwards an
/// exception's own message to a SignalR client when it is a HubException, everything else becomes
/// "Connection closed with an error", which is precisely the mystery this item exists to avoid.
/// The negotiate probe (<c>UseHubConnectionBudgetProbe</c>) is what turns that into a clean failure
/// of the client's own <c>start()</c>; this is the layer that makes the cap real, including for a
/// client that skips negotiation entirely.
/// </summary>
public sealed class HubConnectionBudgetFilter : IHubFilter
{
    private readonly HubConnectionBudget _budget;

    public HubConnectionBudgetFilter(HubConnectionBudget budget) => _budget = budget;

    public async Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
    {
        var connectionId = context.Context.ConnectionId;
        var ipKey = IpKeyOf(context);
        var authenticated = HubConnectionBudget.IsAuthenticated(context.Context.User);

        if (_budget.TryReserve(connectionId, ipKey, authenticated) is not null)
            throw new HubException(HubConnectionBudget.RefusalMessage);

        try
        {
            await next(context);
        }
        catch
        {
            // SignalR does not run OnDisconnectedAsync for a connection whose OnConnectedAsync threw,
            // so without this the slot would be held by a connection that never existed.
            _budget.Release(connectionId);
            throw;
        }
    }

    public Task OnDisconnectedAsync(
        HubLifetimeContext context, Exception? exception, Func<HubLifetimeContext, Exception?, Task> next)
    {
        _budget.Release(context.Context.ConnectionId);
        return next(context, exception);
    }

    private static string IpKeyOf(HubLifetimeContext context)
    {
        // Same shape as RateLimitingSetup.IpKey, deliberately, so the two surfaces partition
        // identically and one caller reads the same on both.
        var ip = context.Context.GetHttpContext()?.Connection.RemoteIpAddress?.ToString();
        return ip is null ? HubConnectionBudget.UnknownAddress : $"ip:{ip}";
    }
}

/// <summary>
/// Tells a client it will not fit <em>before</em> it opens a socket (Phase 10a.2), by answering the
/// SignalR negotiate request with the protocol's own <c>error</c> field.
///
/// <b>Why this exists on top of the filter.</b> SignalR completes its handshake, and therefore the
/// client's <c>start()</c>, before <c>OnConnectedAsync</c> runs. A connection refused in the filter
/// alone is refused after <c>start()</c> has already resolved, so the page sees a connection that
/// opens and then dies, and whatever it does next fails with a timing-dependent message. Refusing at
/// negotiate makes <c>start()</c> itself reject, with our text, which is what
/// <c>ui/app/game/[id]/page.tsx</c> and <c>ui/app/watch/[id]/page.tsx</c> already render.
///
/// <b>200, not 503, and that is not a mistake.</b> The negotiate contract carries the failure in the
/// body: the JS client does <c>if (negotiateResponse.error) throw new Error(negotiateResponse.error)</c>.
/// A non-2xx is reported as "Failed to complete negotiation with the server" with the status text
/// attached, which loses the sentence we are trying to deliver.
///
/// Advisory on purpose: it reserves nothing, so a caller that races between negotiate and connect is
/// stopped by the filter instead. Nothing here is the enforcement point.
/// </summary>
public static class HubConnectionBudgetProbe
{
    public static WebApplication UseHubConnectionBudgetProbe(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            if (IsNegotiate(context.Request))
            {
                var budget = context.RequestServices.GetRequiredService<HubConnectionBudget>();
                var authenticated = HubConnectionBudget.IsAuthenticated(context.User);

                if (!budget.HasHeadroom(RateLimitingSetup.IpKey(context), authenticated))
                {
                    context.Response.StatusCode = StatusCodes.Status200OK;
                    // Serialized rather than string-interpolated: the negotiate contract and this
                    // API's own error envelope happen to agree on the field name, so the same
                    // ErrorResponse both ui/lib/api.ts and SignalR's client already understand is
                    // the right thing to write, and it cannot be broken by a quote in the message.
                    await context.Response.WriteAsJsonAsync(
                        new ErrorResponse(HubConnectionBudget.RefusalMessage));
                    return;
                }
            }

            await next(context);
        });

        return app;
    }

    private static bool IsNegotiate(HttpRequest request) =>
        HttpMethods.IsPost(request.Method)
        && request.Path.StartsWithSegments("/hubs", out var rest)
        && rest.Value?.EndsWith("/negotiate", StringComparison.Ordinal) == true;
}
