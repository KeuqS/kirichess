using KiriChess.Server.Data;
using KiriChess.Server.Data.Entities;
using KiriChess.Server.Moderation;
using Microsoft.EntityFrameworkCore;

namespace KiriChess.Server.Account;

public enum AccountDeletionOutcome { Deleted, NotFound, AlreadyDeleted, GameInProgress }

public sealed record AccountDeletionResult(AccountDeletionOutcome Outcome, DateTimeOffset? DeletedAt);

/// <summary>
/// Phase 8a.2 erasure: anonymises and tombstones an account rather than deleting the row - see
/// README.md scope decision 1 for why, and ground truth 1 for why a row-level DELETE is not possible
/// at all given the Restrict foreign keys on Games, RatingHistories, Reports and UserSanctions.
/// Scoped: takes a DbContext and is only ever called from a request handler.
/// </summary>
public sealed class AccountDeletionService
{
    private readonly KiriChessDbContext _db;
    private readonly BanEvasionMarker _banEvasion;
    private readonly ILogger<AccountDeletionService> _logger;

    // No UserManager here on purpose: every deletion below is a set-based ExecuteDeleteAsync against
    // Identity's own DbSets, which is one statement per table instead of UserManager's load-then-remove
    // round trip per login/claim/token.
    public AccountDeletionService(
        KiriChessDbContext db, BanEvasionMarker banEvasion, ILogger<AccountDeletionService> logger)
    {
        _db = db;
        _banEvasion = banEvasion;
        _logger = logger;
    }

    public async Task<AccountDeletionResult> DeleteAsync(Guid userId, CancellationToken ct)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return new AccountDeletionResult(AccountDeletionOutcome.NotFound, null);
        if (user.DeletedAt is not null) return new AccountDeletionResult(AccountDeletionOutcome.AlreadyDeleted, null);

        // The live-game guard runs FIRST, before any write. README scope decision 3: scrubbing a
        // player mid-game strands the opponent against a session whose owner no longer resolves, and
        // a partial scrub followed by a refusal is the worst outcome available.
        var inGame = await _db.Games.AnyAsync(
            g => g.Result == GameResult.Ongoing && (g.WhitePlayerId == userId || g.BlackPlayerId == userId), ct);
        if (inGame) return new AccountDeletionResult(AccountDeletionOutcome.GameInProgress, null);

        // S-3-1. Both of these are computed BEFORE the retryable block below, and that ordering is
        // load-bearing rather than stylistic: the block scrubs `user.NormalizedEmail` to null in
        // memory, and nothing rolls an in-memory assignment back, so a retried attempt would derive
        // the marker from null and quietly write nothing. Computed out here it is a constant the
        // block assigns, which keeps the block's "every write is an absolute-value assignment"
        // replay-safety property exactly as it was.
        //
        // The ban read is its own query rather than a SanctionPolicy call: that policy fails open on
        // a database error, which is the right answer for a login check and the wrong one here, where
        // failing open means silently not marking a banned tombstone. Note the type filter - only a
        // full AccountBan produces a marker. A Warning or a RatedPlayBan leaves erasure byte for byte
        // what it was before this existed, because neither is something a fresh account escapes.
        var sanctionCheckedAt = DateTimeOffset.UtcNow;
        var hasActiveAccountBan = await _db.UserSanctions.AnyAsync(s =>
            s.UserId == userId
            && s.Type == SanctionType.AccountBan
            && s.LiftedAt == null
            && (s.ExpiresAt == null || s.ExpiresAt > sanctionCheckedAt), ct);
        var banEvasionMarker = _banEvasion.MarkerForErasure(user.NormalizedEmail, hasActiveAccountBan, userId);

