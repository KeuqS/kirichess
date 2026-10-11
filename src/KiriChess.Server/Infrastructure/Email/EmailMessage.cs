namespace KiriChess.Server.Infrastructure.Email;

/// <summary>
/// One transactional email, ready to hand to a provider. Multipart by construction: every mail
/// client renders one of the two parts, and a text/plain alternative is also what keeps a message
/// with a single prominent link out of the spam folder.
/// </summary>
/// <param name="ToEmail">Single recipient. Nothing here sends bulk mail, so there is no list.</param>
/// <param name="Subject">Subject line.</param>
/// <param name="TextBody">text/plain alternative. Must contain the action URL in full.</param>
/// <param name="HtmlBody">text/html alternative.</param>
/// <param name="UnsubscribeHeaderUrl">
/// M-5-1: the URL <see cref="ResendEmailSender"/> puts in the <c>List-Unsubscribe</c> header, distinct
/// from the human-facing footer link baked into <see cref="TextBody"/>/<see cref="HtmlBody"/>. Null for
/// the two security-critical mails, same as the footer link.
/// </param>
/// <param name="UnsubscribeIsOneClick">
/// M-5-2: true only when <see cref="UnsubscribeHeaderUrl"/> is
/// <c>POST /api/notifications/unsubscribe/one-click</c> - a URL that actually answers an RFC 8058
/// bodyless/form POST. Governs whether <see cref="ResendEmailSender"/> also sends
/// <c>List-Unsubscribe-Post</c>; sending that header next to a URL that cannot satisfy it would be a
/// promise the server breaks (see M-5-2's own finding).
/// </param>
/// <param name="ReplyTo">
/// M-5-5: reach-back mail invites a reply ("wants to play again"); null keeps today's behaviour
/// (no Reply-To header, provider defaults to <see cref="EmailOptions.FromHeader"/>).
/// </param>
public sealed record EmailMessage(
    string ToEmail,
    string Subject,
    string TextBody,
    string HtmlBody,
    string? UnsubscribeHeaderUrl = null,
    bool UnsubscribeIsOneClick = false,
    string? ReplyTo = null);

/// <summary>
/// Hands a <see cref="EmailMessage"/> off for delivery. Phase 8a.1 replaced the direct-send
/// contract with a hand-off contract on purpose: the production implementation
/// (<see cref="QueuedEmailSender"/>) enqueues and returns immediately, so completing this task means
/// "accepted for delivery", never "delivered". Callers must not report success to a user on the
/// strength of it.
/// </summary>
public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken ct = default);
}

/// <summary>
/// Fallback <see cref="IEmailSender"/> for any deploy with no provider configured, and the only
/// sender in local development. Logs the plain-text part, which by contract contains the full
/// confirmation/reset URL - so a developer with no Resend key copies the link straight out of the
/// console and the whole auth lifecycle stays exercisable offline.
/// </summary>
public sealed class LoggingEmailSender : IEmailSender
{
    private readonly ILogger<LoggingEmailSender> _logger;
    public LoggingEmailSender(ILogger<LoggingEmailSender> logger) => _logger = logger;

    public Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        _logger.LogInformation("Email to {ToEmail} - {Subject}\n{Body}",
            message.ToEmail, message.Subject, message.TextBody);
        return Task.CompletedTask;
    }
}
