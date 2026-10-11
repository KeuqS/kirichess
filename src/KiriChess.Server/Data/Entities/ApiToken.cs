namespace KiriChess.Server.Data.Entities;

/// <summary>
/// A personal access token (Phase 9f.5). Stored as a SHA-256 hash so the raw value is never
/// persisted, the same shape as <see cref="RefreshToken"/>. Unlike refresh tokens these do not
/// rotate on use: they are independently revocable bot credentials, not a session chain, and reuse
/// detection would revoke a live engine the first time it retried a request. S-1-4: every row has
/// an <see cref="ExpiresAt"/>. The default life is 90 days.
/// </summary>
public sealed class ApiToken
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public ApplicationUser? User { get; set; }

    public string TokenHash { get; set; } = string.Empty;

    /// <summary>The first eight characters of the secret after <c>kirichess_pat_</c>, for display.</summary>
    public string TokenPrefix { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public bool CanRead { get; set; }
    public bool CanPlay { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
}
