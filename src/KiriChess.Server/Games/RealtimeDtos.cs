using KiriChess.Engine.Core;
using KiriChess.Server.Account;
using KiriChess.Server.Infrastructure.OpenApi;

namespace KiriChess.Server.Games;

/// <summary>
/// Full state pushed on join/resync: current state, the side-to-move's legal moves, and the game's
/// move log so far.
///
/// <see cref="Moves"/> is C-1-2 = C-11-2. Before it, this DTO carried state and legal moves only,
/// while <see cref="SpectatorSnapshotDto"/> carried a whole move log, so a player whose transport
/// dropped had nothing to rebuild from on rejoin: any ply broadcast during the outage was gone from
/// their move list, clock graph and captured-material panel for the rest of the game, while a
/// spectator watching the same board kept all three. The board and clocks were right either way
/// (they come from <see cref="State"/>), which is what made the divergence silent.
///
/// It is deliberately the same <see cref="SpectatorMoveDto"/> a spectator receives, because it is
/// the same data: ply, move, mover, both clocks after, both deltas. Sharing the type keeps one
/// schema in the OpenAPI document and one alias in ui/lib/types.ts, and it means the redaction that
/// record documents (no <c>ThinkMs</c>, <c>LagMs</c> or <c>ReceivedAtUtc</c>, the Phase 6e.2
/// anti-cheat telemetry) is pinned by <c>SpectatorTests</c> for both readers at once. A field a
/// participant may see but a spectator may not therefore does <b>not</b> go on
/// <see cref="SpectatorMoveDto"/>: it needs a record of its own, or it leaks onto an anonymous
/// channel.
///
/// Uncapped, matching <see cref="SpectatorSnapshotDto.Moves"/>: there is no truncation on the
/// spectator path either, and inventing one here would make a reconnecting player's history worse
/// than a spectator's again, in the long games where it matters most.
/// </summary>
public sealed record GameSnapshotDto(
    GameStateResponse State,
    IReadOnlyList<string> LegalMoves,
    IReadOnlyList<SpectatorMoveDto> Moves);

/// <summary>
/// Broadcast after a move: new state, the animated deltas/events, and the next legal moves.
///
/// C-2-9 = C-8-7: players and spectators must not share one payload that includes
/// <see cref="LegalMoves"/>. The player group receives the list; the spectator group receives
/// the same record with an empty list. <see cref="SpectatorSnapshotDto"/> still omits the field
/// entirely. Same DTO on both sends, so this is not a wire-schema change.
/// </summary>
public sealed record MovePlayedDto(
    GameStateResponse State,
    string Uci,
    int WhiteDelta,
    int BlackDelta,
    IReadOnlyList<ClockEventDto> Events,
    IReadOnlyList<string> LegalMoves,
    bool GameOver,
    GameRatingChangeDto? RatingChange = null);

/// <summary>Broadcast on a non-move terminal transition (resign / agreed draw / abandon / flag-fall).</summary>
public sealed record GameEndedDto(GameStateResponse State, GameRatingChangeDto? RatingChange = null);

/// <summary>One player's Glicko-2 rating before and after the game that just ended (Phase 11a.2).</summary>
public sealed record PlayerRatingChangeDto(double Before, double After);

/// <summary>
/// Both players' rating changes for a game that just ended rated, carried on <see cref="GameEndedDto"/>
/// and <see cref="MovePlayedDto"/> so the client never has to poll for what <c>RatingService.ApplyAsync</c>
/// already computed in the same transaction that persisted the game. Null on both DTOs for every game
/// that is not rated (or was aborted/solo), <see cref="Ratings.RatingService.ApplyAsync"/> is the single
/// source of truth for eligibility.
/// </summary>
public sealed record GameRatingChangeDto(PlayerRatingChangeDto White, PlayerRatingChangeDto Black);

/// <summary>Sent to the opponent when a draw is offered. <see cref="BySide"/> is filled from <c>session.DrawOfferedBy!.Value.ToString()</c> (a <see cref="Side"/>).</summary>
public sealed record DrawOfferedDto([property: StringEnum(typeof(Side))] string BySide);

/// <summary>Sent to each matched player when matchmaking pairs them. <see cref="Color"/> is filled from the literals "White"/"Black" (<c>GameHub.JoinQueue</c>).</summary>
public sealed record MatchFoundDto(Guid GameId, [property: StringEnum(typeof(Side))] string Color, string Pool);

/// <summary>
/// Broadcast to the new game's group when a rematch offer (Phase 11a.1) is accepted. A single
/// group-wide payload rather than two per-connection sends (unlike <see cref="MatchFoundDto"/>):
/// colour is not attached, each client already knows its own user id and compares it against
/// <see cref="WhitePlayerId"/>/<see cref="BlackPlayerId"/> the same way it derives "my side" from
/// GameStateResponse everywhere else.
/// </summary>
public sealed record RematchAcceptedDto(Guid GameId, Guid WhitePlayerId, Guid BlackPlayerId, string? Pool, bool Rated);

