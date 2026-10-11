using KiriChess.Server.Data.Entities;

namespace KiriChess.Server.Infrastructure.Push;

/// <summary>What happened when <see cref="IPushSender.SendAsync"/> tried to deliver one message.</summary>
public enum PushSendOutcome
{
    /// <summary>Accepted by the push service.</summary>
    Sent,

    /// <summary>
    /// The push service reports the subscription no longer exists (HTTP 404/410) - the browser
    /// unsubscribed, the profile was removed, or the endpoint simply expired.
    /// <see cref="PushNotificationService"/> deletes the row on this outcome, exactly the pruning a
    /// push sender is expected to do.
    /// </summary>
    Gone,

    /// <summary>Any other failure (network, malformed payload, provider outage). The row is kept: this is not evidence the subscription is dead.</summary>
    Failed,
}

/// <summary>
/// Sends one payload to one browser subscription. Its own interface, the same reason
/// <c>IEmailSender</c> exists rather than calling a third-party client directly: it gives
/// <c>PushNotificationServiceTests</c> a fake to substitute, since there is no way to safely exercise a
/// real push service (FCM/Mozilla autopush) from an automated test.
/// </summary>
public interface IPushSender
{
    Task<PushSendOutcome> SendAsync(WebPushSubscription subscription, string payloadJson, CancellationToken ct = default);
}

/// <summary>
/// The real <see cref="IPushSender"/>, a thin adapter over <c>WebPush.WebPushClient</c>. Only ever
/// registered when <see cref="VapidOptions.Enabled"/> is true - see <c>Program.cs</c>.
/// </summary>
public sealed class WebPushSender : IPushSender
{
    private readonly WebPush.WebPushClient _client = new();
    private readonly WebPush.VapidDetails _vapid;
    private readonly ILogger<WebPushSender> _logger;

    public WebPushSender(VapidOptions options, ILogger<WebPushSender> logger)
    {
        _vapid = new WebPush.VapidDetails(options.Subject, options.PublicKey, options.PrivateKey);
        _logger = logger;
    }

    public async Task<PushSendOutcome> SendAsync(WebPushSubscription subscription, string payloadJson, CancellationToken ct = default)
    {
        var pushSubscription = new WebPush.PushSubscription(subscription.Endpoint, subscription.P256dh, subscription.Auth);
        try
        {
            await _client.SendNotificationAsync(pushSubscription, payloadJson, _vapid, cancellationToken: ct);
            return PushSendOutcome.Sent;
        }
        catch (WebPush.WebPushException ex) when (
            ex.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Gone)
        {
            return PushSendOutcome.Gone;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Push send failed for subscription {SubscriptionId}.", subscription.Id);
            return PushSendOutcome.Failed;
        }
    }
}
