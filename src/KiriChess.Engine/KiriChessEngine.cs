using System;
using System.Collections.Generic;
using System.Linq;
using KiriChess.Engine.Config;
using KiriChess.Engine.Core;
using Chess;
using ChessBoard = Chess.ChessBoard;
using ChessMove = Chess.Move;
using CoreMove = KiriChess.Engine.Core.Move;
using CorePieceType = KiriChess.Engine.Core.PieceType;

namespace KiriChess.Engine;

/// <summary>
/// The KiriChess rule engine. Pure and stateless: it knows nothing about databases, networking
/// or UI. It wraps a standard chess move generator (Gera.Chess) and layers the KiriChess clock
/// economy on top. Every game state is captured by an immutable <see cref="GamePosition"/>; the one
/// operation that matters is <see cref="Apply"/>.
/// </summary>
public sealed class KiriChessEngine
{
    /// <summary>Standard chess starting position (FEN).</summary>
    public const string StartFen = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";

    // Auto-detect the endgames that are decidable from a single position/FEN. Repetition is handled
    // by KiriChess itself (Rule 4) because we rebuild the board per move and carry the history
    // in GamePosition instead of the underlying board.
    private const AutoEndgameRules AutoRules = AutoEndgameRules.InsufficientMaterial | AutoEndgameRules.FiftyMoveRule;

    /// <summary>Creates the initial position for a new game from the standard chess start.</summary>
    public GamePosition NewGame(KiriChessConfig config)
    {
        config.Validate();
        return CreatePosition(config, StartFen);
    }

    /// <summary>Creates a position from an arbitrary FEN (useful for tests and puzzles).</summary>
    public GamePosition FromFen(KiriChessConfig config, string fen)
    {
        config.Validate();
        return CreatePosition(config, fen);
    }

    private GamePosition CreatePosition(KiriChessConfig config, string fen)
    {
        var board = ChessBoard.LoadFromFen(fen, AutoRules);
        var key = PositionKey(board, board.ToFen());
        var counts = new Dictionary<string, int> { [key] = 1 };
        return new GamePosition(
            board.ToFen(),
            MapColor(board.Turn),
            config.StartTimeSeconds,
            config.StartTimeSeconds,
            counts);
    }

    /// <summary>All legal moves available in the given position.</summary>
    public IReadOnlyList<CoreMove> LegalMoves(GamePosition position)
    {
        var board = ChessBoard.LoadFromFen(position.Fen, AutoRules);
        return board.Moves().Select(ToCoreMove).ToList();
    }

    /// <summary>Whether a move is legal in the given position.</summary>
    /// <remarks>
    /// Defined as membership in <see cref="LegalMoves"/> rather than as a call to the underlying
    /// library's own validator, and both halves of that are deliberate.
    ///
    /// <para>The library's validator is <b>wider</b> than its generator: it also accepts castling
    /// written as king-onto-own-rook (<c>e1h1</c>, <c>e1a1</c>), which the generator never produces -
    /// 50 accepted from→to pairs against 48 generated moves in the standard "Kiwipete" position. A
    /// move the engine never offered must not be playable: it would be persisted as a non-standard UCI
    /// that <c>ui/lib/costPreview.ts</c> does not recognise as castling, so the client's preview and
    /// the server would price the same move differently.
    ///
    /// <para>The library's validator also <b>throws</b> rather than returning false for input that is
    /// merely wrong - <c>ChessPieceNotFoundException</c> for a move off an empty square,
    /// <c>ChessArgumentException</c> for an off-board square. Both leaked straight through this method,
    /// and <c>GameSession.ApplyMove</c> calls it on client-supplied UCI, so a well-formed move naming
    /// an empty square became an unhandled hub exception instead of "Illegal move". Answering from the
    /// generated set cannot throw.</para>
    ///
    /// <para>One documented exception to exact membership: a promotion submitted without a target
    /// (UCI <c>b7b8</c>) is accepted as the queen variant. The generator never emits a bare
    /// promotion - it emits four fully-qualified ones - and the historical contract, reachable from
    /// the wire via <see cref="CoreMove.Parse"/>, is that a missing target defaults to queen.
    /// Castling written as king-onto-own-rook is <b>not</b> given the same courtesy.</para>
    ///
    /// <para>Cost: one move generation per call. Acceptable - <c>EngineThroughputTests</c> measured
    /// this workload at ~421 <c>LegalMoves</c> calls against 9,322 <c>Apply</c> calls, and the live
    /// path already generates the same list for the move DTO. <see cref="Apply"/> generates once from
    /// the board it is about to mutate, rather than calling this method and then loading a second
    /// board.</para>
    /// </remarks>
    public bool IsLegal(GamePosition position, CoreMove move)
    {
        if (position is null) throw new ArgumentNullException(nameof(position));
        if (move is null) throw new ArgumentNullException(nameof(move));
        return ResolveAgainst(LegalMoves(position), move) is not null;
    }

