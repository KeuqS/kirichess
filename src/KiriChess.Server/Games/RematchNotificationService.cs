using System.Collections.Concurrent;
using KiriChess.Server.Data;
using KiriChess.Server.Infrastructure.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KiriChess.Server.Games;

/// <summary>
/// Emails the other player when a rematch offer (Phase 11a.1) is still open after a delay (Phase
/// 11b.1). Not a hosted service: each offer arms its own one-shot timer, exactly like
/// <see cref="MatchNoShowWatcher"/>. The hosted <see cref="RematchNotificationSweeper"/> is the
/// other half (P-3-7): it flushes rows whose due-at survived a restart inside the delay window.
///
/// Delayed rather than sent the instant <see cref="GameHub.OfferRematch"/> succeeds, and this is the
/// whole point of the design. Both players are, by construction, still on the finished game's own page
/// at the moment an offer is made - that is what "just finished a game together" means - so mailing
/// immediately would notify someone almost certainly still looking at the "Accept rematch" button, the
/// exact opposite of what a reach-back channel exists for (see 11b.1's roadmap note: email is for
/// after the opponent has left, push/live is for while they are still there). Waiting a short window
/// and re-checking whether the offer is still unaccepted turns "no response yet" into a real away
/// signal, without a general online-presence system this codebase does not have - unlike a live game,
/// a finished one has no <see cref="GameConnectionTracker"/> entry to check.
/// </summary>
public sealed class RematchNotificationService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IEmailSender _email;
    private readonly EmailOptions _options;
    private readonly UnsubscribeTokenService _unsubscribeTokens;
    private readonly IConfiguration _configuration;
    private readonly ILogger<RematchNotificationService> _logger;

    public RematchNotificationService(
        IServiceScopeFactory scopeFactory, IEmailSender email, EmailOptions options,
        UnsubscribeTokenService unsubscribeTokens, IConfiguration configuration,
        ILogger<RematchNotificationService> logger)
    {
        _scopeFactory = scopeFactory;
        _email = email;
        _options = options;
        _unsubscribeTokens = unsubscribeTokens;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// How long an <see cref="Arm"/> is remembered for de-duplication, and therefore the longest a
    /// pending or completed send occupies a slot. Matches <see cref="RematchService"/>'s own offer
    /// window: past it the offer is no longer live, so <see cref="PeekOpenOfferAsync"/> would refuse
    /// it anyway and forgetting the entry cannot re-open the multiplier this dictionary exists to
    /// close. Kept as a local constant rather than read from ChallengeOptions because being wrong in
    /// the loose direction here costs one pruned entry, not a duplicate mail.
    /// </summary>
    private static readonly TimeSpan ArmRetention = TimeSpan.FromHours(2);

    /// <summary>
    /// The ceiling on remembered arms, and with it the ceiling on detached sends in flight (P-3-7).
    /// Each entry is two Guids and a timestamp, so this is a few hundred kilobytes at worst, and it
    /// is far above any honest offer rate: it is a backstop against a pathological caller, not a
    /// product limit. Dropping past it is correct - the send that is refused is, by construction,
    /// the ten-thousand-and-first in one window.
    /// </summary>
    private const int MaxArmed = 10_000;

    /// <summary>
    /// One entry per <c>(finishedGameId, opponentId)</c> that has been armed. Retained after the send
    /// completes rather than removed: removing on completion would let the next offer arm again,
    /// which is the exact multiplication this guards. Pruned by age instead, see
    /// <see cref="ArmRetention"/>.
    /// </summary>
    private readonly ConcurrentDictionary<(Guid GameId, Guid OpponentId), DateTimeOffset> _armed = new();

    /// <summary>
    /// Arms the delayed check for one rematch offer, at most once per <c>(game, opponent)</c> pair.
    /// Fire-and-forget by design, the caller (<see cref="GameHub.OfferRematch"/>) must not await this.
    ///
    /// The de-duplication is the second of two guards, not the only one: <c>OfferRematch</c> already
    /// refuses a repeat offer, so in normal operation this is never reached twice for one pair. It is
    /// here because the consequence of the two disagreeing is a duplicate notification to someone who
    /// did not ask for one, and because it is also what bounds the number of detached
    /// <see cref="RunAsync"/> tasks that can exist at once (P-3-7) - before it, nothing did.
    ///
    /// A new offer also persists <see cref="Data.Entities.RematchOffer.NotificationDueAt"/> so a restart inside the
    /// delay window can still send; the in-process delay remains so the common no-restart path does
    /// not wait for the sweep tick.
    /// </summary>
    public void Arm(Guid finishedGameId, Guid opponentId)
    {
        var delaySeconds = _configuration.GetValue("Rematch:NotifyDelaySeconds", 90);
        if (delaySeconds <= 0) return; // 0 disables the notifier

        PruneExpiredArms();

        var now = DateTimeOffset.UtcNow;
        if (_armed.Count >= MaxArmed)
        {
            _logger.LogWarning(
                "Refusing to arm a rematch notification for game {GameId}: {Count} already armed.",
                finishedGameId, _armed.Count);
            return;
        }

        if (!_armed.TryAdd((finishedGameId, opponentId), now)) return;

        _ = RunAsync(finishedGameId, opponentId, delaySeconds);
    }

    /// <summary>
    /// Drops arms older than <see cref="ArmRetention"/>. Opportunistic, on the arming path, rather
    /// than on a timer: this class has no lifecycle of its own (it is a singleton nothing starts or
    /// stops), and the only way the dictionary grows is a call to <see cref="Arm"/>, so the arming
    /// path is the only place a sweep is ever needed.
    /// </summary>
    private void PruneExpiredArms()
    {
        var cutoff = DateTimeOffset.UtcNow - ArmRetention;
        foreach (var (key, armedAt) in _armed)
        {
            if (armedAt < cutoff) _armed.TryRemove(key, out _);
        }
    }

    private async Task RunAsync(Guid finishedGameId, Guid opponentId, int delaySeconds)
    {
        try
        {
            // Persist first so a crash during the delay still leaves an owed row for the sweep.
            // A persist fault must not skip the in-process send: that path is how the no-restart
            // case has always worked.
            try { await PersistDueAtAsync(finishedGameId, delaySeconds); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to persist rematch notification due-at for game {GameId}.", finishedGameId);
            }

            await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
            await TrySendAsync(finishedGameId, opponentId);
        }
        catch (Exception ex)
        {
            // A throw here must never take the process down, this runs detached from any request -
            // same reasoning as MatchNoShowWatcher.RunAsync's own catch-all.
            _logger.LogWarning(ex, "Failed to send rematch-offer notification for game {GameId}.", finishedGameId);
        }
    }

    /// <summary>
    /// Records when this offer's delayed mail becomes sendable. Wall clock, not <see cref="IGameClock"/>:
    /// this singleton does not take the clock today, and DueAt is a persisted instant (the clock's
    /// own rule for UtcNow).
    /// </summary>
    private async Task PersistDueAtAsync(Guid finishedGameId, int delaySeconds)
    {
        var dueAt = DateTimeOffset.UtcNow.AddSeconds(delaySeconds);
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        await db.RematchOffers
            .Where(r => r.GameId == finishedGameId && r.NotificationDueAt == null && r.AcceptedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.NotificationDueAt, dueAt));
    }

    /// <summary>
    /// Sends the rematch-offer mail for one still-open offer, at most once. Shared by the in-process
    /// delay and the durable sweep so a restart cannot double-mail: claiming
    /// <see cref="Data.Entities.RematchOffer.NotificationSentAt"/> is an <c>ExecuteUpdateAsync</c> that only the
    /// first caller wins. Checks (still open, confirmed, subscribed) run before the claim so a skip
    /// does not burn the send marker.
    /// </summary>
    public async Task TrySendAsync(Guid finishedGameId, Guid opponentId, CancellationToken ct = default)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
            var rematches = scope.ServiceProvider.GetRequiredService<RematchService>();
            var preferences = scope.ServiceProvider.GetRequiredService<NotificationPreferenceService>();

            // Still open, and still acceptable by this exact opponent - the identical read
            // GameHub.AcceptRematch itself does, reused rather than re-derived. Covers "already
            // accepted", "opponent isn't actually a player here" (unreachable in practice, kept for
            // the same reason PeekOpenOfferAsync itself keeps the check) and "offer no longer exists".
            if (await rematches.PeekOpenOfferAsync(finishedGameId, opponentId, ct) is null)
            {
                await ClearDueAtIfUnsentAsync(db, finishedGameId, ct);
                return;
            }

            var recipient = await db.Users
                .Where(u => u.Id == opponentId)
                .Select(u => new { u.Email, u.EmailConfirmed })
                .FirstOrDefaultAsync(ct);
            if (recipient?.Email is null || !recipient.EmailConfirmed)
            {
                await ClearDueAtIfUnsentAsync(db, finishedGameId, ct);
                return;
            }
            if (!await preferences.IsSubscribedAsync(opponentId, NotificationCategory.RematchOffered, ct))
            {
                await ClearDueAtIfUnsentAsync(db, finishedGameId, ct);
                return;
            }

            var now = DateTimeOffset.UtcNow;
            var claimed = await db.RematchOffers
                .Where(r => r.GameId == finishedGameId && r.NotificationSentAt == null && r.AcceptedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.NotificationSentAt, now), ct);
            if (claimed == 0) return;

            var unsubscribeUrl = UnsubscribeLink.Build(_options, _unsubscribeTokens, opponentId, NotificationCategory.RematchOffered);
            var oneClickUrl = UnsubscribeLink.BuildOneClick(_options, _unsubscribeTokens, opponentId, NotificationCategory.RematchOffered);
            try
            {
                await _email.SendAsync(
                    EmailTemplates.RematchOffered(_options, recipient.Email, finishedGameId, unsubscribeUrl, oneClickUrl),
                    ct);
            }
            catch
            {
                // Claim-before-send is what stops a timer and a sweep from both mailing. If the
                // provider throws, unclaim so the next sweep can retry; leaving SentAt set would
                // turn a transient send fault into a permanently dropped mail.
                await db.RematchOffers
                    .Where(r => r.GameId == finishedGameId && r.NotificationSentAt == now)
                    .ExecuteUpdateAsync(s => s.SetProperty(r => r.NotificationSentAt, (DateTimeOffset?)null), ct);
                throw;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send rematch-offer notification for game {GameId}.", finishedGameId);
        }
    }

    /// <summary>
    /// Sends every still-open offer whose delay has elapsed and whose mail has not gone out. Used by
    /// <see cref="RematchNotificationSweep"/> and by tests; the in-process timer does not wait for
    /// this. No-ops when <c>Rematch:NotifyDelaySeconds</c> is 0.
    /// </summary>
    public async Task FlushOwedAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var sweep = scope.ServiceProvider.GetRequiredService<RematchNotificationSweep>();
        await sweep.RunAsync(ct);
    }

    /// <summary>
    /// Drops the owed marker so the sweep does not retry a skip (unsubscribed, unconfirmed, gone)
    /// every tick. Leaves <see cref="Data.Entities.RematchOffer.NotificationSentAt"/> null: nothing was mailed.
    /// </summary>
    internal static Task<int> ClearDueAtIfUnsentAsync(
        KiriChessDbContext db, Guid finishedGameId, CancellationToken ct) =>
        db.RematchOffers
            .Where(r => r.GameId == finishedGameId && r.NotificationSentAt == null && r.AcceptedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.NotificationDueAt, (DateTimeOffset?)null), ct);
}
