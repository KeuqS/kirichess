namespace KiriChess.Server.Retention;

/// <summary>Stable machine names for the swept rules. The metric tag and the log field use these.</summary>
public static class RetentionRules
{
    public const string RefreshTokens = "refresh-tokens";
    public const string ExternalLoginCodes = "external-login-codes";
    public const string TwoFactorChallenges = "two-factor-challenges";
    public const string Challenges = "challenges";
    public const string ApiTokens = "api-tokens";
    public const string BanEvasionMarkers = "ban-evasion-markers";
}

/// <summary>One swept category: what it is, how long it is kept past expiry, and why.</summary>
public sealed record RetentionRule(string Name, TimeSpan GracePastExpiry, string Reason);

/// <summary>How many rows one rule removed in one sweep.</summary>
public sealed record RetentionRuleResult(string Name, int RowsDeleted);

/// <summary>The outcome of one sweep. Returned by RetentionSweep.RunAsync so a test can assert on it.</summary>
public sealed record RetentionSweepResult(
    DateTimeOffset RanAt,
    IReadOnlyList<RetentionRuleResult> Rules)
{
    public int TotalRowsDeleted => Rules.Sum(r => r.RowsDeleted);
}

/// <summary>
/// The declared retention schedule (Phase 8a.3). A list rather than a hard-coded pair of queries so
/// that adding a category is one record and one delete in RetentionSweep, and so the cutoff
/// arithmetic - the part that can be wrong by a factor of a thousand - is testable with literal
/// values and no database. docs/decisions/RETENTION.md is this table in prose.
///
/// Only categories that are actually SWEPT appear here. Games, rating history, reports and sanctions
/// are kept indefinitely by decision, not by omission; that decision lives in PRIVACY.md section 5
/// and must not be re-litigated by adding a rule for them here.
/// </summary>
public static class RetentionSchedule
{
    public static IReadOnlyList<RetentionRule> From(RetentionOptions options) =>
    [
        new RetentionRule(
            RetentionRules.RefreshTokens,
            TimeSpan.FromDays(options.RefreshTokenGraceDays),
            "Measured from ExpiresAt, never RevokedAt: a rotated row is the only thing that lets "
            + "RefreshTokenService.RotateAsync tell a replayed stolen token from an unknown one, and "
            + "deleting it early downgrades theft detection to a silent 401."),

        new RetentionRule(
            RetentionRules.ExternalLoginCodes,
            TimeSpan.FromDays(options.ExternalCodeGraceDays),
            "A code lives 60 seconds and ConsumeAsync returns null identically for an unknown code "
            + "and a spent one, so a spent row carries no detection value at all - unlike a refresh "
            + "token. The grace only has to outlast clock skew."),

        new RetentionRule(
            RetentionRules.TwoFactorChallenges,
            TimeSpan.FromDays(options.TwoFactorChallengeGraceDays),
            "A ticket lives five minutes and ResolveAsync returns null identically for an unknown "
            + "ticket and a consumed one, so a spent row carries no detection value either - same "
            + "argument as external-login-codes. The grace only has to outlast clock skew."),

        new RetentionRule(
            RetentionRules.Challenges,
            TimeSpan.FromDays(options.ChallengeGraceDays),
            "Phase 9d.8. A challenge link lives an hour and a spent row (accepted, cancelled or simply "
            + "timed out) carries no detection value once resolved - same argument as the other three "
            + "rows here. The grace is longer than theirs (7 days, not 1) because an accepted "
            + "challenge is the audit trail for how a GAME came to exist, and games are kept "
            + "indefinitely; the extra days give that trail room to outlast a support question, not "
            + "just clock skew."),

        new RetentionRule(
            RetentionRules.ApiTokens,
            TimeSpan.FromDays(options.ApiTokenGraceDays),
            "Phase 9f.5. API tokens do not rotate, so a revoked row is not a theft tripwire the way "
            + "a refresh token is. Sweep revoked tokens, and tokens whose ExpiresAt has passed, after "
            + "the grace. S-1-4: IssueAsync always writes ExpiresAt (default 90 days). A null ExpiresAt "
            + "is not a date the grace can be measured from, so the sweep still leaves one in place."),

        new RetentionRule(
            RetentionRules.BanEvasionMarkers,
            TimeSpan.FromDays(options.BanEvasionMarkerGraceDays),
            "S-3-1. The one rule here that clears a COLUMN rather than deleting a row: it nulls "
            + "ApplicationUser.BanEvasionEmailHash on tombstones whose account ban stopped being "
            + "active (lifted, or expired) longer ago than the grace. The marker is a pseudonymous "
            + "identifier retained under Art. 17(3)(e) for exactly as long as the ban it enforces, "
            + "and an identifier kept past its purpose is a data-protection problem of its own. "
            + "Nothing waits on this sweep for correctness - registration is refused only while the "
            + "ban is still active, which is a query-time condition."),
    ];

    /// <summary>
    /// The instant before which a row matching this rule is removable: rows are compared on their own
    /// ExpiresAt, so the window is (token lifetime) + (grace), not (grace) alone. Pure - `now` is
    /// supplied by the caller so a test never has to sleep.
    /// </summary>
    public static DateTimeOffset CutoffFor(RetentionRule rule, DateTimeOffset now) => now - rule.GracePastExpiry;
}
