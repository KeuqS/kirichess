using KiriChess.Server.Data.Entities;
using KiriChess.Server.Ratings;
using Xunit;

namespace KiriChess.Server.Tests;

public sealed class RatingCalculatorTests
{
    private static readonly Glicko2Rating Fresh = Glicko2Rating.Default;

    [Fact]
    public void WhiteWins_raises_white_and_lowers_black()
    {
        var (white, black) = RatingCalculator.Compute(GameResult.WhiteWins, Fresh, Fresh);
        Assert.True(white.Rating > 1500);
        Assert.True(black.Rating < 1500);
    }

    [Fact]
    public void BlackWins_is_the_mirror()
    {
        var (white, black) = RatingCalculator.Compute(GameResult.BlackWins, Fresh, Fresh);
        Assert.True(black.Rating > 1500);
        Assert.True(white.Rating < 1500);
    }

    [Fact]
    public void Draw_between_equals_leaves_both_near_start()
    {
        var (white, black) = RatingCalculator.Compute(GameResult.Draw, Fresh, Fresh);
        Assert.Equal(1500, white.Rating, 0);
        Assert.Equal(1500, black.Rating, 0);
    }

    [Fact]
    public void Fresh_rating_is_provisional_then_settles()
    {
        Assert.True(RatingConstants.IsProvisional(RatingConstants.DefaultRd));   // 350 > 110
        Assert.False(RatingConstants.IsProvisional(80));
    }
}
