namespace KiriChess.Server.Infrastructure.Email;

/// <summary>
/// Transactional email configuration (Phase 8a.1). Same shape as <c>ExternalAuthOptions</c>: a plain
/// record with a <see cref="From"/> factory, so the enabled/disabled decision is a single readable
/// expression rather than scattered null checks at the call sites.
/// </summary>
/// <param name="ApiKey">Resend API key (<c>re_...</c>). Empty means no provider.</param>
/// <param name="FromAddress">Envelope sender. Must be on a domain verified in Resend, or every send 403s.</param>
/// <param name="FromName">Display name in the From header. Optional.</param>
/// <param name="FrontendBaseUrl">Public site origin, no trailing slash. Every link in every email is built from this.</param>
/// <param name="TimeoutSeconds">Per-attempt HTTP timeout against the provider.</param>
/// <param name="MaxAttempts">Total attempts per message, including the first. 1 disables retrying.</param>
/// <param name="QueueCapacity">Bounded in-process queue depth. Beyond it, the newest write is dropped and logged
/// (<see cref="QueuedEmailSender"/> uses <c>BoundedChannelFullMode.DropWrite</c>, which drops the incoming write,
/// not the oldest queued one - M-5-5).</param>
/// <param name="ApiBaseUrl">
/// M-5-2: the API's own public origin (e.g. <c>https://api.kirichess.com</c>), no trailing slash.
/// Distinct from <see cref="FrontendBaseUrl"/> - see <c>docs/ops/DEPLOYMENT.md</c>'s DNS table, the
/// site and the API are different hosts. Empty means "not configured": <see cref="UnsubscribeLink.BuildOneClick"/>
/// then returns null and reach-back mail falls back to a header that names the footer link without an
/// RFC 8058 one-click promise, rather than pointing a mail client's one-click button at a URL that
/// cannot exist yet.
/// </param>
public sealed record EmailOptions(
    string ApiKey,
    string FromAddress,
    string FromName,
    string FrontendBaseUrl,
    int TimeoutSeconds,
    int MaxAttempts,
    int QueueCapacity,
    string ApiBaseUrl = "")
{
    /// <summary>
    /// Fails closed exactly like <c>Sentry:Dsn</c> (6d.2), <c>Metrics:ScrapeToken</c> (6d.3) and
    /// Google sign-in (6b.3): without all three values the provider client and the dispatcher are
    /// never registered, <see cref="LoggingEmailSender"/> stays in place, and the deploy has zero
    /// egress and zero startup cost. <see cref="FrontendBaseUrl"/> is part of the condition because
    /// a confirmation mail whose link points nowhere is worse than one that was never sent - the
    /// user burns their token on a broken URL and the endpoint has no way to tell them why.
    /// </summary>
    public bool Enabled =>
        !string.IsNullOrWhiteSpace(ApiKey)
        && !string.IsNullOrWhiteSpace(FromAddress)
        && !string.IsNullOrWhiteSpace(FrontendBaseUrl);

    /// <summary>RFC 5322 From header, e.g. <c>KiriChess &lt;noreply@kirichess.app&gt;</c>.</summary>
    public string FromHeader =>
        string.IsNullOrWhiteSpace(FromName) ? FromAddress : $"{FromName} <{FromAddress}>";

    public static EmailOptions From(IConfiguration c) => new(
        c.GetValue("Email:ApiKey", string.Empty) ?? string.Empty,
        c.GetValue("Email:FromAddress", string.Empty) ?? string.Empty,
        c.GetValue("Email:FromName", "KiriChess") ?? "KiriChess",
        (c.GetValue("Frontend:BaseUrl", string.Empty) ?? string.Empty).TrimEnd('/'),
        c.GetValue("Email:TimeoutSeconds", 10),
        c.GetValue("Email:MaxAttempts", 4),
        c.GetValue("Email:QueueCapacity", 512),
        (c.GetValue("Api:BaseUrl", string.Empty) ?? string.Empty).TrimEnd('/'));
}
