using KiriChess.Server.Data;
using KiriChess.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace KiriChess.Server.Moderation.Content;

/// <summary>
/// The database-aware half of Phase 8a.4 display-name policy: uniqueness on top of
/// <see cref="ContentPolicy"/>'s pure checks, plus the external-login fallback. Scoped, because it
/// takes a <see cref="KiriChessDbContext"/> and is only ever used from a request handler.
/// </summary>
public sealed class DisplayNameService
{
    private readonly KiriChessDbContext _db;
    private readonly ContentPolicy _policy;
    private readonly ContentModerationOptions _options;
    private readonly ILogger<DisplayNameService> _logger;

    public DisplayNameService(KiriChessDbContext db, ContentPolicy policy,
        ContentModerationOptions options, ILogger<DisplayNameService> logger)
    {
        _db = db;
        _policy = policy;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Policy check plus the uniqueness check. <paramref name="excludeUserId"/> is the account being
    /// renamed, so its own current key is not treated as a collision with itself; pass null at signup.
    ///
    /// Cooldown is NOT checked here - this answers "is this name usable", and the cooldown is about
    /// _when_ you may change, not _what to_. The endpoint (Task C) enforces cooldown so this route can
    /// still tell a player their desired name is free.
    /// </summary>
    public async Task<DisplayNameVerdict> CheckAsync(string? candidate, Guid? excludeUserId, CancellationToken ct)
    {
        var verdict = _policy.CheckDisplayName(candidate, _options);
        if (!verdict.Allowed) return verdict;

        // Hoisted into a local before the expression tree - a nullable captured directly is a
        // translation hazard, the same care SanctionPolicy takes with DateTimeOffset.UtcNow.
        var exclude = excludeUserId;
        var taken = await _db.Users.AnyAsync(
            u => u.NormalizedDisplayName == verdict.Normalized && (exclude == null || u.Id != exclude), ct);

        if (taken)
        {
            return verdict with
            {
                Allowed = false,
                Rejection = DisplayNameRejection.Taken,
                Message = "That name is already taken."
            };
        }

        return verdict;
    }

    /// <summary>
    /// Writes DisplayName / NormalizedDisplayName / DisplayNameChangedAt onto the entity in memory.
    /// Deliberately does NOT SaveChanges: the register path saves through UserManager.CreateAsync and
    /// the rename path saves through the DbContext, and this method must serve both.
    /// </summary>
    public static void Apply(ApplicationUser user, string displayName, string normalized, DateTimeOffset? changedAt)
    {
        user.DisplayName = displayName;
        user.NormalizedDisplayName = normalized;
        user.DisplayNameChangedAt = changedAt;
    }

    /// <summary>
    /// The external-login fallback. An OAuth callback cannot ask the user to try again - the account is
    /// being created right now, and refusing means the sign-in fails for a user who did nothing wrong.
    /// So this never fails: it sanitizes rather than rejects, and suffixes on collision. Returns a name
    /// that is guaranteed to pass <see cref="CheckAsync"/> at the moment it is returned.
    /// </summary>
    public async Task<string> DeriveAvailableAsync(string? candidate, string? email, CancellationToken ct)
    {
        var sanitized = Sanitize(candidate);

        if (_policy.CheckDisplayName(sanitized, _options).Allowed is not true)
        {
            var fromEmail = Sanitize(email?.Split('@')[0]);
            sanitized = _policy.CheckDisplayName(fromEmail, _options).Allowed ? fromEmail : "Player";
        }

        // Probe for a free key: the bare name, then "{name}-{n}" for n = 2..99, truncating the base so
        // the suffix fits inside MaxLength.
        for (var n = 1; n <= 99; n++)
        {
            var candidateName = n == 1 ? sanitized : Suffix(sanitized, n);
            var verdict = await CheckAsync(candidateName, excludeUserId: null, ct);
            if (verdict.Allowed) return candidateName;
        }

        // All 99 taken - an id-derived name that cannot collide. This is a signal somebody is farming
        // a name, not a routine event.
        _logger.LogInformation("DeriveAvailableAsync exhausted the 1-99 suffix search for {Sanitized}", sanitized);
        var fallback = $"Player-{Guid.NewGuid():N}";
        return fallback.Length > _options.MaxLength ? fallback[.._options.MaxLength] : fallback;
    }

    private string Suffix(string baseName, int n)
    {
        var suffix = $"-{n}";
        var maxBaseLength = _options.MaxLength - suffix.Length;
        var truncated = baseName.Length > maxBaseLength ? baseName[..Math.Max(maxBaseLength, 0)] : baseName;
        truncated = truncated.TrimEnd('-', '_', '.', ' ');
        return truncated.Length == 0 ? $"Player{suffix}" : $"{truncated}{suffix}";
    }

    /// <summary>
    /// Sanitizes a candidate to the legal charset ([A-Za-z0-9 _.-]) by stripping illegal characters
    /// rather than rejecting, collapsing runs of separators, trimming separators off both ends, and
    /// truncating to MaxLength. Used only by <see cref="DeriveAvailableAsync"/> - every other path
    /// rejects instead of sanitizing, because every other path has a human to ask again.
    /// </summary>
    private string Sanitize(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return string.Empty;

        var sb = new System.Text.StringBuilder(candidate.Length);
        foreach (var ch in candidate)
        {
            var legal = ch is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9')
                or ' ' or '_' or '.' or '-';
            if (legal) sb.Append(ch);
        }

        // Collapse runs of separators to one.
        var collapsed = new System.Text.StringBuilder(sb.Length);
        foreach (var ch in sb.ToString())
        {
            var isSeparator = ch is ' ' or '_' or '.' or '-';
            if (isSeparator && collapsed.Length > 0 && IsSeparator(collapsed[^1])) continue;
            collapsed.Append(ch);
        }

        var trimmed = collapsed.ToString().Trim(' ', '_', '.', '-');
        if (trimmed.Length > _options.MaxLength) trimmed = trimmed[.._options.MaxLength].TrimEnd(' ', '_', '.', '-');
        return trimmed;
    }

    private static bool IsSeparator(char ch) => ch is ' ' or '_' or '.' or '-';
}