    /// <summary>
    /// Applies a move, returning the resulting position, the net clock change for each side, the
    /// itemised events behind those numbers, and the game status.
    /// </summary>
    /// <param name="position">Current position.</param>
    /// <param name="move">The move to play (must be legal).</param>
    /// <param name="config">Rule configuration.</param>
    /// <param name="thinkTimeSeconds">Wall-clock seconds the mover spent on this move (drives flag-fall). 0 for pure-economy scenarios.</param>
    public ApplyResult Apply(GamePosition position, CoreMove move, KiriChessConfig config, int thinkTimeSeconds = 0)
    {
        if (position is null) throw new ArgumentNullException(nameof(position));
        if (move is null) throw new ArgumentNullException(nameof(move));
        if (thinkTimeSeconds < 0) throw new ArgumentOutOfRangeException(nameof(thinkTimeSeconds));
        if (move.Promotion is { } target &&
            target is not (CorePieceType.Queen or CorePieceType.Rook or CorePieceType.Bishop or CorePieceType.Knight))
            throw new ArgumentException(
                $"{target} is not a valid promotion target.", nameof(move));
        config.Validate();

        var board = ChessBoard.LoadFromFen(position.Fen, AutoRules);

        // AutoRules auto-detects insufficient material and the fifty-move rule at load time, so
        // FromFen will happily hand back a position the library already considers finished (K+N vs K,
        // K+B vs K, K vs K, or a halfmove clock at 100). LegalMoves still lists moves there and
        // IsLegal still says yes, and board.Move below would then throw Chess.ChessGameEndedException,
        // a type a caller has to reference the chess package to catch. IsLegal's remarks promise no
        // library exception crosses this boundary; that promise was true of IsLegal and not of Apply.
        // Unreachable from live play today, since Game.StartFen is only ever KiriChessEngine.StartFen,
        // and reachable the moment anything admits a caller-supplied FEN, which is what FromFen's own
        // summary advertises it for.
        if (board.IsEndGame)
            throw new ArgumentException(
                $"Position {position.Fen} is already finished ({board.EndGame!.EndgameType}); no move can be applied.",
                nameof(position));

        var mover = position.SideToMove;

        // Membership in the generated set, not board.IsValidMove - see IsLegal's remarks.
        // Resolved here, before ToChessMove, so an off-board square never reaches the library
        // constructor that would throw ChessArgumentException. A bare promotion (UCI without a
        // piece letter) is mapped onto the queen variant the generator actually emits.
        var legal = board.Moves().Select(ToCoreMove).ToList();
        var resolved = ResolveAgainst(legal, move)
            ?? throw new ArgumentException($"Illegal move {Describe(move)} in position {position.Fen}", nameof(move));

        var chessMove = ToChessMove(resolved);
        if (resolved.Promotion is { } promo)
            board.OnPromotePawn += (_, e) => e.PromotionResult = ToPromotionType(promo);

        board.Move(chessMove);

        // SAN comes off the library rather than being derived here: disambiguation ("Rae1") needs the
        // position before the move and the +/# suffix needs the position after it, and Move() is where
        // both are known at once. It stamps the San onto the move object it executed, which is why this
        // reads chessMove and not a freshly parsed one.
        var san = NormalizeSan(chessMove.San);

        var newFen = board.ToFen();
        var key = PositionKey(board, newFen);
        var seenBefore = position.RepetitionCounts.ContainsKey(key);
        var newCount = position.RepetitionCounts.GetValueOrDefault(key) + 1;

        var events = new List<ClockEvent>();
        int whiteDelta = 0, blackDelta = 0;

        void Add(ClockEventType type, Side side, int amount, string? detail = null)
        {
            if (amount == 0) return;
            events.Add(new ClockEvent(type, side, amount, detail));
            if (side == Side.White) whiteDelta += amount; else blackDelta += amount;
        }

        var nullified = config.RepeatedPositionNullifiesTime && seenBefore;

        if (nullified)
        {
            // Rule 4: "once seen, always seen", this move's economy is cancelled entirely.
            events.Add(new ClockEvent(ClockEventType.RepetitionNullified, mover, 0, key));
        }
        else
        {
            ApplyEconomy(config, board, chessMove, mover, Add);
        }

        // Base time-control increment is not a KiriChess rule and is always credited.
        Add(ClockEventType.Increment, mover, config.BaseIncrement);

        // Resolve clocks. Think time is a wall-clock cost borne only by the mover.
        var rawWhite = position.WhiteTimeSeconds + whiteDelta - (mover == Side.White ? thinkTimeSeconds : 0);
        var rawBlack = position.BlackTimeSeconds + blackDelta - (mover == Side.Black ? thinkTimeSeconds : 0);
        var moverRaw = mover == Side.White ? rawWhite : rawBlack;

        var (status, winner) = ResolveStatus(config, board, mover, moverRaw, newCount, events);

        var newCounts = new Dictionary<string, int>(position.RepetitionCounts) { [key] = newCount };
        var newPosition = new GamePosition(
            newFen,
            MapColor(board.Turn),
            Math.Max(0, rawWhite),
            Math.Max(0, rawBlack),
            newCounts);

        return new ApplyResult(newPosition, whiteDelta, blackDelta, events, status, winner, san);
    }

