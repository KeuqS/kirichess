using System.Threading.Channels;

namespace KiriChess.Server.Infrastructure.Email;

/// <summary>
/// The <see cref="IEmailSender"/> the endpoints actually get when a provider is configured
/// (Phase 8a.1). It writes to a bounded in-process channel and returns synchronously, so
/// <c>POST /api/auth/register</c> never waits on - and never fails because of - a third party.
/// <see cref="EmailDispatchService"/> drains it.
///
/// The queue is deliberately not durable: its contents are lost on restart. The alternative is an
/// outbox table and a delivery-state machine, which is a lot of machinery to guarantee something the
/// product already solves - /api/auth/resend-confirmation and /api/auth/forgot-password both exist
/// and both cost the user one click. Revisit only if restarts ever become frequent enough to matter.
/// </summary>
public sealed class QueuedEmailSender : IEmailSender
{
    private readonly Channel<EmailMessage> _channel;
    private readonly ILogger<QueuedEmailSender> _logger;

    public QueuedEmailSender(EmailOptions options, ILogger<QueuedEmailSender> logger)
    {
        _logger = logger;
        // DropWrite rather than Wait: back-pressure here would push the wait back into the request
        // thread, which is exactly the coupling this class exists to remove. A full queue means the
        // provider has been unreachable for a long time, and the honest response is to shed the
        // message loudly rather than to slow every signup down behind it.
        _channel = Channel.CreateBounded<EmailMessage>(new BoundedChannelOptions(options.QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false
        });
    }

    /// <summary>Read side, for <see cref="EmailDispatchService"/> and for tests.</summary>
    internal ChannelReader<EmailMessage> Reader => _channel.Reader;

    public Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        // The recipient is left out of this log line on purpose: it is PII, and the subject already
        // identifies which of the two mails was lost.
        if (!_channel.Writer.TryWrite(message))
            _logger.LogWarning("Email queue is full; dropped a message with subject {Subject}.", message.Subject);

        return Task.CompletedTask;
    }
}
