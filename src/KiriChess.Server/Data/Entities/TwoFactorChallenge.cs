namespace KiriChess.Server.Data.Entities;

/// <summary>
/// Phase 9d.2. The ticket handed back by /api/auth/login when the account has 2FA enabled, exchanged
/// once at /api/auth/login/2fa for a real token pair.
///
/// Deliberately an opaque database row and not a "half-authenticated" JWT: a JWT would validate
/// against JwtBearerDefaults and therefore be accepted by every RequireAuthorization() endpoint in
/// the server unless each one separately checked a purpose claim - a 2FA bypass one forgotten check
/// away. A row cannot be presented as a bearer token at all.
///
/// Stored as a SHA-256 hash for the same reason <see cref="RefreshToken"/> and
/// <see cref="ExternalLoginCode"/> are: a database leak must not hand over usable credentials.
/// </summary>
public sealed class TwoFactorChallenge
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public ApplicationUser? User { get; set; }

    public string TicketHash { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
}
