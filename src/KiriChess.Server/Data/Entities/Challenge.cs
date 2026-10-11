using KiriChess.Engine.Config;

namespace KiriChess.Server.Data.Entities;

/// <summary>The challenger's chosen side. <see cref="Random"/> is resolved at accept time, not here.</summary>
public enum ChallengeColour { White, Black, Random }

/// <summary>
/// Phase 9d.8. A pending "play me" invitation: an unguessable token, a TTL, and single use.
///
/// Unlike <see cref="RefreshToken"/>, <see cref="ExternalLoginCode"/> and
/// <see cref="TwoFactorChallenge"/>, the token here is stored in PLAINTEXT and that is deliberate.
/// Those three are credentials - presenting one is presenting an account. This one grants exactly
/// one game against the person who created it: no identity, no privilege, and the worst case of a
/// leak is a stranger playing you once, which is what the public matchmaking queue does on purpose.
/// It also has to be re-displayable, because the lobby shows an open challenge with a Copy button
/// after a reload and a hash cannot be turned back into a link. The protections are therefore
/// unguessability (128 bits), <see cref="ExpiresAt"/>, single use (<see cref="AcceptedAt"/>),
/// revocation (<see cref="CancelledAt"/>) and a creation rate limit - not secrecy at rest.
/// See Phase 9d.8 scope decision 1.
/// </summary>
public sealed class Challenge
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>16 random bytes, URL-safe base64, unpadded - 22 characters. The path segment of the link.</summary>
    public string Token { get; set; } = string.Empty;

    public Guid ChallengerId { get; set; }
    public ApplicationUser? Challenger { get; set; }

    /// <summary>
    /// True when this invitation was minted with an API token (<c>POST /api/v1/challenges</c>).
    /// The accept page says the opponent is a bot. A person who also owns a token and creates
    /// the link on the site stays false: the label follows this request, not the account.
    /// </summary>
    public bool ChallengerViaApi { get; set; }

    /// <summary>
    /// Official pool key, or <c>custom</c> when <see cref="CustomConfig"/> is set. Friend challenges
    /// are always unrated: a shared link is too easy to farm Glicko against a cooperating partner.
    /// </summary>
    public string Pool { get; set; } = string.Empty;

    /// <summary>
    /// The custom ruleset when <see cref="Pool"/> is <c>custom</c>. Null for official-pool challenges.
    /// Stored as jsonb, same as <see cref="Game.ConfigSnapshot"/>, so accept uses what was minted
    /// rather than re-trusting a later request.
    /// </summary>
    public KiriChessConfig? CustomConfig { get; set; }

    public ChallengeColour Colour { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Set by the single guarded UPDATE that claims the challenge. Null means still open.</summary>
    public DateTimeOffset? AcceptedAt { get; set; }
    public Guid? AcceptedById { get; set; }
    public ApplicationUser? AcceptedBy { get; set; }

    /// <summary>Written after the game is created, so a late arrival can be told where the game went.</summary>
    public Guid? GameId { get; set; }

    public DateTimeOffset? CancelledAt { get; set; }
}
