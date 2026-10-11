using KiriChess.Engine;
using KiriChess.Engine.Config;
using KiriChess.Engine.Core;
using KiriChess.Server.Data.Entities;
using KiriChess.Server.Infrastructure.OpenApi;

namespace KiriChess.Server.Games;

/// <summary>
/// Create a game. If <see cref="OpponentId"/> is null the caller plays both sides (for bot matches),
/// this is what lets Phase 2 script a full game to exercise persistence before matchmaking exists.
/// <see cref="Preset"/> (bounty1/burn1/steal1/steal2/steal3/burn2) is required unless
/// <see cref="Custom"/> is supplied, in which case the preset is ignored and the custom config is
/// used verbatim, custom games are always unrated.
///
/// Phase 9d.8: <see cref="OpponentId"/> is self-only now, null (bot/solo) or the caller's own id;
/// anything else is a 400. Inviting a specific other person goes through POST /api/challenges, which
/// requires that person's consent (they have to accept the link). This endpoint never did, and was
/// only safe because nothing in ui/ ever sent a third-party id, see README.md scope decision 5.
/// </summary>
public sealed record CreateGameRequest(Guid? OpponentId, string? Preset, bool Rated, CustomConfigDto? Custom = null);

/// <summary>
/// The caller's one live game (<c>GET /api/games/playing</c>), so a surface that refused a new
/// game can link back to the board they left. <see cref="Solo"/> is a bot or practice game, one
/// account on both sides. <see cref="Pool"/> is null for those and for custom games: a bot's
/// strength and the preset it was started from live in the client URL, not on the row.
/// </summary>
public sealed record CurrentGameResponse(Guid GameId, string? Pool, bool Solo);

public sealed record PieceInts5Dto(int Pawn, int Knight, int Bishop, int Rook, int Queen);

public sealed record PieceInts6Dto(int Pawn, int Knight, int Bishop, int Rook, int Queen, int King);

/// <summary>Full <see cref="KiriChessConfig"/> mirror sent by the Custom game builder. See <c>BuildCustomConfig</c>.</summary>
public sealed record CustomConfigDto(
    int StartTimeSeconds,
    int BaseIncrement,
    int EconomyScale,
    PieceInts5Dto PieceValues,
    int CaptureMult,
    bool CaptureCaptorGainEnabled,
    bool CaptureVictimLossEnabled,
    [property: StringEnum(typeof(MoveCostMode))] string MoveCostMode,
    PieceInts6Dto MoveCostPerPiece,
    int CastleCostFlat,
    int MoveCostFlat,
    int MoveCostPerSquare,
    int CheckCheckerGain,
    int CheckVictimLoss,
    int DoubleCheckChipMultiplier,
    int PromotionCostMult,
    bool RepeatedPositionNullifiesTime,
    bool ThreefoldRepetitionIsDraw,
    bool FlagFallLoses,
    bool FlagVsInsufficientMaterialIsDraw);

public sealed record ClockEventDto(
    [property: StringEnum(typeof(ClockEventType))] string Type,
    [property: StringEnum(typeof(Side))] string Side,
    int Amount, string? Detail);

/// <summary>
/// The economy actually governing a game, echoed back so the client can price move previews and
/// size the game-over clock graph without hardcoding preset tables. Mirrors the tunable subset of
/// <see cref="KiriChessConfig"/> (everything the client needs to reproduce <c>ApplyEconomy</c>),
/// plus <see cref="BaseIncrement"/> and the four end-condition booleans. A custom game has no
/// published rules page, so a challenge and a replay have to be able to state those themselves.
/// The board's cost preview (<c>ui/lib/costPreview.ts</c>) includes <see cref="BaseIncrement"/>.
/// It still omits check-chip magnitude and repetition nullification, both of which need more
/// than this config and the move.
/// </summary>
public sealed record EconomyDto(
    int StartTimeSeconds,
    int EconomyScale,
    PieceInts5Dto PieceValues,
    int CaptureMult,
    bool CaptureCaptorGainEnabled,
    bool CaptureVictimLossEnabled,
    [property: StringEnum(typeof(MoveCostMode))] string MoveCostMode,
    PieceInts6Dto MoveCostPerPiece,
    int CastleCostFlat,
    int MoveCostFlat,
    int MoveCostPerSquare,
    int PromotionCostMult,
    int CheckCheckerGain,
    int CheckVictimLoss,
    int DoubleCheckChipMultiplier,
    int BaseIncrement,
    bool RepeatedPositionNullifiesTime,
    bool ThreefoldRepetitionIsDraw,
    bool FlagFallLoses,
    bool FlagVsInsufficientMaterialIsDraw);

