using KiriChess.Server.Infrastructure.OpenApi;
using KiriChess.Server.Ratings;

namespace KiriChess.Server.Account;

/// <summary>
/// What one account looks like to a stranger. Deliberately narrow: every field here is already
/// public somewhere else on the site, so this endpoint aggregates rather than discloses.
///
/// - <paramref name="DisplayName"/> and <paramref name="PatronTier"/> are on /leaderboards, every
///   live board and every replay.
/// - <paramref name="Ratings"/> is exactly what GET /api/ratings/users/{id} already answers
///   anonymously, reusing that route's own <see cref="RatingDto"/> so the two cannot drift.
/// - <paramref name="JoinedAt"/> and <paramref name="GamesPlayed"/> are the two additions, both the
///   kind of "is this account new or established" signal the page exists to answer.
/// - <paramref name="AvatarUrl"/> is a same-API path (or null for initials), already shown on
///   /leaderboards next to the same name. It is never an off-site URL.
///
/// Not here on purpose: the email, anything from the 8a.2 consent record, the patron *date*
/// (the badge is the public fact, when someone paid is not), and a game list. A per-opponent game
/// list keyed by someone else's id is the thing GameEndpoints' "/mine" comment explicitly declined
/// to build without a scope decision; nothing about this page forces that call now.
/// </summary>
public sealed record PublicProfileDto(
    Guid UserId,
    string DisplayName,
    DateTimeOffset JoinedAt,
    [property: StringEnum(typeof(PatronTier))] string PatronTier,
    int GamesPlayed,
    IReadOnlyList<RatingDto> Ratings,
    string? AvatarUrl);
