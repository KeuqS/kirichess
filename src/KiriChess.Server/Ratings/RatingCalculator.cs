using KiriChess.Server.Data.Entities;

namespace KiriChess.Server.Ratings;

/// <summary>Pure mapping from a game result to both players' post-game Glicko-2 states.</summary>
public static class RatingCalculator
{
    /// <summary>Both outputs are computed from the PRE-game inputs (neither update sees the other's new value).</summary>
    public static (Glicko2Rating White, Glicko2Rating Black) Compute(
        GameResult result, Glicko2Rating white, Glicko2Rating black)
    {
        double whiteScore = result switch
        {
            GameResult.WhiteWins => 1.0,
            GameResult.BlackWins => 0.0,
            _ => 0.5, // Draw (and any non-decisive-but-finished result)
        };

        var whiteAfter = Glicko2.UpdateSingle(white, black, whiteScore);
        var blackAfter = Glicko2.UpdateSingle(black, white, 1.0 - whiteScore);
        return (whiteAfter, blackAfter);
    }
}