    // ----------------------------------------------------------------------------------
    // Rule economy (Rules 1, 2, 4 + promotion/castling costs)
    // ----------------------------------------------------------------------------------
    private static void ApplyEconomy(
        KiriChessConfig config,
        ChessBoard board,
        ChessMove move,
        Side mover,
        Action<ClockEventType, Side, int, string?> add)
    {
        var opponent = mover.Opposite();

        // Rule 1, capture economy (captor gains, victim loses; scaled by captured piece value and EconomyScale).
        if (move.CapturedPiece is { } captured)
        {
            var value = config.PieceValues[MapPiece(captured.Type)];
            var baseAmount = value * config.CaptureMult * config.EconomyScale;

            if (config.CaptureCaptorGainEnabled)
                add(ClockEventType.CaptureGain, mover, baseAmount, captured.Type.AsChar.ToString());

            if (config.CaptureVictimLossEnabled)
                add(ClockEventType.CaptureLoss, opponent, -baseAmount, captured.Type.AsChar.ToString());
        }

        // Movement toll + castling cost (Rule 2). Castling replaces the king's toll with a flat fee.
        if (move.IsCastling)
        {
            if (config.MoveCostMode != MoveCostMode.None)
            {
                var cost = config.CastleCostFlat * config.EconomyScale;
                add(ClockEventType.CastleCost, mover, -cost, null);
            }
        }
        else
        {
            var toll = MovementToll(config, move) * config.EconomyScale;
            add(ClockEventType.MovementToll, mover, -toll, null);
        }

        // Promotion cost, priced off the promoted-to piece, so under-promotion is cheaper.
        if (move.IsPromotion && move.Promotion is { } promoted)
        {
            var value = config.PieceValues[MapPiece(promoted.Type)];
            var cost = config.PromotionCostMult * value * config.EconomyScale;
            add(ClockEventType.PromotionCost, mover, -cost, promoted.Type.AsChar.ToString());
        }

        // Rule 3, check chip (a checkmate ends the game, so it is not treated as a chip).
        if (move.IsCheck && !move.IsMate)
        {
            int gain = config.CheckCheckerGain * config.EconomyScale;
            int loss = config.CheckVictimLoss * config.EconomyScale;

            // IsDoubleCheck is the most expensive thing in the move path: 64 squares and up to sixteen
            // full per-piece generations, on every checking move. Skip it in the two cases where the
            // answer provably cannot change an amount. Multiplier 1 leaves both amounts untouched
            // (x * 1 == x), and both amounts already zero stay zero whatever the multiplier is
            // (0 * m == 0, and the multiplier is validated non-negative). Neither is a rounding
            // argument, they are identities, so no priced move changes.
            if (config.DoubleCheckChipMultiplier != 1 && (gain != 0 || loss != 0) && IsDoubleCheck(board, mover))
            {
                gain *= config.DoubleCheckChipMultiplier;
                loss *= config.DoubleCheckChipMultiplier;
            }

            add(ClockEventType.CheckGain, mover, gain, null);
            add(ClockEventType.CheckLoss, opponent, -loss, null);
        }
    }

