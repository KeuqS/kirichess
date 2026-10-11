namespace KiriChess.Server.Infrastructure;

/// <summary>
/// I-2-2: refuse non-probe traffic that arrives with the public Fly hostname as <c>Host</c>.
/// </summary>
/// <remarks>
/// The finding's first sketch was a Cloudflare Transform Rule that stamps a secret header, then
/// reject anything at Fly that lacks it. That sketch assumed <c>api.kirichess.com</c> was orange-clouded.
/// It is DNS-only (I-2-1, 2026-09-04): player traffic never passes Cloudflare, so a required edge
/// header would lock out every real request, not just the Fly hostname.
///
/// What is still true: <c>kirichess-api.fly.dev</c> is a second public name for the same machine.
/// Blocking that <c>Host</c> (except the probes Better Stack and Fly themselves use) stops casual
/// origin bypass. It does not stop someone connecting to the same IP with
/// <c>Host: api.kirichess.com</c>. <c>ForwardLimit</c> therefore stays at the framework default of 1;
/// raising it is still unsafe on a grey API.
///
/// Staging has no custom domain. Do not set <c>OriginLock:BlockedHosts</c> there: the Fly hostname
/// <i>is</i> the player hostname, and it belongs in <c>OriginLock:AllowedHosts</c> instead.
///
/// I-7-7: an optional allow-list rejects every other Host on a non-probe path. ASP.NET
/// <c>AllowedHosts</c> cannot do this job. Fly scrapes metrics on the machine's private 6PN
/// address, which is not a name that can be written into a static list, and that middleware 400s
/// the scrape. Probe paths stay exempt here, so the allow-list never sees that request.
/// An empty allow-list leaves the check off (local and compose).
/// </remarks>
public static class OriginHostLock
{
    public const string BlockedHostsKey = "OriginLock:BlockedHosts";
    public const string AllowedHostsKey = "OriginLock:AllowedHosts";

    /// <summary>
    /// Fly liveness/readiness, and Better Stack's Prometheus scrape. Both are pointed at
    /// <c>kirichess-api.fly.dev</c> on purpose so origin-up/edge-down stays distinguishable.
    /// <c>/health/ready</c> is a child of <c>/health</c> under <see cref="PathString.StartsWithSegments(PathString)"/>.
    /// </summary>
    public static bool IsExemptPath(PathString path) =>
        path.StartsWithSegments("/health") || path.StartsWithSegments("/metrics");

    /// <summary>
    /// True when <see cref="RejectionStatus"/> would refuse the request. The three-argument form
    /// is the block-list check alone (allow-list off).
    /// </summary>
    public static bool ShouldReject(
        HostString requestHost,
        PathString path,
        IReadOnlyList<string> blockedHosts,
        IReadOnlyList<string>? allowedHosts = null) =>
        RejectionStatus(requestHost, path, blockedHosts, allowedHosts ?? []) is not null;

    /// <summary>
    /// Status to send, or null to continue. A blocked host is 404 so the public Fly name looks
    /// absent. A host outside a configured allow-list is 400, the same answer ASP.NET's host
    /// filter would have given. Probe paths are never refused.
    /// </summary>
    public static int? RejectionStatus(
        HostString requestHost,
        PathString path,
        IReadOnlyList<string> blockedHosts,
        IReadOnlyList<string> allowedHosts)
    {
        if (IsExemptPath(path) || (blockedHosts.Count == 0 && allowedHosts.Count == 0))
            return null;

        var host = requestHost.Host;
        if (string.IsNullOrEmpty(host))
            return allowedHosts.Count == 0 ? null : StatusCodes.Status400BadRequest;

        if (ContainsHost(blockedHosts, host))
            return StatusCodes.Status404NotFound;

        if (allowedHosts.Count == 0 || ContainsHost(allowedHosts, host))
            return null;

        return StatusCodes.Status400BadRequest;
    }

    public static string[] ReadBlockedHosts(IConfiguration configuration) =>
        ReadHosts(configuration, BlockedHostsKey);

    public static string[] ReadAllowedHosts(IConfiguration configuration) =>
        ReadHosts(configuration, AllowedHostsKey);

    /// <summary>
    /// 404 rather than 403 for a blocked host: the Fly hostname should look like it does not serve
    /// the API, not like a lock is sitting in front of it. A host outside the allow-list is 400.
    /// </summary>
    public static IApplicationBuilder UseOriginHostLock(this WebApplication app)
    {
        var blocked = ReadBlockedHosts(app.Configuration);
        var allowed = ReadAllowedHosts(app.Configuration);
        if (blocked.Length == 0 && allowed.Length == 0)
            return app;

        app.Use(async (context, next) =>
        {
            var status = RejectionStatus(context.Request.Host, context.Request.Path, blocked, allowed);
            if (status is int code)
            {
                context.Response.StatusCode = code;
                return;
            }

            await next(context);
        });

        return app;
    }

    private static bool ContainsHost(IReadOnlyList<string> hosts, string host)
    {
        for (var i = 0; i < hosts.Count; i++)
        {
            if (string.Equals(host, hosts[i], StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string[] ReadHosts(IConfiguration configuration, string key) =>
        (configuration.GetSection(key).Get<string[]>() ?? [])
        .Where(h => !string.IsNullOrWhiteSpace(h))
        .Select(h => h.Trim())
        .ToArray();
}
