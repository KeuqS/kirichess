using KiriChess.Engine.Config;
using KiriChess.Engine.Core;

namespace KiriChess.Engine.Tests;

public class Rule4RepetitionGuardTests
{
    private readonly KiriChessEngine _engine = new();

    // A rook + king vs king perpetual: the rook checks along the 8th and 7th ranks while the black
    // king shuffles a8<->a7. After four half-moves the first checking position recurs.
    private const string PerpetualFen = "k7/8/8/8/8/8/8/4K2R w - - 0 1";

    [Fact]
    public void PerpetualCheck_FirstCheckPays_RepeatsPayNothing()
    {
        var config = TestSupport.CheckOnly(gain: 2, loss: 2);
        var pos = _engine.FromFen(config, PerpetualFen);

        var m1 = _engine.Apply(pos, new Move("h1", "h8"), config);     // Rh8+  (first time -> pays)
        Assert.True(m1.Has(ClockEventType.CheckGain));
        Assert.Equal(+2, m1.WhiteTimeDelta);
        Assert.Equal(-2, m1.BlackTimeDelta);

        var m2 = _engine.Apply(m1.NewPosition, new Move("a8", "a7"), config); // Ka7
        var m3 = _engine.Apply(m2.NewPosition, new Move("h8", "h7"), config); // Rh7+ (new position -> pays)
        Assert.True(m3.Has(ClockEventType.CheckGain));
        Assert.Equal(+2, m3.WhiteTimeDelta);

        var m4 = _engine.Apply(m3.NewPosition, new Move("a7", "a8"), config); // Ka8
        var m5 = _engine.Apply(m4.NewPosition, new Move("h7", "h8"), config); // Rh8+ (REPEAT -> nullified)

        Assert.True(m5.Has(ClockEventType.RepetitionNullified));
        Assert.False(m5.Has(ClockEventType.CheckGain));
        Assert.Equal(0, m5.WhiteTimeDelta);
        Assert.Equal(0, m5.BlackTimeDelta);
    }

    [Fact]
    public void AlternatingTwoPatterns_StayNullified_OnceSeenAlwaysSeen()
    {
        var config = TestSupport.CheckOnly(gain: 2, loss: 2);
        var pos = _engine.FromFen(config, PerpetualFen);

        var m1 = _engine.Apply(pos, new Move("h1", "h8"), config);            // pattern A (first)
        var m2 = _engine.Apply(m1.NewPosition, new Move("a8", "a7"), config);
        var m3 = _engine.Apply(m2.NewPosition, new Move("h8", "h7"), config); // pattern B (first)
        var m4 = _engine.Apply(m3.NewPosition, new Move("a7", "a8"), config);
        var m5 = _engine.Apply(m4.NewPosition, new Move("h7", "h8"), config); // pattern A (repeat)
        var m6 = _engine.Apply(m5.NewPosition, new Move("a8", "a7"), config);
        var m7 = _engine.Apply(m6.NewPosition, new Move("h8", "h7"), config); // pattern B (repeat)

        // Both alternating checking positions are nullified on their second occurrence.
        Assert.True(m5.Has(ClockEventType.RepetitionNullified));
        Assert.False(m5.Has(ClockEventType.CheckGain));
        Assert.True(m7.Has(ClockEventType.RepetitionNullified));
        Assert.False(m7.Has(ClockEventType.CheckGain));
    }

    [Fact]
    public void ThreefoldRepetition_IsDrawn()
    {
        var config = TestSupport.CheckOnly(gain: 0, loss: 0);
        config.ThreefoldRepetitionIsDraw = true;

        // Knight shuffle from the start returns to the initial position every four half-moves.
        var pos = _engine.NewGame(config);
        GameStatus last = GameStatus.Ongoing;
        var cycle = new[]
        {
            new Move("g1", "f3"), new Move("g8", "f6"), new Move("f3", "g1"), new Move("f6", "g8")
        };
        // Two full cycles brings the start position to its third occurrence.
        for (int i = 0; i < 8 && last == GameStatus.Ongoing; i++)
        {
            var r = _engine.Apply(pos, cycle[i % 4], config);
            pos = r.NewPosition;
            last = r.Status;
        }
        Assert.Equal(GameStatus.DrawByRepetition, last);
    }

