namespace KiriChess.Server.Ratings;

/// <summary>A player's rating state on the Glicko-2 (public) scale.</summary>
public readonly record struct Glicko2Rating(double Rating, double Rd, double Volatility)
{
    public static Glicko2Rating Default =>
        new(RatingConstants.DefaultRating, RatingConstants.DefaultRd, RatingConstants.DefaultVolatility);
}

/// <summary>
/// Pure Glicko-2 rating calculation (Mark Glickman's algorithm). Deterministic and side-effect free
/// so it can be unit-tested against the paper's worked example. A single finished game is treated as
/// a one-game rating period for each player, which is how online chess servers apply Glicko-2 live.
/// </summary>
public static class Glicko2
{
    private const double Scale = 173.7178; // maps the 1500/±public scale to the internal scale
    private const double Tau = 0.5;        // system constant: constrains volatility change
    private const double Epsilon = 1e-6;   // convergence tolerance for the volatility solver

    /// <summary>Updates <paramref name="player"/> given results against one or more opponents.
    /// Each result is the opponent's PRE-period rating and the player's score (1 win / 0.5 draw / 0 loss).</summary>
    public static Glicko2Rating Update(Glicko2Rating player, IReadOnlyList<(Glicko2Rating Opponent, double Score)> results)
    {
        // Step 2: to internal scale.
        double mu = (player.Rating - RatingConstants.DefaultRating) / Scale;
        double phi = player.Rd / Scale;
        double sigma = player.Volatility;

        if (results.Count == 0)
        {
            // No games: RD grows by the volatility (unused in per-game flow, included for completeness).
            double phiStarOnly = Math.Sqrt(phi * phi + sigma * sigma);
            return new Glicko2Rating(player.Rating, phiStarOnly * Scale, sigma);
        }

        // Step 3 & 4: variance (v) and estimated improvement direction (sum of g*(s-E)).
        double vInv = 0.0;
        double deltaSum = 0.0;
        foreach (var (opp, score) in results)
        {
            double muJ = (opp.Rating - RatingConstants.DefaultRating) / Scale;
            double phiJ = opp.Rd / Scale;
            double g = G(phiJ);
            double e = E(mu, muJ, phiJ);
            vInv += g * g * e * (1.0 - e);
            deltaSum += g * (score - e);
        }
        double v = 1.0 / vInv;
        double delta = v * deltaSum;

        // Step 5: new volatility via the Illinois (regula-falsi) algorithm.
        double a = Math.Log(sigma * sigma);
        double A = a;
        double B;
        double d2 = delta * delta;
        double phi2 = phi * phi;
        if (d2 > phi2 + v)
        {
            B = Math.Log(d2 - phi2 - v);
        }
        else
        {
            int k = 1;
            while (F(a - k * Tau, a, delta, phi, v) < 0) k++;
            B = a - k * Tau;
        }

        double fA = F(A, a, delta, phi, v);
        double fB = F(B, a, delta, phi, v);
        while (Math.Abs(B - A) > Epsilon)
        {
            double C = A + (A - B) * fA / (fB - fA);
            double fC = F(C, a, delta, phi, v);
            if (fC * fB <= 0) { A = B; fA = fB; }
            else { fA /= 2.0; }
            B = C; fB = fC;
        }
        double newSigma = Math.Exp(A / 2.0);

        // Step 6 & 7: pre-period RD then new RD.
        double phiStar = Math.Sqrt(phi2 + newSigma * newSigma);
        double newPhi = 1.0 / Math.Sqrt(1.0 / (phiStar * phiStar) + 1.0 / v);

        // Step 8: new rating.
        double newMu = mu + newPhi * newPhi * deltaSum;

        // Step 9: back to the public scale.
        return new Glicko2Rating(
            newMu * Scale + RatingConstants.DefaultRating,
            newPhi * Scale,
            newSigma);
    }

    /// <summary>Convenience overload for a single-opponent game.</summary>
    public static Glicko2Rating UpdateSingle(Glicko2Rating player, Glicko2Rating opponent, double score)
        => Update(player, new[] { (opponent, score) });

    private static double G(double phi) => 1.0 / Math.Sqrt(1.0 + 3.0 * phi * phi / (Math.PI * Math.PI));

    private static double E(double mu, double muJ, double phiJ)
        => 1.0 / (1.0 + Math.Exp(-G(phiJ) * (mu - muJ)));

    private static double F(double x, double a, double delta, double phi, double v)
    {
        double ex = Math.Exp(x);
        double num = ex * (delta * delta - phi * phi - v - ex);
        double den = 2.0 * Math.Pow(phi * phi + v + ex, 2);
        return num / den - (x - a) / (Tau * Tau);
    }
}
