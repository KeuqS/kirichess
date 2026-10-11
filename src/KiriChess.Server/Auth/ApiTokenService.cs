using System.Data;
using System.Security.Cryptography;
using System.Text;
using KiriChess.Server.Data;
using KiriChess.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace KiriChess.Server.Auth;

/// <summary>
/// Issues, lists, authenticates, and revokes API tokens (Phase 9f.5). Raw tokens are never stored,
/// only their SHA-256 hash, matching <see cref="RefreshTokenService"/>.
/// </summary>
public sealed class ApiTokenService
{
    private static readonly TimeSpan LastUsedThrottle = TimeSpan.FromMinutes(1);

    private readonly KiriChessDbContext _db;

    public ApiTokenService(KiriChessDbContext db) => _db = db;

    /// <summary>
    /// Mints a token. Returns the raw secret once; it is never recoverable afterwards.
    /// <paramref name="scopes"/> is case-insensitive; <c>play</c> always implies <c>read</c>.
    /// <paramref name="expiresInDays"/> omitted means <see cref="ApiTokenDefaults.DefaultExpiresInDays"/>.
    /// </summary>
    public async Task<(ApiToken Token, string Raw)> IssueAsync(
        Guid userId, string name, IEnumerable<string> scopes, int? expiresInDays = null, CancellationToken ct = default)
    {
        var trimmedName = name.Trim();
        if (trimmedName.Length is < 1 or > 64)
            throw new ArgumentException("Name must be between 1 and 64 characters.");

        var days = expiresInDays ?? ApiTokenDefaults.DefaultExpiresInDays;
        if (days is < 1 or > ApiTokenDefaults.MaxExpiresInDays)
            throw new ArgumentException(
                $"ExpiresInDays must be between 1 and {ApiTokenDefaults.MaxExpiresInDays}.");

        var (canRead, canPlay) = ParseScopes(scopes);
        if (!canRead && !canPlay)
            throw new ArgumentException(
                $"At least one scope is required. Known: {ApiTokenScopes.Read}, {ApiTokenScopes.Play}.");
        if (canPlay) canRead = true;

        // S-4-5: the count and the insert used to be two round trips with nothing behind them, so two
        // concurrent mints could each read a count below the cap and each insert, letting an account
        // hold more than the documented ten. Serializable isolation makes the pair atomic with respect
        // to a second, concurrent call to this method: Postgres detects the write skew and aborts one
        // transaction at commit rather than letting both succeed. CreateExecutionStrategy().ExecuteAsync
        // is required for the same reason AccountDeletionService.cs's transaction uses it -
        // EnableRetryOnFailure installs a retrying execution strategy, and EF throws on any
        // user-initiated transaction that does not run through one - and it is what makes the
        // serialization failure transparently retried instead of surfacing as a 500.
        //
        // The retry is also why `pending` exists. Constructing the entity fresh inside the delegate is
        // not on its own enough: EF does not detach on rollback, so the entity a failed attempt handed
        // to the change tracker is still sitting there in Added state when the strategy runs the
        // delegate again, and the next SaveChangesAsync would insert that one as well as the new one.
        // A retry firing here means a genuine serialization failure, which is precisely the concurrent
        // case this whole change exists for, so it would have let the cap be exceeded on the one path
        // it is meant to close. Detaching the previous attempt's entity is deliberately narrower than
        // ChangeTracker.Clear(): this DbContext is request-scoped and shared with the rest of the
        // handler, which may be tracking entities of its own that have nothing to do with this mint.
        ApiToken? pending = null;
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            if (pending is not null)
            {
                _db.Entry(pending).State = EntityState.Detached;
                pending = null;
            }

            await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);

            var now = DateTimeOffset.UtcNow;
            var active = await _db.ApiTokens.CountAsync(
                t => t.UserId == userId && t.RevokedAt == null && (t.ExpiresAt == null || t.ExpiresAt > now), ct);
            if (active >= ApiTokenDefaults.MaxActivePerUser)
                throw new InvalidOperationException(
                    $"You already have {ApiTokenDefaults.MaxActivePerUser} active API tokens. Revoke one first.");

