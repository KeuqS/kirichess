using KiriChess.Engine;
using KiriChess.Engine.Config;
using KiriChess.Engine.Core;
using KiriChess.Server.Data.Entities;

namespace KiriChess.Server.Games;

/// <summary>
/// A live game held in server memory. Clocks are authoritative here and computed on demand off a
/// monotonic anchor (Phase 6f.1: wall clock steps, NTP, VM migration, host sync, and cannot be
/// trusted to measure an interval), never persisted mid-game. The wall-clock stamp
/// (<see cref="LastMoveAtUtc"/>) is kept alongside it purely for display and persistence. Persisted
/// only at game end (by the registry). The monotonic elapsed interval is further net of each side's
/// server-measured network round-trip before it is charged (Phase 6f.2), see
/// <see cref="ChargedElapsed"/>, the single function every caller (move-charging and both flag-fall
/// paths) goes through.
/// </summary>
public sealed class GameSession
{
    private readonly KiriChessEngine _engine;
    private readonly IGameClock _clock;
    private readonly LagCompensationOptions _lag;
    private readonly List<MoveRecord> _moves = new();

    /// <summary>
    /// Serialises everything that reads or writes this session's mutable state. A session is reached
    /// concurrently from at least five places - the hub's move path (a second tab, or a forwarded
    /// command from another instance), the HTTP move endpoint, the public bot API, the one-second
    /// flag-fall sweep (plus the lazy <see cref="GameRegistry.Get"/> check on every read), the latency
    /// probe, and the write-behind snapshot persister - and none of them were coordinated before.
    /// Without this, two callers could both pass the turn/legality checks in <see cref="ApplyMove"/>
    /// before either wrote, producing two broadcasts describing different positions and one ply number
    /// handed out twice, and the persister could enumerate <see cref="_moves"/> mid-<c>Add</c>.
    ///
    /// A plain monitor is the right tool and not a <see cref="SemaphoreSlim"/>: every critical section
    /// below is microseconds of in-memory work, and nothing inside one awaits or performs I/O. Keep it
    /// that way - the lock must never be held across an await. It is per-session, so contention only
    /// ever exists between two writers to the same game and costs nothing at the instance ceiling.
    /// </summary>
    private readonly object _gate = new();

    /// <summary>
    /// Monotonic anchor for "when did the current turn start" (Phase 6f.1). <see cref="LastMoveAtUtc"/>
    /// is the same instant on the wall clock and is what gets displayed and persisted; this is what
    /// elapsed time is actually measured from, because only this one cannot step.
    /// </summary>
    private long _lastMoveTimestamp;

    /// <summary>
    /// Sub-second think time a side has spent but not yet been charged for, carried into that side's
    /// next turn (C-4-2). Clocks are whole seconds end to end, so a ply charges whole seconds and the
    /// remainder used to be discarded when the anchor was reset: every move was undercharged by up to
    /// a full second and the shortfall accumulated linearly with move count. Measured before this, on
    /// a 60s config, twelve plies at exactly 1.9s each charged 6 seconds against 11.4 real ones, and a
    /// caller timing its submissions just under an integer boundary could pay about half of what it
    /// used, repeatably and without leaving a trace.
    ///
    /// <para>Per side, not one shared field, because the players alternate and the remainder belongs
    /// to whoever spent it. Written only by <see cref="ApplyMove"/>, under <see cref="_gate"/> like
    /// <see cref="_lastMoveTimestamp"/>, which is the field it partners: the pair is "when did this
    /// turn start" and "what was already owing when it did". The read-only clock paths
    /// (<see cref="LiveRemaining"/>, and therefore <see cref="CheckFlagFall"/>) add the carry in but
    /// never clear it, so a flag decision sees the same seconds the next move would charge and neither
    /// double-counts the other. Each entry is always under one second and is deliberately not
    /// persisted: a restore does not charge downtime, so it does not carry a remainder either.</para>
    /// </summary>
    private TimeSpan _whiteCarry;
    private TimeSpan _blackCarry;

