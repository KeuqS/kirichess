using KiriChess.Server.Ratings;
using Xunit;

namespace KiriChess.Server.Tests;

public sealed class Glicko2Tests
{
    [Fact]
    public void Update_matches_Glickman_worked_example()
    {
        var player = new Glicko2Rating(1500, 200, 0.06);
        var results = new (Glicko2Rating, double)[]
        {
            (new Glicko2Rating(1400, 30, 0.06), 1.0),
            (new Glicko2Rating(1550, 100, 0.06), 0.0),
            (new Glicko2Rating(1700, 300, 0.06), 0.0),
        };

        var after = Glicko2.Update(player, results);

        // Assert.Equal(expected, actual, precision) rounds both sides via Math.Round, which is brittle
        // right at a rounding boundary (1464.05 is not exactly representable as a double). Per the plan's
        // own guidance, use an explicit tolerance instead of loosening the intent of the assertion.
        Assert.True(Math.Abs(after.Rating - 1464.05) < 0.1, $"Rating was {after.Rating}");
        Assert.True(Math.Abs(after.Rd - 151.52) < 0.1, $"Rd was {after.Rd}");
        Assert.True(Math.Abs(after.Volatility - 0.05999) < 0.0001, $"Volatility was {after.Volatility}");
    }

    [Fact]
    public void Winning_raises_rating_losing_lowers_and_RD_shrinks()
    {
        var p = Glicko2Rating.Default; // 1500 / 350 / 0.06
        var opp = new Glicko2Rating(1500, 50, 0.06);

        var win = Glicko2.UpdateSingle(p, opp, 1.0);
        var loss = Glicko2.UpdateSingle(p, opp, 0.0);

        Assert.True(win.Rating > 1500);
        Assert.True(loss.Rating < 1500);
        Assert.True(win.Rd < p.Rd); // playing a game reduces uncertainty
    }

    [Fact]
    public void Draw_between_equal_established_players_barely_moves_rating()
    {
        var p = new Glicko2Rating(1600, 60, 0.06);
        var after = Glicko2.UpdateSingle(p, new Glicko2Rating(1600, 60, 0.06), 0.5);
        Assert.Equal(1600, after.Rating, 0); // within 0.5
    }

    /// <summary>
    /// Step 5 of the volatility solver has two ways to pick the lower bracket B: the closed form
    /// when d^2 exceeds phi^2 + v, and the bracket-widening loop otherwise. Every other case in this
    /// file feeds a surprising result and therefore takes the closed form, which left the widening
    /// branch, the one an ordinary game goes through, with no assertion on it at all. A sign error
    /// there (B = a + k*tau instead of a - k*tau) would pass every other test in this file.
    ///
    /// So: a low-RD player drawing an identically rated opponent. The result is exactly what the
    /// rating predicted, which is what puts delta at zero and forces the else branch.
    /// </summary>
    [Fact]
    public void Volatility_solver_widens_the_bracket_for_an_expected_result()
    {
        var player = new Glicko2Rating(1500, 50, 0.06);
        var opponent = new Glicko2Rating(1500, 50, 0.06);

        // Recompute the branch discriminant from the same inputs the engine uses, so this test
        // proves it takes the else branch rather than assuming it. Glicko2's internals are private,
        // so g, E, v and delta are rebuilt here from the paper's steps 3 and 4 for one opponent.
        const double scale = 173.7178;
        var phi = player.Rd / scale;
        var phiJ = opponent.Rd / scale;
        var g = 1.0 / Math.Sqrt(1.0 + 3.0 * phiJ * phiJ / (Math.PI * Math.PI));
        var e = 1.0 / (1.0 + Math.Exp(-g * ((player.Rating - opponent.Rating) / scale)));
        var v = 1.0 / (g * g * e * (1.0 - e));
        var delta = v * g * (0.5 - e);
        var d2 = delta * delta;
        var phi2 = phi * phi;

        Assert.True(d2 <= phi2 + v,
            $"d2 was {d2} and phi2 + v was {phi2 + v}; these inputs take the closed-form branch, not the widening one.");

        var after = Glicko2.UpdateSingle(player, opponent, 0.5);

        // A perfectly expected result should barely move volatility, and the direction matters: it
        // must ease very slightly downward, not jump. Pinned to a tolerance rather than a rounded
        // equality for the reason the worked-example test above gives.
        // 1e-7 is tight on purpose. The degenerate failure mode of the widening branch is B == A,
        // which skips the Illinois loop entirely and returns the volatility unchanged at exactly
        // 0.06; a looser tolerance would wave that through, since the true answer is only 3.2e-6
        // away from it.
        Assert.True(Math.Abs(after.Volatility - 0.0599968) < 1e-7, $"Volatility was {after.Volatility}");
        Assert.True(after.Volatility < player.Volatility, $"Volatility was {after.Volatility}");

        // The rest of the update still has to be sane. RD barely moves, and notably does not shrink:
        // Update treats one game as a whole rating period, so step 6's pre-period inflation
        // (phi* = sqrt(phi^2 + sigma^2)) is applied too, and at RD 50 that inflation just about
        // cancels the information a single drawn game carries. The same assertion at RD 350
        // (Winning_raises_rating_losing_lowers_and_RD_shrinks) does shrink, because there the
        // evidence dominates.
        Assert.True(Math.Abs(after.Rd - player.Rd) < 1.0, $"Rd was {after.Rd}");
        Assert.True(Math.Abs(after.Rating - 1500) < 0.01, $"Rating was {after.Rating}");
    }
}