/// <summary>
/// Wire state for a game. <see cref="RematchOfferedById"/> is who holds the unaccepted rematch
/// offer on a finished game, or null. First-party participant reads (GET /api/games/{id} and
/// JoinGame) populate it so a cold load of the mail link can render Accept rather than Offer. Live
/// games, non-participants, and the public /api/v1 restore path leave it null: that path serves
/// finished games to anyone, and the offerer id is not a public fact.
///
/// <see cref="ActiveClockOffsetMs"/> is the fraction of the current second already charged to the
/// side to move. <see cref="WhiteTime"/> and <see cref="BlackTime"/> are whole seconds; a countdown
/// subtracts this before it starts, or the face shows up to a second the flag will not allow.
///
/// <see cref="WhiteViaApi"/> and <see cref="BlackViaApi"/> are true when that seat sat down through
/// the public API. The game page labels that opponent as a bot. Both stay false for matchmaking
/// and for a challenge created and accepted on the site.
/// </summary>
public sealed record GameStateResponse(
    Guid GameId,
    string Fen,
    [property: StringEnum(typeof(Side))] string SideToMove,
    int WhiteTime,
    int BlackTime,
    [property: StringEnum(typeof(GameStatus))] string Status,
    [property: StringEnum(typeof(Side))] string? Winner,
    [property: StringEnum(typeof(TerminationReason))] string Termination,
    Guid WhitePlayerId,
    Guid BlackPlayerId,
    int MoveCount,
    EconomyDto Economy,
    bool Rated,
    int ActiveClockOffsetMs,
    Guid? RematchOfferedById = null,
    bool WhiteViaApi = false,
    bool BlackViaApi = false);

/// <summary>
/// One row of the caller's own finished-game history (Phase 9e.1). Opponent-centric rather than
/// White/Black-centric, unlike <see cref="ReplayResponse"/>: this list exists for
/// "who did I play and how did it go", not for reconstructing the game. <see cref="OpponentName"/>
/// reads "Deleted account" for an erased opponent through the same plain <c>Users</c> lookup the
/// replay endpoint uses (8a.2 never deletes the row, only anonymises it), no special-case handling
/// needed. <see cref="RatingBefore"/>/<see cref="RatingAfter"/> are null for unrated games and are
/// exactly what a player needs to check a disputed rating change against.
/// </summary>
public sealed record GameHistoryEntryDto(
    Guid GameId,
    Guid OpponentId,
    string OpponentName,
    [property: StringEnum(typeof(Side))] string MySide,
    [property: StringEnum(typeof(GameResult))] string Result,
    [property: StringEnum(typeof(TerminationReason))] string Termination,
    string? RatingPool,
    bool IsRated,
    int PlyCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset? EndedAt,
    double? RatingBefore,
    double? RatingAfter);

public sealed record MoveRequest(string Uci);

public sealed record MoveResponse(
    GameStateResponse State,
    int WhiteDelta,
    int BlackDelta,
    IReadOnlyList<ClockEventDto> Events,
    bool GameOver);

public static class GameMapping
{
    public static ClockEventDto ToDto(this ClockEvent e) =>
        new(e.Type.ToString(), e.Side.ToString(), e.Amount, e.Detail);

    public static EconomyDto ToEconomyDto(this KiriChessConfig c) => new(
        c.StartTimeSeconds,
        c.EconomyScale,
        new PieceInts5Dto(
            c.PieceValues[PieceType.Pawn],
            c.PieceValues[PieceType.Knight],
            c.PieceValues[PieceType.Bishop],
            c.PieceValues[PieceType.Rook],
            c.PieceValues[PieceType.Queen]),
        c.CaptureMult,
        c.CaptureCaptorGainEnabled,
        c.CaptureVictimLossEnabled,
        c.MoveCostMode.ToString(),
        new PieceInts6Dto(
            c.MoveCostPerPiece[PieceType.Pawn],
            c.MoveCostPerPiece[PieceType.Knight],
            c.MoveCostPerPiece[PieceType.Bishop],
            c.MoveCostPerPiece[PieceType.Rook],
            c.MoveCostPerPiece[PieceType.Queen],
            c.MoveCostPerPiece[PieceType.King]),
        c.CastleCostFlat,
        c.MoveCostFlat,
        c.MoveCostPerSquare,
        c.PromotionCostMult,
        c.CheckCheckerGain,
        c.CheckVictimLoss,
        c.DoubleCheckChipMultiplier,
        c.BaseIncrement,
        c.RepeatedPositionNullifiesTime,
        c.ThreefoldRepetitionIsDraw,
        c.FlagFallLoses,
        c.FlagVsInsufficientMaterialIsDraw);

