namespace KiriChess.Server.Moderation.Content;

/// <summary>
/// Stable machine names for why a candidate was refused. The player sees
/// <see cref="DisplayNameVerdict.Message"/>, never these; these exist so the UI can style a field and
/// the moderator scan can group rows.
/// </summary>
public static class DisplayNameRejection
{
    public const string TooShort           = "too-short";
    public const string TooLong            = "too-long";
    public const string IllegalCharacter   = "illegal-character";
    public const string SeparatorPlacement = "separator-placement";
    public const string NotEnoughSubstance = "not-enough-substance";
    public const string Reserved           = "reserved";
    public const string Profanity          = "profanity";
    public const string Taken              = "taken";
    public const string Cooldown           = "cooldown";
}

/// <summary>
/// The result of checking one candidate name. <see cref="Message"/> is player-facing and MUST NOT
/// echo a blocklist term (README scope decision 2). <see cref="Normalized"/> is the identity key and
/// is the empty string when the candidate has no alphanumeric substance at all.
/// <see cref="LengthLimit"/> carries the configured number behind <see cref="Message"/> for the two
/// rejections that quote one (TooShort: MinLength, TooLong: MaxLength) so a translated client can
/// rebuild the sentence in the player's language instead of echoing server-side English; null for
/// every other rejection and when Allowed.
/// </summary>
public sealed record DisplayNameVerdict(
    bool Allowed,
    string? Rejection,      // one of DisplayNameRejection; null when Allowed
    string Message,
    string Normalized,
    int? LengthLimit = null);

/// <summary>One row of the moderator display-name scan. Admin-only.</summary>
public sealed record DisplayNameScanRowDto(
    Guid UserId,
    string DisplayName,
    string StoredNormalized,
    string RecomputedNormalized,
    bool NormalizedDrift,               // stored != recomputed, the weak SQL backfill (scope decision 5)
    string? Rejection,                  // why this name fails policy today; null when it passes
    string[] CollidesWithDisplayNames,  // other accounts sharing RecomputedNormalized
    DateTimeOffset CreatedAt);

public sealed record ChangeDisplayNameRequest(string DisplayName);

/// <summary><see cref="Reason"/> is stamped on the moderator's audit trail, not shown to the player.</summary>
public sealed record ModeratorRenameRequest(string DisplayName, string Reason);

public sealed record DisplayNameResponse(string DisplayName, DateTimeOffset ChangedAt);
