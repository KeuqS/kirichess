using System.Globalization;
using System.Text;
using KiriChess.Engine;
using KiriChess.Engine.Config;
using KiriChess.Engine.Core;
using KiriChess.Server.Data.Entities;
using KiriChess.Server.Infrastructure;
using Microsoft.Extensions.Logging;

namespace KiriChess.Server.Games;

/// <summary>
/// Rebuilds a downloadable PGN from a persisted game (Phase 9e.2), the same walk
/// <see cref="GameReplayBuilder.Build"/> does, with a different output. Pure and synchronous for the
/// same testability reason: no database, no clock, so it is unit-testable with no Docker, which is
/// the only reason the replay logic (and now this) has real tests at all.
///
/// The format this emits is an external contract specified in <c>docs/game/PGN.md</c> and reviewed
/// separately from this code (Phase 9e.2, "The format"). Changing the shape of the
/// output, the header order, the <c>%kc*</c> comment grammar, or the <c>%kcfg</c> key order is a
/// breaking change to every file a player has already downloaded, not a refactor: those files are on
/// disk, in other people's chess software, outside this repository's control the moment they leave
/// the server.
/// </summary>
public static class PgnBuilder
{
    /// <summary>
    /// The value of the <c>[KiriChessFormat]</c> tag. Bump only for a change that a KiriChess-aware
    /// parser could not tolerate; adding a new <c>%kc*</c> command or appending a <c>%kcfg</c> key does
    /// not qualify, both are designed to be ignored by a reader that predates them.
    /// </summary>
    public const string FormatVersion = "1";

    /// <summary>
    /// <paramref name="logger"/> and <paramref name="metrics"/> are optional (C-8-3) so every existing
    /// pure unit test keeps calling this with just the six original arguments; the two live callers
    /// (<see cref="ReplayEndpoints"/>, <see cref="PublicApiEndpoints"/>) pass both.
    /// </summary>
    public static string Build(
        KiriChessEngine engine,
        Game entity,
        string? whiteName,
        string? blackName,
        int? whiteElo,
        int? blackElo,
        string? siteBaseUrl,
        ILogger? logger = null,
        KiriChessMetrics? metrics = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(entity);

        var config = entity.ConfigSnapshot;

        // Same blank-StartFen fallback as GameReplayBuilder.Build: rows written before StartFen was
        // populated, and any row hand-seeded without one, still export correctly from the standard
        // start, the same position NewGame would have produced.
        var startFen = string.IsNullOrWhiteSpace(entity.StartFen) ? KiriChessEngine.StartFen : entity.StartFen;

        GamePosition position;
        try
        {
            // C-8-6: mirrors GameReplayBuilder.Build's identical fix. FromFen calls
            // config.Validate() and then ChessBoard.LoadFromFen, either of which can throw for the
            // same corrupt-row shape the per-ply loop below exists to survive; this call used to sit
            // outside that protection and 500 a public download permanently.
            position = engine.FromFen(config, startFen);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException or Chess.ChessException)
        {
            logger?.LogWarning(ex,
                "PGN export of game {GameId} could not even load its starting position; the config snapshot or start FEN is corrupt. Exporting a headers-only file.",
                entity.Id);
            metrics?.ReplayTruncated("pgn");
            return BuildUnplayable(entity, config, whiteName, blackName, whiteElo, blackElo, siteBaseUrl, startFen, logger);
        }

        var plies = new List<Ply>(entity.Moves.Count);
        foreach (var record in entity.Moves)
        {
            ApplyResult result;
            try
            {
                // thinkTimeSeconds: 0, exactly like GameReplayBuilder.Build. The clocks this produces
                // are discarded; only the position and the SAN are kept, the real clocks come off the
                // stored MoveRecord below.
                result = engine.Apply(position, Move.Parse(record.Uci), config, 0);
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException or Chess.ChessException)
            {
                // A move that will not replay means the stored log and the stored config disagree, a
                // corrupt row, not a bad request. Export the prefix that does replay rather than 500ing
                // a public download: a truncated file is still a useful record, and an exception here
                // would be permanent for that game. Mirrors GameReplayBuilder.Build's identical catch,
                // including the ChessException case: it fires for board.Move throwing
                // ChessGameEndedException on an already-finished position (C-3-3/C-3-5) as well as for
                // a move referencing a piece that is not on the board.
                //
                // C-8-3: this used to be silent - no log, no metric, no field on the file - so a jsonb
                // row that stopped exporting was invisible to the maintainer forever, and the file
                // still asserted [Result]/[KiriChessTermination] from the entity over a movetext that
                // stops short of them.
                logger?.LogWarning(ex,
                    "PGN export of game {GameId} truncated at ply {Ply} of {TotalPlies}: the stored move log and config snapshot disagree.",
                    entity.Id, record.Ply, entity.Moves.Count);
                metrics?.ReplayTruncated("pgn");
                break;
            }

            position = result.NewPosition;
            var san = string.IsNullOrEmpty(result.San) ? record.Uci : result.San;
            plies.Add(new Ply(san, record.Mover, record.WhiteTimeAfter, record.BlackTimeAfter, record.WhiteDelta, record.BlackDelta));
        }

        // Header block, then a blank line, then the movetext, then a blank line: each tag line from
        // AppendHeaders already ends in '\n', so one more '\n' opens a blank line before the
        // movetext; the movetext itself carries no trailing newline, so it takes two more, one to
        // terminate its own last line and one to leave the file ending on a blank line, the PGN
        // convention that lets several exported games be concatenated and still parse as separate
        // games.
        var sb = new StringBuilder();
        AppendHeaders(sb, entity, config, whiteName, blackName, whiteElo, blackElo, siteBaseUrl, startFen);
        sb.Append('\n');
        sb.Append(BuildMovetext(config, startFen, plies, entity.Result, logger, entity.Id));
        sb.Append('\n').Append('\n');
        return sb.ToString();
    }

