namespace KiriChess.Server.Games.Distributed;

/// <summary>
/// The Redis live-state document (Phase 8.4), pinned verbatim in Phase 8.4 so Task
/// C, which cannot see this task's code and reads this shape in parallel, never has to guess it.
/// Written by <see cref="LiveGameIndex.PublishAsync"/> to <c>{p}game:state:{gameId}</c>, serialized
/// with <c>JsonNamingPolicy.CamelCase</c>.
///
/// <see cref="WhiteTimeSeconds"/>/<see cref="BlackTimeSeconds"/> are the <b>base</b> remaining figures
/// (<c>session.Position.TimeFor(side)</c>), not <c>GameSession.LiveRemaining</c>: a reader approximates
/// the running side's clock as
/// <c>Math.Max(0, base - (int)(DateTimeOffset.UtcNow - LastMoveAtUtc).TotalSeconds)</c>, using the wall
/// clock and no lag credit. That approximation is display-only, README scope decision 5, and must
/// never be read back as a chargeable time; every authoritative clock read still happens on the owning
/// instance, in <see cref="GameSession"/>, off the monotonic anchor.
///
/// <see cref="Fen"/> carries the same display-only standing, and is nullable for the same reason the
/// clocks are approximate: it is a read-model, not a record. An entry published by an instance running
/// code older than the field deserializes with it absent, so every reader must tolerate null rather
/// than assume a position is present. Move legality is still decided on the owning instance, off
/// <c>GameSession.Position</c>, never off this string.
///
/// <see cref="WhitePatronTier"/>/<see cref="BlackPatronTier"/> follow the exact same nullable-trailing
/// contract as <see cref="Fen"/>: an entry published by an older instance deserializes with them
/// absent, so a reader treats null the same as "None" rather than assuming no badge.
/// </summary>
public sealed record LiveGameStateDto(
    Guid GameId,
    string OwnerInstanceId,
    Guid WhitePlayerId,
    Guid BlackPlayerId,
    string WhiteName,
    string BlackName,
    string? RatingPool,
    bool IsRated,
    string SideToMove,            // "White" | "Black"
    int WhiteTimeSeconds,         // base remaining, NOT net of the current turn's elapsed
    int BlackTimeSeconds,
    int MoveCount,
    bool IsOver,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastMoveAtUtc, // so a reader can approximate the running side's clock
    DateTimeOffset UpdatedAt,
    string? Fen = null,           // display-only, may be absent on an entry from an older instance
    string? WhitePatronTier = null,
    string? BlackPatronTier = null);
