namespace KiriChess.Server.Infrastructure.Email;

/// <summary>
/// Builds the one-click unsubscribe URL every "reach-back" mail carries (Phase 11b.1). A tiny shared
/// helper rather than each sender (<c>PairingNotificationService</c>, <c>RematchNotificationService</c>)
/// assembling its own query string, so the link's shape and the anonymous
/// <c>POST /api/notifications/unsubscribe</c> endpoint that reads it can never drift apart.
/// </summary>
public static class UnsubscribeLink
{
    public static string Build(
        EmailOptions options, UnsubscribeTokenService tokens, Guid userId, NotificationCategory category)
    {
        var token = tokens.GenerateToken(userId, category);
        return $"{options.FrontendBaseUrl}/unsubscribe?userId={userId}&category={category}&token={token}";
    }

    /// <summary>
    /// M-5-2: the URL an RFC 8058 one-click button actually POSTs to -
    /// <c>POST /api/notifications/unsubscribe/one-click</c> on the API host directly, not the frontend
    /// page <see cref="Build"/> targets. A mail client's one-click flow never runs JavaScript, so it
    /// cannot use the frontend page the way a human reading the footer link does; it needs a URL that
    /// answers a bodyless/form POST on its own (the second route in
    /// <see cref="NotificationEndpoints.MapNotificationEndpoints"/>).
    /// Returns null when <see cref="EmailOptions.ApiBaseUrl"/> is not configured - callers must then
    /// fall back to <see cref="Build"/>'s URL without promising one-click (see
    /// <see cref="EmailMessage.UnsubscribeIsOneClick"/>).
    /// </summary>
    public static string? BuildOneClick(
        EmailOptions options, UnsubscribeTokenService tokens, Guid userId, NotificationCategory category)
    {
        if (string.IsNullOrWhiteSpace(options.ApiBaseUrl)) return null;

        var token = tokens.GenerateToken(userId, category);
        return $"{options.ApiBaseUrl}/api/notifications/unsubscribe/one-click"
             + $"?userId={userId}&category={category}&token={token}";
    }
}
