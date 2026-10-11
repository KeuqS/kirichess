using System.Collections.Frozen;

namespace KiriChess.Server.Analytics;

/// <summary>
/// The server-side allowlist (phase8a5 README scope decision 5). An anonymous endpoint that accepted
/// arbitrary keys would be an unbounded-row-growth primitive pointed at our own database by anyone
/// with curl; this bounds the table at |All| x |CounterSources.All| x days forever. The client's map
/// from route to key is a convenience, this is the control.
/// </summary>
public static class CounterKeys
{
    // --- Page loads (fired by the browser beacon, one per route) ---
    public const string Landing = "landing";
    public const string Register = "register";   // a tab on /login, not a route
    public const string Login = "login";
    public const string Rules = "rules";
    public const string Learn = "learn";
    public const string Watch = "watch";
    public const string Leaderboards = "leaderboards";
    public const string Custom = "custom";
    public const string Donate = "donate";
    public const string About = "about";

    // --- First-session funnel (11a.3) ---

    /// <summary>Reached the last tutorial step. Denominator is <see cref="Learn"/>.</summary>
    public const string LearnComplete = "learn-complete";

    /// <summary>Pressed Skip in the tutorial. A deliberate exit, unlike simply closing the tab.</summary>
    public const string LearnSkip = "learn-skip";

    /// <summary>
    /// Registration succeeded. An APPROXIMATION kept only for source attribution - AspNetUsers.CreatedAt
    /// is the authoritative signup count and the two can disagree (README scope decision 15).
    /// </summary>
    public const string Signup = "signup";

    /// <summary>Entered matchmaking. Recorded server-side in MatchmakingService, once per entrant.</summary>
    public const string QueueJoin = "queue-join";

    /// <summary>
    /// Got paired. Recorded server-side, incremented by TWO per pairing (both seats), so that the
    /// ratio against the per-person <see cref="QueueJoin"/> compares like with like. Abandonment is
    /// QueueJoin - QueueMatch and is deliberately not its own key (README ground-truth correction 2).
    /// </summary>
    public const string QueueMatch = "queue-match";

    /// <summary>Recorded in GameRegistry.PersistIfOverAsync for a persisted finish with PlyCount &gt; 0.</summary>
    public const string FirstGameFinish = "first-game-finish";

    /// <summary>Client bounce from <c>/</c> when the seek list was empty.</summary>
    public const string EmptyLobbyLeave = "empty-lobby-leave";

    /// <summary>Increments only for a new rematch offer row (<c>IsNewOffer</c>).</summary>
    public const string RematchOffer = "rematch-offer";

    /// <summary>Increments only for a successful RematchService.TryClaimAsync.</summary>
    public const string RematchAccept = "rematch-accept";

    /// <summary>Page keys only. TotalPageViews is summed over this subset so "views" still means views.</summary>
    public static readonly FrozenSet<string> Pages = new[]
    {
        Landing, Register, Login, Rules, Learn, Watch, Leaderboards, Custom, Donate, About
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Everything the write endpoint accepts.</summary>
    public static readonly FrozenSet<string> All = Pages
        .Concat([
            LearnComplete, LearnSkip, Signup, QueueJoin, QueueMatch,
            FirstGameFinish, EmptyLobbyLeave, RematchOffer, RematchAccept,
        ])
        .ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Trims and lowercases, then checks the allowlist. False means "record nothing": there is no visit to
    /// attribute, only a client on a stale build sending a retired key (README scope decision 11).
    /// </summary>
    public static bool TryNormalize(string? raw, out string key)
    {
        key = raw?.Trim().ToLowerInvariant() ?? string.Empty;
        return All.Contains(key);
    }

    /// <summary>
    /// Trims and lowercases, then falls back to <see cref="CounterSources.Direct"/> for anything not on the
    /// allowlist. Deliberately NOT symmetrical with <see cref="TryNormalize"/>: an unrecognised source is a
    /// real visit whose origin we do not know, and dropping it to punish a typo in a query string would
    /// corrupt the very number this endpoint exists to produce (README scope decision 11).
    /// </summary>
    public static string NormalizeSource(string? raw)
    {
        var s = raw?.Trim().ToLowerInvariant();
        return s is not null && CounterSources.All.Contains(s) ? s : CounterSources.Direct;
    }
}

/// <summary>
/// Allowlisted campaign buckets for AD.6. A value is one word we chose, appearing in a link we wrote
/// (kirichess.com/?src=reddit) - never a referrer, see README scope decision 12. An unrecognised value
/// is counted as <see cref="Direct"/> rather than dropped: the visit is real, only the attribution is
/// unknown (README scope decision 11). Adding a channel is one constant here plus one link.
/// </summary>
public static class CounterSources
{
    /// <summary>No allowlisted source was known. Also every server-recorded row, by construction.</summary>
    public const string Direct = "direct";

    public const string Reddit = "reddit";
    public const string HackerNews = "hn";
    public const string Instagram = "instagram";
    public const string X = "x";
    public const string Discord = "discord";
    public const string YouTube = "youtube";
    public const string Lichess = "lichess";
    public const string ChessCom = "chesscom";
    public const string Press = "press";      // AD.1's press kit
    public const string Directory = "directory";  // AD.3's variant-directory submission
    public const string Qr = "qr";         // a printed or offline code
    public const string Friend = "friend";     // a link sent to one person

    /// <summary>Includes <see cref="Direct"/>: it is a legal stored value, not merely a fallback.</summary>
    public static readonly FrozenSet<string> All = new[]
    {
        Direct, Reddit, HackerNews, Instagram, X, Discord, YouTube, Lichess, ChessCom, Press, Directory, Qr, Friend
    }.ToFrozenSet(StringComparer.Ordinal);
}