    /// <summary>
    /// C-8-6: the file for a row so corrupt its starting position cannot even be loaded - the header
    /// block (which never touches the engine) plus an empty movetext, the same shape
    /// <c>PgnExportTests.An_empty_move_list_still_produces_a_valid_file</c> already proves is a valid
    /// PGN. <c>%kcfg</c> falls back to a placeholder if the config itself is what failed
    /// <see cref="KiriChessConfig.Validate"/>, via the same guard <see cref="BuildMovetext"/> takes
    /// for that call below.
    /// </summary>
    private static string BuildUnplayable(
        Game entity, KiriChessConfig config, string? whiteName, string? blackName,
        int? whiteElo, int? blackElo, string? siteBaseUrl, string startFen, ILogger? logger)
    {
        var sb = new StringBuilder();
        AppendHeaders(sb, entity, config, whiteName, blackName, whiteElo, blackElo, siteBaseUrl, startFen);
        sb.Append('\n');
        sb.Append(BuildMovetext(config, startFen, Array.Empty<Ply>(), entity.Result, logger, entity.Id));
        sb.Append('\n').Append('\n');
        return sb.ToString();
    }

    /// <summary>One replayed ply, carrying only what the movetext needs: the SAN and the four
    /// <see cref="MoveRecord"/> numbers verbatim, never the replay's own (zero-think-time) clocks.
    /// See <see cref="ReplayPlyDto"/>'s doc comment for why those two clocks always disagree.</summary>
    private readonly record struct Ply(string San, Side Mover, int WhiteTimeAfter, int BlackTimeAfter, int WhiteDelta, int BlackDelta);

