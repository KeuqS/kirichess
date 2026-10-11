using KiriChess.Server.Data.Entities;
using KiriChess.Server.Infrastructure.Email;
using KiriChess.Server.Infrastructure.OpenApi;

namespace KiriChess.Server.Account;

/// <summary>
/// The Phase 8a.2 data-portability payload (GDPR Art. 20). Serialised straight to the response as a
/// file download.
///
/// Two rules govern every record in this file, and both exist because "everything about me" is not
/// the same request as "every row that mentions me" - see README scope decision 5:
///   1. Nothing here identifies another user beyond their display name, which is already public on
///      /leaderboards, every game page and every replay.
///   2. No moderator note and no credential material appears anywhere in it, ever.
/// </summary>
public sealed record AccountExportDto(
    string ExportVersion,
    DateTimeOffset GeneratedAt,
    AccountExportProfileDto Profile,
    IReadOnlyList<AccountExportRatingDto> Ratings,
    IReadOnlyList<AccountExportRatingHistoryDto> RatingHistory,
    IReadOnlyList<AccountExportGameDto> Games,
    IReadOnlyList<AccountExportReportDto> ReportsFiled,
    IReadOnlyList<AccountExportReportAgainstDto> ReportsAgainst,
    IReadOnlyList<AccountExportSanctionDto> Sanctions,
    IReadOnlyList<AccountExportSessionDto> Sessions,
    IReadOnlyList<AccountExportApiTokenDto> ApiTokens,
    AccountExportTruncationDto Truncation);

public sealed record AccountExportProfileDto(
    Guid UserId, string? Email, bool EmailConfirmed, string DisplayName,
    DateTimeOffset CreatedAt, DateTimeOffset? DisplayNameChangedAt,
    DateTimeOffset? TermsAcceptedAt, string? TermsVersion, DateTimeOffset? AgeConfirmedAt,
    bool TwoFactorEnabled,                                    // <- 9d.2
    DateTimeOffset? PairingFoundEmailUnsubscribedAt,          // <- 11b.1
    DateTimeOffset? RematchOfferedEmailUnsubscribedAt,        // <- 11b.1
    bool IsPatron,                                            // <- patron badge
    [property: StringEnum(typeof(PatronTier))] string PatronTier,
    [property: StringEnum(typeof(PatronKind))] string PatronKind,
    DateTimeOffset? PatronSince,
    DateTimeOffset? PatronExpiresAt,
    string[] Roles,
    string? AvatarJpegBase64);

public sealed record AccountExportRatingDto(
    string Pool, double Rating, double RatingDeviation, double Volatility,
    int GamesPlayed, DateTimeOffset UpdatedAt);

public sealed record AccountExportRatingHistoryDto(
    string Pool, Guid GameId, double Rating, double RatingDeviation, DateTimeOffset CreatedAt);

/// <summary>
/// One game the user played. <paramref name="OpponentDisplayName"/> is the ONLY thing said about the
/// other player - no id, no email. Moves are the full ply list: it is the user's own game record and
/// the replay page already publishes it. <paramref name="DisconnectCount"/> is this player's own
/// seat: times they dropped with no connection left. The opponent's count is not in this file.
/// </summary>
// 9d.7: Colour is deliberately NOT [StringEnum(typeof(Side))] - AccountExportBuilder writes it as the
// literal lowercase "white"/"black" (a display-export convention), not Side.ToString() ("White"/
// "Black"). Documenting it as the Side enum's own casing would describe a value this endpoint never
// actually sends - see the phase README/plan note on leaving non-enum-derived strings alone.
public sealed record AccountExportGameDto(
    Guid GameId, string Colour, string OpponentDisplayName,
    [property: StringEnum(typeof(GameResult))] string Result,
    [property: StringEnum(typeof(TerminationReason))] string Termination,
    string? RatingPool, bool IsRated, int PlyCount, string StartFen,
    IReadOnlyList<string> MovesUci, DateTimeOffset CreatedAt, DateTimeOffset? EndedAt,
    int DisconnectCount);

/// <summary>A report the user filed. Theirs, so it is complete - minus the moderator's note.</summary>
public sealed record AccountExportReportDto(
    Guid ReportId, Guid? GameId,
    [property: StringEnum(typeof(ReportReason))] string Reason,
    string? Details,
    [property: StringEnum(typeof(ReportStatus))] string Status,
    DateTimeOffset CreatedAt, DateTimeOffset? ResolvedAt);

/// <summary>
/// A report filed AGAINST the user. Deliberately narrower than the record above: no reporter id and
/// no reporter name. The accusation concerns this user; who made it is that person's data, and
/// exporting it turns the report queue into a retaliation tool.
/// </summary>
public sealed record AccountExportReportAgainstDto(
    Guid ReportId, Guid? GameId,
    [property: StringEnum(typeof(ReportReason))] string Reason,
    [property: StringEnum(typeof(ReportStatus))] string Status,
    DateTimeOffset CreatedAt);

