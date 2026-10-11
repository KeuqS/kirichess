using System.Globalization;
using KiriChess.Server.Data;
using KiriChess.Server.Data.Entities;
using KiriChess.Server.Games.Distributed;
using KiriChess.Server.Infrastructure.Redis;
using KiriChess.Server.Moderation;
using Microsoft.EntityFrameworkCore;

namespace KiriChess.Server.Games;

/// <summary>
/// Phase 9d.8. The rules that decide whether a user is allowed into a two-player game, extracted
/// from MatchmakingService.TryJoinAsync so that matchmaking and direct challenges cannot drift apart.
///
/// The roadmap's own framing of 9d.8 is that "the eligibility rules that stop [spam] are the design,
/// not the plumbing" - so there is exactly one implementation of them, and both entry points call it.
/// Every message here is asserted verbatim by existing tests; treat them as part of the contract.
/// </summary>
public sealed class PlayEligibilityPolicy
{
    private readonly GameRegistry _registry;
    private readonly AbandonmentPolicy _abandonment;
    private readonly SanctionPolicy _sanctions;
    private readonly IRedisConnection _connection;
    private readonly LiveGameIndex _liveGames;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<PlayEligibilityPolicy> _logger;

    public PlayEligibilityPolicy(
        GameRegistry registry, AbandonmentPolicy abandonment, SanctionPolicy sanctions,
        IRedisConnection connection, LiveGameIndex liveGames, IServiceScopeFactory scopeFactory,
        IConfiguration config, ILogger<PlayEligibilityPolicy> logger)
    {
        _registry = registry;
        _abandonment = abandonment;
        _sanctions = sanctions;
        _connection = connection;
        _liveGames = liveGames;
        _scopeFactory = scopeFactory;
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Cluster-wide when Redis is enabled (Phase 8.4) - the per-process GameRegistry.PlayersInGame is
    /// worthless once a player's live game can be owned by a different instance from the one serving
    /// this call. Uses <see cref="LiveGameIndex.IsPlayerInGameAsync"/> (SISMEMBER) rather than loading
    /// every seated id (I-1-3). <paramref name="message"/> lets the challenge path say "Your opponent is
    /// already in a game." about somebody who is not the caller.
    /// </summary>
    public async Task EnsureNotAlreadyInGameAsync(
        Guid userId, CancellationToken ct = default, string message = "You're already in a game.")
    {
        var alreadyInGame = _connection.Enabled
            ? await _liveGames.IsPlayerInGameAsync(userId, ct)
            : _registry.PlayersInGame.Contains(userId);
        if (alreadyInGame) throw new InvalidGameActionException(message);
    }

    /// <summary>
    /// The 8.5 split: only an AccountBan blocks creating a game at all. A RatedPlayBan still permits
    /// an unrated game, which is exactly what it is meant to leave open (see GameEndpoints' POST /).
    /// </summary>
    public async Task EnsureAccountNotBannedAsync(Guid userId, CancellationToken ct = default)
    {
        var ban = await _sanctions.BlockingSanctionAsync(userId, SanctionScope.Account, ct);
        if (ban is not null)
            throw new InvalidGameActionException("Your account is suspended and cannot create games.");
    }

    /// <summary>
    /// The rated-play gate: the 8.5 sanction check then the 6e.3 abandonment cooldown, in that order
    /// and with the terse messages the matchmaking panel renders (the full reason with its expiry
    /// lives on the profile page via GET /api/moderation/my-sanctions).
    /// </summary>
    public async Task EnsureEligibleForRatedPlayAsync(Guid userId, CancellationToken ct = default)
    {
        // Phase 8.5: checked before the abandonment cooldown, deliberately terse and without the
        // reason text - this throws into a SignalR hub call and renders in the matchmaking panel; the
        // full reason with its expiry lives on the profile page via GET /api/moderation/my-sanctions.
        var sanction = await _sanctions.BlockingSanctionAsync(userId, SanctionScope.RatedPlay, ct);
        if (sanction is not null)
            throw new InvalidGameActionException(
                sanction.Type == SanctionType.AccountBan
                    ? "Your account is suspended and cannot play rated games."
                    : "You are not currently eligible for rated play.");

        var verdict = await _abandonment.EvaluateAsync(userId, ct);
        if (!verdict.Allowed)
        {
            var minutes = (int)Math.Ceiling(verdict.RetryAfter.TotalMinutes);
            throw new InvalidGameActionException(
                $"Too many recent abandoned games, you can queue again in {minutes} minute{(minutes == 1 ? "" : "s")}.");
        }
    }

    /// <summary>The message every walled path returns, asserted verbatim by tests; part of the contract.</summary>
    public const string EmailNotConfirmedMessage = "Confirm your email address to start playing.";

    /// <summary>
    /// The confirmed-email gate on two-player play. Distinct from <c>Auth:RequireConfirmedEmail</c>,
    /// which gates *sign-in* and is read in <see cref="Auth.AuthEndpoints"/>: that flag cannot express
    /// this rule, because <c>POST /api/auth/register</c> issues a token pair unconditionally, so a new
    /// account is signed in and can play a whole first session before the login gate is ever consulted.
    /// Gating the play paths instead is what actually makes a confirmed address a precondition for a
    /// game, and it leaves signup, <c>/learn</c>, <c>/rules</c> and spectating open to somebody whose
    /// mail has not landed yet.
    ///
    /// <para>Driven by <c>Auth:RequireConfirmedEmailForPlayFrom</c>, an ISO-8601 instant:</para>
    /// <list type="bullet">
    /// <item>Unset or unparseable - the gate is off entirely, which is the default and the state every
    /// existing deployment starts in. A typo must not wall the whole player base, so it fails off and
    /// says so in the log rather than throwing at boot.</item>
    /// <item>Set - accounts created at or after that instant must confirm before they play; accounts
    /// created before it are grandfathered forever. Deliberately a cutoff rather than a bool: turning a
    /// bare flag on would lock out every player who already signed up and never had a reason to click
    /// the link, which is a support incident rather than an anti-abuse win.</item>
    /// </list>
    ///
    /// <para>Read per call, never captured at construction: the flag is a lever the deployment owner
    /// expects to move without a redeploy, and an eager read also cannot be overridden by a test's
    /// configuration (see the same rule stated at <see cref="Auth.AuthEndpoints"/>'s login check).</para>
    ///
    /// <para>The verdict is a database read rather than a claim on the access token, and that is the
    /// whole design. A claim is minted at login and would stay stale until the next refresh, so a
    /// player who clicked the link seconds ago would still be told to confirm - the one moment the
    /// gate must not fire. <c>docs/decisions/AUTH.md</c> §1.5 records the same staleness trap for the
    /// <c>mfa</c> claim; this avoids it by asking the row.</para>
    ///
    /// <para>Enforcement points, in full - treat as a contract, and see <c>PlayGatePathTests</c>, which
    /// fails if a new play-entry path appears without a decision:</para>
    /// <list type="bullet">
    /// <item>Queue join - <see cref="MatchmakingService"/></item>
    /// <item><c>POST /api/games</c> and <c>POST /api/v1/games</c></item>
    /// <item><c>POST /api/challenges</c> and <c>POST /api/v1/challenges</c></item>
    /// <item>Challenge accept - <c>ChallengeEndpoints.AcceptChallengeAsync</c>, shared by both surfaces</item>
    /// <item>Rematch accept - <see cref="GameHub"/></item>
    /// </list>
    /// The move and resign paths are deliberately NOT enforcement points, for the reason
    /// <see cref="SanctionPolicy"/> already states about sanctions: a rejected move can lose a game on
    /// time, so a gate there would turn a configuration change into a flagfall. A player seated when
    /// the cutoff moves finishes that game and cannot start another.
    /// </summary>
    public async Task EnsureEmailConfirmedAsync(Guid userId, CancellationToken ct = default)
    {
        if (await IsBlockedByEmailGateAsync(userId, ct))
            throw new InvalidGameActionException(EmailNotConfirmedMessage);
    }

    /// <summary>
    /// The same verdict as <see cref="EnsureEmailConfirmedAsync"/> without the throw, so the account
    /// surface can tell a player they are gated *before* they click Play and hit a 403. Both read this
    /// one implementation: a banner that re-derived the cutoff client-side would be a second copy of
    /// the rule, free to disagree with the one the play paths actually enforce.
    /// </summary>
    public async Task<bool> IsBlockedByEmailGateAsync(Guid userId, CancellationToken ct = default)
    {
        var raw = _config["Auth:RequireConfirmedEmailForPlayFrom"];
        if (string.IsNullOrWhiteSpace(raw)) return false;

        if (!DateTimeOffset.TryParse(
                raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var cutoff))
        {
            _logger.LogError(
                "Auth:RequireConfirmedEmailForPlayFrom is set to {Value}, which is not an ISO-8601 instant; "
                + "the confirmed-email play gate is OFF until it is corrected", raw);
            return false;
        }

        // Fail open on a database fault, the same trade SanctionPolicy makes and for the same reason:
        // a hiccup here must not deny every player a game. Only the read sits inside the try, so a
        // future refusal added below cannot be mistaken for the fault and swallowed by this catch.
        AccountGateFacts? account;
        try
        {
            using var scopeHandle = _scopeFactory.CreateScope();
            var db = scopeHandle.ServiceProvider.GetRequiredService<KiriChessDbContext>();
            account = await db.Users.AsNoTracking()
                .Where(u => u.Id == userId)
                .Select(u => new AccountGateFacts(u.EmailConfirmed, u.CreatedAt))
                .FirstOrDefaultAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex, "PlayEligibilityPolicy fail-open: confirmed-email check errored for user {UserId}; allowing play",
                userId);
            return false;
        }

        // No row is not this policy's refusal to make. The caller is authenticated, so the id came off
        // a validated token; an account erased since then fails on the calling path's own terms, and
        // answering "confirm your email" to somebody with no account would be a lie.
        if (account is null) return false;
        if (account.Confirmed) return false;
        return account.CreatedAt >= cutoff;
    }

    private sealed record AccountGateFacts(bool Confirmed, DateTimeOffset CreatedAt);
}
