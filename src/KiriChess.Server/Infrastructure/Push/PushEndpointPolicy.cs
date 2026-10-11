namespace KiriChess.Server.Infrastructure.Push;

/// <summary>
/// S-1-8: what a client is allowed to register as a push endpoint.
///
/// <b>Why this is a security control and not input tidying.</b> Whatever string is stored here is a
/// URL <see cref="WebPushSender"/> later POSTs to from inside the app's own container, so an
/// unvalidated endpoint is a server-side request forgery primitive aimed at whatever that container
/// can reach (Fly's private 6PN network included). The body is discarded, but
/// <see cref="IPushSender.SendAsync"/> distinguishes 404/410 from every other failure, which is a
/// one-bit oracle. Real push services are a short, well-known list, so an allowlist costs nothing a
/// browser legitimately needs.
///
/// <b>What this deliberately does not do: a DNS lookup.</b> The finding also suggested rejecting an
/// endpoint that resolves to a private or link-local address. That is not done here, for three
/// reasons, and the omission is a decision rather than an oversight:
/// <list type="number">
/// <item>A lookup at subscribe time is itself an outbound request driven by attacker-controlled
/// input, which is a smaller version of the very thing being prevented.</item>
/// <item>It is time-of-check to time-of-use: a name that resolves publicly now can resolve to
/// 169.254.169.254 by the time a rematch actually fires, so the check would buy confidence it cannot
/// keep (DNS rebinding).</item>
/// <item>With the allowlist in place it is redundant for the case it was aimed at. Every allowed
/// host is a public push service operated by Google, Mozilla, Microsoft or Apple; a host that is a
/// literal IP address (private, link-local, loopback or otherwise) matches no suffix below and is
/// additionally refused by the <see cref="UriHostNameType.Dns"/> check.</item>
/// </list>
/// The residual risk the DNS half would have covered is one of those four vendors pointing a
/// subdomain at a private range of ours, which is not a threat model that holds together.
/// </summary>
public static class PushEndpointPolicy
{
    /// <summary>
    /// What a rejected subscriber is told. Deliberately does not echo the endpoint back or name
    /// which rule failed: the caller supplied the string and does not need it graded.
    /// </summary>
    public const string RejectionMessage = "Endpoint must be an https URL on a supported push service.";

    /// <summary>
    /// Matches <c>WebPushSubscription.Endpoint</c>'s column width
    /// (<c>KiriChessDbContext</c>, <c>HasMaxLength(512)</c>), so an over-long endpoint is a clean 400
    /// here rather than a 500 out of <c>SaveChangesAsync</c>. Real endpoints are well under 200
    /// characters.
    /// </summary>
    public const int MaxLength = 512;

    /// <summary>
    /// The push services actually reachable from a browser. Each entry matches the host exactly or
    /// any subdomain of it.
    ///
    /// <c>googleapis.com</c> is broader than <c>fcm.googleapis.com</c> on purpose: Chrome has shipped
    /// both <c>fcm.googleapis.com</c> and, historically, <c>android.googleapis.com/gcm/send</c>, and a
    /// browser hands the app whichever one it chose. <c>web.push.apple.com</c> is Safari's, which the
    /// finding's list omitted; without it, enabling VAPID would silently refuse every Safari user.
    /// </summary>
    private static readonly string[] AllowedHostSuffixes =
    [
        "googleapis.com",
        "push.services.mozilla.com",
        "notify.windows.com",
        "web.push.apple.com",
    ];

    /// <summary>
    /// True when <paramref name="endpoint"/> is safe to store and later POST to. Pure string work: no
    /// I/O, no DNS, no network, so it is cheap enough to run on every subscribe.
    /// </summary>
    public static bool IsAllowed(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return false;
        if (endpoint.Length > MaxLength) return false;
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)) return false;

        // https only. A plaintext endpoint would leak the encrypted payload's routing and is not
        // something any real push service offers.
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)) return false;

        // An allowlisted host on a non-standard port is a port scan wearing a trusted name.
        if (!uri.IsDefaultPort) return false;

        // https://user:pass@fcm.googleapis.com/... has no legitimate use and is a well-worn way to
        // make a URL read as one host to a human and parse as another to some clients.
        if (!string.IsNullOrEmpty(uri.UserInfo)) return false;

        // A literal IPv4/IPv6 address, which is how the private and link-local targets would be
        // named, never has a DNS host name type. The suffix check below would refuse it anyway;
        // this states the intent where a reader looking for the SSRF guard will find it.
        if (uri.HostNameType != UriHostNameType.Dns) return false;

        // IdnHost, not Host: a Unicode homograph is compared in its punycode form, so it cannot
        // match an ASCII suffix by looking like it.
        var host = uri.IdnHost;
        foreach (var suffix in AllowedHostSuffixes)
        {
            if (host.Equals(suffix, StringComparison.OrdinalIgnoreCase)) return true;
            if (host.Length > suffix.Length
                && host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                && host[host.Length - suffix.Length - 1] == '.')
                return true;
        }

        return false;
    }
}
