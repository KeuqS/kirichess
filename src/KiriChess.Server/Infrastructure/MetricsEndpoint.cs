using System.Security.Cryptography;
using System.Text;

namespace KiriChess.Server.Infrastructure;

/// <summary>Configuration for the Prometheus scrape endpoint (6d.3).</summary>
public sealed record MetricsOptions(int Port, string? ScrapeToken)
{
    public const string Path = "/metrics";

    /// <summary>
    /// The endpoint is only mapped when at least one gate is configured. An unconfigured deploy must
    /// not publish live-game counts and queue depth to the internet, so this fails closed the same
    /// way an unconfigured <c>Cors:AllowedOrigins</c> does.
    /// </summary>
    public bool Enabled => Port > 0 || !string.IsNullOrWhiteSpace(ScrapeToken);

    public static MetricsOptions From(IConfiguration configuration) => new(
        configuration.GetValue("Metrics:Port", 0),
        configuration.GetValue<string?>("Metrics:ScrapeToken"));
}

/// <summary>What the guard decided about one scrape request. See <see cref="MetricsEndpoint.Evaluate"/>.</summary>
internal enum MetricsAccess
{
    /// <summary>Serve the scrape.</summary>
    Allow,

    /// <summary>Pretend the endpoint does not exist: no gate on this port can ever admit the caller.</summary>
    NotFound,

    /// <summary>A gate exists on this port and the request did not satisfy it.</summary>
    Unauthorized
}

/// <summary>Path gate in front of the Prometheus scrape endpoint. See <see cref="MetricsOptions"/>.</summary>
public static class MetricsEndpoint
{
    /// <summary>
    /// The two gates are alternatives, not a conjunction, and that is the whole point of this method.
    /// <para>
    /// <b>Metrics port, no token.</b> <c>Metrics:Port</c> is bound in addition to the public port and
    /// is not published by <c>[http_service]</c>, so it is reachable only over Fly's private network.
    /// Fly's built-in Prometheus scrapes it and <i>cannot be configured to send a header</i>, so a
    /// token requirement there would lock out the only scraper the port exists for.
    /// </para>
    /// <para>
    /// <b>Any other port, token required.</b> This is the public port, so the bearer token is the
    /// entire access control (an external scraper such as Better Stack's has no way onto the private
    /// network). With no token configured this is <see cref="MetricsAccess.NotFound"/> rather than
    /// <see cref="MetricsAccess.Unauthorized"/>: an unconfigured deploy must not even admit that live
    /// game counts and queue depth are being published here.
    /// </para>
    /// <para>
    /// <b>Do not restore the old <c>LocalPort != Port</c> 404.</b> It made the two gates a conjunction,
    /// which meant a correct token on the public port was rejected as hard as no token at all, so
    /// <c>/metrics</c> was unreachable by anything off the private network no matter how it
    /// authenticated. That shipped, and cost 9c.5 an alerting half that scraped a permanent 404 while
    /// <c>/health</c> on the same machine returned 200 - which reads exactly like the app being
    /// misconfigured rather than the guard doing this deliberately.
    /// </para>
    /// </summary>
    internal static MetricsAccess Evaluate(MetricsOptions options, int localPort, string authorizationHeader)
    {
        if (options.Port > 0 && localPort == options.Port)
            return MetricsAccess.Allow;

        if (string.IsNullOrWhiteSpace(options.ScrapeToken))
            return MetricsAccess.NotFound;

        // S-1-12: this is the only shared-secret comparison in the server that was an ordinal string
        // compare rather than a hash lookup or an HMAC, so it is the only place a timing argument
        // applies. FixedTimeEquals over the UTF-8 bytes removes the early-exit-on-first-differing-byte
        // shape; both sides must be equal length for FixedTimeEquals's own contract, so a length
        // mismatch is folded into "not equal" up front rather than passed to it.
        var expected = Encoding.UTF8.GetBytes($"Bearer {options.ScrapeToken}");
        var actual = Encoding.UTF8.GetBytes(authorizationHeader);
        var match = expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(expected, actual);
        return match ? MetricsAccess.Allow : MetricsAccess.Unauthorized;
    }

    /// <summary>
    /// Rejects scrape requests that arrive on the wrong port or without the configured bearer token,
    /// before routing gets a chance to serve them. Runs as middleware rather than an endpoint filter
    /// because the exporter maps a branched pipeline, not a route handler.
    /// </summary>
    public static IApplicationBuilder UseMetricsScrapeGuard(this WebApplication app, MetricsOptions options)
    {
        if (!options.Enabled) return app;

        app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments(MetricsOptions.Path))
            {
                await next(context);
                return;
            }

            switch (Evaluate(options, context.Connection.LocalPort, context.Request.Headers.Authorization.ToString()))
            {
                case MetricsAccess.NotFound:
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                case MetricsAccess.Unauthorized:
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                default:
                    await next(context);
                    return;
            }
        });

        return app;
    }
}
