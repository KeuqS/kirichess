using KiriChess.Engine;
using KiriChess.Engine.Config;
using KiriChess.Engine.Core;
using KiriChess.Server.Data.Entities;
using KiriChess.Server.Infrastructure;
using Microsoft.Extensions.Logging;

namespace KiriChess.Server.Games;

/// <summary>
/// Rebuilds a viewable replay from a persisted game by replaying its move log through the engine,
/// the same trick <see cref="GameSession.Restore"/> uses for crash recovery, minus the live session.
/// Pure and synchronous: it touches no database and no clock, so it is unit-testable without Docker,
/// which is the only reason the replay logic has real tests at all.
///
/// <b>C-8-10: a replayed FlagFall cannot always be reproduced.</b> Every <see cref="MoveRecord"/> is
/// re-applied at <c>thinkTimeSeconds: 0</c> (see the loop below), so a flag that fell because the
/// mover's think time ran the real clock to zero cannot be re-derived here: the final ply's
/// <c>Events</c> array omits the <c>FlagFall</c> event a viewer would expect next to a game whose
/// <c>Termination</c> is <c>Flagfall</c>. A flag driven by the economy alone (a capture or check chip
/// that would have zeroed the clock on its own) does still reproduce, so the event is present
/// sometimes and absent sometimes for the same termination. This is a stated exclusion, not silently
/// missing behaviour: the alternative, synthesising a <c>FlagFall</c> event onto the last ply from
/// <c>entity.Termination</c>, is a behaviour change on a public route and was deliberately not taken
/// here - see C-8.md's own two-option fix for the reasoning either way.
/// </summary>
public static class GameReplayBuilder
{
    /// <summary>Shown when a player's account no longer exists; a replay must outlive its players.</summary>
    public const string UnknownPlayerName = "Unknown player";

    /// <summary>Shown when a player has no badge, or their account is gone. Never null on the wire.</summary>
    public const string NoPatronTier = "None";

    /// <summary>
    /// <paramref name="logger"/> and <paramref name="metrics"/> are optional (C-8-3) so every existing
    /// pure unit test keeps calling this with just the five original arguments; the two live callers
    /// (<see cref="ReplayEndpoints"/>, <see cref="PublicApiEndpoints"/>) pass both.
    /// </summary>
    public static ReplayResponse Build(
        KiriChessEngine engine, Game entity, string? whiteName, string? blackName,
        string? whitePatronTier = null, string? blackPatronTier = null,
        ILogger? logger = null, KiriChessMetrics? metrics = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(entity);

        var config = entity.ConfigSnapshot;

        // Rows written before StartFen was populated, and any row hand-seeded without one, still
        // replay correctly from the standard start, the same position NewGame would have produced.
        var startFen = string.IsNullOrWhiteSpace(entity.StartFen) ? KiriChessEngine.StartFen : entity.StartFen;

        GamePosition position;
        try
        {
            // C-8-6: FromFen calls config.Validate() and then ChessBoard.LoadFromFen, either of which
            // can throw for exactly the class of corrupt row the loop below exists to survive (an
            // older jsonb shape missing a since-added rule variable, or a malformed FEN). This call
            // used to sit outside that protection entirely, so a row this corrupt 500'd permanently on
            // an anonymous public route instead of degrading the way every other corruption here does.
            position = engine.FromFen(config, startFen);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException or Chess.ChessException)
        {
            logger?.LogWarning(ex,
                "Replay of game {GameId} could not even load its starting position; the config snapshot or start FEN is corrupt. Serving a headers-only response.",
                entity.Id);
            metrics?.ReplayTruncated("replay");
            return BuildUnplayable(entity, whiteName, blackName, whitePatronTier, blackPatronTier, startFen, config, logger);
        }

        var plies = new List<ReplayPlyDto>(entity.Moves.Count);
        int? truncatedAtPly = null;
        foreach (var record in entity.Moves)
        {
            ApplyResult result;
            try
            {
                // thinkTimeSeconds: 0, see this class's own remarks above. The clocks this produces
                // are discarded; only the position and the itemised events are kept (FlagFall
                // excepted, per this class's remarks).
                result = engine.Apply(position, Move.Parse(record.Uci), config, 0);
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException or Chess.ChessException)
            {
                // A move that will not replay means the stored log and the stored config disagree,
                // a corrupt row, not a bad request. Serve the prefix that does replay rather than
                // 500ing a public page: a truncated replay is still a useful record, and an
                // exception here would be permanent for that game.
                //
                // ChessException (and its subtypes, e.g. ChessPieceNotFoundException) is the
                // underlying Chess library's own signal for board.Move throwing
                // ChessGameEndedException when the position is already finished (checkmate,
                // stalemate, insufficient material, fifty-move - see C-3-3/C-3-5), as well as for a
                // move referencing a piece that simply is not on the board, the corruption shape this
                // catch exists for.
                //
                // C-8-3: this used to be silent - no log, no metric, no field on the response - so a
                // jsonb row that stopped replaying was invisible to the maintainer forever.
                // entity.Result and entity.Termination are still written from the row below, so without
                // TruncatedAtPly a truncated replay reads as a complete record of a game whose movetext
                // does not actually reach that result. The ply reported is the first one omitted.
                logger?.LogWarning(ex,
                    "Replay of game {GameId} truncated at ply {Ply} of {TotalPlies}: the stored move log and config snapshot disagree.",
                    entity.Id, record.Ply, entity.Moves.Count);
                metrics?.ReplayTruncated("replay");
                truncatedAtPly = record.Ply;
                break;
            }

            position = result.NewPosition;
            plies.Add(new ReplayPlyDto(
                record.Ply,
                record.Uci,
                result.San,
                record.Mover.ToString(),
                position.Fen,
                record.WhiteTimeAfter,
                record.BlackTimeAfter,
                record.WhiteDelta,
                record.BlackDelta,
                result.Events.Select(e => e.ToDto()).ToArray()));
        }

        return new ReplayResponse(
            entity.Id,
            startFen,
            entity.WhitePlayerId,
            entity.BlackPlayerId,
            string.IsNullOrWhiteSpace(whiteName) ? UnknownPlayerName : whiteName,
            string.IsNullOrWhiteSpace(blackName) ? UnknownPlayerName : blackName,
            string.IsNullOrWhiteSpace(whitePatronTier) ? NoPatronTier : whitePatronTier,
            string.IsNullOrWhiteSpace(blackPatronTier) ? NoPatronTier : blackPatronTier,
            entity.Result.ToString(),
            entity.Termination.ToString(),
            entity.RatingPool,
            entity.IsRated,
            entity.CreatedAt,
            entity.EndedAt,
            EconomyDtoOrDefault(config, logger, entity.Id),
            plies,
            truncatedAtPly);
    }

