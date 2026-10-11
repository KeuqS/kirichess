namespace KiriChess.Server.Auth;

/// <summary>Provider names and scheme names for external sign-in, in one place so adding a second provider is additive.</summary>
public static class ExternalAuthDefaults
{
    /// <summary>
    /// The short-lived cookie scheme the provider handler signs into. This server authenticates
    /// with JWTs; the cookie exists only for the few hundred milliseconds between the provider's
    /// callback and our exchange of that identity for our own tokens, and is signed out immediately
    /// after. It is deliberately *not* the default scheme, [Authorize] still means "bearer JWT".
    /// </summary>
    public const string ExternalScheme = "External";
}

/// <summary>
/// External-auth configuration. Follows the <c>RateLimitOptions</c>/<c>MetricsOptions</c> shape: a
/// plain record with a <see cref="From"/> factory, constructed after the host is built rather than
/// off <c>builder.Configuration</c> eagerly (see the README's "never read config eagerly" rule).
/// </summary>
public sealed record ExternalAuthOptions(
    string GoogleClientId,
    string GoogleClientSecret,
    string FrontendBaseUrl,
    int CodeLifetimeSeconds)
{
    /// <summary>
    /// Fails closed, exactly like <c>Sentry:Dsn</c> (6d.2) and <c>Metrics:ScrapeToken</c> (6d.3): an
    /// unconfigured deploy does not register the handler at all, zero egress, zero startup cost, and
    /// the start endpoint 404s rather than half-working. <see cref="FrontendBaseUrl"/> is part of the
    /// condition because without it there is nowhere to send the user after a successful callback,
    /// and a sign-in that cannot complete is worse than one that is visibly switched off.
    /// </summary>
    public bool GoogleEnabled =>
        !string.IsNullOrWhiteSpace(GoogleClientId)
        && !string.IsNullOrWhiteSpace(GoogleClientSecret)
        && !string.IsNullOrWhiteSpace(FrontendBaseUrl);

    public static ExternalAuthOptions From(IConfiguration c) => new(
        c.GetValue("Authentication:Google:ClientId", string.Empty) ?? string.Empty,
        c.GetValue("Authentication:Google:ClientSecret", string.Empty) ?? string.Empty,
        (c.GetValue("Frontend:BaseUrl", string.Empty) ?? string.Empty).TrimEnd('/'),
        c.GetValue("Auth:ExternalCodeLifetimeSeconds", 60));
}
