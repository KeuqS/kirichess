namespace KiriChess.Server.Infrastructure.Email;

/// <summary>
/// Drains <see cref="QueuedEmailSender"/> and delivers each message through
/// <see cref="ResendEmailSender"/>, retrying the failures worth retrying (Phase 8a.1). Registered
/// only when <see cref="EmailOptions.Enabled"/>.
/// </summary>
public sealed class EmailDispatchService : BackgroundService
{
    private readonly QueuedEmailSender _queue;
    private readonly ResendEmailSender _sender;
    private readonly EmailOptions _options;
    private readonly ILogger<EmailDispatchService> _logger;

    public EmailDispatchService(
        QueuedEmailSender queue,
        ResendEmailSender sender,
        EmailOptions options,
        ILogger<EmailDispatchService> logger)
    {
        _queue = queue;
        _sender = sender;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var message in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await DeliverAsync(message, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // One malformed message must never take the pump down: the next item in the queue is
                // a different user's signup, and a dead pump fails silently and permanently.
                _logger.LogError(ex, "Unexpected failure dispatching an email with subject {Subject}.", message.Subject);
            }
        }
    }

    private async Task DeliverAsync(EmailMessage message, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await _sender.SendAsync(message, ct);
                if (attempt > 1)
                    _logger.LogInformation("Email {Subject} delivered on attempt {Attempt}.", message.Subject, attempt);
                return;
            }
            catch (EmailDeliveryException ex) when (ex.Retryable && attempt < _options.MaxAttempts)
            {
                // 1s, 2s, 4s, ... - a provider blip is measured in seconds, and what is queued behind
                // this message is a handful of signups, not a batch job.
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt - 1));
                _logger.LogWarning(ex,
                    "Email {Subject} attempt {Attempt} of {MaxAttempts} failed; retrying in {DelaySeconds}s.",
                    message.Subject, attempt, _options.MaxAttempts, delay.TotalSeconds);
                await Task.Delay(delay, ct);
            }
            catch (EmailDeliveryException ex)
            {
                _logger.LogError(ex,
                    "Giving up on email {Subject} after {Attempt} attempt(s); it will not be retried.",
                    message.Subject, attempt);
                return;
            }
        }
    }
}
