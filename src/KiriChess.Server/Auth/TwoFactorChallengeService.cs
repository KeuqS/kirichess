using System.Security.Cryptography;
using System.Text;
using KiriChess.Server.Data;
using KiriChess.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace KiriChess.Server.Auth;

/// <summary>
/// Phase 9d.2. Issues, resolves and consumes the one-time 2FA login ticket. Mirrors
/// <see cref="ExternalLoginCodeService"/>'s hashing shape: only a SHA-256 hash is ever persisted.
///
/// Resolve and Consume are separate on purpose. A wrong code must NOT destroy the ticket - a typo
/// would otherwise force a full re-login, and the attempt window is already bounded by the 5-minute
/// TTL and by 6b.2's lockout counter, which /login/2fa increments on every failure.
/// </summary>
public sealed class TwoFactorChallengeService
{
    private readonly KiriChessDbContext _db;
    private readonly TwoFactorOptions _opt;

    public TwoFactorChallengeService(KiriChessDbContext db, IOptions<TwoFactorOptions> opt)
    {
        _db = db;
        _opt = opt.Value;
    }

    public async Task<string> IssueAsync(Guid userId, CancellationToken ct = default)
    {
        var raw = GenerateRawTicket();
        _db.TwoFactorChallenges.Add(new TwoFactorChallenge
        {
            UserId = userId,
            TicketHash = Hash(raw),
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(_opt.ChallengeLifetimeSeconds)
        });
        await _db.SaveChangesAsync(ct);
        return raw;
    }

    /// <summary>Returns the user id for a live ticket, without consuming it. Null when unknown, already consumed, or expired.</summary>
    public async Task<Guid?> ResolveAsync(string rawTicket, CancellationToken ct = default)
    {
        var hash = Hash(rawTicket);
        var existing = await _db.TwoFactorChallenges.SingleOrDefaultAsync(c => c.TicketHash == hash, ct);
        if (existing is null) return null;
        if (existing.ConsumedAt is not null) return null;
        if (existing.ExpiresAt <= DateTimeOffset.UtcNow) return null;
        return existing.UserId;
    }

    /// <summary>
    /// Marks the ticket consumed. Called only after the second factor has actually verified, so a
    /// replayed ticket - two tabs racing the same submit - cannot mint two token pairs from one
    /// successful code.
    /// </summary>
    public async Task ConsumeAsync(string rawTicket, CancellationToken ct = default)
    {
        var hash = Hash(rawTicket);
        var existing = await _db.TwoFactorChallenges.SingleOrDefaultAsync(c => c.TicketHash == hash, ct);
        if (existing is null || existing.ConsumedAt is not null) return;
        existing.ConsumedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Kills every outstanding ticket for a user. Called when 2FA is disabled: a ticket issued a
    /// moment earlier must not still be redeemable against an account that no longer has a second
    /// factor to check it with.
    /// </summary>
    public async Task RevokeAllForUserAsync(Guid userId, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var live = await _db.TwoFactorChallenges
            .Where(c => c.UserId == userId && c.ConsumedAt == null)
            .ToListAsync(ct);
        foreach (var c in live) c.ConsumedAt = now;
        if (live.Count > 0) await _db.SaveChangesAsync(ct);
    }

    // Base64Url for the same reason ExternalLoginCodeService uses it: the value is echoed through a
    // JSON body and then back in another one, and a raw '+' survives that badly across proxies.
    private static string GenerateRawTicket() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static string Hash(string raw) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
}