    // Per-side smoothed round-trip estimate, in milliseconds; null until the first measurement. Not a
    // dictionary keyed by player id, because a solo/practice game has the same id on both sides
    // (Phase 6f.2).
    private double? _whiteRttMs;
    private double? _blackRttMs;

    /// <summary>
    /// Ceiling on a single RTT sample before it enters the EMA. One pathological sample (a client that
    /// was garbage-collecting, a laptop lid closing) must not poison a player's estimate for the rest
    /// of the game; the compensation cap alone would not prevent that, because the EMA keeps the bad
    /// value around for many plies.
    /// </summary>
    private const double MaxRttSampleMs = 2_000;

    public Guid Id { get; }
    public Guid WhitePlayerId { get; }
    public Guid BlackPlayerId { get; }
    public string? RatingPool { get; }
    public bool IsRated { get; }

    /// <summary>White sat down through the public API. Fixed for the life of the session.</summary>
    public bool WhiteViaApi { get; }

    /// <summary>Black's counterpart of <see cref="WhiteViaApi"/>.</summary>
    public bool BlackViaApi { get; }

    public KiriChessConfig Config { get; }

    public GamePosition Position { get; private set; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset LastMoveAtUtc { get; private set; }
    public DateTimeOffset? EndedAt { get; private set; }

    public GameStatus Status { get; private set; } = GameStatus.Ongoing;
    public Side? Winner { get; private set; }
    public TerminationReason Termination { get; private set; } = TerminationReason.None;
    public Guid? NoShowAtFaultPlayerId { get; private set; }

    /// <summary>The side with a standing (unanswered) draw offer, or null. Cleared on any move.</summary>
    public Side? DrawOfferedBy { get; private set; }

    /// <summary>
    /// A snapshot of the move log, copied under <see cref="_gate"/>. Deliberately not the live list:
    /// handing out <see cref="_moves"/> itself let a reader (the snapshot persister, a spectator join,
    /// the flag-fall sweep) enumerate it while a move was being appended. Callers that only need the
    /// ply count should use <see cref="MoveCount"/>, which does not allocate.
    /// </summary>
    public IReadOnlyList<MoveRecord> Moves
    {
        get { lock (_gate) return _moves.ToArray(); }
    }

    /// <summary>
    /// Plies with <see cref="MoveRecord.Ply"/> above <paramref name="throughPly"/>, copied under
    /// <see cref="_gate"/>. The snapshot writer uses this so a long game does not allocate a copy of
    /// every earlier ply just to insert the new one.
    /// </summary>
    public MoveRecord[] MovesAfter(int throughPly)
    {
        lock (_gate)
        {
            var start = 0;
            while (start < _moves.Count && _moves[start].Ply <= throughPly) start++;
            var count = _moves.Count - start;
            if (count <= 0) return [];
            var tail = new MoveRecord[count];
            _moves.CopyTo(start, tail, 0, count);
            return tail;
        }
    }

    /// <summary>Plies played so far. The allocation-free form of <c>Moves.Count</c>.</summary>
    public int MoveCount
    {
        get { lock (_gate) return _moves.Count; }
    }

    public bool IsOver => Status != GameStatus.Ongoing;

    public GameSession(
        KiriChessEngine engine,
        KiriChessConfig config,
        Guid whitePlayerId,
        Guid blackPlayerId,
        string? ratingPool,
        bool isRated,
        IGameClock? clock = null,
        LagCompensationOptions? lag = null,
        bool whiteViaApi = false,
        bool blackViaApi = false)
    {
        _engine = engine;
        _clock = clock ?? SystemGameClock.Instance;
        _lag = lag ?? new LagCompensationOptions();
        Config = config;
        Id = Guid.NewGuid();
        WhitePlayerId = whitePlayerId;
        BlackPlayerId = blackPlayerId;
        RatingPool = ratingPool;
        IsRated = isRated;
        WhiteViaApi = whiteViaApi;
        BlackViaApi = blackViaApi;

        Position = _engine.NewGame(config);
        CreatedAt = _clock.UtcNow;
        LastMoveAtUtc = CreatedAt;
        _lastMoveTimestamp = _clock.Timestamp;
    }

    /// <summary>
    /// Private ctor used by <see cref="Restore"/> to rebuild a session with a known id/creation time
    /// from a persisted entity. Sets every readonly field directly; the "Started" structured log
    /// line lives in <see cref="GameRegistry.CreateAsync"/>, which is only reached for genuinely new
    /// games, not boot-time recoveries.
    /// </summary>
    private GameSession(
        KiriChessEngine engine,
        KiriChessConfig config,
        Guid id,
        Guid whitePlayerId,
        Guid blackPlayerId,
        string? ratingPool,
        bool isRated,
        DateTimeOffset createdAt,
        IGameClock clock,
        LagCompensationOptions lag,
        bool whiteViaApi,
        bool blackViaApi)
    {
        _engine = engine;
        _clock = clock;
        _lag = lag;
        Config = config;
        Id = id;
        WhitePlayerId = whitePlayerId;
        BlackPlayerId = blackPlayerId;
        RatingPool = ratingPool;
        IsRated = isRated;
        WhiteViaApi = whiteViaApi;
        BlackViaApi = blackViaApi;
        Position = engine.NewGame(config); // placeholder; Restore overwrites after replay
        CreatedAt = createdAt;
        LastMoveAtUtc = createdAt;
        _lastMoveTimestamp = clock.Timestamp;
    }

    /// <summary>Player id whose turn it is.</summary>
    public Guid PlayerOnTurn => Position.SideToMove == Side.White ? WhitePlayerId : BlackPlayerId;

    /// <summary>Live remaining seconds for a side (authoritative minus the on-the-clock elapsed).</summary>
    public int LiveRemaining(Side side)
    {
        lock (_gate) return RemainingUnlocked(side);
    }

    /// <summary>
    /// One reading of both clocks plus the milliseconds a client must subtract from the side to
    /// move before counting down. <see cref="LiveRemaining"/> is whole seconds, so the fraction of
    /// the current second (carry from the previous turn, plus whatever this turn has already used)
    /// is invisible on that integer. A countdown that starts from the integer alone shows up to a
    /// second more than <see cref="CheckFlagFall"/> will allow.
    ///
    /// <para>The offset also includes half the side-to-move's measured round-trip once that credit
    /// is used up. The credit itself already stops the opening of a turn - and the flight of the
    /// move that started it - from being charged, so it is not subtracted again while it still
    /// covers the interval. After it is spent, the payload is in flight while the clock is running,
    /// and half the round-trip is the estimate of that one-way delay.</para>
    /// </summary>
    public (int WhiteSeconds, int BlackSeconds, int ActiveOffsetMs) LiveClockSnapshot()
    {
        lock (_gate)
        {
            var (charged, raw, creditMs) = ReadCharge();
            var whole = WholeSeconds(charged);
            var white = Position.TimeFor(Side.White);
            var black = Position.TimeFor(Side.Black);
            if (!IsOver)
            {
                if (Position.SideToMove == Side.White) white = Math.Max(0, white - whole);
                else black = Math.Max(0, black - whole);
            }

            return (white, black, OffsetMs(charged, whole, raw, creditMs));
        }
    }

    private int RemainingUnlocked(Side side)
    {
        var baseRemaining = Position.TimeFor(side);
        if (IsOver || side != Position.SideToMove) return baseRemaining;
        return Math.Max(0, baseRemaining - ElapsedSeconds());
    }

    private int OffsetMs(TimeSpan charged, int wholeSeconds, TimeSpan raw, int creditMs)
    {
        if (IsOver) return 0;
        var remainder = charged - TimeSpan.FromSeconds(wholeSeconds);
        // Ceil, so the face never shows a millisecond the flag has already consumed.
        var fracMs = remainder.Ticks <= 0
            ? 0
            : (int)((remainder.Ticks + TimeSpan.TicksPerMillisecond - 1) / TimeSpan.TicksPerMillisecond);
        var transitMs = raw > TimeSpan.FromMilliseconds(creditMs) ? creditMs / 2 : 0;
        return fracMs + transitMs;
    }

    /// <summary>
    /// Records a server-measured round-trip for one of the players (Phase 6f.2) and folds it into that
    /// side's exponential moving average. Called by <see cref="LatencyProbe"/>; deliberately tolerant,
    /// an id that is not a player in this game is ignored rather than throwing, because a probe result
    /// arriving for a game the player already left must never surface as an error to anybody.
    /// </summary>
    public void ReportLatency(Guid playerId, TimeSpan roundTrip)
    {
        if (playerId != WhitePlayerId && playerId != BlackPlayerId) return;

        var sample = Math.Clamp(roundTrip.TotalMilliseconds, 0, MaxRttSampleMs);
        var alpha = Math.Clamp(_lag.EmaAlpha, 0, 1);

        // A solo/practice game has one player on both sides, so the one measured connection is the
        // authority for both, otherwise Black would sit permanently uncompensated in a game the same
        // person is playing against themselves.
        lock (_gate)
        {
            if (playerId == WhitePlayerId) _whiteRttMs = Blend(_whiteRttMs, sample, alpha);
            if (playerId == BlackPlayerId) _blackRttMs = Blend(_blackRttMs, sample, alpha);
        }

        static double Blend(double? current, double sample, double alpha) =>
            current is null ? sample : alpha * sample + (1 - alpha) * current.Value;
    }

    /// <summary>
    /// Milliseconds credited to <paramref name="side"/> for network round-trip, capped. Zero when
    /// compensation is off or no measurement exists yet, a player we have never probed is charged
    /// exactly as before, which is also why bot/practice games and the HTTP move endpoint are
    /// untouched by 6f.2.
    /// </summary>
    internal int CompensationMs(Side side)
    {
        if (!_lag.LagCompensationEnabled) return 0;
        // A double? is not read atomically, and LatencyProbe writes these from its own thread. The
        // monitor is reentrant, so the callers below that already hold it pay nothing extra.
        lock (_gate)
        {
            var ema = side == Side.White ? _whiteRttMs : _blackRttMs;
            if (ema is null) return 0;
            return (int)Math.Clamp(ema.Value, 0, _lag.MaxCompensationMs);
        }
    }

    /// <summary>
    /// The interval the player on move is actually charged for: monotonic elapsed (Phase 6f.1) minus
    /// their capped round-trip credit (Phase 6f.2). Every caller goes through here, move-charging,
    /// <see cref="LiveRemaining"/>, and therefore both flag-fall paths, which is deliberate: a
    /// high-latency player is at their most vulnerable at the moment they are about to flag, and
    /// compensating only the move-charging path would leave exactly that case broken.
    /// </summary>
    private TimeSpan ChargedElapsed() => ReadCharge().Charged;

    /// <summary>
    /// One sample of the on-turn charge: monotonic elapsed, net of the lag credit, plus that side's
    /// sub-second carry. Callers that also need the raw interval or the credit (the display offset)
    /// must use this rather than reading the clock again, so the whole seconds and the fraction
    /// describe the same instant.
    /// </summary>
    private (TimeSpan Charged, TimeSpan Raw, int CreditMs) ReadCharge()
    {
        var side = Position.SideToMove;
        var raw = _clock.Elapsed(_lastMoveTimestamp);
        var creditMs = CompensationMs(side);
        var credit = TimeSpan.FromMilliseconds(creditMs);
        var net = raw > credit ? raw - credit : TimeSpan.Zero;
        return (net + CarryFor(side), raw, creditMs);
    }

    /// <summary>The unbilled sub-second remainder owing by <paramref name="side"/> (C-4-2).</summary>
    private TimeSpan CarryFor(Side side) => side == Side.White ? _whiteCarry : _blackCarry;

    /// <summary>
    /// Seconds the player on move has been on the clock, measured off the **monotonic** source
    /// (Phase 6f.1) and net of their latency credit (Phase 6f.2). This deliberately does not subtract
    /// two <see cref="LastMoveAtUtc"/>-style wall readings: a wall clock can step, and a step lands
    /// directly on a player's remaining time. Floors to whole seconds because the engine's clock is
    /// whole seconds end to end, and the sub-second remainder that flooring leaves behind is not lost:
    /// <see cref="ApplyMove"/> retains it in that side's carry (C-4-2), and
    /// <see cref="ChargedElapsed"/> adds it back on their next turn.
    /// </summary>
    private int ElapsedSeconds() => WholeSeconds(ChargedElapsed());

    /// <summary>
    /// Whole seconds in <paramref name="span"/>, from ticks rather than <see cref="TimeSpan.TotalSeconds"/>.
    /// The double division rounds, and a value a fraction under an integer can floor a second low.
    /// The display offset is the remainder of this same split, so the two have to partition one span.
    /// </summary>
    private static int WholeSeconds(TimeSpan span) =>
        span.Ticks <= 0 ? 0 : (int)(span.Ticks / TimeSpan.TicksPerSecond);

    /// <summary>
    /// Legal moves from the current position. The (IsOver, Position) pair is snapshotted under the
    /// gate so the two cannot come from different plies; generation itself then runs outside it,
    /// because the engine parallelises move generation and that is not work to hold a lock across.
    /// </summary>
    public IReadOnlyList<Move> LegalMoves()
    {
        GamePosition position;
        lock (_gate)
        {
            if (IsOver) return Array.Empty<Move>();
            position = Position;
        }

        return _engine.LegalMoves(position);
    }

    /// <summary>
    /// Applies a move by <paramref name="playerId"/>. Throws <see cref="InvalidGameActionException"/>
    /// if the game is over, it isn't the player's turn, or the move is illegal.
    /// </summary>
    public ApplyResult ApplyMove(Guid playerId, Move move)
    {
        // The checks and the writes are one critical section on purpose: two callers that both passed
        // the turn and legality checks before either wrote would broadcast two different positions for
        // the same game, take the same ply number twice, and charge one anchor's think-time twice.
        lock (_gate)
        {
            if (IsOver) throw new InvalidGameActionException("Game is already over.");
            if (playerId != PlayerOnTurn) throw new InvalidGameActionException("Not your turn.");
            if (!_engine.IsLegal(Position, move)) throw new InvalidGameActionException($"Illegal move '{move}'.");

            var mover = Position.SideToMove;

            // One reading of the charged interval, used for both the seconds billed and the remainder
            // kept (C-4-2). Taking it twice would let the monotonic source advance between them and
            // put that sliver into the carry as well.
            var charged = ChargedElapsed();
            var think = WholeSeconds(charged);
            var lagMs = CompensationMs(mover);   // 6f.2: what this ply was credited, for telemetry
            var result = _engine.Apply(Position, move, Config, think);

            // Two readings, one per time domain (Phase 6f.1). `elapsed` is monotonic and is the honest
            // interval, it is what ThinkMs is derived from, so the 6e.2 telemetry can no longer be skewed
            // by an NTP step. `now` is wall clock and is only ever an *instant*: the persisted
            // ReceivedAtUtc and the displayed LastMoveAtUtc. Both anchors are re-set together below;
            // letting them drift apart would make LastMoveAtUtc describe a different moment than the one
            // the clock is actually charging from.
            var elapsed = _clock.Elapsed(_lastMoveTimestamp);
            var now = _clock.UtcNow;
            var thinkMs = (int)Math.Clamp(elapsed.TotalMilliseconds, 0, int.MaxValue);

            // What this ply used but could not be billed for in whole seconds, kept for the mover's
            // next turn. Always under a second, and set before Position moves on so it lands on the
            // side that actually spent it rather than on their opponent.
            var remainder = charged - TimeSpan.FromSeconds(think);
            if (mover == Side.White) _whiteCarry = remainder; else _blackCarry = remainder;

            Position = result.NewPosition;
            LastMoveAtUtc = now;
            _lastMoveTimestamp = _clock.Timestamp;
            Status = result.Status;
            Winner = result.Winner;
            DrawOfferedBy = null; // a move supersedes any pending draw offer

            _moves.Add(new MoveRecord(
                _moves.Count + 1,
                move.ToString(),
                result.NewPosition.SideToMove.Opposite(),
                result.NewPosition.WhiteTimeSeconds,
                result.NewPosition.BlackTimeSeconds,
                result.WhiteTimeDelta,
                result.BlackTimeDelta,
                result.Status.ToString(),
                thinkMs,
                now,
                lagMs));

            if (result.IsGameOver) Finish(MapTermination(result.Status));
            return result;
        }
    }

    /// <summary>The side-to-move resigns (server-side outcome, not an engine concept).</summary>
    public void Resign(Guid playerId)
    {
        // Every game-ending action takes the gate for the same reason ApplyMove does: an ending that
        // interleaved with a move would leave Status describing the move and Termination describing
        // the ending, so a player could win on time and then not have.
        lock (_gate)
        {
            if (IsOver) throw new InvalidGameActionException("Game is already over.");
            var loser = SideOf(playerId);
            Winner = loser.Opposite();
            Status = GameStatus.FlagFall; // reuse "decisive" bucket; Termination carries the real reason
            Finish(TerminationReason.Resignation);
        }
    }

    /// <summary>Records a draw offer from <paramref name="playerId"/> (overwrites any prior offer).</summary>
    public void OfferDraw(Guid playerId)
    {
        lock (_gate)
        {
            if (IsOver) throw new InvalidGameActionException("Game is already over.");
            DrawOfferedBy = SideOf(playerId);
        }
    }

    /// <summary>
    /// Responds to a standing draw offer. Accepting from the *other* side ends the game in a draw;
    /// declining just clears the offer. Responding to your own offer, or with none pending, throws.
    /// </summary>
    public void RespondDraw(Guid playerId, bool accept)
    {
        lock (_gate)
        {
            if (IsOver) throw new InvalidGameActionException("Game is already over.");
            if (DrawOfferedBy is null) throw new InvalidGameActionException("No draw offer to respond to.");
            if (SideOf(playerId) == DrawOfferedBy) throw new InvalidGameActionException("Cannot answer your own draw offer.");

            if (!accept) { DrawOfferedBy = null; return; }

            Winner = null;
            Status = GameStatus.DrawByRepetition; // internal "over" marker only; Termination is the truth
            Finish(TerminationReason.DrawByAgreement);
        }
    }

    /// <summary>The given player forfeits (e.g. disconnect past the grace window); opponent wins.</summary>
    public void Abandon(Guid playerId)
    {
        lock (_gate)
        {
            if (IsOver) throw new InvalidGameActionException("Game is already over.");
            Winner = SideOf(playerId).Opposite();
            Status = GameStatus.FlagFall; // decisive bucket, like Resign; Termination = Abandoned
            Finish(TerminationReason.Abandoned);
        }
    }

    /// <summary>
    /// Voids the game without a winner, used when restart recovery finds nobody to resume it.
    /// Deliberately not a forfeit: a server restart must never hand either player a win.
    /// </summary>
    public void Abort()
    {
        lock (_gate)
        {
            if (IsOver) throw new InvalidGameActionException("Game is already over.");
            Winner = null;
            Status = GameStatus.Aborted;
            Finish(TerminationReason.ServerRestart);
        }
    }

    /// <summary>
    /// Voids the game because a matched player never joined it. Deliberately not a forfeit, for the
    /// same reason <see cref="Abort"/> is not: an opponent's flaky network must never hand out a
    /// rated win.
    ///
    /// C-6-2: the deterrent for repeat no-shows is the matchmaking cooldown, not a rated result.
    /// <paramref name="noShowAtFaultPlayerId"/> is stored on the row so
    /// <see cref="AbandonmentPolicy"/> can count an attributed <c>NoShow</c> in the same 24-hour
    /// window as an <c>Abandoned</c> loss. Both seats missing stores null and charges nobody; the
    /// player who showed up is never the at-fault id. A no-show stays an abort with no winner and
    /// no Glicko.
    /// </summary>
    public void AbortNoShow(Guid? noShowAtFaultPlayerId)
    {
        lock (_gate)
        {
            if (IsOver) throw new InvalidGameActionException("Game is already over.");
            NoShowAtFaultPlayerId = noShowAtFaultPlayerId;
            Winner = null;
            Status = GameStatus.Aborted;
            Finish(TerminationReason.NoShow);
        }
    }

    private Side SideOf(Guid playerId) =>
        playerId == WhitePlayerId ? Side.White
        : playerId == BlackPlayerId ? Side.Black
        : throw new InvalidGameActionException("Not a player in this game.");

    /// <summary>Lazily flags the on-the-clock player if their live time hit zero (no move submitted).</summary>
    public bool CheckFlagFall()
    {
        // Called from GameRegistry.Get on every read as well as from the one-second sweep, so this is
        // the most concurrent path on the session. The test and the ending are one critical section:
        // a move landing in between would be applied to a game this call has already decided is lost.
        lock (_gate)
        {
            if (IsOver) return false;
            if (!Config.FlagFallLoses) return false;
            if (LiveRemaining(Position.SideToMove) > 0) return false;

            // The outcome is the engine's to decide, not this method's. A flag against an opponent who
            // cannot possibly mate is a draw (docs/RULES.md, "You run out of time"), and this is the
            // path that actually fires for an ordinary think-time flag: GameRegistry.Get runs it on
            // every read, so the flagged player's late move hits IsOver and never reaches Apply. While
            // this method decided the result for itself and never looked at material, a bare king won
            // on time, and PersistIfOverAsync fed that invented win straight into Glicko-2.
            //
            // Only the flag needs resolving here, not the whole ResolveStatus ordering: nothing has
            // moved since the last ply, so stalemate, threefold and the fifty-move rule cannot have
            // become newly true. If any of them were true they ended the game inside Apply already and
            // IsOver returned above. The clock is the only thing that changed, and the clock is exactly
            // what this branch prices.
            //
            // Cheap enough for the gate: a substring and a scan of the FEN placement, no I/O, no lock.
            var (status, winner) = KiriChessEngine.ResolveFlagFall(Config, Position.Fen, Position.SideToMove);
            Status = status;
            Winner = winner;
            Finish(MapTermination(status));
            return true;
        }
    }

    private void Finish(TerminationReason reason)
    {
        Termination = reason;
        EndedAt = _clock.UtcNow;
    }

    private static TerminationReason MapTermination(GameStatus status) => status switch
    {
        GameStatus.Checkmate => TerminationReason.Checkmate,
        GameStatus.FlagFall => TerminationReason.Flagfall,
        GameStatus.Stalemate => TerminationReason.Stalemate,
        GameStatus.DrawByRepetition => TerminationReason.DrawByRepetition,
        GameStatus.DrawByInsufficientMaterial => TerminationReason.DrawByInsufficientMaterial,
        GameStatus.DrawByFiftyMove => TerminationReason.DrawByFiftyMove,
        _ => TerminationReason.None
    };

    /// <summary>Builds the persisted entity from the finished session (called by the registry).</summary>
    public Game ToEntity()
    {
        // One critical section for the whole entity, not one per field: this row is the durability
        // record, and a result read from before a move with a move list read from after it would be a
        // persisted game that never happened.
        lock (_gate)
        {
            var result = Status == GameStatus.Aborted
                ? GameResult.Aborted
                : Winner switch
                {
                    Side.White => GameResult.WhiteWins,
                    Side.Black => GameResult.BlackWins,
                    _ => Status == GameStatus.Ongoing ? GameResult.Ongoing : GameResult.Draw
                };

            return new Game
            {
                Id = Id,
                WhitePlayerId = WhitePlayerId,
                BlackPlayerId = BlackPlayerId,
                Result = result,
                Termination = Termination,
                RatingPool = RatingPool,
                IsRated = IsRated,
                WhiteViaApi = WhiteViaApi,
                BlackViaApi = BlackViaApi,
                StartFen = KiriChessEngine.StartFen,
                ConfigSnapshot = Config,
                Moves = _moves.ToList(),
                PlyCount = _moves.Count,
                CreatedAt = CreatedAt,
                EndedAt = EndedAt,
                NoShowAtFaultPlayerId = NoShowAtFaultPlayerId
            };
        }
    }

    /// <summary>
    /// Rebuilds a live session from a persisted ongoing game by replaying its move log through the
    /// engine (Phase 2's bet: config snapshot + moves reproduce any position). Clocks come from the
    /// last MoveRecord, replay passes think-time 0, and LastMoveAtUtc restarts at "now" so server
    /// downtime is not charged to the player on turn. If the replay itself ends the game (the crash
    /// landed between the last move-snapshot write and the final write), the returned session is
    /// already over and the caller must persist it rather than register it.
    /// </summary>
    public static GameSession Restore(
        KiriChessEngine engine, Game entity, IGameClock? clock = null, LagCompensationOptions? lag = null)
    {
        var clk = clock ?? SystemGameClock.Instance;
        var lagOptions = lag ?? new LagCompensationOptions();
        var session = new GameSession(
            engine, entity.ConfigSnapshot, entity.Id,
            entity.WhitePlayerId, entity.BlackPlayerId,
            entity.RatingPool, entity.IsRated, entity.CreatedAt, clk, lagOptions,
            entity.WhiteViaApi, entity.BlackViaApi);

        // Same blank-StartFen fallback as GameReplayBuilder.Build and PgnBuilder: rows written before
        // StartFen was populated, and any row hand-seeded without one, still replay from the standard
        // start, the same position NewGame would have produced. Replaying a non-standard start through
        // NewGame would have resumed a live rated game from the wrong root, which is worse here than in
        // either builder, because this is the path whose output is played on rather than displayed.
        var startFen = string.IsNullOrWhiteSpace(entity.StartFen) ? KiriChessEngine.StartFen : entity.StartFen;
        var position = engine.FromFen(entity.ConfigSnapshot, startFen);
        var status = GameStatus.Ongoing;
        Side? winner = null;
        foreach (var record in entity.Moves)
        {
            var result = engine.Apply(position, Move.Parse(record.Uci), entity.ConfigSnapshot, 0);
            position = result.NewPosition;
            status = result.Status;
            winner = result.Winner;
        }

        if (entity.Moves.Count > 0)
        {
            var last = entity.Moves[^1];
            position = new GamePosition(
                position.Fen, position.SideToMove,
                last.WhiteTimeAfter, last.BlackTimeAfter,
                position.RepetitionCounts);
        }

        session.Position = position;
        session._moves.AddRange(entity.Moves);
        // Downtime is not charged to the player on turn: both anchors restart at "now" (Phase 6c.2),
        // and 6f.1 means the monotonic one is a fresh anchor in this process, which is also the only
        // correct thing to do, since a monotonic reading from the *previous* process is meaningless here.
        session.LastMoveAtUtc = clk.UtcNow;
        session._lastMoveTimestamp = clk.Timestamp;
        // The per-side RTT estimates (Phase 6f.2) deliberately do not survive a restore: they are
        // per-connection facts about a process that no longer exists, and every player has to
        // reconnect anyway, the fresh probe loop will re-measure them within one interval.
        session.Status = status;
        session.Winner = winner;
        if (status != GameStatus.Ongoing)
            session.Finish(MapTermination(status));
        return session;
    }
}

/// <summary>
/// Thrown when a game action is invalid (wrong turn, illegal move, game over). Maps to HTTP 400.
///
/// Not sealed, as of S-5-1: <see cref="GameCreationPausedException"/> derives from it so that a
/// creation path which does not know about the kill switch still refuses the game with an
/// intelligible message rather than throwing a 500. Nothing matches on the exact type (the one
/// pattern test, in <c>GameActionExecutor</c>, is an <c>is</c> check that a subtype satisfies), so
/// deriving changes no existing behaviour.
/// </summary>
public class InvalidGameActionException : Exception
{
    public InvalidGameActionException(string message) : base(message) { }
}
