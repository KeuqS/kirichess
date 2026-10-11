using KiriChess.Engine.Core;
using KiriChess.Server.Data.Entities;
using KiriChess.Server.Infrastructure.OpenApi;

namespace KiriChess.Server.Games;

/// <summary>
/// One ply of a replay: the move, the position it produced, and the clock state the players actually
/// saw. <see cref="FenAfter"/>, <see cref="San"/> and <see cref="Events"/> are recomputed by replaying
/// the move through the engine (they are not, and cannot be, stored); every number is read straight
/// off the persisted <see cref="Data.Entities.MoveRecord"/>. The replay charges no think time, so its
/// own clocks drift from the real game's, which is exactly why they are never used here.
/// </summary>
public sealed record ReplayPlyDto(
    int Ply,
    string Uci,
    string San,
    [property: StringEnum(typeof(Side))] string Mover,
    string FenAfter,
    int WhiteTimeAfter,
    int BlackTimeAfter,
    int WhiteDelta,
    int BlackDelta,
    IReadOnlyList<ClockEventDto> Events);

/// <summary>
/// A finished game, reconstructed for public viewing (Phase 8.2). Everything here comes from the
/// game's own row, the config snapshot and the move list, which is the bet Phase 2 made when it
/// chose to store both: a game is fully reproducible from them, so replays need no new schema.
///
/// Deliberately not <see cref="GameStateResponse"/>: that describes a *live* session held in memory,
/// this describes a durable record, and conflating the two would tie a public read path to the
/// lifetime of a <see cref="GameSession"/>.
/// </summary>
public sealed record ReplayResponse(
    Guid GameId,
    string StartFen,
    Guid WhitePlayerId,
    Guid BlackPlayerId,
    string WhitePlayerName,
    string BlackPlayerName,
    // Trailing-and-defaulted for the same reason the names above are looked up rather than joined:
    // a replay outlives its players. An account erased since the game was played resolves to "None"
    // and the replay still serves, rather than the badge lookup being able to fail the page.
    [property: StringEnum(typeof(Account.PatronTier))] string WhitePatronTier,
    [property: StringEnum(typeof(Account.PatronTier))] string BlackPatronTier,
    [property: StringEnum(typeof(GameResult))] string Result,
    [property: StringEnum(typeof(TerminationReason))] string Termination,
    string? RatingPool,
    bool IsRated,
    DateTimeOffset CreatedAt,
    DateTimeOffset? EndedAt,
    EconomyDto Economy,
    IReadOnlyList<ReplayPlyDto> Plies,
    /// <summary>
    /// C-8-3. Null when every stored move replayed. Otherwise the ply of the first move that could
    /// not be replayed, or 0 when the starting position itself could not be loaded. <see cref="Result"/>
    /// and <see cref="Termination"/> are still the values stored on the game row, and they may describe
    /// a finish this movetext never reaches.
    /// </summary>
    int? TruncatedAtPly);
