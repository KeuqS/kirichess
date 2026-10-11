using KiriChess.Engine.Config;

namespace KiriChess.Server.Data.Entities;

/// <summary>
/// A persisted game. The row is inserted when the game is CREATED, not when it ends - see
/// GameRegistry.CreateAsync, "a game is durable from ply 0" - and updated in place at game end. So
/// <see cref="CreatedAt"/> is a true "this player started a game" timestamp (Phase 8a.5 funnel relies
/// on that), and an Ongoing row with <see cref="PlyCount"/> 0 is a pairing nobody ever moved in.
/// </summary>
public sealed class Game
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid WhitePlayerId { get; set; }
    public Guid BlackPlayerId { get; set; }
    public ApplicationUser? WhitePlayer { get; set; }
    public ApplicationUser? BlackPlayer { get; set; }

    public GameResult Result { get; set; } = GameResult.Ongoing;
    public TerminationReason Termination { get; set; } = TerminationReason.None;

    /// <summary>Rating pool ("bounty1"/"steal1"/"steal2"/"burn1"/"steal3"/"burn2") or null for unrated/custom configs.</summary>
    public string? RatingPool { get; set; }
    public bool IsRated { get; set; }

    /// <summary>
    /// White entered this game through the public API. Set once, at creation, from the challenge
    /// that produced the game. Matchmade and site-created games stay false.
    /// </summary>
    public bool WhiteViaApi { get; set; }

    /// <summary>Black's counterpart of <see cref="WhiteViaApi"/>.</summary>
    public bool BlackViaApi { get; set; }

    public string StartFen { get; set; } = string.Empty;

    /// <summary>The exact rule config the game was played under (jsonb).</summary>
    public KiriChessConfig ConfigSnapshot { get; set; } = new();

    /// <summary>Ordered ply list (jsonb).</summary>
    public List<MoveRecord> Moves { get; set; } = new();

    /// <summary>
    /// Number of plies in <see cref="Moves"/>, denormalised so balance analytics (8.3) can average
    /// game length without deserialising every game's jsonb move list. Written wherever
    /// <see cref="Moves"/> is written, never derive it lazily, or a game that ends between two
    /// writes reports a stale length.
    /// </summary>
    public int PlyCount { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? EndedAt { get; set; }

    /// <summary>
    /// Who failed to join a matched game that ended <see cref="TerminationReason.NoShow"/>.
    /// Null when both seats were missing (cannot attribute) or when the game is not a no-show.
    /// </summary>
    public Guid? NoShowAtFaultPlayerId { get; set; }

    /// <summary>
    /// How many times white dropped with no connection of theirs left, arming the grace timer
    /// (S-2-6). Includes drops they returned from inside the window. A spare tab closing while
    /// another connection stays does not increment this. Advisory only: no cooldown or sanction
    /// reads it. A solo game (both seats the same account) increments only this column.
    /// </summary>
    public int WhiteDisconnectCount { get; set; }

    /// <summary>Black's counterpart of <see cref="WhiteDisconnectCount"/>.</summary>
    public int BlackDisconnectCount { get; set; }
}
