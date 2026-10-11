using KiriChess.Server.Infrastructure.OpenApi;

namespace KiriChess.Server.Retention;

/// <summary>Why an account is in the dormant report. Stable machine names - the UI keys off these.</summary>
public static class DormancyReasons
{
    /// <summary>Registered, never confirmed the email address, never played. Nothing was ever done with it.</summary>
    public const string NeverActivated = "never-activated";
    /// <summary>Confirmed, but has never played a single game.</summary>
    public const string NeverPlayed    = "never-played";
    /// <summary>Played, then stopped - last game is older than the window.</summary>
    public const string Inactive       = "inactive";
}

/// <summary>
/// One account in the dormant report. Carries NO email address - see README scope decision 8. The
/// (userId, displayName) pair matches what DisplayNameScanRowDto already returns to the same admin
/// surface; a moderator who needs the email uses /api/moderation/users/{userId:guid}.
/// </summary>
public sealed record DormantAccountRowDto(
    Guid UserId,
    string DisplayName,
    // [StringConstants] states this to the compiler and the OpenAPI document (Phase 9d.7, Task C):
    // DormancyReasons is a static class of const strings, not an enum, so [StringEnum] cannot describe
    // it - see StringConstantsAttribute for why that attribute exists.
    [property: StringConstants(typeof(DormancyReasons))] string Reason,
    DateTimeOffset CreatedAt,
    bool EmailConfirmed,
    int GamesPlayed,
    DateTimeOffset? LastGameAt,
    bool HasSanctions);             // true blocks nothing; it is a "look before you act" flag

/// <summary>
/// The dormant-account report. Counts and rows only - this endpoint deletes nothing and this phase
/// builds no reaper (README scope decision 2).
/// </summary>
public sealed record DormantAccountScanDto(
    DateTimeOffset GeneratedAt,
    int InactiveDays,
    int TotalAccounts,      // live accounts considered (tombstones excluded)
    int DormantCount,       // how many matched, across the whole scan - NOT the page size
    bool Truncated,         // a cap was hit; DormantCount is a floor, not a total
    IReadOnlyList<DormantAccountRowDto> Rows);