/// <summary>
/// A sanction against the user. Reason is included - they are entitled to know why. ModeratorNote is
/// not, which is the same withholding SanctionDto already does on the 8.5 player-facing surface.
/// IssuedByUserId is not, because which moderator acted is not this user's personal data.
/// </summary>
public sealed record AccountExportSanctionDto(
    Guid SanctionId, [property: StringEnum(typeof(SanctionType))] string Type, string Reason, DateTimeOffset IssuedAt,
    DateTimeOffset? ExpiresAt, DateTimeOffset? LiftedAt);

/// <summary>
/// A refresh-token session. Timestamps only. TokenHash is NEVER exported - it is credential material,
/// and a credential in a file the user downloads and forwards is a credential leak.
/// </summary>
public sealed record AccountExportSessionDto(
    DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, DateTimeOffset? RevokedAt);

/// <summary>
/// One API token. Timestamps, name, prefix and scopes only. TokenHash is NEVER exported, same
/// credential-material rule as <see cref="AccountExportSessionDto"/>.
/// </summary>
public sealed record AccountExportApiTokenDto(
    string Name, string TokenPrefix, IReadOnlyList<string> Scopes,
    DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt, DateTimeOffset? RevokedAt, DateTimeOffset? LastUsedAt);

/// <summary>One active API token as listed on the profile page. The raw secret is never included.</summary>
public sealed record ApiTokenDto(
    Guid Id, string Name, string TokenPrefix, IReadOnlyList<string> Scopes,
    DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt, DateTimeOffset? LastUsedAt);

/// <summary>Create-token response: the list shape plus the raw secret, shown once.</summary>
public sealed record CreatedApiTokenDto(
    Guid Id, string Name, string TokenPrefix, IReadOnlyList<string> Scopes,
    DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt, DateTimeOffset? LastUsedAt, string Token);

/// <summary>
/// Create-token request. <paramref name="Scopes"/> is <c>read</c> and/or <c>play</c>.
/// S-1-2: <paramref name="Password"/> is required when the account has one; Google-only mint skips.
/// S-1-4: <paramref name="ExpiresInDays"/> omitted means 90 days. Allowed range is 1 through 365.
/// </summary>
public sealed record CreateApiTokenRequest(
    string Name, IReadOnlyList<string> Scopes, string? Password, int? ExpiresInDays = null);

/// <summary>
/// Whether any collection hit its cap. A silently truncated export is worse than none; one that says
/// so is fine. Caps are in AccountExportBuilder.
/// </summary>
public sealed record AccountExportTruncationDto(
    bool GamesTruncated, int GamesCap, bool RatingHistoryTruncated, int RatingHistoryCap);

/// <summary>
/// The delete confirmation. <paramref name="Acknowledgement"/> must be the exact string
/// <c>DELETE MY ACCOUNT</c> - a deliberate speed bump on an irreversible, un-undoable action, and the
/// server's own check rather than something only the UI enforces. S-1-2:
/// <paramref name="Password"/> is required when the account has one; Google-only erasure skips so
/// Art. 17 stays reachable.
/// </summary>
public sealed record DeleteAccountRequest(string Acknowledgement, string? Password);

public sealed record DeleteAccountResponse(DateTimeOffset DeletedAt);

/// <summary>One mail category (Phase 11b.1) and whether this account currently receives it.</summary>
public sealed record NotificationCategoryPreferenceDto(
    [property: StringEnum(typeof(NotificationCategory))] string Category, bool Subscribed);

/// <summary><c>GET /api/account/notifications</c>'s body: every category this account can opt out of, current state.</summary>
public sealed record NotificationPreferencesDto(IReadOnlyList<NotificationCategoryPreferenceDto> Categories);

/// <summary><c>PUT /api/account/notifications</c>'s body: one category, its new state. One toggle per request, matching one click in the profile UI.</summary>
public sealed record UpdateNotificationPreferenceRequest(
    [property: StringEnum(typeof(NotificationCategory))] string Category, bool Subscribed);

/// <summary>
/// The account's current avatar. <paramref name="AvatarUrl"/> is a same-API path with a cache-bust
/// query, or null for the initials fallback. It is never an off-site URL.
/// </summary>
public sealed record AvatarResponse(string? AvatarUrl);

/// <summary>
/// <c>GET /api/account/email</c>'s body: the caller's own address and where it stands with the
/// confirmed-email play gate.
/// </summary>
/// <param name="PlayBlocked">
/// Whether the gate is actually stopping this account from starting a game right now. Not the same as
/// <c>!Confirmed</c>: the gate is off by default, and an account created before the cutoff is
/// grandfathered, so an unconfirmed address is usually not a block. This is
/// <c>PlayEligibilityPolicy</c>'s own answer, so the banner cannot disagree with the play paths.
/// </param>
public sealed record EmailStatusDto(string? Email, bool Confirmed, bool PlayBlocked);
