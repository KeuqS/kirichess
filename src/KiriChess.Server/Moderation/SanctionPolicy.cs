using KiriChess.Server.Data;
using KiriChess.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KiriChess.Server.Moderation;

/// <summary>What is being attempted, so the policy knows which sanction types bite.</summary>
public enum SanctionScope
{
    /// <summary>Signing in or refreshing a token. Only AccountBan blocks this.</summary>
    Account,
    /// <summary>Joining a rated queue. AccountBan and RatedPlayBan both block it.</summary>
    RatedPlay
}

/// <summary>
/// Whether an account may act right now, given its active sanctions (Phase 8.5). Singleton, opening
/// its own scope per call - the same shape as <see cref="KiriChess.Server.Games.AbandonmentPolicy"/>,
/// and for the same reason: <c>MatchmakingService</c> is a singleton and cannot hold a scoped DbContext.
///
/// Enforcement happens at every path that mints a token pair, plus queue join and game creation, and
/// *never* on the move path. 6e.1's governing rule is that a rejected move can lose a game, so a
/// sanction check there would turn a moderation action into a flagfall; and pulling a player out of a
/// live game would hand their opponent a win by moderator action, which is its own griefing vector. A
/// player banned mid-game finishes that game and cannot start another.
///
/// The enforcement points, in full (C-1-1). This list previously read as complete while naming only
/// four of them, which is exactly what made the misses below invisible, so treat it as a contract:
///
/// <list type="bullet">
/// <item><c>POST /api/auth/login</c> - <see cref="KiriChess.Server.Auth.AuthEndpoints"/></item>
/// <item><c>POST /api/auth/login/2fa</c> - same file</item>
/// <item><c>POST /api/auth/refresh</c> - same file, the point at which an already-issued session finally dies</item>
/// <item><c>POST /api/auth/external/exchange</c> - <see cref="KiriChess.Server.Auth.ExternalAuthEndpoints"/> (added by C-1-1)</item>
/// <item><c>POST /api/account/2fa/enable</c> and <c>/disable</c> - <see cref="KiriChess.Server.Auth.TwoFactorEndpoints"/>, both mint a fresh pair (added by C-1-1)</item>
/// <item><c>POST /api/games</c> - game creation, via <see cref="KiriChess.Server.Games.PlayEligibilityPolicy"/> since C-6-5, <c>SanctionScope.Account</c> only, so a RatedPlayBan still permits an unrated game</item>
/// <item>Queue join - <c>MatchmakingService</c>, <c>SanctionScope.RatedPlay</c></item>
/// </list>
///
/// Two paths mint a token pair and deliberately do NOT check, both recorded in
/// <c>TokenMintingPathTests</c>, which fails if a new minting call site appears without a decision:
/// <c>POST /api/auth/register</c> (the account does not exist until the row is created, so it cannot
/// carry a sanction) and the shared <c>IssueAuthResponseAsync</c> helper itself (it is the mint, not a
/// policy point; every caller checks before calling it).
///
/// <c>POST /api/account/delete</c> is deliberately NOT an enforcement point. See that endpoint's own
/// comment: refusing erasure to a banned account would deny a GDPR Art. 17 right, so the anti-evasion
/// property is delivered on the other side of the erasure instead - see
/// <see cref="BanEvasionMarker"/> (S-3-1). Register is where a banned identity is refused, and it is
/// refused on that marker rather than through this policy, because after erasure there is no account
/// id left to key this policy on.
/// </summary>
public sealed class SanctionPolicy
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SanctionPolicy> _logger;

    public SanctionPolicy(IServiceScopeFactory scopeFactory, ILogger<SanctionPolicy> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>The strongest active sanction blocking <paramref name="action"/>, or null if clear.</summary>
    public async Task<UserSanction?> BlockingSanctionAsync(
        Guid userId, SanctionScope scope, CancellationToken ct = default)
    {
        try
        {
            using var scopeHandle = _scopeFactory.CreateScope();
            var db = scopeHandle.ServiceProvider.GetRequiredService<KiriChessDbContext>();

            // Captured once: DateTimeOffset.UtcNow inside an expression tree is both a translation
            // hazard and a nondeterminism hazard (two evaluations in one query could disagree).
            var now = DateTimeOffset.UtcNow;

            var query = db.UserSanctions.Where(s =>
                s.UserId == userId && s.LiftedAt == null && (s.ExpiresAt == null || s.ExpiresAt > now));

            // Warning blocks nothing, ever - it is never included in either branch below.
            query = scope == SanctionScope.Account
                ? query.Where(s => s.Type == SanctionType.AccountBan)
                : query.Where(s => s.Type == SanctionType.AccountBan || s.Type == SanctionType.RatedPlayBan);

            return await query
                // A permanent ban (null ExpiresAt) outranks a temporary one.
                .OrderBy(s => s.ExpiresAt == null ? 0 : 1)
                .ThenByDescending(s => s.ExpiresAt)
                .FirstOrDefaultAsync(ct);
        }
        catch (Exception ex)
        {
            // Fail open: a database hiccup must not lock every player out of the site. Same instinct
            // as 8.4 Task E's "a limiter must never convert a cache outage into lost games". S-3-7:
            // this is still the right trade, but the failure was previously invisible - a LogWarning
            // with no metric and no alert rule means nobody learns that every ban on the service was
            // silently lifted for the duration of the outage. Error, not Warning, and a distinctive
            // message so log-based alerting (Better Stack) can key on this one string without a new
            // metric. Still fails open: the level change is a signal, not a behaviour change.
            _logger.LogError(ex, "SanctionPolicy fail-open: sanction check errored for user {UserId}, scope {Scope}; treating as unsanctioned", userId, scope);
            return null;
        }
    }
}
