using System.Security.Cryptography;
using KiriChess.Engine.Config;
using KiriChess.Server.Data;
using KiriChess.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace KiriChess.Server.Games;

/// <summary>The four states a challenge can be in, in <see cref="ChallengeService.StatusOf"/>'s precedence order.</summary>
public enum ChallengeStatus { Open, Accepted, Cancelled, Expired }

/// <summary>
/// Phase 9d.8. CRUD and status logic for <see cref="Challenge"/> rows. Scoped - it holds a
/// <see cref="KiriChessDbContext"/> and is only ever used from a request handler; the accept endpoint's
/// single-use guard is a direct <c>ExecuteUpdateAsync</c> against the context, not a method here (README
/// scope decision 3), so this class stays free of the one operation that has to be exactly one UPDATE.
/// </summary>
public sealed class ChallengeService
{
    private readonly KiriChessDbContext _db;
    private readonly ChallengeOptions _options;

    public ChallengeService(KiriChessDbContext db, IOptions<ChallengeOptions> options)
    {
        _db = db;
        _options = options.Value;
    }

    /// <summary>
    /// Sentinel <see cref="Challenge.Pool"/> for a custom-config invite. Not an official rated pool;
    /// accept builds the game from <see cref="Challenge.CustomConfig"/> instead of a preset.
    /// </summary>
    public const string CustomPool = "custom";

    /// <summary>
    /// Creates a challenge, enforcing the per-user open-challenge cap (6e.3) BEFORE inserting - a
    /// count-then-throw after the insert would need a compensating delete, and a race between two
    /// concurrent creates from the same user is not worth guarding: losing that race just means a
    /// fourth open challenge exists for a moment, not a security or correctness problem.
    /// </summary>
    public async Task<Challenge> CreateAsync(
        Guid challengerId, string pool, ChallengeColour colour, CancellationToken ct,
        KiriChessConfig? customConfig = null, bool challengerViaApi = false)
    {
        var now = DateTimeOffset.UtcNow;
        var open = await _db.Challenges.CountAsync(
            c => c.ChallengerId == challengerId && c.AcceptedAt == null && c.CancelledAt == null && c.ExpiresAt > now, ct);
        if (open >= _options.MaxOpenPerUser)
            throw new InvalidGameActionException(
                $"You already have {_options.MaxOpenPerUser} open challenges. Cancel one first.");

        var challenge = new Challenge
        {
            Token = NewToken(),
            ChallengerId = challengerId,
            Pool = customConfig is null ? pool : CustomPool,
            CustomConfig = customConfig,
            Colour = colour,
            ChallengerViaApi = challengerViaApi,
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(_options.TtlMinutes),
        };
        _db.Challenges.Add(challenge);
        await _db.SaveChangesAsync(ct);

        // The caller (POST /api/challenges) needs Challenger.DisplayName to build the response DTO;
        // loading the navigation here means the endpoint does not need a second round trip.
        await _db.Entry(challenge).Reference(c => c.Challenger).LoadAsync(ct);
        return challenge;
    }

    /// <summary>Looks up a challenge by token, including <see cref="Challenge.Challenger"/> for the display name.</summary>
    public Task<Challenge?> FindAsync(string token, CancellationToken ct) =>
        _db.Challenges.Include(c => c.Challenger).FirstOrDefaultAsync(c => c.Token == token, ct);

    /// <summary>
    /// The caller's own open challenges, newest first - the lobby panel's "your pending links" list.
    /// Includes <see cref="Challenge.Challenger"/> (always the caller here) so <c>ToDto</c> has the
    /// display name without a per-row round trip.
    /// </summary>
    public async Task<IReadOnlyList<Challenge>> MineAsync(Guid userId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        return await _db.Challenges
            .Include(c => c.Challenger)
            .Where(c => c.ChallengerId == userId && c.AcceptedAt == null && c.CancelledAt == null && c.ExpiresAt > now)
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Revokes an open challenge. Challenger-only and guarded in the same UPDATE-decides-the-winner
    /// style as acceptance: a stale double-click cancels nothing the second time rather than throwing.
    /// Returns false if the token is unknown, already resolved, or not owned by <paramref name="challengerId"/>.
    /// </summary>
    public async Task<bool> CancelAsync(string token, Guid challengerId, CancellationToken ct)
    {
        var updated = await _db.Challenges
            .Where(c => c.Token == token && c.ChallengerId == challengerId
                     && c.AcceptedAt == null && c.CancelledAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.CancelledAt, DateTimeOffset.UtcNow), ct);
        return updated > 0;
    }

    /// <summary>
    /// 16 bytes from the CSPRNG, URL-safe base64, unpadded: 22 characters that survive a Discord paste,
    /// a QR code and a double-click-to-select. 128 bits is far past guessable, which is the only secrecy
    /// this token needs (README scope decision 1).
    /// </summary>
    public static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(16))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    /// <summary>
    /// Pure and static so a unit test can pin all four states with no database. Precedence order -
    /// Cancelled, then Accepted, then Expired, then Open - matters: a cancelled-then-expired row reads
    /// as Cancelled (the truthful answer about what actually happened to it), and an accepted row that
    /// is read back after its TTL has passed still reads as Accepted rather than flipping to Expired -
    /// the game it produced is what matters by then, not the link's own clock.
    /// </summary>
    public static ChallengeStatus StatusOf(Challenge c, DateTimeOffset now)
    {
        if (c.CancelledAt is not null) return ChallengeStatus.Cancelled;
        if (c.AcceptedAt is not null) return ChallengeStatus.Accepted;
        if (c.ExpiresAt <= now) return ChallengeStatus.Expired;
        return ChallengeStatus.Open;
    }
}