/// <summary>Live lobby counts broadcast on the presence hub: signed-in users online, and of those, how many are playing.</summary>
public sealed record PresenceDto(int Online, int InGame);

/// <summary>
/// One ply as a spectator sees it (Phase 8.1): a mirror of the persisted
/// <see cref="Data.Entities.MoveRecord"/> minus its anti-cheat telemetry. <c>ThinkMs</c>,
/// <c>LagMs</c> and <c>ReceivedAtUtc</c> are deliberately absent, they are the Phase 6e.2 dataset a
/// future engine-assistance detector runs on, and publishing them on an anonymous channel would tell
/// a cheater exactly which signal is being measured.
///
/// Since C-1-2 this is also the ply a <b>participant</b> sees, on
/// <see cref="GameSnapshotDto.Moves"/>. The name still says spectator because the redaction above is
/// the constraint the record exists to hold: adding a field here publishes it anonymously, whoever
/// asked for it. Something only a participant is entitled to needs its own record.
/// </summary>
public sealed record SpectatorMoveDto(
    int Ply,
    string Uci,
    // Filled from m.Mover.ToString() (GameDtos.cs, ToSpectatorDto), a Side.
    [property: StringEnum(typeof(Side))] string Mover,
    int WhiteTimeAfter,
    int BlackTimeAfter,
    int WhiteDelta,
    int BlackDelta);

/// <summary>
/// Full state handed to a spectator on join (Phase 8.1). Carries the whole move log so somebody
/// arriving at ply 30 still gets a complete move list and clock graph rather than a board with no
/// history, and carries display names because the hub snapshot otherwise has only player ids and a
/// shared link would render as "White vs Black".
///
/// Deliberately carries **no legal moves**, unlike <see cref="GameSnapshotDto"/>: a spectator has no
/// move to make, so shipping the engine's move-generator output to an unauthenticated caller is free
/// surface with no consumer. The live feed matches that omission: spectators sit in
/// <c>{gameId}:spectators</c> and receive <see cref="MovePlayedDto"/> with an empty
/// <see cref="MovePlayedDto.LegalMoves"/> list (C-2-9 = C-8-7).
/// </summary>
public sealed record SpectatorSnapshotDto(
    GameStateResponse State,
    string WhiteName,
    string BlackName,
    [property: StringEnum(typeof(PatronTier))] string WhitePatronTier,
    [property: StringEnum(typeof(PatronTier))] string BlackPatronTier,
    IReadOnlyList<SpectatorMoveDto> Moves,
    int Spectators);

/// <summary>
/// Broadcast to everyone in a game's group when the spectator count changes. Players receive it too,
/// on purpose, "3 watching" on your own board is a feature, and there is nothing in the count a
/// player could not learn by opening the watch list.
/// </summary>
public sealed record SpectatorsChangedDto(int Count);

/// <summary>
/// Broadcast to a game's group when a player's connection drops, carrying how long they have to come
/// back before the abandon timer fires. Promoted from an anonymous object to a named record in Phase
/// 9e.9: `new { graceSeconds = ... }` is a shape no schema can point at, so this was the one hub
/// payload that could not be described even in principle. The JSON is unchanged.
/// </summary>
public sealed record OpponentDisconnectedDto(int GraceSeconds);

/// <summary>
/// One row of the public live-games list (Phase 8.1). Solo/practice games are filtered out by the
/// endpoint, so the two names always name two different people.
///
/// <see cref="Fen"/> is what lets the list draw a mini-board per game rather than a row of text: the
/// server already holds the position (it had to, to accept the move), so the client re-draws it
/// instead of anything being simulated or rendered per viewer. Nullable because the Redis-backed path
/// reads a published read-model that may predate the field (see <c>LiveGameStateDto</c>); a client
/// must render the card without a board rather than substitute a start position, which would be
/// actively wrong for a game already thirty moves in.
/// </summary>
public sealed record LiveGameDto(
    Guid GameId,
    string WhiteName,
    string BlackName,
    [property: StringEnum(typeof(PatronTier))] string WhitePatronTier,
    [property: StringEnum(typeof(PatronTier))] string BlackPatronTier,
    string? Pool,
    bool Rated,
    int WhiteTime,
    int BlackTime,
    [property: StringEnum(typeof(Side))] string SideToMove,
    int MoveCount,
    int Spectators,
    DateTimeOffset StartedAt,
    string? Fen = null);

/// <summary>
/// One row of the public open-seek list (Phase 9e.4): a pool with a player already waiting in its
/// matchmaking queue, and since when. Anonymous, like <see cref="LiveGameDto"/>, and carries no
/// opponent identity, there is none to show: matchmaking pairs the closest-rated stranger, not a
/// chosen one, so "accepting" a seek is just joining the same pool's queue (GameHub.JoinQueue),
/// unchanged, which pairs instantly against the row this DTO represents.
/// </summary>
public sealed record OpenSeekDto(string Pool, DateTimeOffset WaitingSinceUtc);