    private static void AppendHeaders(
        StringBuilder sb, Game entity, KiriChessConfig config,
        string? whiteName, string? blackName, int? whiteElo, int? blackElo,
        string? siteBaseUrl, string startFen)
    {
        var createdAtUtc = entity.CreatedAt.UtcDateTime;
        var dateTag = createdAtUtc.ToString("yyyy.MM.dd", CultureInfo.InvariantCulture);
        var isStandardStart = startFen == KiriChessEngine.StartFen;

        // [Event] is prose a human reads, so it carries the pool's display name; [KiriChessPool]
        // below carries the storage key. Those are deliberately different strings: the key is a
        // neutral id that never moves, and writing it here would put "KiriChess rated steal1 game"
        // in front of a person. PoolLabel falls back to the key for a pool the config no longer
        // declares, so an archived game from a retired pool still exports.
        var eventTag = entity.IsRated && entity.RatingPool is not null
            ? $"KiriChess rated {KiriChessConfig.PoolLabel(entity.RatingPool)} game"
            : "KiriChess casual game";
        var siteTag = string.IsNullOrEmpty(siteBaseUrl) ? "?" : $"{siteBaseUrl}/replay/{entity.Id}";
        var whiteTag = string.IsNullOrWhiteSpace(whiteName) ? GameReplayBuilder.UnknownPlayerName : whiteName;
        var blackTag = string.IsNullOrWhiteSpace(blackName) ? GameReplayBuilder.UnknownPlayerName : blackName;

        // The Seven Tag Roster: always all seven, in this exact order, per PGN §8.1.1.
        AppendTag(sb, "Event", eventTag);
        AppendTag(sb, "Site", siteTag);
        AppendTag(sb, "Date", dateTag);
        AppendTag(sb, "Round", "-");
        AppendTag(sb, "White", whiteTag);
        AppendTag(sb, "Black", blackTag);
        AppendTag(sb, "Result", ResultToken(entity.Result));

        AppendTag(sb, "UTCDate", dateTag);
        AppendTag(sb, "UTCTime", createdAtUtc.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
        if (whiteElo is { } we) AppendTag(sb, "WhiteElo", we.ToString(CultureInfo.InvariantCulture));
        if (blackElo is { } be) AppendTag(sb, "BlackElo", be.ToString(CultureInfo.InvariantCulture));
        AppendTag(sb, "TimeControl", $"{config.StartTimeSeconds}+{config.BaseIncrement}");
        AppendTag(sb, "Termination", TerminationTag(entity.Termination));

        // No [Variant] tag: the moves and positions are standard chess, only the clock is unusual,
        // and declaring a variant would make some tools refuse a file they can read perfectly for no
        // benefit (Phase 9e.2, "The format", Decision 3).
        if (!isStandardStart)
        {
            AppendTag(sb, "FEN", startFen);
            AppendTag(sb, "SetUp", "1");
        }

        AppendTag(sb, "KiriChessFormat", FormatVersion);
        AppendTag(sb, "KiriChessId", entity.Id.ToString());
        if (!string.IsNullOrEmpty(entity.RatingPool)) AppendTag(sb, "KiriChessPool", entity.RatingPool);
        AppendTag(sb, "KiriChessRated", entity.IsRated ? "true" : "false");
        AppendTag(sb, "KiriChessTermination", entity.Termination.ToString());
    }

    private static void AppendTag(StringBuilder sb, string name, string value) =>
        sb.Append('[').Append(name).Append(" \"").Append(EscapeTagValue(value)).Append("\"]\n");

    /// <summary>PGN §8.1.1 string escaping: a backslash must be escaped before a quote is, or an
    /// escaped quote's own backslash would itself get re-escaped.</summary>
    private static string EscapeTagValue(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string ResultToken(GameResult result) => result switch
    {
        GameResult.WhiteWins => "1-0",
        GameResult.BlackWins => "0-1",
        GameResult.Draw => "1/2-1/2",
        GameResult.Aborted => "*",
        GameResult.Ongoing => "*",
        _ => "*"
    };

    /// <summary>
    /// Standard PGN [Termination] vocabulary ("Normal", "Time forfeit", "Abandoned", "Unterminated").
    /// Written as an explicit arm per <see cref="TerminationReason"/> member with no discard pattern,
    /// so that CS8509 (a named member with no arm) fires the day a new reason is added here rather
    /// than that reason silently rendering as "Normal", a lie to every parser that reads the tag. A
    /// discard arm would suppress CS8509 as well, which is exactly why there is not one; the price is
    /// CS8524, which fires permanently because an enum can hold a value no member names, and which is
    /// suppressed on the next line rather than paid on every build. Suppressing the one does not
    /// weaken the other: they are separate diagnostics. Confirmed empirically that only CS8524 is
    /// emitted today, and that it names the cast-to-12 case rather than any real reason.
    ///
    /// The exact KiriChess reason, which has no standard vocabulary to fit into, goes in
    /// [KiriChessTermination] instead (entity.Termination.ToString(), see AppendHeaders).
    /// </summary>
#pragma warning disable CS8524
    private static string TerminationTag(TerminationReason reason) => reason switch
    {
        TerminationReason.None => "Unterminated",
        TerminationReason.Checkmate => "Normal",
        TerminationReason.Flagfall => "Time forfeit",
        TerminationReason.Stalemate => "Normal",
        TerminationReason.Resignation => "Normal",
        TerminationReason.DrawByRepetition => "Normal",
        TerminationReason.DrawByInsufficientMaterial => "Normal",
        TerminationReason.DrawByFiftyMove => "Normal",
        TerminationReason.Abandoned => "Abandoned",
        TerminationReason.DrawByAgreement => "Normal",
        TerminationReason.ServerRestart => "Unterminated",
        TerminationReason.NoShow => "Abandoned",
    };
#pragma warning restore CS8524

    private static string BuildMovetext(
        KiriChessConfig config, string startFen, IReadOnlyList<Ply> plies, GameResult result,
        ILogger? logger = null, Guid gameId = default)
    {
        var tokens = new List<string> { $"{{ [%kcfg {ConfigStringOrFallback(config, logger, gameId)}] }}" };

        // A SetUp game numbers its first move, and picks its first side to move, off the FEN itself
        // (fields 6 and 2, 1-indexed per the PGN/FEN spec), not off move 1. Getting this wrong is the
        // one thing in this format that produces a file no PGN reader will load at all.
        var fenFields = startFen.Split(' ');
        var moveNumber = fenFields.Length > 5 && int.TryParse(fenFields[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var fullmove)
            ? fullmove
            : 1;
        var isBlackToMove = fenFields.Length > 1 && fenFields[1] == "b";

        foreach (var ply in plies)
        {
            tokens.Add(isBlackToMove ? $"{moveNumber}..." : $"{moveNumber}.");
            tokens.Add(ply.San);

            // C-8-8: the mover for %clk comes from the stored Ply.Mover (captured from
            // MoveRecord.Mover), not from isBlackToMove. The two always agree for a healthy game,
            // which is exactly why this stored-but-unread field survived: for the same corrupt-row
            // shape the truncation guard above exists for, they need not, and reading the FEN
            // alternation named the wrong player's clock rather than the truncation just stopping.
            var moverTimeAfter = ply.Mover == Side.Black ? ply.BlackTimeAfter : ply.WhiteTimeAfter;
            tokens.Add($"{{ [%clk {FormatClock(moverTimeAfter)}] [%kc {ply.WhiteTimeAfter},{ply.BlackTimeAfter},{ply.WhiteDelta},{ply.BlackDelta}] }}");

            // isBlackToMove still drives the move number alternation alone (the SetUp comment above
            // is what it is actually for); it no longer selects the mover.
            if (isBlackToMove) moveNumber++;
            isBlackToMove = !isBlackToMove;
        }

        tokens.Add(ResultToken(result));
        return WrapTokens(tokens);
    }

    /// <summary>
    /// Guards <see cref="KiriChessConfig.ToConfigString"/> (which indexes every rule variable's
    /// dictionary field) against the one config <see cref="BuildUnplayable"/> can hand it: one that
    /// already failed <see cref="KiriChessConfig.Validate"/> (e.g. a jsonb row missing a
    /// since-added piece key). Falls back to a marker value no real config could ever equal, rather
    /// than letting this second indexing throw where the first one (FromFen's own Validate call)
    /// already did.
    /// </summary>
    private static string ConfigStringOrFallback(KiriChessConfig config, ILogger? logger, Guid gameId)
    {
        try
        {
            return config.ToConfigString();
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException)
        {
            logger?.LogWarning(ex,
                "Could not serialise game {GameId}'s corrupt config snapshot into %kcfg; using a placeholder instead.",
                gameId);
            return "CORRUPT=true";
        }
    }

    /// <summary>Seconds as H:MM:SS, hours not zero-padded. Clocks are non-negative by construction; a
    /// KiriChess clock can exceed its own starting value (the economy's whole point), so this never
    /// clamps upward.</summary>
    private static string FormatClock(int seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}";
    }

    /// <summary>Wraps at 80 columns on token boundaries. A comment is one unbreakable token: the
    /// %kcfg comment is the only one ever long enough to exceed 80 alone, and it is left alone on its
    /// own line rather than split, which would corrupt the brace-comment grammar.</summary>
    private static string WrapTokens(IReadOnlyList<string> tokens)
    {
        var sb = new StringBuilder();
        var lineLength = 0;
        foreach (var token in tokens)
        {
            if (lineLength == 0)
            {
                sb.Append(token);
                lineLength = token.Length;
            }
            else if (lineLength + 1 + token.Length <= 80)
            {
                sb.Append(' ').Append(token);
                lineLength += 1 + token.Length;
            }
            else
            {
                sb.Append('\n').Append(token);
                lineLength = token.Length;
            }
        }

        return sb.ToString();
    }
}
