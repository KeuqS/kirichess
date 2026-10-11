namespace KiriChess.Server.Games;

/// <summary>Binds to the <c>Challenges</c> configuration section (Phase 9d.8).</summary>
public sealed class ChallengeOptions
{
    public const string SectionName = "Challenges";

    /// <summary>
    /// How long a link stays open. An hour: long enough to paste into Discord and wait for a friend
    /// to read it, short enough that a link leaked into a public channel dies the same evening.
    /// </summary>
    public int TtlMinutes { get; set; } = 60;

    /// <summary>
    /// 6e.3: one account must not be able to mint invitations without bound. Three is enough to have
    /// a link out for each of a few friends and small enough that a spammer gains nothing.
    /// </summary>
    public int MaxOpenPerUser { get; set; } = 3;
}
