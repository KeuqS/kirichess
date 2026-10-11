namespace KiriChess.Server.Data.Entities;

/// <summary>
/// What a sanction actually does. Deliberately three members and no more - every one of them has an
/// enforcement point in this task, and an enum member with nothing enforcing it is a lie in a dropdown.
/// </summary>
public enum SanctionType
{
    /// <summary>Recorded and shown to the player. Changes nothing about what they can do.</summary>
    Warning,
    /// <summary>Blocked from rated matchmaking. Unrated and custom games still work.</summary>
    RatedPlayBan,
    /// <summary>Cannot log in, refresh a token, queue, or create a game.</summary>
    AccountBan
}

/// <summary>
/// A moderator action against one account (Phase 8.5). Rows are never deleted: lifting a sanction sets
/// <see cref="LiftedAt"/> and leaves the row, because the row history is the audit record of what the
/// moderator did and when - it is not an appeal path (S-3-3 corrected this comment: it previously
/// claimed the row history filled that gap, and it does not - it is the moderator's own record of
/// their own action, not a channel the sanctioned player can use). The actual, deliberately minimal
/// appeal route is the contact address <c>AuthEndpoints.BanMessage</c> now points a banned
/// player at. No detector ever writes one of these - detection is advisory (see
/// Phase 8.5 scope decision 1) and a human's id is always in
/// <see cref="IssuedByUserId"/>.
/// </summary>
public sealed class UserSanction
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }
    public ApplicationUser? User { get; set; }

    public SanctionType Type { get; set; }

    /// <summary>
    /// Shown to the sanctioned player verbatim. Write it as if they will read it, because they will.
    ///
    /// B-2-5: do not name or sign the moderator here. The structural design already withholds the
    /// moderator's identity from the sanctioned player (<c>MySanctionDto</c> carries no issuer, no
    /// note, no id), but this field is free text written by the one person who is both the only
    /// moderator and the named controller (see <c>docs/legal/PRIVACY.md</c> §1) - "warned by Michiel
    /// for..." or a signed reason re-opens by hand the identity the API withholds by construction.
    /// <see cref="ModeratorNote"/> already exists for anything that should stay moderator-only.
    /// </summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>Moderator-only context. Never leaves an admin route - see the MySanctionDto contract.</summary>
    public string? ModeratorNote { get; set; }

    public Guid IssuedByUserId { get; set; }
    public ApplicationUser? IssuedBy { get; set; }
    public DateTimeOffset IssuedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Null means permanent. A past value means expired, and expiry is evaluated at read time - there is no sweeper.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    public DateTimeOffset? LiftedAt { get; set; }
    public Guid? LiftedByUserId { get; set; }

    /// <summary>
    /// Same player-visible exposure as <see cref="Reason"/>, and the same rule (B-2-5): do not name
    /// or sign the moderator here either.
    /// </summary>
    public string? LiftReason { get; set; }

    /// <summary>The report that prompted this, when there was one. Nullable: a moderator can act without a report.</summary>
    public Guid? ReportId { get; set; }
    public Report? Report { get; set; }

    /// <summary>
    /// Active means unlifted and unexpired. Evaluated against a caller-supplied instant rather than
    /// <c>UtcNow</c> so tests can pin expiry behaviour without sleeping. Not mapped - EF must never try
    /// to translate this; every query filters on the columns directly.
    /// </summary>
    public bool IsActiveAt(DateTimeOffset now)
        => LiftedAt is null && (ExpiresAt is null || ExpiresAt > now);
}