    /// <summary>
    /// C-8-6: the response for a row so corrupt its starting position cannot even be loaded - the
    /// entity-level headers (which never touch the engine) plus an empty movetext, the same
    /// "an empty move list is still a valid response" shape
    /// <c>PgnExportTests.An_empty_move_list_still_produces_a_valid_file</c> proves for the PGN
    /// sibling. Never throws: <see cref="EconomyDtoOrDefault"/> guards the one remaining call that
    /// could still touch the corrupt config.
    /// </summary>
    private static ReplayResponse BuildUnplayable(
        Game entity, string? whiteName, string? blackName, string? whitePatronTier, string? blackPatronTier,
        string startFen, KiriChessConfig config, ILogger? logger) => new(
        entity.Id,
        startFen,
        entity.WhitePlayerId,
        entity.BlackPlayerId,
        string.IsNullOrWhiteSpace(whiteName) ? UnknownPlayerName : whiteName,
        string.IsNullOrWhiteSpace(blackName) ? UnknownPlayerName : blackName,
        string.IsNullOrWhiteSpace(whitePatronTier) ? NoPatronTier : whitePatronTier,
        string.IsNullOrWhiteSpace(blackPatronTier) ? NoPatronTier : blackPatronTier,
        entity.Result.ToString(),
        entity.Termination.ToString(),
        entity.RatingPool,
        entity.IsRated,
        entity.CreatedAt,
        entity.EndedAt,
        EconomyDtoOrDefault(config, logger, entity.Id),
        Array.Empty<ReplayPlyDto>(),
        // 0, not a move's ply: the start position never loaded, so there is no first omitted move.
        0);

    /// <summary>
    /// <see cref="GameMapping.ToEconomyDto"/> indexes <c>PieceValues</c>/<c>MoveCostPerPiece</c> by
    /// every <see cref="PieceType"/>, which is exactly what a config that failed
    /// <see cref="KiriChessConfig.Validate"/> (the case <see cref="BuildUnplayable"/> exists for,
    /// e.g. a jsonb row missing a since-added piece key) cannot guarantee. Falls back to an
    /// all-zero/None economy rather than letting that second indexing throw where the first one
    /// (FromFen) already did.
    /// </summary>
    private static EconomyDto EconomyDtoOrDefault(KiriChessConfig config, ILogger? logger, Guid gameId)
    {
        try
        {
            return config.ToEconomyDto();
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException)
        {
            logger?.LogWarning(ex,
                "Could not summarise game {GameId}'s corrupt config snapshot as an economy; returning a zeroed placeholder instead.",
                gameId);
            return new EconomyDto(
                0, 0, new PieceInts5Dto(0, 0, 0, 0, 0), 0, false, false,
                nameof(MoveCostMode.None), new PieceInts6Dto(0, 0, 0, 0, 0, 0),
                0, 0, 0, 0, 0, 0, 0,
                0, false, false, false, false);
        }
    }
}
