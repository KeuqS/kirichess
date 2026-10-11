using System.Security.Cryptography;
using System.Text;
using KiriChess.Server.Data;
using KiriChess.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace KiriChess.Server.Auth;

/// <summary>
/// Issues and consumes the one-time exchange code handed to the browser after a successful OAuth
/// callback (see <see cref="ExternalLoginCode"/> for why a code and not the tokens themselves).
/// Mirrors <see cref="RefreshTokenService"/>'s hashing shape: only a SHA-256 hash is ever persisted.
/// </summary>
public sealed class ExternalLoginCodeService
{
    private readonly KiriChessDbContext _db;
    private readonly ExternalAuthOptions _opt;

    public ExternalLoginCodeService(KiriChessDbContext db, ExternalAuthOptions opt)
    {
        _db = db;
        _opt = opt;
    }

    public async Task<string> IssueAsync(Guid userId, bool accountCreated = false, CancellationToken ct = default)
    {
        var raw = GenerateRawToken();
        _db.ExternalLoginCodes.Add(new ExternalLoginCode
        {
            UserId = userId,
            CodeHash = Hash(raw),
            AccountCreated = accountCreated,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(_opt.CodeLifetimeSeconds)
        });
        await _db.SaveChangesAsync(ct);
        return raw;
    }

    /// <summary>
    /// Returns the user id and whether this handshake registered the account, and marks the code
    /// consumed, or null when the code is unknown, already consumed, or expired. Consumption is
    /// atomic, <see cref="ExternalLoginCode.ConsumedAt"/> is set and saved before the id is returned
    /// to the caller, so a double-submit (e.g. two tabs racing the exchange call, or a replayed
    /// callback URL) cannot mint two token pairs from one code.
    /// </summary>
    public async Task<ConsumedExternalCode?> ConsumeAsync(string rawCode, CancellationToken ct = default)
    {
        var hash = Hash(rawCode);
        var existing = await _db.ExternalLoginCodes.SingleOrDefaultAsync(c => c.CodeHash == hash, ct);
        if (existing is null) return null;
        if (existing.ConsumedAt is not null) return null;
        if (existing.ExpiresAt <= DateTimeOffset.UtcNow) return null;

        existing.ConsumedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return new ConsumedExternalCode(existing.UserId, existing.AccountCreated);
    }

    // Base64Url, not plain Base64: the raw value travels in a query string, and a '+' there silently
    // decodes to a space by the time it reaches the browser or a proxy, corrupting the code before
    // it is ever exchanged.
    private static string GenerateRawToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static string Hash(string raw) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
}

/// <summary>
/// What <see cref="ExternalLoginCodeService.ConsumeAsync"/> hands back. <see cref="AccountCreated"/>
/// is the M-4-1 flag: true only for the handshake that registered the account.
/// </summary>
public readonly record struct ConsumedExternalCode(Guid UserId, bool AccountCreated);