    public static GameStateResponse ToState(this GameSession s)
    {
        // One snapshot: the integers and the offset have to come from the same charged instant,
        // or a second boundary between the two readings hands the client a second the flag does not.
        var clocks = s.LiveClockSnapshot();
        return new(
            s.Id,
            s.Position.Fen,
            s.Position.SideToMove.ToString(),
            clocks.WhiteSeconds,
            clocks.BlackSeconds,
            s.Status.ToString(),
            s.Winner?.ToString(),
            s.Termination.ToString(),
            s.WhitePlayerId,
            s.BlackPlayerId,
            s.MoveCount,
            s.Config.ToEconomyDto(),
            s.IsRated,
            clocks.ActiveOffsetMs,
            WhiteViaApi: s.WhiteViaApi,
            BlackViaApi: s.BlackViaApi);
    }

    /// <summary>
    /// Rebuilds the wire state from a persisted row without registering a live session. Used by the
    /// public GET fallback and by the first-party finished-game read path (P-3-1a). Resignation and
    /// abandon are not replayable from the move list, so the persisted result/termination overwrite
    /// whatever <see cref="GameSession.Restore"/> inferred from plies alone.
    /// </summary>
    public static GameStateResponse FromPersisted(KiriChessEngine engine, Game row)
    {
        var session = GameSession.Restore(engine, row);
        var state = session.ToState();
        if (row.Result == GameResult.Ongoing) return state;
        return state with
        {
            Status = StatusFromPersisted(row.Result, row.Termination).ToString(),
            Winner = WinnerFrom(row.Result)?.ToString(),
            Termination = row.Termination.ToString(),
            Rated = row.IsRated
        };
    }

    private static GameStatus StatusFromPersisted(GameResult result, TerminationReason termination)
    {
        if (result == GameResult.Aborted) return GameStatus.Aborted;
        return termination switch
        {
            TerminationReason.Checkmate => GameStatus.Checkmate,
            TerminationReason.Stalemate => GameStatus.Stalemate,
            TerminationReason.DrawByRepetition => GameStatus.DrawByRepetition,
            TerminationReason.DrawByInsufficientMaterial => GameStatus.DrawByInsufficientMaterial,
            TerminationReason.DrawByFiftyMove => GameStatus.DrawByFiftyMove,
            TerminationReason.Flagfall => GameStatus.FlagFall,
            TerminationReason.Resignation => GameStatus.FlagFall,
            TerminationReason.Abandoned => GameStatus.FlagFall,
            TerminationReason.NoShow => GameStatus.FlagFall,
            TerminationReason.DrawByAgreement => GameStatus.DrawByRepetition,
            TerminationReason.ServerRestart => GameStatus.Aborted,
            _ => GameStatus.Ongoing
        };
    }

    private static Side? WinnerFrom(GameResult result) => result switch
    {
        GameResult.WhiteWins => Side.White,
        GameResult.BlackWins => Side.Black,
        _ => null
    };

    /// <summary>
    /// The participant join payload. Carries the same move log <see cref="ToSpectatorSnapshot"/>
    /// builds, and for the same reason (C-1-2): somebody arriving at ply 30, or coming back after a
    /// dropped socket, needs the whole log to rebuild a move list and a clock graph from, not just
    /// the position those plies produced.
    /// </summary>
    public static GameSnapshotDto ToSnapshot(this GameSession s) =>
        new(s.ToState(), s.LegalMoveStrings(), s.Moves.Select(m => m.ToSpectatorDto()).ToArray());

    public static string[] LegalMoveStrings(this GameSession s) =>
        s.LegalMoves().Select(m => m.ToString()).ToArray();

    public static SpectatorMoveDto ToSpectatorDto(this MoveRecord m) => new(
        m.Ply, m.Uci, m.Mover.ToString(), m.WhiteTimeAfter, m.BlackTimeAfter, m.WhiteDelta, m.BlackDelta);

    /// <summary>
    /// The spectator join payload (Phase 8.1). Kept here rather than inline in the hub so the shape
    /// of what an anonymous caller receives is unit-testable without standing up a SignalR harness,
    /// which is the only test that would otherwise cover the omission of the 6e.2 telemetry.
    /// </summary>
    public static SpectatorSnapshotDto ToSpectatorSnapshot(
        this GameSession s, string whiteName, string blackName, string whitePatronTier, string blackPatronTier, int spectators) => new(
        s.ToState(),
        whiteName,
        blackName,
        whitePatronTier,
        blackPatronTier,
        s.Moves.Select(m => m.ToSpectatorDto()).ToArray(),
        spectators);
}
