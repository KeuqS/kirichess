using KiriChess.Engine.Config;
using KiriChess.Server.Data;
using KiriChess.Server.Infrastructure.Email;
using KiriChess.Server.Infrastructure.Push;
using Microsoft.EntityFrameworkCore;

namespace KiriChess.Server.Games;

/// <summary>
/// Phase 9e.5. Emails the challenger when their challenge link is accepted while they are not
/// watching for it. <see cref="ChallengeEndpoints"/> already pushes "ChallengeAccepted" over
/// SignalR to the <c>challenge:{token}</c> group, but that only reaches a connection currently
/// subscribed to it - and the entire point of a shareable link is that the challenger is often gone
/// by the time a friend clicks it. Scoped like <see cref="ChallengeService"/>: only ever called from
/// a request handler.
///
/// Deliberately not wired into the matchmaking queue (<c>GameHub.JoinQueue</c>): queue membership
/// requires a live connection (<c>MatchmakingService.Leave</c> runs on disconnect), so both sides of
/// a queue match are, by construction, connected at the moment SignalR's "MatchFound" already fires
/// - unlike a challenge link, which can sit unopened for the challenge's full TTL. Emailing on every
/// queue match would be noise, not signal.
///
/// Best-effort: a lookup or send failure here must never fail the pairing itself, so
/// <see cref="NotifyChallengeAcceptedAsync"/> swallows and logs rather than throwing.
/// </summary>
public sealed class PairingNotificationService
{
    private readonly KiriChessDbContext _db;
    private readonly IEmailSender _email;
    private readonly EmailOptions _options;
    private readonly NotificationPreferenceService _preferences;
    private readonly UnsubscribeTokenService _unsubscribeTokens;
    private readonly PushNotificationService _push;
    private readonly ILogger<PairingNotificationService> _logger;

    public PairingNotificationService(
        KiriChessDbContext db, IEmailSender email, EmailOptions options,
        NotificationPreferenceService preferences, UnsubscribeTokenService unsubscribeTokens,
        PushNotificationService push, ILogger<PairingNotificationService> logger)
    {
        _db = db;
        _email = email;
        _options = options;
        _preferences = preferences;
        _unsubscribeTokens = unsubscribeTokens;
        _push = push;
        _logger = logger;
    }

    /// <summary>
    /// Notifies <paramref name="challengerId"/> that their challenge was accepted and
    /// <paramref name="gameId"/> is underway. Silently does nothing for an unconfirmed or missing
    /// email - same gate <c>/resend-confirmation</c> uses - since this is a convenience mail, not a
    /// security-critical one, and there is no reason to mail an address nobody has proven ownership of.
    /// Also silently does nothing once the recipient has unsubscribed from
    /// <see cref="NotificationCategory.PairingFound"/> (Phase 11b.1).
    ///
    /// Push (11b.1 follow-up) is attempted unconditionally alongside mail, not gated behind the same
    /// email-confirmed/preference checks: a browser push subscription is its own opt-in (the user only
    /// has one if they granted notification permission and this site registered it), unlike mail there
    /// is no unconfirmed-address or category-preference concept to check first.
    /// </summary>
    public async Task NotifyChallengeAcceptedAsync(Guid challengerId, Guid gameId, string pool, CancellationToken ct = default)
    {
        try
        {
            await _push.NotifyChallengeAccepted(challengerId, gameId);

            var challenger = await _db.Users
                .Where(u => u.Id == challengerId)
                .Select(u => new { u.Email, u.EmailConfirmed })
                .FirstOrDefaultAsync(ct);
            if (challenger?.Email is null || !challenger.EmailConfirmed) return;
            if (!await _preferences.IsSubscribedAsync(challengerId, NotificationCategory.PairingFound, ct)) return;

            var unsubscribeUrl = UnsubscribeLink.Build(_options, _unsubscribeTokens, challengerId, NotificationCategory.PairingFound);
            var oneClickUrl = UnsubscribeLink.BuildOneClick(_options, _unsubscribeTokens, challengerId, NotificationCategory.PairingFound);
            await _email.SendAsync(
                EmailTemplates.PairingFound(
                    _options, challenger.Email, gameId, KiriChessConfig.PoolLabel(pool), rated: false, unsubscribeUrl,
                    oneClickUrl),
                ct);
        }
        catch (Exception ex)
        {
            // Same reasoning as EmailDispatchService's catch-all: one failed lookup or send must
            // never take the accept endpoint down with it, the game itself already exists.
            _logger.LogWarning(ex, "Failed to send pairing notification to challenger {ChallengerId}.", challengerId);
        }
    }
}
