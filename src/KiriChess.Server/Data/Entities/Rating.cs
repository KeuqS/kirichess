namespace KiriChess.Server.Data.Entities;

/// <summary>A user's Glicko-2 state within one rating pool. (Glicko math itself is Phase 5.)</summary>
public sealed class Rating
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public ApplicationUser? User { get; set; }

    public string Pool { get; set; } = string.Empty;

    public double Value { get; set; } = 1500.0;
    public double RatingDeviation { get; set; } = 350.0;
    public double Volatility { get; set; } = 0.06;

    /// <summary>Rated games completed in this pool. Shown on the leaderboard; it does not decide who is listed.</summary>
    public int GamesPlayed { get; set; }

    /// <summary>
    /// When a finished rated game last wrote this row. The inactivity sweep advances
    /// <see cref="UpdatedAt"/> and must not advance this: the established leaderboard treats it as
    /// "last played", and <see cref="UpdatedAt"/> no longer means that.
    /// </summary>
    public DateTimeOffset LastPlayedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// PostgreSQL <c>xmin</c>. Not a column this schema creates: every row already has it, and it
    /// changes on every write. EF sends it back on update so a second game finishing for this
    /// player fails the save instead of overwriting the first game's rating (C-7-6).
    /// </summary>
    public uint Version { get; set; }
}
