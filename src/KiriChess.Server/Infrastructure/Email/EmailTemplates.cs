using System.Net;

namespace KiriChess.Server.Infrastructure.Email;

/// <summary>
/// Builds the two transactional emails this application sends (Phase 8a.1). Kept separate from the
/// provider client so the copy and the link construction are unit-testable without an HTTP stub, and
/// so swapping providers never touches the wording.
/// </summary>
public static class EmailTemplates
{
    public const string ConfirmationSubject = "Confirm your KiriChess account";
    public const string PasswordResetSubject = "Reset your KiriChess password";
    public const string PairingFoundSubject = "Your KiriChess opponent is ready";
    public const string RematchOfferedSubject = "A rematch is waiting for you";

    /// <summary>M-5-5: Reply-To for the two reach-back categories only; confirmation/reset stay headerless.</summary>
    private const string ReachBackReplyTo = "contact@kirichess.com";

    /// <summary>
    /// Email-confirmation mail. The link targets <c>/verify-email</c>, whose page reads exactly the
    /// <c>userId</c> and <c>token</c> parameters written here.
    /// </summary>
    public static EmailMessage Confirmation(EmailOptions options, string toEmail, Guid userId, string token) =>
        Build(
            toEmail,
            ConfirmationSubject,
            intro: "Confirm your email address to finish setting up your KiriChess account.",
            action: "Confirm my email",
            url: Link(options, "/verify-email", ("userId", userId.ToString()), ("token", token)),
            footer: "If you did not create a KiriChess account, you can ignore this email - "
                  + "no account is active until this link is used.");

    /// <summary>
    /// Password-reset mail. The link targets <c>/reset-password</c>, whose page reads exactly the
    /// <c>email</c> and <c>token</c> parameters written here.
    /// </summary>
    public static EmailMessage PasswordReset(EmailOptions options, string toEmail, string token) =>
        Build(
            toEmail,
            PasswordResetSubject,
            intro: "Use the link below to choose a new KiriChess password.",
            action: "Reset my password",
            url: Link(options, "/reset-password", ("email", toEmail), ("token", token)),
            footer: "If you did not ask for a password reset, you can ignore this email - "
                  + "your current password still works.");

    /// <summary>
    /// Pairing-notification mail (Phase 9e.5). Sent to a challenger whose link was accepted while
    /// they were not watching for it - see <see cref="Games.PairingNotificationService"/>. The link
    /// targets <c>/game/{gameId}</c> directly rather than the challenge token: the challenge is
    /// already resolved by the time this mail is sent, so the token has nothing left to do.
    ///
    /// <paramref name="unsubscribeUrl"/> is <see cref="NotificationCategory.PairingFound"/>'s link
    /// (Phase 11b.1), built by <see cref="Games.PairingNotificationService"/> via
    /// <see cref="UnsubscribeTokenService"/> - every caller passes one, this mail is never sent
    /// without an opt-out. <paramref name="oneClickUrl"/> is the M-5-2 one-click variant of the same
    /// link (<see cref="UnsubscribeLink.BuildOneClick"/>), null when <c>Api:BaseUrl</c> is unconfigured.
    /// Reply-To is <c>contact@kirichess.com</c> (M-5-5): this mail says "your opponent is ready", a
    /// reply currently reached nobody.
    /// </summary>
    public static EmailMessage PairingFound(
        EmailOptions options, string toEmail, Guid gameId, string pool, bool rated, string unsubscribeUrl,
        string? oneClickUrl = null) =>
        Build(
            toEmail,
            PairingFoundSubject,
            intro: $"Your challenge was accepted - a {(rated ? "rated" : "casual")} {pool} game is underway.",
            action: "Play now",
            url: Link(options, $"/game/{gameId}"),
            footer: "If the game has already finished by the time you open this, that's fine - the result is on your profile.",
            unsubscribeUrl: unsubscribeUrl,
            oneClickUnsubscribeUrl: oneClickUrl,
            replyTo: ReachBackReplyTo);

    /// <summary>
    /// Rematch-offer mail (Phase 11b.1). Sent to the player who did not offer, when
    /// <see cref="Games.RematchNotificationService"/>'s delayed check finds the offer still open - see
    /// that type's doc comment for why the check is delayed rather than immediate. The link targets
    /// <c>/game/{finishedGameId}</c>: the overlay that offers "Accept rematch" lives on the finished
    /// game's own page, there is no separate rematch route.
    /// </summary>
    public static EmailMessage RematchOffered(
        EmailOptions options, string toEmail, Guid finishedGameId, string unsubscribeUrl,
        string? oneClickUrl = null) =>
        Build(
            toEmail,
            RematchOfferedSubject,
            intro: "Your last opponent wants to play again.",
            action: "Accept the rematch",
            url: Link(options, $"/game/{finishedGameId}"),
            footer: "If they've already found another game, the offer may no longer be open - that's fine, no action needed.",
            unsubscribeUrl: unsubscribeUrl,
            oneClickUnsubscribeUrl: oneClickUrl,
            replyTo: ReachBackReplyTo);

