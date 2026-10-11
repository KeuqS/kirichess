using KiriChess.Server.Account;
using KiriChess.Server.Infrastructure.OpenApi;

namespace KiriChess.Server.Ratings;

/// <summary>A single pool rating for a user.</summary>
public sealed record RatingDto(
    string Pool,
    double Rating,
    double RatingDeviation,
    double Volatility,
    int GamesPlayed,
    bool Provisional,
    DateTimeOffset UpdatedAt);

/// <summary>One ranked row on a pool leaderboard.</summary>
public sealed record LeaderboardEntryDto(
    int Rank,
    Guid UserId,
    string DisplayName,
    double Rating,
    double RatingDeviation,
    int GamesPlayed,
    bool Provisional,
    [property: StringEnum(typeof(PatronTier))] string PatronTier,
    string? AvatarUrl);

/// <summary>One point on a user's rating-over-time graph in a pool.</summary>
public sealed record RatingHistoryPointDto(
    Guid GameId,
    double ValueBefore,
    double ValueAfter,
    double RdAfter,
    DateTimeOffset CreatedAt);

/// <summary>
/// One player's rating change from a single <see cref="RatingService.ApplyAsync"/> call (Phase 8.4).
/// Collected by the caller and written to <see cref="LeaderboardCache"/> only after the surrounding
/// SaveChanges has actually committed, see the collector-parameter comment on ApplyAsync.
/// </summary>
public sealed record LeaderboardUpdate(
    string Pool, Guid UserId, double Rating, double RatingDeviation, DateTimeOffset LastPlayedAt);

/// <summary>
/// The Postgres row a leaderboard entry is built from, projected once and looked up by user id, see
/// <see cref="RatingEndpoints.RankByCacheOrder"/>, the piece that turns a ZSET's id order plus these
/// rows into ranked <see cref="LeaderboardEntryDto"/>s without ever trusting the database's own
/// row order.
/// </summary>
public sealed record LeaderboardRow(Guid UserId, string DisplayName, double Value, double RatingDeviation, int GamesPlayed, PatronTier PatronTier, DateTimeOffset? AvatarUpdatedAt);
