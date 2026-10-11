using KiriChess.Engine.Config;

namespace KiriChess.Server.Ratings;

/// <summary>Rating-pool constants and Glicko-2 defaults.</summary>
public static class RatingConstants
{
    public const double DefaultRating = 1500.0;
    public const double DefaultRd = 350.0;
    public const double DefaultVolatility = 0.06;

    /// <summary>Above this RD a rating is still settling, surfaced to the UI as "provisional".</summary>
    public const double ProvisionalRdThreshold = 110.0;

    /// <summary>
    /// One Glicko-2 rating period, in wall-clock time. This is the site's half of the algorithm, not
    /// a value the paper supplies: Glicko-2 defines the RD an idle player accrues PER RATING PERIOD
    /// and leaves the period's length to the operator, so this number is a product decision.
    ///
    /// Seven days, chosen against this site's volume rather than copied from anywhere. Glickman's
    /// guidance is that a rating period should hold roughly 10 to 15 games for an active player;
    /// KiriChess is pre-launch with no measured volume, and a week is the shortest window in which a
    /// casually active player on a small chess site plausibly plays that many. Shorter (daily) would
    /// inflate the RD of somebody who plays every weekend, which is the opposite of what inflation is
    /// for; longer (monthly) makes the inflation invisible.
    ///
    /// At the default volatility of 0.06 one idle period adds only about 10 RD in quadrature, so a
    /// settled player at RD 48 needs roughly 90 idle weeks to cross <see cref="ProvisionalRdThreshold"/>.
    /// That is Glicko-2 behaving as specified. It is also why the established leaderboard does not
    /// use deviation alone: <see cref="LeaderboardActiveWindow"/> is what takes a departed leader off
    /// the board a visitor actually sees.
    /// </summary>
    public static readonly TimeSpan RatingPeriod = TimeSpan.FromDays(7);

    /// <summary>
    /// How recently a player must have finished a rated game in a pool to stay on that pool's
    /// established leaderboard. Deviation inflation takes on the order of ninety idle weeks to push
    /// a settled rating back over <see cref="ProvisionalRdThreshold"/>, so a player who stops is
    /// otherwise the leader a visitor sees for the life of a small site. Established means both:
    /// deviation at or under the provisional threshold, and a game inside this window.
    ///
    /// Twenty-eight days, four rating periods. A week would drop someone who plays most weekends and
    /// skips one; a season would leave the departed leader in place, which is the failure this
    /// closes. The same number is stated in <c>leaderboards.rdExplainer</c> in all six catalogues;
    /// change them together.
    /// </summary>
    public static readonly TimeSpan LeaderboardActiveWindow = TimeSpan.FromDays(28);

    /// <summary>
    /// The only rated pools, read from <c>config/kirichess.rules.json</c>, the same document the
    /// engine builds its presets from. Any other config (a custom game) is unrated. Does not include
    /// the parallel <see cref="BotPools"/> namespace (Phase 9f.5): those are a server-side rating
    /// partition, not UI presets.
    /// </summary>
    public static readonly IReadOnlyList<string> OfficialPools = KiriChessConfig.RatedPoolKeys;

    /// <summary>Prefix for the parallel bot Glicko pools. Human queues never use this namespace.</summary>
    public const string BotPoolPrefix = "bot:";

    /// <summary>One <c>bot:{preset}</c> key per official pool, for API-token matchmaking (Phase 9f.5).</summary>
    public static readonly IReadOnlyList<string> BotPools =
        OfficialPools.Select(p => BotPoolPrefix + p).ToArray();

    /// <summary>
    /// Every Redis matchmaking KEY the pairing Lua script must know about: human official pools plus
    /// the bot namespace. Public seek lists stay on <see cref="OfficialPools"/> only.
    /// </summary>
    public static readonly IReadOnlyList<string> MatchmakingPools =
        OfficialPools.Concat(BotPools).ToArray();

    public static bool IsOfficialPool(string? pool)
        => pool is not null && OfficialPools.Contains(pool);

    public static bool IsBotPool(string? pool)
        => pool is not null
           && pool.StartsWith(BotPoolPrefix, StringComparison.Ordinal)
           && IsOfficialPool(pool[BotPoolPrefix.Length..]);

    public static string ToBotPool(string officialPool) => BotPoolPrefix + officialPool;

    /// <summary>
    /// Maps an API-token <c>JoinQueue</c> argument onto a bot-pool key: <c>bounty1</c> and
    /// <c>bot:bounty1</c> both become <c>bot:bounty1</c>. Null if the name is not an official preset.
    /// </summary>
    public static string? RemapApiTokenQueuePool(string? pool)
    {
        if (string.IsNullOrWhiteSpace(pool)) return null;
        var key = pool.Trim().ToLowerInvariant();
        if (IsOfficialPool(key)) return ToBotPool(key);
        if (IsBotPool(key)) return key;
        return null;
    }

    public static bool TryGetOfficialPoolFromBot(string pool, out string official)
    {
        if (IsBotPool(pool))
        {
            official = pool[BotPoolPrefix.Length..];
            return true;
        }

        official = "";
        return false;
    }

    public static bool IsProvisional(double rd) => rd > ProvisionalRdThreshold;
}