        // ExecuteDeleteAsync writes immediately and does not participate in SaveChanges's unit of
        // work, so without an explicit transaction a failure halfway through would leave an account
        // with its sessions destroyed and its email intact.
        //
        // 9a.4 wraps this in CreateExecutionStrategy().ExecuteAsync: EnableRetryOnFailure installs a
        // retrying execution strategy, and EF throws on any user-initiated transaction that isn't run
        // through one, this is the only explicit BeginTransactionAsync in the server. The block is
        // safe to replay: every write is either an idempotent ExecuteDeleteAsync or an absolute-value
        // field assignment on `user`, so a retry after a transient failure (nothing committed) repeats
        // the same end state rather than double-applying anything.
        var strategy = _db.Database.CreateExecutionStrategy();
        var deletedAt = await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);

            // Cascade-delete the credential and session surface - the account's own, purely.
            // Everything else (Games, RatingHistories, Reports, UserSanctions) is Restrict and stays,
            // pointing at the tombstone below.
            await _db.RefreshTokens.Where(t => t.UserId == userId).ExecuteDeleteAsync(ct);
            await _db.ApiTokens.Where(t => t.UserId == userId).ExecuteDeleteAsync(ct);
            await _db.ExternalLoginCodes.Where(c => c.UserId == userId).ExecuteDeleteAsync(ct);
            // Phase 9d.2: the 2FA login ticket surface, same shape as ExternalLoginCodes above.
            await _db.TwoFactorChallenges.Where(c => c.UserId == userId).ExecuteDeleteAsync(ct);
            // L-2-1: WebPushSubscription's FK is Cascade, but erasure never deletes the user row - it
            // scrubs it in place - so that cascade never fires, same trap as every table above.
            await _db.WebPushSubscriptions.Where(s => s.UserId == userId).ExecuteDeleteAsync(ct);
            // Phase 9d.8: both directions. ChallengerId is the account's own invitations; AcceptedById
            // is as much a link to a person as a created-by one, and both would otherwise survive
            // erasure pointing at a userId that no longer resolves to anything.
            await _db.Challenges.Where(c => c.ChallengerId == userId || c.AcceptedById == userId).ExecuteDeleteAsync(ct);
            await _db.RematchOffers.Where(r => r.OfferedById == userId || r.AcceptedById == userId).ExecuteDeleteAsync(ct);
            await _db.Ratings.Where(r => r.UserId == userId).ExecuteDeleteAsync(ct);
            await _db.UserLogins.Where(l => l.UserId == userId).ExecuteDeleteAsync(ct);
            await _db.UserClaims.Where(c => c.UserId == userId).ExecuteDeleteAsync(ct);
            // UserTokens is also where Identity stores the TOTP shared secret and the recovery-code
            // list (Phase 9d.2) - this delete already covered the entire 2FA credential surface before
            // this phase touched anything. See Phase 9d.2 § 1.2.
            await _db.UserTokens.Where(t => t.UserId == userId).ExecuteDeleteAsync(ct);
            await _db.UserRoles.Where(r => r.UserId == userId).ExecuteDeleteAsync(ct);

            // Scrub the row in place - exactly the table in README scope decision 1.
            var deletedAt = DateTimeOffset.UtcNow;
            // S-3-1. Already derived above, from the address this row still carried at that point;
            // this is only the assignment. Null on every account that was not banned at erasure time
            // and on every deployment with no marker secret set, so it is a no-op for ordinary
            // erasure and the column stays empty on all but the rows it exists for.
            user.BanEvasionEmailHash = banEvasionMarker;
            user.Email = null;
            user.NormalizedEmail = null;
            user.EmailConfirmed = false;
            user.UserName = $"deleted-{user.Id:N}";
            user.NormalizedUserName = $"DELETED-{user.Id:N}".ToUpperInvariant();
            user.PasswordHash = null;
            user.SecurityStamp = Guid.NewGuid().ToString("N");
            user.PhoneNumber = null;
            user.PhoneNumberConfirmed = false;
            user.TwoFactorEnabled = false;
            user.LockoutEnd = null;
            // DisplayName only - NormalizedDisplayName is derived by SyncNormalizedDisplayNames on
            // SaveChanges (8a.4's mechanism), so a call site here cannot forget to key it.
            user.DisplayName = "Deleted account";
            user.AvatarImage = null;
            user.AvatarUpdatedAt = null;
            user.DeletedAt = deletedAt;

            // A tombstone must not keep a badge, and StripeCustomerId/StripeSubscriptionId are
            // identifying, same reasoning as every other column scrubbed above. PatronService.Clear
            // leaves StripeCustomerId alone (it is not part of the badge itself), so it is scrubbed
            // here explicitly, alongside every other identifying column.
            PatronService.Clear(user);
            user.StripeCustomerId = null;

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return deletedAt;
        });

        // The user id and nothing else - never the email, never the old display name. A name erased
        // for cause is itself a blocklist term, and 8a.4 scope decision 2 keeps those out of logs.
        _logger.LogInformation("Account {UserId} erased.", userId);

        return new AccountDeletionResult(AccountDeletionOutcome.Deleted, deletedAt);
    }
}
