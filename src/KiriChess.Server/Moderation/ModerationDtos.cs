using KiriChess.Server.Data.Entities;
using KiriChess.Server.Infrastructure.OpenApi;

namespace KiriChess.Server.Moderation;

/// <summary>A sanction as seen by a moderator. Carries everything, including the note the sanctioned player never sees.</summary>
public sealed record SanctionDto(
    Guid Id, Guid UserId, string UserDisplayName,
    [property: StringEnum(typeof(SanctionType))] string Type,   // "Warning" | "RatedPlayBan" | "AccountBan"
    string Reason,                 // shown to the sanctioned player
    string? ModeratorNote,         // NEVER shown to the sanctioned player; admin routes only
    Guid IssuedByUserId, string IssuedByDisplayName, DateTimeOffset IssuedAt,
    DateTimeOffset? ExpiresAt,     // null = permanent
    DateTimeOffset? LiftedAt, Guid? LiftedByUserId, string? LiftReason,
    Guid? ReportId,
    bool IsActive);                // LiftedAt is null && (ExpiresAt is null || ExpiresAt > now)

/// <summary>What the sanctioned player themselves sees. No note, no issuer, no report id.</summary>
public sealed record MySanctionDto(
    [property: StringEnum(typeof(SanctionType))] string Type,
    string Reason, DateTimeOffset IssuedAt, DateTimeOffset? ExpiresAt);

/// <summary>
/// A report as seen by a moderator - the queue row.
/// <see cref="OpenReportsAgainstReportedUser"/> is the repeat-offender signal: the count of DISTINCT
/// reporters with an open report against this user, not the count of open report rows (S-3-4 - one
/// account filing several reports against the same target, one per game, must not read the same as
/// several different people complaining).
/// </summary>
public sealed record ModerationReportDto(
    Guid Id,
    Guid ReporterId, string ReporterDisplayName,
    Guid ReportedUserId, string ReportedUserDisplayName,
    Guid? GameId,
    [property: StringEnum(typeof(ReportReason))] string Reason,
    string? Details,
    [property: StringEnum(typeof(ReportStatus))] string Status,
    string? ModeratorNote, Guid? ReviewedByUserId, DateTimeOffset? ReviewedAt,
    DateTimeOffset CreatedAt,
    int OpenReportsAgainstReportedUser);   // distinct-reporter repeat-offender signal, straight in the queue row

/// <summary>
/// The moderator's dossier on one account: identity, ratings, activity counts, sanction history.
/// <paramref name="GraceDisconnects"/> sums this account's per-seat counts: times they dropped with
/// no connection left, including returns inside the grace window. It is not an input to any
/// cooldown or sanction.
/// </summary>
public sealed record ModerationUserDto(
    Guid UserId, string DisplayName, string Email, DateTimeOffset CreatedAt,
    bool EmailConfirmed,
    ModerationRatingDto[] Ratings,
    int GamesPlayed, int GamesAbandoned,
    int ReportsAgainst, int ReportsFiled,
    SanctionDto[] Sanctions,
    int GraceDisconnects);

public sealed record ModerationRatingDto(
    string Pool, double Value, double RatingDeviation, int GamesPlayed, bool IsProvisional);

/// <summary>
/// <see cref="IssuedByUserId"/> is never taken from this body - it comes from the caller's own claims.
/// <see cref="Reason"/> is shown to the sanctioned player verbatim (B-2-5): do not name or sign the
/// moderator in it - use <see cref="ModeratorNote"/> for anything that should stay moderator-only.
/// </summary>
public sealed record CreateSanctionRequest(
    Guid UserId, [property: StringEnum(typeof(SanctionType))] string Type, string Reason,
    string? ModeratorNote, DateTimeOffset? ExpiresAt, Guid? ReportId);

/// <summary><see cref="Reason"/> is player-visible, the same rule as <see cref="CreateSanctionRequest.Reason"/> (B-2-5).</summary>
public sealed record LiftSanctionRequest(string Reason);

/// <summary>
/// <see cref="Status"/> must parse to Reviewed or Dismissed - Open is rejected, un-resolving is not a
/// workflow. <c>[StringEnum]</c> documents the full <c>ReportStatus</c> vocabulary regardless (Phase
/// 9d.7, Task C): the document cannot express "two of these three names", and the 400 the endpoint
/// returns for Open is the real enforcement, not the document.
/// </summary>
public sealed record ResolveReportRequest(
    [property: StringEnum(typeof(ReportStatus))] string Status, string? ModeratorNote);

/// <summary>
/// L-2-3 / L-4-2: operator-invoked erasure for a banned account's Art. 17 request, or an under-16
/// account on credible notice. Distinct from the self-serve phrase so a stolen admin session cannot
/// be confused with a player deleting themselves.
/// </summary>
public sealed record EraseAccountRequest(string Acknowledgement);