    private static bool IsDoubleCheck(ChessBoard board, Side checker)
    {
        var checkerColor = checker == Side.White ? PieceColor.White : PieceColor.Black;
        var kingPos = checker == Side.White ? board.BlackKing : board.WhiteKing;

        int attackCount = 0;
        for (short x = 0; x < 8; x++)
        {
            for (short y = 0; y < 8; y++)
            {
                var piece = board[x, y];
                if (piece != null && piece.Color == checkerColor)
                {
                    var moves = board.GeneratePositions(new Position(x, y));
                    if (moves != null && moves.Any(m => m.X == kingPos.X && m.Y == kingPos.Y))
                    {
                        attackCount++;
                        if (attackCount >= 2) return true;
                    }
                }
            }
        }
        return false;
    }

    private static int MovementToll(KiriChessConfig config, ChessMove move)
    {
        var pieceType = MapPiece(move.Piece.Type);
        var distance = ChebyshevDistance(move);
        return config.MoveCostMode switch
        {
            MoveCostMode.None => 0,
            MoveCostMode.Flat => config.MoveCostFlat,
            MoveCostMode.PerPiece => config.MoveCostPerPiece[pieceType],
            MoveCostMode.Distance => config.MoveCostPerSquare * distance,
            MoveCostMode.PerPieceXDistance => config.MoveCostPerPiece[pieceType] * distance,
            _ => 0
        };
    }

    private static int ChebyshevDistance(ChessMove move)
    {
        var dx = Math.Abs(move.NewPosition.X - move.OriginalPosition.X);
        var dy = Math.Abs(move.NewPosition.Y - move.OriginalPosition.Y);
        return Math.Max(dx, dy);
    }

