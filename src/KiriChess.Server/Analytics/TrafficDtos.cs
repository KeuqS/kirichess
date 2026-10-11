namespace KiriChess.Server.Analytics;

/// <summary>One counter key and its total over the window.</summary>
public sealed record CounterCountDto(string Key, long Count);

/// <summary>
/// One campaign bucket. <see cref="Signups"/> is the client-fired signup COUNTER, not the account
/// table, so the sum of these can be lower than TrafficSummaryDto.TotalSignups - see README scope
/// decision 15, and show the difference rather than hiding it.
/// </summary>
public sealed record SourceCountDto(string Source, long PageViews, long Signups);

/// <summary>
/// One UTC day. Games are queried from the Games table rather than counted by a beacon (README
/// ground-truth correction 1); signups come from AspNetUsers.CreatedAt.
/// </summary>
public sealed record TrafficDayDto(
    DateOnly Date,
    long PageViews,
    int Signups,
    long GamesStarted,
    long GamesFinished,
    IReadOnlyList<CounterCountDto> Pages);

/// <summary>
/// 11a.3's cliff, over the whole window. The three percentages are null - never zero - when their
/// denominator is 0.
///
/// GameFinishPercent can legitimately exceed 100: a game started before the window can finish inside
/// it. It is NOT clamped, because a clamp would hide that artefact and make a boundary effect look
/// like a fact.
/// </summary>
public sealed record FirstSessionDto(
    long LearnStarted,
    long LearnCompleted,
    long LearnSkipped,
    long QueueJoined,
    long QueueMatched,
    long GamesStarted,
    long GamesFinished,
    double? LearnCompletionPercent,
    double? QueueMatchPercent,
    double? GameFinishPercent);

/// <summary>
/// <see cref="SignupsPerHundredLandingViews"/> is deliberately not called a conversion rate: views
/// cannot be deduplicated (phase8a5 README scope decision 4), so this is signups per hundred VIEWS,
/// not per hundred people. Null when there are no landing views in the window.
/// </summary>
public sealed record TrafficSummaryDto(
    DateTimeOffset GeneratedAt,
    int SinceDays,
    long TotalPageViews,
    int TotalSignups,
    double? SignupsPerHundredLandingViews,
    bool Truncated,
    IReadOnlyList<CounterCountDto> ByPage,     // page keys only, descending by Count
    IReadOnlyList<SourceCountDto> BySource,    // descending by PageViews
    FirstSessionDto FirstSession,
    IReadOnlyList<TrafficDayDto> Days);        // oldest first, zero-filled gaps
