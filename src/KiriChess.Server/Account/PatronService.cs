using KiriChess.Server.Data;
using KiriChess.Server.Games;
using Microsoft.EntityFrameworkCore;

namespace KiriChess.Server.Account;

public enum PatronGrantOutcome { Granted, NoReference, NoUser, NoTier }

/// <summary>
/// The whole patron-badge feature, deliberately Stripe-free: it takes plain values
/// (a reference, an amount, a subscription flag/id, a customer id), never a Stripe object, so it is
/// testable without a webhook payload. A donation is a random single-use reference row, not the
/// account id, so Stripe never receives a KiriChess identifier. A one-off badge expires
/// (<see cref="PatronOptions.OneTimeGrantMonths"/>); a monthly one lasts until Stripe says the
/// subscription ended.
/// </summary>
public sealed class PatronService(KiriChessDbContext db, PatronOptions options, IGameClock clock, ILogger<PatronService> logger)
{
    /// <summary>
    /// Redeems a donation reference and grants (or extends) the badge it points at. Never throws:
    /// an anonymous, expired or already-used reference is a normal, supported outcome, not an error.
    /// </summary>
    public async Task<PatronGrantOutcome> GrantAsync(
        string? reference, long amountTotalCents, bool isSubscription, string? subscriptionId,
        string? customerId, CancellationToken ct)
    {
        var now = clock.UtcNow;

        if (string.IsNullOrEmpty(reference))
        {
            logger.LogInformation("Checkout completed with no donation reference; nothing to grant.");
            return PatronGrantOutcome.NoReference;
        }

        // $1-5: consume is the conditional write. Two in-flight deliveries of the same event used
        // to both pass `ConsumedAt is not null` and both grant, stacking a one-off to twenty-four
        // months. Zero rows affected means missing, expired, or already taken.
        var claimed = await db.DonationReferences
            .Where(r => r.Reference == reference && r.ConsumedAt == null && r.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.ConsumedAt, now), ct);
        if (claimed == 0)
        {
            logger.LogInformation("Donation reference {Reference} is missing, consumed or expired; nothing to grant.", reference);
            return PatronGrantOutcome.NoReference;
        }

        var row = await db.DonationReferences.AsNoTracking()
            .FirstAsync(r => r.Reference == reference, ct);

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == row.UserId, ct);
        if (user is null || user.DeletedAt is not null)
            return PatronGrantOutcome.NoUser;

        var tier = PatronTiers.FromAmountCents(amountTotalCents);
        if (tier == PatronTier.None)
            return PatronGrantOutcome.NoTier;

        // Never downgrade an active badge: a Queen monthly patron who also drops a 5 EUR one-off
        // must not be demoted to Pawn by their own generosity.
        var current = PatronBadge.For(user, now);
        var resolvedTier = current != PatronTier.None && current > tier ? current : tier;

        user.IsPatron = true;
        user.PatronTier = resolvedTier;
        user.PatronSince ??= now;
        user.StripeCustomerId = customerId ?? user.StripeCustomerId;

        if (isSubscription)
        {
            user.PatronKind = PatronKind.Monthly;
            user.StripeSubscriptionId = subscriptionId;
            // PatronExpiresAt is deliberately left alone rather than nulled. A Monthly badge does not
            // expire (PatronBadge.For ignores the column for Monthly), so the value is invisible while
            // the subscription lives, and any one-off life already banked there has been paid for and
            // must survive to be fallen back on if the subscription ends.
        }
        else
        {
            // Two one-off donations stack the expiry rather than one overwriting the other, and a
            // one-off bought during a subscription banks its life the same way. It stays invisible
            // until RevokeBySubscriptionAsync falls back to it, which is the point: the donor paid for
            // it, so cancelling the subscription must not silently discard it.
            var baseline = user.PatronExpiresAt is { } existing && existing > now ? existing : now;
            user.PatronExpiresAt = baseline.AddMonths(options.OneTimeGrantMonths);

            // An active subscription outranks a one-off, so the kind only moves when there is no
            // subscription to outrank it.
            if (user.PatronKind != PatronKind.Monthly)
                user.PatronKind = PatronKind.OneTime;
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Granted patron badge to user {UserId}: tier {Tier}, kind {Kind}.",
            user.Id, user.PatronTier, user.PatronKind);
        return PatronGrantOutcome.Granted;
    }

    /// <summary>
    /// Ends the subscription half of an account's badge. A subscription that predates this feature, or
    /// one already revoked, is not an error, just a no-op.
    ///
    /// This is not simply <see cref="Clear"/>: a one-off donation made while the subscription was live
    /// banked its own expiry in <c>PatronExpiresAt</c> (see <see cref="GrantAsync"/>), and that donor
    /// paid for time they have not used yet. So the account falls back to whatever one-off life
    /// remains, as a <see cref="PatronKind.OneTime"/> badge, and only clears outright when there is
    /// none.
    /// </summary>
    public async Task<bool> RevokeBySubscriptionAsync(string subscriptionId, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.StripeSubscriptionId == subscriptionId, ct);
        if (user is null)
        {
            logger.LogInformation("No account holds subscription {SubscriptionId}; nothing to revoke.", subscriptionId);
            return false;
        }

        var now = clock.UtcNow;
        if (user.IsPatron && user.PatronExpiresAt is { } remaining && remaining > now)
        {
            user.PatronKind = PatronKind.OneTime;
            user.StripeSubscriptionId = null;
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "Subscription {SubscriptionId} ended for user {UserId}; badge falls back to a one-off expiring {ExpiresAt}.",
                subscriptionId, user.Id, remaining);
            return true;
        }

        Clear(user);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Revoked patron badge for user {UserId} (subscription {SubscriptionId} ended).", user.Id, subscriptionId);
        return true;
    }

    /// <summary>
    /// Clears every patron-badge column without saving, so <see cref="AccountDeletionService"/> can
    /// call it inside its own transaction. StripeCustomerId is deliberately left alone here; the
    /// caller (erasure) scrubs it itself alongside the rest of the identifying columns.
    /// </summary>
    public static void Clear(Data.Entities.ApplicationUser user)
    {
        user.IsPatron = false;
        user.PatronTier = PatronTier.None;
        user.PatronKind = PatronKind.None;
        user.PatronExpiresAt = null;
        user.PatronSince = null;
        user.StripeSubscriptionId = null;
    }
}
