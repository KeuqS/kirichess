using Microsoft.AspNetCore.Identity;

namespace KiriChess.Server.Data.Entities;

/// <summary>KiriChess user identity + profile. Keyed by <see cref="Guid"/>.</summary>
public sealed class ApplicationUser : IdentityUser<Guid>
{
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Phase 8a.4 identity key for <see cref="DisplayName"/>, see DisplayNameNormalizer. Carries the
    /// unique index that makes impersonation by lookalike name impossible.
    /// </summary>
    public string NormalizedDisplayName { get; set; } = string.Empty;

    /// <summary>Null means the name has never been changed since the account was created.</summary>
    public DateTimeOffset? DisplayNameChangedAt { get; set; }

    /// <summary>
    /// Cropped square JPEG bytes, or null for the initials fallback. Never a URL: the public
    /// path is always <c>/api/users/{id}/avatar</c>, and only <see cref="Account.AvatarService"/>
    /// writes this column after <see cref="Account.JpegAvatar"/> accepts the body.
    /// </summary>
    public byte[]? AvatarImage { get; set; }

    /// <summary>
    /// When <see cref="AvatarImage"/> was last replaced. Null means initials. The public URL's
    /// <c>v</c> query is this instant so a cached JPEG is not served after a change.
    /// </summary>
    public DateTimeOffset? AvatarUpdatedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Phase 8a.2. Non-null means this account has been erased at the owner's request: the row
    /// survives as a tombstone so that games, rating history, reports and sanctions keep a valid
    /// foreign key, but every identifying column on it has been scrubbed. See
    /// Phase 8a.2 scope decision 1 for the exact column-by-column contract.
    ///
    /// This is also the filter on the NormalizedDisplayName unique index, every tombstone shares the
    /// key "deletedaccount", which only works because deleted rows are not in that index.
    /// </summary>
    public DateTimeOffset? DeletedAt { get; set; }

    /// <summary>
    /// S-3-1. A keyed HMAC-SHA256 of this account's <c>NormalizedEmail</c>, written by
    /// <c>AccountDeletionService</c> immediately before that column is nulled, and <b>only</b> when a
    /// full account ban was active at that instant. Null on every live account, on every tombstone
    /// that was not banned, and on every tombstone erased while no marker secret was configured.
    ///
    /// It is what makes TERMS § 6's "deleting your account and registering again cannot be used to
    /// launder a ban" true: <c>/api/auth/register</c> hashes the candidate address the same way and
    /// refuses on a match. See <see cref="KiriChess.Server.Moderation.BanEvasionMarker"/> for the
    /// derivation, the re-identification argument, and the lifetime rules; it is not reversible to an
    /// address, and the retention sweep clears it once the ban behind it has been inactive for the
    /// grace period.
    /// </summary>
    public string? BanEvasionEmailHash { get; set; }

    /// <summary>
    /// Phase 8a.2 consent record. Null means the account predates the policy (README scope decision
    /// 7), it does not mean the terms were refused, and it must not be read as a reason to block
    /// anything. Written by the register path and by external-login account creation (Task B).
    /// </summary>
    public DateTimeOffset? TermsAcceptedAt { get; set; }

    /// <summary>
    /// Which version of the terms was accepted, a plain date string such as "2026-08-06", matching
    /// the `Last updated` line in docs/legal/TERMS.md. Exists so a future re-consent pass can find
    /// everyone still on an older version; nothing reads it yet.
    /// </summary>
    public string? TermsVersion { get; set; }

    /// <summary>
    /// When the account holder declared they meet the minimum age. Deliberately a timestamp and not a
    /// date of birth, README scope decision 6 explains why storing a real DOB was rejected.
    /// </summary>
    public DateTimeOffset? AgeConfirmedAt { get; set; }

    /// <summary>
    /// Whether the account currently shows a patron badge. Written together with
    /// <see cref="PatronTier"/>, <see cref="PatronKind"/>, <see cref="PatronExpiresAt"/> and
    /// <see cref="StripeSubscriptionId"/> by <see cref="Account.PatronService"/>, never independently.
    /// A raw read of this column is still not "does the badge show right now": see
    /// <see cref="Account.PatronBadge"/> for the one place that answers that.
    /// </summary>
    public bool IsPatron { get; set; }

    /// <summary>
    /// When the account first became a patron. Kept across a later top-up or renewal
    /// (<see cref="Account.PatronService.GrantAsync"/> writes it with <c>??=</c>), and cleared on
    /// revoke, so it reads as "since" for the current badge, not a lifetime-first-ever date.
    /// </summary>
    public DateTimeOffset? PatronSince { get; set; }

    /// <summary>
    /// Which donation thank-you tier the account currently holds. Public surfaces
    /// show one heart regardless of this value. <see cref="PatronTier.None"/> whenever
    /// <see cref="IsPatron"/> is false; the two are written together and never independently.
    /// </summary>
    public Account.PatronTier PatronTier { get; set; } = Account.PatronTier.None;

    /// <summary>Whether the badge came from a single payment or from a live subscription.</summary>
    public Account.PatronKind PatronKind { get; set; } = Account.PatronKind.None;

    /// <summary>
    /// When a one-off donation's badge lapses. Null means it does not lapse on a timer, which is the
    /// case for every <see cref="PatronKind.Monthly"/> grant: those end when Stripe says the
    /// subscription ended, not on a date known in advance.
    ///
    /// Read it, never sweep it. A lapsed badge is a row whose expiry is in the past, and every read
    /// path filters on that. There is deliberately no background job: a badge that has lapsed is
    /// invisible from the instant it lapses, without a sweep having had to run.
    /// </summary>
    public DateTimeOffset? PatronExpiresAt { get; set; }

    /// <summary>
    /// The Stripe subscription this badge hangs on, for <see cref="PatronKind.Monthly"/> only. It is
    /// the entire revoke path: <c>customer.subscription.deleted</c> arrives carrying nothing but the
    /// subscription id, and this column is how that id finds an account. Unique among non-null
    /// values, so two accounts cannot share one subscription.
    /// </summary>
    public string? StripeSubscriptionId { get; set; }

    /// <summary>
    /// The Stripe customer behind the payment. Nothing reads it today; it is stored so a support
    /// question ("I cancelled and still have the badge") is answerable without guessing, and so a
    /// future billing-portal link has something to open. Scrubbed on erasure like every other
    /// identifying column.
    /// </summary>
    public string? StripeCustomerId { get; set; }

    /// <summary>
    /// Phase 11b.1 per-category mail preference, modelled the same way as the 8a.2 consent record
    /// above: null (the default) means subscribed, a timestamp records the instant the user opted
    /// out, not merely a bare bool, so a dispute over "did I unsubscribe" has an answer. Gates
    /// <see cref="Games.PairingNotificationService"/>'s "your challenge was accepted" mail.
    /// </summary>
    public DateTimeOffset? PairingFoundEmailUnsubscribedAt { get; set; }

    /// <summary>Same shape as <see cref="PairingFoundEmailUnsubscribedAt"/>, for the Phase 11b.1 "your opponent wants a rematch" mail.</summary>
    public DateTimeOffset? RematchOfferedEmailUnsubscribedAt { get; set; }
}
