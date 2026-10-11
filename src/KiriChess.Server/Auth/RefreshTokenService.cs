using System.Security.Cryptography;
using System.Text;
using KiriChess.Server.Data;
using KiriChess.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace KiriChess.Server.Auth;

/// <summary>Issues, rotates, and revokes refresh tokens. Raw tokens are never stored, only their SHA-256 hash.</summary>
public sealed class RefreshTokenService
{
    private readonly KiriChessDbContext _db;
    private readonly JwtOptions _opt;

    public RefreshTokenService(KiriChessDbContext db, IOptions<JwtOptions> opt)
    {
        _db = db;
        _opt = opt.Value;
    }

    public async Task<string> IssueAsync(Guid userId, CancellationToken ct = default)
    {
        var raw = GenerateRawToken();
        _db.RefreshTokens.Add(new RefreshToken
        {
            UserId = userId,
            TokenHash = Hash(raw),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(_opt.RefreshTokenExpiryDays)
        });
        await _db.SaveChangesAsync(ct);
        return raw;
    }

    /// <summary>
    /// Validates and rotates a refresh token. Returns null on a missing, expired, or revoked token.
    /// Presenting an *already-revoked* token specifically (as opposed to an unknown one) is treated
    /// as evidence of theft, every active token for that user is revoked in response.
    /// </summary>
    public async Task<(Guid UserId, string RefreshToken)?> RotateAsync(string rawToken, CancellationToken ct = default)
    {
        var hash = Hash(rawToken);
        var existing = await _db.RefreshTokens.SingleOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (existing is null) return null;

        if (existing.RevokedAt is not null)
        {
            await RevokeAllForUserAsync(existing.UserId, ct);
            return null;
        }

        if (existing.ExpiresAt <= DateTimeOffset.UtcNow) return null;

        var raw = GenerateRawToken();
        var next = new RefreshToken
        {
            UserId = existing.UserId,
            TokenHash = Hash(raw),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(_opt.RefreshTokenExpiryDays)
        };
        existing.RevokedAt = DateTimeOffset.UtcNow;
        existing.ReplacedByTokenId = next.Id;
        _db.RefreshTokens.Add(next);
        await _db.SaveChangesAsync(ct);
        return (existing.UserId, raw);
    }

    public async Task RevokeAsync(string rawToken, CancellationToken ct = default)
    {
        var hash = Hash(rawToken);
        var existing = await _db.RefreshTokens.SingleOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (existing is null || existing.RevokedAt is not null) return;
        existing.RevokedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    public async Task RevokeAllForUserAsync(Guid userId, CancellationToken ct = default)
    {
        var active = await _db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .ToListAsync(ct);
        foreach (var t in active) t.RevokedAt = DateTimeOffset.UtcNow;
        if (active.Count > 0) await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The caller's own active sessions (Phase 9e.6): not revoked, not expired, newest first. Each
    /// row is one refresh token, i.e. one device/browser that has not signed out and has not been
    /// idle past <see cref="JwtOptions.RefreshTokenExpiryDays"/>.
    /// </summary>
    public async Task<List<RefreshToken>> ListActiveAsync(Guid userId, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        return await _db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null && t.ExpiresAt > now)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Revokes one session by id, scoped to <paramref name="userId"/> so a caller can only ever
    /// revoke their own sessions. Silently no-ops on an unknown id or one that belongs to someone
    /// else - same no-oracle shape as <see cref="RevokeAsync"/>, the caller never learns which.
    /// </summary>
    public async Task RevokeByIdAsync(Guid userId, Guid tokenId, CancellationToken ct = default)
    {
        var existing = await _db.RefreshTokens
            .SingleOrDefaultAsync(t => t.Id == tokenId && t.UserId == userId, ct);
        if (existing is null || existing.RevokedAt is not null) return;
        existing.RevokedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Exposes the same hash <see cref="RotateAsync"/> and <see cref="RevokeAsync"/> compare against,
    /// so a caller holding a raw refresh token (e.g. the active-session list marking "this device")
    /// can match it against a <see cref="RefreshToken.TokenHash"/> without the service storing or
    /// accepting the raw value back.
    /// </summary>
    public static string HashToken(string raw) => Hash(raw);

    private static string GenerateRawToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private static string Hash(string raw) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
}
