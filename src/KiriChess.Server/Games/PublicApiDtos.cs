using KiriChess.Engine.Core;
using KiriChess.Server.Infrastructure.OpenApi;

namespace KiriChess.Server.Games;

/// <summary>Authenticated bot identity on <c>GET /api/v1/account</c> (Phase 9f.5).</summary>
public sealed record PublicAccountDto(Guid Id, string DisplayName, IReadOnlyList<string> Scopes);

/// <summary>One live game the token's owner is sitting in (<c>GET /api/v1/account/playing</c>).</summary>
public sealed record PublicPlayingGameDto(
    Guid GameId,
    string? RatingPool,
    bool IsRated,
    [property: StringEnum(typeof(Side))] string MySide,
    Guid WhitePlayerId,
    Guid BlackPlayerId,
    [property: StringEnum(typeof(Side))] string SideToMove,
    int MoveCount);
