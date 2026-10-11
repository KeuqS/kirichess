namespace KiriChess.Server.Retention;

/// <summary>Binds to the <c>Retention</c> configuration section (Phase 8a.3).</summary>
public sealed class RetentionOptions
{
    public const string SectionName = "Retention";

    /// <summary>Master switch. On by default: the sweep only removes credential rows that are already expired.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Hours between sweeps. One a day is ample for two tables whose windows are measured in weeks.</summary>
    public int SweepIntervalHours { get; set; } = 24;

    /// <summary>
    /// Delay before the first sweep, so a boot is not competing with migrations, the leaderboard
    /// warmup and game recovery for the same connection pool.
    /// </summary>
    public int InitialDelaySeconds { get; set; } = 60;

    /// <summary>
    /// Days past a refresh token's OWN expiry before its row is removed. Measured from ExpiresAt and
    /// never from RevokedAt - see Phase 8a.3 ground truth 2. Shortening this below
    /// Jwt:RefreshTokenExpiryDays would disarm reuse detection for still-live token families.
    /// </summary>
    public int RefreshTokenGraceDays { get; set; } = 30;

    /// <summary>
    /// Days past an external login code's expiry before its row is removed. A code lives 60 seconds
    /// and a consumed one is indistinguishable from an unknown one, so this only needs to outlast
    /// clock skew and a support question.
    /// </summary>
    public int ExternalCodeGraceDays { get; set; } = 1;

    /// <summary>
    /// Days past a 2FA login challenge ticket's expiry before its row is removed (Phase 9d.2). A
    /// ticket lives five minutes and a consumed one is indistinguishable from an unknown one to
    /// TwoFactorChallengeService.ResolveAsync, so this only needs to outlast clock skew, same
    /// reasoning as ExternalCodeGraceDays.
    /// </summary>
    public int TwoFactorChallengeGraceDays { get; set; } = 1;

    /// <summary>
    /// Days past a direct-challenge link's expiry before its row is removed (Phase 9d.8). Longer than
    /// the other three grace periods (7, not 1) because an accepted challenge is the audit trail for
    /// how a game came to exist, and games are kept indefinitely - the extra days give that trail room
    /// to outlast a support question, not just clock skew.
    /// </summary>
    public int ChallengeGraceDays { get; set; } = 7;

    /// <summary>
    /// Days past an API token's own expiry or revocation before its row is removed (Phase 9f.5).
    /// Active never-expiring tokens are not swept.
    /// </summary>
    public int ApiTokenGraceDays { get; set; } = 30;

    /// <summary>
    /// Days past the instant a tombstone's account ban stopped being active (lifted, or expired)
    /// before the ban-evasion marker on that row is cleared (S-3-1). Not measured from the erasure:
    /// the marker's purpose is the ban, so its life is the ban's.
    ///
    /// A grace at all, rather than clearing the moment the ban goes inactive, because the query-time
    /// check already stops refusing registrations on the same request the ban is lifted - nothing
    /// waits on this sweep for correctness. What the window buys is a mistaken lift being reversible
    /// for as long as PRIVACY.md section 5 says a lifted sanction stays a candidate for human review.
    /// </summary>
    public int BanEvasionMarkerGraceDays { get; set; } = 30;
}