    // ------------------------------------------------------------------------------
    // C-3-2: the en-passant field of the repetition key is normalised away unless a legal
    // en-passant capture actually exists, which is how FIDE defines "the same position".
    // The keys themselves are observable through GamePosition.RepetitionCounts, so these
    // assert on the key rather than only on the economy that follows from it.
    // ------------------------------------------------------------------------------

    private static string EnPassantFieldOf(string positionKey) => positionKey.Split(' ')[3];

    [Fact]
    public void PositionAfterADoublePush_RecursByAReversibleManoeuvre_IsOnePositionNotTwo()
    {
        // 1.e4 Nf6 2.Nf3 Ng8 3.Ng1 returns to the position after 1.e4. Gera.Chess writes the
        // en-passant target after every double push, so the two occurrences differed only on
        // that field and Rule 4 charged the second one as if it were new.
        var config = TestSupport.AllZeroed();
        config.MoveCostMode = MoveCostMode.Flat;
        config.MoveCostFlat = 3;

        var pos = _engine.NewGame(config);
        var m1 = _engine.Apply(pos, new Move("e2", "e4"), config); // no black pawn can take on e3
        var m2 = _engine.Apply(m1.NewPosition, new Move("g8", "f6"), config);
        var m3 = _engine.Apply(m2.NewPosition, new Move("g1", "f3"), config);
        var m4 = _engine.Apply(m3.NewPosition, new Move("f6", "g8"), config);
        var m5 = _engine.Apply(m4.NewPosition, new Move("f3", "g1"), config);

        Assert.All(m5.NewPosition.RepetitionCounts.Keys, k => Assert.Equal("-", EnPassantFieldOf(k)));

        var afterE4 = m1.NewPosition.RepetitionCounts
            .Single(kv => kv.Key.StartsWith("rnbqkbnr/pppppppp/8/8/4P3/8/PPPP1PPP/RNBQKBNR b", StringComparison.Ordinal));
        Assert.Equal(2, m5.NewPosition.RepetitionCounts[afterE4.Key]);

        Assert.True(m5.Has(ClockEventType.RepetitionNullified));
        Assert.Equal(0, m5.WhiteTimeDelta);
        Assert.Equal(0, m5.BlackTimeDelta);
    }

    [Fact]
    public void EnPassantField_IsKept_WhenTheCaptureIsGenuinelyAvailable()
    {
        // Black pawn on d4, white plays c2-c4: dxc3 e.p. is legal, so this really is a
        // different position from the same placement without the capture available.
        var config = TestSupport.AllZeroed();
        var pos = _engine.FromFen(config, "4k3/8/8/8/3p4/8/2P5/4K3 w - - 0 1");

        var r = _engine.Apply(pos, new Move("c2", "c4"), config);

        var newest = r.NewPosition.RepetitionCounts.Keys
            .Single(k => k.StartsWith("4k3/8/8/8/2Pp4/8/8/4K3 b", StringComparison.Ordinal));
        Assert.Equal("c3", EnPassantFieldOf(newest));
    }

    [Fact]
    public void EnPassantField_IsDropped_WhenTheOnlyCaptureIsIllegal()
    {
        // The horizontal-pin case: after c2-c4 the black d4 pawn is the only candidate, and
        // dxc3 e.p. would vacate both d4 and c4 at once, exposing the black king on h4 to the
        // rook on a4. Gera.Chess's *legal* move list is what decides that, which is exactly
        // why the probe uses board.Moves() and not a pseudo-legal generation.
        var config = TestSupport.AllZeroed();
        var pos = _engine.FromFen(config, "8/8/8/8/R2p3k/8/2P5/4K3 w - - 0 1");

        var r = _engine.Apply(pos, new Move("c2", "c4"), config);

        var newest = r.NewPosition.RepetitionCounts.Keys
            .Single(k => k.StartsWith("8/8/8/8/R1Pp3k/8/8/4K3 b", StringComparison.Ordinal));
        Assert.Equal("-", EnPassantFieldOf(newest));
    }
}
