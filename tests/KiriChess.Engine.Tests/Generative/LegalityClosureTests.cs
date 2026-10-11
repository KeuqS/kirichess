using KiriChess.Engine.Config;
using KiriChess.Engine.Core;
using GeraBoard = Chess.ChessBoard;
using GeraMove = Chess.Move;

namespace KiriChess.Engine.Tests.Generative;

/// <summary>
/// The roadmap names the direction that matters here: every generated move must be accepted
/// (<c>LegalMoves ⊆ accepted</c>), <b>and</b> every rejected move must be absent from the generator
/// (<c>accepted ⊆ LegalMoves</c>) - the second direction being the one that catches a missing legal
/// move rather than an illegal one. Three tests, and they are not all the same kind of check:
///
/// <list type="bullet">
/// <item><b>3a</b> is a <b>regression guard, not an oracle.</b> After Step 1c,
/// <see cref="KiriChessEngine.IsLegal"/> and the legality gate inside
/// <see cref="KiriChessEngine.Apply"/> are both *defined* as membership in
/// <see cref="KiriChessEngine.LegalMoves"/>, so this test cannot by itself prove any of the three
/// concepts independently correct - it can only fail if a future refactor re-delegates legality to
/// the underlying library (which is exactly what Step 1c undid). 3b below is the test with
/// independent content.</item>
/// <item><b>3b</b> is the <b>oracle</b>, and the reason 1c exists at all: it calls Gera.Chess directly,
/// bypassing KiriChess's own definitions, to show the library's own validator really does accept
/// moves its own generator never produces.</item>
    /// <item><b>3c</b> pins the malformed-input contract (never throw a <c>Chess.*</c> exception),
    /// the documented queen-default for a bare promotion, and that a spurious promotion target on a
    /// non-promotion move is rejected rather than ignored (membership is the definition; ignoring it
    /// would reopen a hole the old <c>IsValidMove</c> path had because it never saw the field).</item>
/// </list>
///
/// <para><b>Why 3a samples rather than sweeping every position.</b> Before Step 1c,
/// <c>IsLegal</c> was one <c>board.IsValidMove</c> call - cheap, independent of movecount. After it,
/// <c>IsLegal</c> and <c>Apply</c>'s legality gate both cost one full <see cref="LegalMoves"/>
/// generation *each* - measured on this machine: a plain 4,096-pair sweep against one position now
/// costs ~750 ms for <c>IsLegal</c> alone (not the ~29 ms a cheap validator would cost), because every
/// one of the 4,096 calls regenerates the entire legal-move list to answer one membership question.
/// Multiplying that by a promotion-extended sweep *and* a dozen-plus sampled positions would alone
/// exceed this item's entire 25 s shared budget. So this test samples a handful of positions rather
/// than the fuller set the plan sketched, and says so here rather than silently shrinking scope.</para>
/// </summary>
public class LegalityClosureTests
{
    private static readonly PieceType?[] PromotionTargets =
        { null, PieceType.Queen, PieceType.Rook, PieceType.Bishop, PieceType.Knight };

    private static readonly string[] Squares = BuildSquares();

    private static string[] BuildSquares()
    {
        var squares = new string[64];
        var i = 0;
        for (var file = 'a'; file <= 'h'; file++)
            for (var rank = '1'; rank <= '8'; rank++)
                squares[i++] = $"{file}{rank}";
        return squares;
    }

    /// <summary>Every (from, to) pair with no promotion - the plain 4,096-candidate space.</summary>
    private static IEnumerable<Move> PlainCandidates()
    {
        foreach (var from in Squares)
            foreach (var to in Squares)
                yield return new Move(from, to);
    }

    /// <summary>
    /// The promotion-relevant slice of the candidate space: every 7th/2nd-rank square paired with
    /// every 8th/1st-rank square (straight pushes and diagonal captures alike - the cheap sweep above
    /// already rules out the geometrically impossible pairs, so this only needs to add the promotion
    /// dimension) crossed with all four promotion pieces. Far smaller than extending the full 4,096
    /// pairs by 4, and it is exactly the subspace <see cref="KiriChessEngine.Apply"/>'s Step 1b guard
    /// and the generator's promotion handling actually interact with.
    /// </summary>
    private static IEnumerable<Move> PromotionCandidates()
    {
        var whiteFrom = Squares.Where(s => s[1] == '7');
        var whiteTo = Squares.Where(s => s[1] == '8');
        var blackFrom = Squares.Where(s => s[1] == '2');
        var blackTo = Squares.Where(s => s[1] == '1');

        foreach (var (froms, tos) in new[] { (whiteFrom, whiteTo), (blackFrom, blackTo) })
            foreach (var from in froms)
                foreach (var to in tos)
                    foreach (var promo in new PieceType?[] { PieceType.Queen, PieceType.Rook, PieceType.Bishop, PieceType.Knight })
                        yield return new Move(from, to, promo);
    }

