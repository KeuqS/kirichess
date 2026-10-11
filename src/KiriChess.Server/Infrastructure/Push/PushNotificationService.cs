using System.Text.Json;
using KiriChess.Server.Data;
using KiriChess.Server.Infrastructure.Email;
using Microsoft.EntityFrameworkCore;

namespace KiriChess.Server.Infrastructure.Push;

/// <summary>
/// Sends a web push notification to every subscription a user holds (11b.1 follow-up, the piece the
/// roadmap note left out of 11b.1 itself: "push/live is for while they are still there", where email
/// is for after they have left). A singleton that opens its own scope per call, the same shape as
/// <c>RematchNotificationService</c>, so a caller with no scope of its own (<c>GameHub</c>) can fire
/// this without waiting on it.
///
/// Unlike the delayed rematch-offer mail, push is sent immediately at the moment of the trigger event -
/// the whole reason to reach for push instead of (or alongside) email is that it can reach someone who
/// is still on the site, in a different tab, the instant something happens.
/// </summary>
public sealed class PushNotificationService
{
    private static readonly JsonSerializerOptions PayloadJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly VapidOptions _vapid;
    private readonly ILogger<PushNotificationService> _logger;

    public PushNotificationService(IServiceScopeFactory scopeFactory, VapidOptions vapid, ILogger<PushNotificationService> logger)
    {
        _scopeFactory = scopeFactory;
        _vapid = vapid;
        _logger = logger;
    }

    /// <summary>
    /// "Your challenge was accepted" (9e.5's pairing-found trigger). Callers may await this directly -
    /// it never throws - or discard it for fire-and-forget, same as <see cref="NotifyRematchOffered"/>.
    ///
    /// M-5-3: gated on <see cref="NotificationCategory.PairingFound"/>, the same preference the mail
    /// half already honours (<c>PairingNotificationService</c>). This was the half C-6-3 left open -
    /// only the rematch trigger moved onto a preference at the time - so unsubscribing from
    /// "your opponent is ready" mail no longer leaves the matching push arriving anyway.
    /// </summary>
    public Task NotifyChallengeAccepted(Guid challengerId, Guid gameId) =>
        SendToUserAsync(
            challengerId, "Your opponent is ready", "Your challenge was accepted - the game is underway.",
            $"/game/{gameId}", NotificationCategory.PairingFound);

    /// <summary>
    /// "Your opponent wants a rematch" (11a.1's offer trigger). Deliberately not routed through
    /// <c>RematchNotificationService</c>'s delayed check: see this class's own doc comment.
    ///
    /// C-6-3: gated on <see cref="NotificationCategory.RematchOffered"/>, the same preference the mail
    /// half already honours. Before this, unsubscribing from rematch notices silenced the mail and left
    /// the push arriving, which is the worse half to leave on - it is the one that lights up a phone.
    /// A reader who turned this category off asked not to be told about rematch offers, not to be told
    /// about them by one transport.
    /// </summary>
    public Task NotifyRematchOffered(Guid finishedGameId, Guid opponentId) =>
        SendToUserAsync(
            opponentId, "A rematch is waiting", "Your last opponent wants to play again.",
            $"/game/{finishedGameId}", NotificationCategory.RematchOffered);

    /// <summary>
    /// No-op when push is not configured (<see cref="VapidOptions.Enabled"/> is false), the user has
    /// unsubscribed from <paramref name="category"/>, or the user has no subscriptions. Never throws:
    /// a failed or partially-failed send must not take down the caller, exactly the reasoning
    /// <c>PairingNotificationService.NotifyChallengeAcceptedAsync</c>'s own catch-all already documents.
    ///
    /// <paramref name="category"/> stays nullable for any future trigger this method grows that has no
    /// stored preference to check (there is none today - both current callers pass one, M-5-3).
    /// Checked inside the one scope this method already opens, so honouring a preference costs no
    /// extra scope.
    /// </summary>
    private async Task SendToUserAsync(
        Guid userId, string title, string body, string url,
        NotificationCategory? category = null, CancellationToken ct = default)
    {
        if (!_vapid.Enabled) return;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
            var sender = scope.ServiceProvider.GetRequiredService<IPushSender>();

            if (category is { } gated)
            {
                var preferences = scope.ServiceProvider.GetRequiredService<NotificationPreferenceService>();
                if (!await preferences.IsSubscribedAsync(userId, gated, ct)) return;
            }

            var subscriptions = await db.WebPushSubscriptions.Where(s => s.UserId == userId).ToListAsync(ct);
            if (subscriptions.Count == 0) return;

            // JsonSerializerDefaults.Web (camelCase) so sw.js's push handler - plain JS reading
            // data.title, data.body, data.url - gets the property names it expects without a mismatch.
            var payload = JsonSerializer.Serialize(new PushPayload(title, body, url), PayloadJsonOptions);

            var gone = new List<Guid>();
            foreach (var subscription in subscriptions)
            {
                var outcome = await sender.SendAsync(subscription, payload, ct);
                if (outcome == PushSendOutcome.Gone) gone.Add(subscription.Id);
            }

            // Pruning gone subscriptions is what keeps this list from growing forever with dead
            // endpoints from uninstalled browsers/revoked permissions - the same cleanup a push sender
            // is always expected to do, not specific to any one trigger.
            if (gone.Count > 0)
                await db.WebPushSubscriptions.Where(s => gone.Contains(s.Id)).ExecuteDeleteAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send push notification to user {UserId}.", userId);
        }
    }

    /// <summary>The wire shape <c>sw.js</c>'s <c>push</c> handler parses. Kept intentionally tiny: a title, a body, and where to go on click.</summary>
    private sealed record PushPayload(string Title, string Body, string Url);
}
