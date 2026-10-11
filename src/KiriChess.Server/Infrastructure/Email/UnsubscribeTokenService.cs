using System.Security.Cryptography;
using System.Text;
using KiriChess.Server.Auth;
using Microsoft.Extensions.Options;

namespace KiriChess.Server.Infrastructure.Email;

/// <summary>
/// Signs and verifies the token on a one-click unsubscribe link (Phase 11b.1). Deliberately not a
/// stored, single-use code like <c>ExternalLoginCode</c>/<c>TwoFactorChallenge</c>: an unsubscribe link
/// has to keep working in *every future* email of that category, not just the most recent one, so it
/// is a stateless HMAC over (userId, category) rather than a row anything expires or consumes. Nothing
/// it protects is security-critical, a forged token can only flip a mail preference, never sign
/// anything in or move money, so a keyed HMAC with no expiry is proportionate.
///
/// Keyed off <see cref="JwtOptions.Key"/> rather than a new configuration secret: this needs 32+ bytes
/// of server-only entropy that already exists and is already validated present at startup
/// (<c>StartupValidation</c>). A fifth secret, just to protect a mail checkbox, is not worth
/// provisioning. The domain-separation label below is what
/// keeps this a distinct key from the one JWTs are actually signed with, deriving a JWT from a leaked
/// unsubscribe token (or vice versa) is not possible from the SHA-256 output alone.
/// </summary>
public sealed class UnsubscribeTokenService
{
    private const string DomainSeparationLabel = "kirichess-unsubscribe-v1";
    private readonly byte[] _key;

    public UnsubscribeTokenService(IOptions<JwtOptions> jwtOptions)
    {
        var jwtKey = Encoding.UTF8.GetBytes(jwtOptions.Value.Key);
        _key = SHA256.HashData([.. jwtKey, .. Encoding.UTF8.GetBytes(DomainSeparationLabel)]);
    }

    public string GenerateToken(Guid userId, NotificationCategory category) =>
        Convert.ToHexString(HMACSHA256.HashData(_key, Payload(userId, category))).ToLowerInvariant();

    /// <summary>Constant-time comparison: a token is attacker-supplied input on an anonymous route.</summary>
    public bool Verify(Guid userId, NotificationCategory category, string token)
    {
        if (string.IsNullOrEmpty(token)) return false;
        byte[] given;
        try { given = Convert.FromHexString(token); }
        catch (FormatException) { return false; }

        var expected = HMACSHA256.HashData(_key, Payload(userId, category));
        return given.Length == expected.Length && CryptographicOperations.FixedTimeEquals(given, expected);
    }

    private static byte[] Payload(Guid userId, NotificationCategory category) =>
        Encoding.UTF8.GetBytes($"{userId}:{category}");
}