    /// <summary>
    /// A deliberately small sample: the start position, Kiwipete (the position 3b's surplus castling
    /// notation lives in), and the promotions FEN (the position where the promotion-candidate slice
    /// above actually has legal members to find). See the class remarks for why this list is short.
    /// </summary>
    private static IEnumerable<(string Label, GamePosition Position, KiriChessConfig Config)> SamplePositions()
    {
        var engine = new KiriChessEngine();
        var config = new KiriChessConfig();

        yield return ("start", engine.NewGame(config), config);
        yield return ("kiwipete", engine.FromFen(config, "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1"), config);
        yield return ("promotions", engine.FromFen(config, "r3k2r/Pppp1ppp/1b3nbN/nP6/BBP1P3/q4N2/Pp1P2PP/R2Q1RK1 w kq - 0 1"), config);
    }

    /// <summary>
    /// See the class remarks - this asserts the three concepts still agree, which is what a future
    /// refactor could break, not that they are independently correct.
    /// </summary>
    [Fact]
    public void LegalMoves_IsLegal_and_Apply_agree_over_the_candidate_space()
    {
        var engine = new KiriChessEngine();
        long checkedCandidates = 0;
        var failures = new List<string>();

        void Check(string label, GamePosition position, KiriChessConfig config, HashSet<Move> legalSet, Move candidate)
        {
            checkedCandidates++;
            var expectedLegal = legalSet.Contains(candidate)
                || (candidate.Promotion is null
                    && legalSet.Contains(new Move(candidate.From, candidate.To, PieceType.Queen)));

            var isLegal = engine.IsLegal(position, candidate);
            if (isLegal != expectedLegal && failures.Count < 5)
                failures.Add($"{label}: IsLegal({candidate})={isLegal}, expected {expectedLegal}.");

            Exception? thrown = null;
            try { engine.Apply(position, candidate, config); }
            catch (Exception ex) { thrown = ex; }

            if (thrown is not null && thrown is not ArgumentException && failures.Count < 5)
                failures.Add($"{label}: Apply({candidate}) threw {thrown.GetType().FullName}, only ArgumentException is allowed here. {thrown.Message}");

            var applyAccepted = thrown is null;
            if (applyAccepted != expectedLegal && failures.Count < 5)
                failures.Add(
                    $"{label}: Apply({candidate}) {(applyAccepted ? "succeeded" : "threw " + thrown!.GetType().Name)}, " +
                    $"expected legal={expectedLegal}.");
        }

        foreach (var (label, position, config) in SamplePositions())
        {
            var legalSet = new HashSet<Move>(engine.LegalMoves(position));

            foreach (var candidate in PlainCandidates())
                Check(label, position, config, legalSet, candidate);

            foreach (var candidate in PromotionCandidates())
                Check(label, position, config, legalSet, candidate);
        }

        Assert.True(checkedCandidates > 0, "No candidates were checked.");
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    /// <summary>
    /// The oracle. Calls Gera.Chess directly - <c>Chess.ChessBoard.LoadFromFen</c> plus
    /// <c>Moves()</c> and <c>IsValidMove</c> - bypassing every KiriChess definition, to show the
    /// surplus this whole item's Step 1c exists to close: the library's validator accepts castling
    /// written as king-onto-own-rook (<c>e1h1</c>, <c>e1a1</c>) in Kiwipete, which its own generator
    /// never produces. 50 accepted from→to pairs against 48 generated moves - exactly two extra, and
    /// they are exactly these two squares.
    ///
    /// <para>This test is expected to <b>fail if Gera.Chess is ever upgraded and fixes this</b> - at
    /// which point <see cref="KiriChessEngine.IsLegal"/> may go back to delegating to the library. That
    /// is the repo's own drift-guard idiom (<c>LegalDocumentDriftTests</c>, <c>MetricsContractTests</c>)
    /// applied to a third-party dependency instead of a generated file.</para>
    /// </summary>
    [Fact]
    public void Library_validator_is_wider_than_its_own_generator_in_kiwipete()
    {
        const string kiwipete = "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1";
        var board = GeraBoard.LoadFromFen(kiwipete);

        var generated = board.Moves();
        Assert.Equal(48, generated.Length);

        var accepted = new List<string>();
        foreach (var from in Squares)
            foreach (var to in Squares)
            {
                try
                {
                    if (board.IsValidMove(new GeraMove(from, to)))
                        accepted.Add($"{from}{to}");
                }
                catch (Exception)
                {
                    // The library throws on empty from-squares rather than returning false - that is
                    // F2, and this oracle is only counting the moves it positively accepts.
                }
            }

        Assert.Equal(50, accepted.Count);

        var generatedUci = generated.Select(m => $"{Sq(m.OriginalPosition)}{Sq(m.NewPosition)}").ToHashSet();
        var surplus = accepted.Where(uci => !generatedUci.Contains(uci)).OrderBy(s => s, StringComparer.Ordinal).ToList();

        Assert.Equal(new[] { "e1a1", "e1h1" }, surplus);
    }

    private static string Sq(Chess.Position p) => $"{(char)('a' + p.X)}{p.Y + 1}";

    public static IEnumerable<object[]> MalformedOrOutOfContractMoves()
    {
        // (label, from, to, promotion) - cases whose current behaviour was the F2/F3 defect.
        yield return new object[] { "off-board", "z9", "a1", null! };
        yield return new object[] { "empty from-square", "e3", "e4", null! };
        yield return new object[] { "null move", "e2", "e2", null! };
        yield return new object[] { "own-piece capture", "a1", "a2", null! };
        yield return new object[] { "wrong side's piece", "e7", "e5", null! };
        yield return new object[] { "no such move", "a1", "h8", null! };
    }

    /// <summary>
    /// Malformed and out-of-contract input from the start position (F2's territory): <c>IsLegal</c>
    /// must answer <c>false</c>, and <c>Apply</c> must throw <c>ArgumentException</c> - never a
    /// <c>Chess.*</c> exception, which is exactly what leaked through before Step 1c.
    /// </summary>
    [Theory]
    [MemberData(nameof(MalformedOrOutOfContractMoves))]
    public void Malformed_input_is_rejected_never_thrown_at(string label, string from, string to, PieceType? promotion)
    {
        var engine = new KiriChessEngine();
        var config = new KiriChessConfig();
        var position = engine.NewGame(config);
        var move = new Move(from, to, promotion);

        Assert.False(engine.IsLegal(position, move), $"{label}: {move} should not be legal from the start position.");

        var ex = Assert.Throws<ArgumentException>(() => engine.Apply(position, move, config));
        Assert.IsType<ArgumentException>(ex);
    }

    /// <summary>
    /// F3's own territory: an invalid promotion target, in a position where the base move
    /// (<c>b7b8</c>) is a real promotion. Both <c>PieceType.King</c> and <c>PieceType.Pawn</c> must be
    /// rejected with <c>ArgumentException</c> and never reach the library's own promotion handler
    /// (which is what used to throw off the caller's stack, from inside <c>ChessEvents.cs</c>).
    /// </summary>
    [Theory]
    [InlineData(PieceType.King)]
    [InlineData(PieceType.Pawn)]
    public void Invalid_promotion_target_is_rejected_with_ArgumentException(PieceType invalidTarget)
    {
        var engine = new KiriChessEngine();
        var config = new KiriChessConfig();
        var position = engine.FromFen(config, "8/PPPk4/8/8/8/8/4Kppp/8 w - - 0 1");
        var move = new Move("b7", "b8", invalidTarget);

        Assert.False(engine.IsLegal(position, move));
        Assert.Throws<ArgumentException>(() => engine.Apply(position, move, config));
    }

    /// <summary>
    /// Two promotion-edge contracts. A fully-specified promotion still works. A bare promotion
    /// (UCI without a piece letter) defaults to queen - that is the documented, wire-reachable
    /// exception to exact membership in <see cref="KiriChessEngine.LegalMoves"/>. A spurious
    /// promotion target on a non-promotion move is rejected: membership is the definition, and
    /// ignoring the field would reopen the hole the old <c>IsValidMove</c> path had because
    /// <c>ToChessMove</c> never carried it.
    /// </summary>
    [Fact]
    public void Bare_promotion_defaults_to_queen_and_a_spurious_target_is_rejected()
    {
        var engine = new KiriChessEngine();
        var config = new KiriChessConfig();
        var promoPos = engine.FromFen(config, "4k3/P7/8/8/8/8/8/4K3 w - - 0 1");

        var explicitQueen = engine.Apply(promoPos, new Move("a7", "a8", PieceType.Queen), config);
        Assert.StartsWith("a8=Q", explicitQueen.San, StringComparison.Ordinal);

        Assert.True(engine.IsLegal(promoPos, new Move("a7", "a8")));
        var bare = engine.Apply(promoPos, new Move("a7", "a8"), config);
        Assert.StartsWith("a8=Q", bare.San, StringComparison.Ordinal);

        var startPos = engine.NewGame(config);
        Assert.False(engine.IsLegal(startPos, new Move("e2", "e4", PieceType.Queen)));
        var spuriousEx = Assert.Throws<ArgumentException>(
            () => engine.Apply(startPos, new Move("e2", "e4", PieceType.Queen), config));
        Assert.Contains("Illegal move", spuriousEx.Message, StringComparison.Ordinal);
    }
}