    // ----------------------------------------------------------------------------------
    // Game-status resolution
    // ----------------------------------------------------------------------------------
    private static (GameStatus, Side?) ResolveStatus(
        KiriChessConfig config,
        ChessBoard board,
        Side mover,
        int moverRawClock,
        int newRepetitionCount,
        List<ClockEvent> events)
    {
        // Checkmate ends the game immediately, it wins regardless of any flag the move might trip.
        if (board.IsEndGame && board.EndGame!.EndgameType == EndgameType.Checkmate)
            return (GameStatus.Checkmate, mover);

        // Every other position-decided ending outranks the flag for exactly the reason checkmate does:
        // the game was already over when the clock reached zero, so there is no clock left to lose on.
        // docs/RULES.md's "How games end" table says so ("Stalemate: Draw, as in classical chess",
        // "Threefold repetition: Draw, automatic"), and so does the ThreefoldEndsTheGame invariant in
        // EconomyInvariantTests. Before this ordering the mover lost on time on the very ply that
        // stalemated, repeated, or crossed the fifty-move mark.
        //
        // No FlagFall clock event is emitted on these plies. The event is the itemised record of the
        // clock deciding an outcome, and here the position decided it: the same draw is returned
        // whatever the mover's remaining time was, so "flag fell" in the move list of a drawn game
        // would describe a cause that did not apply. The two endings the flag really does decide,
        // below, still emit it.
        if (board.IsEndGame)
        {
            switch (board.EndGame!.EndgameType)
            {
                case EndgameType.Stalemate: return (GameStatus.Stalemate, null);
                case EndgameType.InsufficientMaterial: return (GameStatus.DrawByInsufficientMaterial, null);
                case EndgameType.FiftyMoveRule: return (GameStatus.DrawByFiftyMove, null);
            }
        }

        if (config.ThreefoldRepetitionIsDraw && newRepetitionCount >= 3)
            return (GameStatus.DrawByRepetition, null);

        // Flag-fall: the mover ran their own clock to zero on this move, and nothing above had already
        // ended the game. The outcome itself comes from ResolveFlagFall so that this path and the
        // server's idle-flag sweep cannot answer the same question differently.
        if (config.FlagFallLoses && moverRawClock <= 0)
        {
            events.Add(new ClockEvent(ClockEventType.FlagFall, mover, 0));
            return ResolveFlagFall(config, board.ToFen(), mover);
        }

        return (GameStatus.Ongoing, null);
    }

    /// <summary>
    /// The outcome when <paramref name="flagged"/>'s clock reaches zero in the position
    /// <paramref name="fen"/>: a loss, unless <see cref="KiriChessConfig.FlagVsInsufficientMaterialIsDraw"/>
    /// is set and the opponent cannot possibly mate, in which case it is a draw
    /// (<c>docs/RULES.md</c>, "You run out of time"). The caller decides that a flag has fallen at all,
    /// including honouring <see cref="KiriChessConfig.FlagFallLoses"/>; this only prices the result.
    /// </summary>
    /// <remarks>
    /// Public because a flag arrives by two routes that must agree. The mover can run their own clock
    /// out on a move they submitted, which lands in <see cref="Apply"/>; far more commonly they simply
    /// stop moving, and the server notices the on-turn clock at zero with no move to apply at all. The
    /// second route used to decide the outcome for itself and never consulted material, so a bare king
    /// won on time in precisely the case that actually fires.
    ///
    /// <para>Pure and deterministic like the rest of the engine, and cheap enough for the server's
    /// per-read flag check: it reads the placement field of the FEN and nothing else, allocates one
    /// substring, and touches no board.</para>
    /// </remarks>
    public static (GameStatus Status, Side? Winner) ResolveFlagFall(KiriChessConfig config, string fen, Side flagged)
    {
        if (config is null) throw new ArgumentNullException(nameof(config));
        if (fen is null) throw new ArgumentNullException(nameof(fen));

        if (config.FlagVsInsufficientMaterialIsDraw && !HasMatingMaterial(fen, flagged.Opposite()))
            return (GameStatus.DrawByInsufficientMaterial, null);
        return (GameStatus.FlagFall, flagged.Opposite());
    }

    /// <summary>
    /// Whether <paramref name="side"/> holds enough material to deliver checkmate, used for the
    /// "flag vs. insufficient material is a draw" rule. Conservative: lone king or king + a single
    /// minor is insufficient; anything else counts as sufficient.
    /// </summary>
    private static bool HasMatingMaterial(string fen, Side side)
    {
        var placement = fen.Split(' ')[0];
        int minors = 0;
        foreach (var c in placement)
        {
            var isWhitePiece = char.IsUpper(c);
            if ((side == Side.White) != isWhitePiece) continue;
            switch (char.ToLowerInvariant(c))
            {
                case 'p':
                case 'r':
                case 'q':
                    return true;
                case 'n':
                case 'b':
                    minors++;
                    break;
            }
        }
        return minors >= 2;
    }

