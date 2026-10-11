using KiriChess.Server.Data.Entities;
using KiriChess.Server.Infrastructure.OpenApi;

namespace KiriChess.Server.Analytics;

/// <summary>One finished game, flattened to the scalar columns the balance summary needs.</summary>
/// <remarks>
/// Projected in the endpoint's EF query so the jsonb <c>Moves</c>/<c>ConfigSnapshot</c> columns are
/// never read, see <see cref="BalanceAnalytics"/> for why the aggregation is done in memory.
/// </remarks>
public readonly record struct GameFact(
    string? RatingPool,
    bool IsRated,
    GameResult Result,
    TerminationReason Termination,
    int PlyCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset? EndedAt);

public sealed record TerminationSliceDto(
    [property: StringEnum(typeof(TerminationReason))] string Reason, int Count, double Percent);

public sealed record BalancePoolDto(
    string Pool,
    int Games,
    int ExcludedGames,
    double WhiteWinPercent,
    double BlackWinPercent,
    double DrawPercent,
    double CheckmatePercent,
    double FlagfallPercent,
    double AvgPlies,
    double MedianPlies,
    double AvgDurationSeconds,
    double MedianDurationSeconds,
    IReadOnlyList<TerminationSliceDto> Terminations);

public sealed record BalanceSummaryDto(
    DateTimeOffset GeneratedAt,
    int SinceDays,
    bool RatedOnly,
    int TotalGames,
    int ExcludedGames,
    bool Truncated,
    BalancePoolDto Overall,
    IReadOnlyList<BalancePoolDto> Pools);