    /// <summary>
    /// Builds an absolute link with every value percent-encoded. This is not defensive: ASP.NET
    /// Identity's confirmation and reset tokens are Base64, so they routinely contain '+', '/' and
    /// '='. A raw '+' in a query string decodes to a space and the token is rejected with a generic
    /// "invalid token" that gives nobody a clue why. Encoding here is what makes the link work at
    /// all.
    /// </summary>
    private static string Link(EmailOptions options, string path, params (string Key, string Value)[] query)
    {
        var pairs = query.Select(q => $"{q.Key}={Uri.EscapeDataString(q.Value)}");
        return $"{options.FrontendBaseUrl}{path}?{string.Join("&", pairs)}";
    }

    /// <param name="unsubscribeUrl">
    /// Null for the two security-critical mails (confirmation, password reset), which carry no
    /// opt-out - see <see cref="NotificationCategory"/>'s own doc comment. Every other caller passes
    /// one.
    /// </param>
    private static EmailMessage Build(
        string toEmail, string subject, string intro, string action, string url, string footer,
        string? unsubscribeUrl = null, string? oneClickUnsubscribeUrl = null, string? replyTo = null)
    {
        var unsubscribeText = unsubscribeUrl is null
            ? ""
            : $"\n\nDon't want these emails? Unsubscribe: {unsubscribeUrl}";
        var text = $"{intro}\n\n{url}\n\n{footer}{unsubscribeText}\n\n-- KiriChess";

        // The '&' separators between query parameters are markup in an href attribute and must be
        // '&amp;'. Every parameter value is already percent-encoded by Link, so this encode only
        // ever touches the separators - it cannot double-encode a token.
        var href = WebUtility.HtmlEncode(url);
        var unsubscribeHref = unsubscribeUrl is null ? null : WebUtility.HtmlEncode(unsubscribeUrl);
        var unsubscribeHtml = unsubscribeHref is null
            ? ""
            : $"""<p style="margin:16px 0 0;font-size:12px;color:#9197a3;"><a href="{unsubscribeHref}" style="color:#9197a3;">Unsubscribe</a> from this kind of email.</p>""";

        // Inline colours are the tokens from ui/app/globals.css. Email clients will not load
        // that stylesheet, so the values are copied here: --background #282c34, --surface
        // #21252b, --border #3e4451, --foreground #abb2bf, --muted #9197a3, --bounty #e5c07b.
        // Layout matches the auth cards (login / reset / verify): square corners, king
        // wordmark, full-width bounty CTA. The site uses Tabler's chess-king icon; most
        // email clients will not render that SVG, so this is the Unicode king in bounty
        // colour - the same piece, not a CSS diamond.
        var html = $"""
            <!doctype html>
            <html lang="en">
              <head>
                <meta charset="utf-8" />
                <meta name="viewport" content="width=device-width, initial-scale=1" />
                <meta name="color-scheme" content="dark" />
                <meta name="supported-color-schemes" content="dark" />
              </head>
              <body style="margin:0;padding:24px;background:#282c34;color:#abb2bf;font-family:Montserrat,Arial,Helvetica,sans-serif;">
                <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0">
                  <tr>
                    <td align="center">
                      <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="max-width:384px;background:#21252b;border:1px solid #3e4451;">
                        <tr>
                          <td style="padding:32px;color:#abb2bf;font-size:14px;line-height:1.6;">
                            <table role="presentation" cellpadding="0" cellspacing="0" border="0" align="center" style="margin:0 auto 32px;">
                              <tr>
                                <td style="padding-right:12px;vertical-align:middle;font-size:24px;line-height:1;color:#e5c07b;">♔</td>
                                <td style="font-size:24px;font-weight:700;letter-spacing:0.1em;color:#ffffff;vertical-align:middle;">KIRICHESS</td>
                              </tr>
                            </table>
                            <p style="margin:0 0 28px;">{intro}</p>
                            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="margin:0 0 28px;">
                              <tr>
                                <td>
                                  <a href="{href}" style="display:block;padding:12px 24px;background:#e5c07b;color:#282c34;font-weight:700;text-decoration:none;text-align:center;">{action}</a>
                                </td>
                              </tr>
                            </table>
                            <p style="margin:0 0 28px;font-size:13px;color:#9197a3;">
                              If the button does not work, paste this link into your browser:<br />
                              <a href="{href}" style="color:#e5c07b;text-decoration:underline;word-break:break-all;"><span style="color:#e5c07b;">{href}</span></a>
                            </p>
                            <p style="margin:0;font-size:13px;color:#9197a3;">{footer}</p>
                            {unsubscribeHtml}
                          </td>
                        </tr>
                      </table>
                    </td>
                  </tr>
                </table>
              </body>
            </html>
            """;

        // M-5-1/M-5-2: the header target is the one-click URL when one exists (Api:BaseUrl configured),
        // else the same footer link a human reads - never both, so List-Unsubscribe never names two
        // http(s) targets. Only the former is allowed to carry List-Unsubscribe-Post.
        var headerUrl = oneClickUnsubscribeUrl ?? unsubscribeUrl;
        return new EmailMessage(
            toEmail, subject, text, html,
            UnsubscribeHeaderUrl: headerUrl,
            UnsubscribeIsOneClick: oneClickUnsubscribeUrl is not null,
            ReplyTo: replyTo);
    }
}
