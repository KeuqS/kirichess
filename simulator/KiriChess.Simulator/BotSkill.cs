namespace KiriChess.Simulator;

/// <summary>
/// Port of <c>ui/lib/bot.ts</c>'s Elo → skill/depth mapping and the below-800 random blend.
/// Move quality only; think time lives in <see cref="BotClock"/>.
/// </summary>
public static class BotSkill
{
    public readonly record struct Search(int SkillLevel, int Depth, double RandomChance);

    public static Search ForElo(int elo)
    {
        var targetElo = Math.Max(400, Math.Min(3000, elo));
        var randomChance = targetElo < 800 ? 1 - ((targetElo - 400) / 400.0) : 0;

        var skillLevel = 0;
        if (targetElo >= 800)
        {
            skillLevel = (int)Math.Floor(((targetElo - 800) / (3000.0 - 800)) * 20);
            skillLevel = Math.Max(0, Math.Min(20, skillLevel));
        }

        var depth = targetElo switch
        {
            < 1000 => 1,
            < 1300 => 2,
            < 1600 => 3,
            < 1900 => 5,
            < 2300 => 8,
            < 2700 => 12,
            _ => 15,
        };

        return new Search(skillLevel, depth, randomChance);
    }

    public static string PickRandomMove(IReadOnlyList<string> legalMoves, Random rng)
    {
        if (legalMoves.Count == 0) return "";
        return legalMoves[rng.Next(legalMoves.Count)];
    }
}