    // ----------------------------------------------------------------------------------
    // Mapping between engine-neutral types and the underlying chess library
    // ----------------------------------------------------------------------------------
    /// <summary>
    /// The repetition-relevant part of a FEN: placement, side to move, castling, en passant, with the
    /// en-passant field normalised to <c>-</c> unless an en-passant capture is genuinely available.
    /// </summary>
    /// <remarks>
    /// Gera.Chess writes an en-passant target square after <em>every</em> double pawn push, whether or
    /// not an enemy pawn could actually take there. FIDE defines repetition by whether the capture is
    /// available, so taking the FEN field literally split one position into two: after
    /// <c>1.e4 Nf6 2.Nf3 Ng8 3.Ng1</c> the engine held the e4 placement under both <c>... e3</c> and
    /// <c>... -</c>, one occurrence each, for what the official definition calls one position twice.
    /// Both consequences ran in the unsafe direction: Rule 4 failed to nullify the first recurrence, so
    /// the economy paid again where the guard says it must not, and the threefold counter was split, so
    /// the automatic draw could arrive a cycle late.
    ///
    /// <para>Availability is read off <c>board.Moves()</c>, the legal move list, not off pawn placement:
    /// a capture that would expose the capturing side's own king is not available, and the horizontal-pin
    /// case (the capture vacates both the taking pawn's square and the taken pawn's square at once) is
    /// exactly where placement alone gets it wrong. The generation only runs when the FEN carries an
    /// en-passant square at all, so an ordinary ply in the hot path pays nothing for this.</para>
    /// </remarks>
    private static string PositionKey(ChessBoard board, string fen)
    {
        var parts = fen.Split(' ');
        var enPassant = parts[3];
        if (enPassant != "-" && !EnPassantCaptureAvailable(board, enPassant))
            enPassant = "-";
        return string.Join(' ', parts[0], parts[1], parts[2], enPassant);
    }

    /// <summary>Whether the side to move has a legal en-passant capture onto <paramref name="square"/>.</summary>
    /// <remarks>
    /// This sits in the move path, so it is deliberately built to touch as little as possible while
    /// still answering the legal question rather than a placement-shaped approximation of it.
    ///
    /// <para>An en-passant capture can only start from the two squares flanking the pawn that just
    /// double-pushed, so two board reads decide the common case with no generation at all. Only when a
    /// pawn of the side to move actually stands on one of them does anything get generated, and then
    /// it is <c>board.Moves(from, ...)</c> for that one pawn with <c>generateSan: false</c>, not the
    /// whole-board <c>board.Moves()</c>. That matters twice over: SAN generation runs check and mate
    /// detection, which is the expensive half of a generation and is not needed to answer "can this
    /// pawn take onto that square", and a whole-board generation here would be a second full
    /// generation per ply on top of the one <see cref="Apply"/> already does before the move.</para>
    ///
    /// <para>The per-piece list is the library's legal list, so a capture that would leave the
    /// capturing side's own king in check is correctly reported as unavailable. That is not a detail:
    /// the horizontal-pin case, where the capture vacates the taking pawn's square and the taken
    /// pawn's square at once and exposes the king along the rank, is exactly where a placement-only
    /// test gives the wrong answer, and <c>EnPassantField_IsDropped_WhenTheOnlyCaptureIsIllegal</c>
    /// pins it.</para>
    ///
    /// <para>A pawn on a flanking square reaching the target square can only be capturing en passant:
    /// the target is empty by construction, and a straight push from a flanking square lands on that
    /// square's own file, not on the target's. So landing on the target is the whole test.</para>
    /// </remarks>
    private static bool EnPassantCaptureAvailable(ChessBoard board, string square)
    {
        if (square.Length != 2) return false;
        int x = square[0] - 'a';
        int y = square[1] - '1';
        if (x is < 0 or > 7 || y is < 0 or > 7) return false;

        // Rank the capturing pawn must stand on: alongside the double-pushed pawn, which sits one
        // square beyond the target. Target on rank 3 means White pushed and Black takes from rank 4;
        // target on rank 6 means Black pushed and White takes from rank 5. A target on any other rank
        // is not a position this library can produce, and "no capture available" is the safe reading:
        // it normalises the field away rather than preserving a square nothing can be taken on.
        int fromRank = y switch { 2 => 3, 5 => 4, _ => -1 };
        if (fromRank < 0) return false;

        return CanTakeFrom(x - 1) || CanTakeFrom(x + 1);

        bool CanTakeFrom(int file)
        {
            if (file is < 0 or > 7) return false;

            var piece = board[(short)file, (short)fromRank];
            if (piece is null || piece.Color != board.Turn || piece.Type.AsChar != 'p') return false;

            var moves = board.Moves(new Position((short)file, (short)fromRank), false, false);
            if (moves is null) return false;
            foreach (var m in moves)
                if (m.NewPosition.X == x && m.NewPosition.Y == y) return true;
            return false;
        }
    }

