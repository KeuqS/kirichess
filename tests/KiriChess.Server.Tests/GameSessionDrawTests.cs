using KiriChess.Engine;
using KiriChess.Engine.Config;
using KiriChess.Engine.Core;
using KiriChess.Server.Data.Entities;
using KiriChess.Server.Games;
using Xunit;

namespace KiriChess.Server.Tests;

public sealed class GameSessionDrawTests
{
    private static GameSession Create(Guid white, Guid black) =>
        new(new KiriChessEngine(), new KiriChessConfig { MoveCostMode = MoveCostMode.None, CheckCheckerGain = 0, CheckVictimLoss = 0 }, white, black, "bounty1", true);

    [Fact]
    public void Accepted_draw_offer_ends_game_as_draw_by_agreement()
    {
        var white = Guid.NewGuid();
        var black = Guid.NewGuid();
        var s = Create(white, black);

        s.OfferDraw(white);
        Assert.Equal(Side.White, s.DrawOfferedBy);
        s.RespondDraw(black, accept: true);

        Assert.True(s.IsOver);
        Assert.Null(s.Winner);
        Assert.Equal(TerminationReason.DrawByAgreement, s.Termination);
        Assert.Equal(GameResult.Draw, s.ToEntity().Result);
    }

    [Fact]
    public void Declined_draw_offer_clears_and_keeps_game_going()
    {
        var white = Guid.NewGuid();
        var black = Guid.NewGuid();
        var s = Create(white, black);

        s.OfferDraw(white);
        s.RespondDraw(black, accept: false);

        Assert.False(s.IsOver);
        Assert.Null(s.DrawOfferedBy);
    }

    [Fact]
    public void Responding_to_your_own_offer_throws()
    {
        var white = Guid.NewGuid();
        var black = Guid.NewGuid();
        var s = Create(white, black);

        s.OfferDraw(white);
        Assert.Throws<InvalidGameActionException>(() => s.RespondDraw(white, accept: true));
    }

    [Fact]
    public void A_move_clears_a_standing_draw_offer()
    {
        var white = Guid.NewGuid();
        var black = Guid.NewGuid();
        var s = Create(white, black);

        s.OfferDraw(white);
        s.ApplyMove(white, Move.Parse("e2e4"));
        Assert.Null(s.DrawOfferedBy);
    }

    [Fact]
    public void Abandon_makes_the_opponent_win_by_abandonment()
    {
        var white = Guid.NewGuid();
        var black = Guid.NewGuid();
        var s = Create(white, black);

        s.Abandon(white);

        Assert.True(s.IsOver);
        Assert.Equal(Side.Black, s.Winner);
        Assert.Equal(TerminationReason.Abandoned, s.Termination);
    }
}
