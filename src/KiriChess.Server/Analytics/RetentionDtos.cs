namespace KiriChess.Server.Analytics;

/// <summary>One account's signup, projected straight out of EF. Kept separate from
/// <see cref="UserFact"/> because this is what the query can express and that is what the maths needs.</summary>
public readonly record struct SignupFact(Guid UserId, DateTimeOffset SignedUpAt);

/// <summary>
/// One game, flattened to its two seats and its start time. Both seats travel together (rather than
/// one row per participant) because the fan-out has to dedupe a self-paired game, and that is a
/// decision for the pure code, not for SQL.
/// </summary>
public readonly record struct GameParticipationFact(
    Guid WhitePlayerId,
    Guid BlackPlayerId,
    DateTimeOffset StartedAt);

/// <summary>
/// One account, flattened to the timestamps the funnel needs. Built in memory from two capped
/// queries (see RetentionAnalytics.BuildFacts) rather than a correlated subquery per user.
///
/// UserId exists only to group games onto accounts. It is NEVER serialised - see README scope
/// decision 7, which is what makes counting erased-account tombstones acceptable.
/// </summary>
public readonly record struct UserFact(
    Guid UserId,
    DateTimeOffset SignedUpAt,
    DateTimeOffset? FirstGameAt,
    DateTimeOffset? SecondGameAt,
    DateTimeOffset? LastGameAt,
    int GamesPlayed);

/// <summary>
/// One horizon on the retention curve. <see cref="Percent"/> is null - never zero - when
/// <see cref="Eligible"/> is 0, because a cohort too young to have reached this horizon has not
/// failed it (README scope decision 2).
/// </summary>
public sealed record RetentionPointDto(int Day, int Eligible, int Retained, double? Percent);

/// <summary>
/// One step of the acquisition funnel. <see cref="Eligible"/> is the denominator this step is
/// actually entitled to; <see cref="PercentOfSignups"/> and <see cref="PercentOfPrevious"/> are null
/// when their denominator is 0.
/// </summary>
public sealed record FunnelStepDto(
    string Step,            // one of FunnelSteps
    string Label,           // human text for the dashboard, e.g. "Played a second game"
    int Count,
    int Eligible,
    double? PercentOfSignups,
    double? PercentOfPrevious);

/// <summary>Stable machine names for the funnel steps. The UI keys off these, never off Label.</summary>
public static class FunnelSteps
{
    public const string Signup = "signup";
    public const string FirstGame = "first-game";
    public const string SecondGame = "second-game";
    public const string RetainedD7 = "retained-d7";
}

/// <summary>One signup cohort. Contains counts only - no account is identifiable from this row.</summary>
public sealed record CohortRowDto(
    DateOnly CohortStart,
    int Signups,
    int PlayedFirstGame,
    int PlayedSecondGame,
    IReadOnlyList<RetentionPointDto> Retention);

public sealed record RetentionSummaryDto(
    DateTimeOffset GeneratedAt,
    int SinceDays,
    string Grain,               // "day" | "week"
    bool Truncated,             // a cap was hit; the window shown is a recent sample, not everything
    int TotalSignups,
    IReadOnlyList<FunnelStepDto> Funnel,
    IReadOnlyList<RetentionPointDto> Retention,   // every cohort pooled
    IReadOnlyList<CohortRowDto> Cohorts);         // newest cohort first