            var raw = GenerateRawToken();
            var remainder = raw[ApiTokenDefaults.Prefix.Length..];
            var entity = new ApiToken
            {
                UserId = userId,
                TokenHash = Hash(raw),
                TokenPrefix = remainder.Length <= ApiTokenDefaults.PrefixDisplayLength
                    ? remainder
                    : remainder[..ApiTokenDefaults.PrefixDisplayLength],
                Name = trimmedName,
                CanRead = canRead,
                CanPlay = canPlay,
                CreatedAt = now,
                ExpiresAt = now.AddDays(days)
            };
            _db.ApiTokens.Add(entity);
            pending = entity;
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return (entity, raw);
        });
    }

    public async Task<List<ApiToken>> ListActiveAsync(Guid userId, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        return await _db.ApiTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null && (t.ExpiresAt == null || t.ExpiresAt > now))
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Validates a raw token and returns the live row with its user. Null on missing, revoked,
    /// expired, or a tombstoned account. Touches <see cref="ApiToken.LastUsedAt"/> at most once a minute.
    /// </summary>
    public async Task<ApiToken?> FindValidAsync(string raw, CancellationToken ct = default)
    {
        // A bad checksum is not a credential. Skipping the hash lookup is what lets a scanner
        // make the same decision offline (S-4-3).
        if (!ApiTokenFormat.IsWellFormed(raw))
            return null;

        var hash = Hash(raw);
        var existing = await _db.ApiTokens
            .Include(t => t.User)
            .SingleOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (existing is null || existing.RevokedAt is not null) return null;
        if (existing.ExpiresAt is { } expiry && expiry <= DateTimeOffset.UtcNow) return null;
        if (existing.User is null || existing.User.DeletedAt is not null) return null;

        var now = DateTimeOffset.UtcNow;
        if (existing.LastUsedAt is null || now - existing.LastUsedAt >= LastUsedThrottle)
        {
            existing.LastUsedAt = now;
            await _db.SaveChangesAsync(ct);
        }

        return existing;
    }

    /// <summary>
    /// S-4-1: the same liveness question <see cref="FindValidAsync"/> answers, asked by token id
    /// instead of by secret, for a caller that has already been authenticated once and no longer
    /// holds the raw token - specifically a live SignalR connection, whose <c>ClaimsPrincipal</c>
    /// was frozen at connect time and carries only <see cref="ApiTokenClaims.TokenId"/>.
    ///
    /// Read-only on purpose: it does not touch <see cref="ApiToken.LastUsedAt"/>. That column means
    /// "when did this key last present itself", which a background re-check is not, and writing it
    /// here would turn a periodic check into a periodic write on every live bot connection.
    ///
    /// A ban is covered without asking about sanctions at all, because
    /// <c>SanctionService.IssueAsync</c> revokes every API token on an <c>AccountBan</c>, so a banned
    /// account's tokens already carry <see cref="ApiToken.RevokedAt"/>.
    /// </summary>
    public Task<bool> IsStillValidAsync(Guid tokenId, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        return _db.ApiTokens
            .AsNoTracking()
            .AnyAsync(t => t.Id == tokenId
                && t.RevokedAt == null
                && (t.ExpiresAt == null || t.ExpiresAt > now)
                && t.User != null
                && t.User.DeletedAt == null, ct);
    }

    /// <summary>
    /// Revokes one token by id, scoped to <paramref name="userId"/>. Silently no-ops on an unknown
    /// id or one that belongs to someone else, same no-oracle shape as
    /// <see cref="RefreshTokenService.RevokeByIdAsync"/>.
    /// </summary>
    public async Task RevokeByIdAsync(Guid userId, Guid tokenId, CancellationToken ct = default)
    {
        var existing = await _db.ApiTokens
            .SingleOrDefaultAsync(t => t.Id == tokenId && t.UserId == userId, ct);
        if (existing is null || existing.RevokedAt is not null) return;
        existing.RevokedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    public async Task RevokeAllForUserAsync(Guid userId, CancellationToken ct = default)
    {
        var active = await _db.ApiTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .ToListAsync(ct);
        foreach (var t in active) t.RevokedAt = DateTimeOffset.UtcNow;
        if (active.Count > 0) await _db.SaveChangesAsync(ct);
    }

    public static IReadOnlyList<string> ScopeList(ApiToken token)
    {
        var scopes = new List<string>(2);
        if (token.CanRead) scopes.Add(ApiTokenScopes.Read);
        if (token.CanPlay) scopes.Add(ApiTokenScopes.Play);
        return scopes;
    }

    public static string HashToken(string raw) => Hash(raw);

    private static (bool CanRead, bool CanPlay) ParseScopes(IEnumerable<string> scopes)
    {
        var canRead = false;
        var canPlay = false;
        foreach (var raw in scopes)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var scope = raw.Trim().ToLowerInvariant();
            if (scope == ApiTokenScopes.Read) canRead = true;
            else if (scope == ApiTokenScopes.Play) canPlay = true;
            else
                throw new ArgumentException(
                    $"Unknown scope '{raw}'. Known: {ApiTokenScopes.Read}, {ApiTokenScopes.Play}.");
        }

        return (canRead, canPlay);
    }

    private static string GenerateRawToken() => ApiTokenFormat.Create(RandomNumberGenerator.GetBytes(32));

    private static string Hash(string raw) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
}