    private static Side MapColor(PieceColor color) => color == PieceColor.White ? Side.White : Side.Black;

    private static CorePieceType MapPiece(Chess.PieceType type) => type.AsChar switch
    {
        'p' => CorePieceType.Pawn,
        'n' => CorePieceType.Knight,
        'b' => CorePieceType.Bishop,
        'r' => CorePieceType.Rook,
        'q' => CorePieceType.Queen,
        'k' => CorePieceType.King,
        _ => throw new InvalidOperationException($"Unknown piece '{type.AsChar}'")
    };

    private static PromotionType ToPromotionType(CorePieceType type) => type switch
    {
        CorePieceType.Queen => PromotionType.ToQueen,
        CorePieceType.Rook => PromotionType.ToRook,
        CorePieceType.Bishop => PromotionType.ToBishop,
        CorePieceType.Knight => PromotionType.ToKnight,
        _ => throw new ArgumentException($"{type} is not a valid promotion target.")
    };

    /// <summary>
    /// The library's SAN with its non-standard stalemate marker removed. Gera.Chess appends <c>$</c>
    /// to a stalemating move; standard SAN has no stalemate suffix at all (the result is carried by
    /// the PGN result tag), and in PGN movetext a bare <c>$</c> begins a NAG, so <c>Qg6$</c> makes the
    /// exported game unparseable by anything but this library. A sweep across every shipped pool found
    /// <c>$</c> is the only non-standard character SAN ever emits, and that it appears exactly when the
    /// status is <see cref="GameStatus.Stalemate"/>.
    /// </summary>
    private static string NormalizeSan(string? san) =>
        string.IsNullOrEmpty(san) ? string.Empty
        : san[^1] == '$' ? san[..^1]
        : san;

    /// <summary>
    /// Maps a caller-supplied move onto the generated set. Exact membership first. A promotion
    /// submitted without a target is the one documented exception: it resolves to queen when that
    /// variant is legal. King-onto-own-rook castling is not mapped.
    /// </summary>
    private static CoreMove? ResolveAgainst(IReadOnlyList<CoreMove> legal, CoreMove move)
    {
        if (legal.Contains(move)) return move;
        if (move.Promotion is null)
        {
            var asQueen = new CoreMove(move.From, move.To, CorePieceType.Queen);
            if (legal.Contains(asQueen)) return asQueen;
        }
        return null;
    }

    /// <summary>
    /// <see cref="CoreMove.ToString"/> throws for an invalid promotion piece, so exception messages
    /// must not interpolate the move itself in that case.
    /// </summary>
    private static string Describe(CoreMove move) =>
        move.Promotion is null
            or CorePieceType.Queen or CorePieceType.Rook or CorePieceType.Bishop or CorePieceType.Knight
            ? move.ToString()
            : $"{move.From}{move.To}";

    private static ChessMove ToChessMove(CoreMove move) => new(move.From, move.To);

    private static CoreMove ToCoreMove(ChessMove move)
    {
        var from = Algebraic(move.OriginalPosition);
        var to = Algebraic(move.NewPosition);
        CorePieceType? promo = move.IsPromotion && move.Promotion is not null
            ? MapPiece(move.Promotion.Type)
            : null;
        return new CoreMove(from, to, promo);
    }

    private static string Algebraic(Position pos) => $"{(char)('a' + pos.X)}{pos.Y + 1}";
}
