namespace KiriChess.Server.Data.Entities;

/// <summary>
/// An issued refresh token, stored as a SHA-256 hash so the raw value is never persisted. Rotated
/// on every use; reuse of an already-rotated token revokes every active token for the user, since
/// that pattern only happens if a stolen token and the legitimate client both tried to use it.
/// </summary>
public sealed class RefreshToken
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public ApplicationUser? User { get; set; }

    public string TokenHash { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public Guid? ReplacedByTokenId { get; set; }
}
