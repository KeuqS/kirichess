using KiriChess.Server.Auth;
using KiriChess.Server.Data;
using KiriChess.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace KiriChess.Server.Moderation;

/// <summary>
/// Reads and writes <see cref="UserSanction"/> rows for the moderation endpoints (Phase 8.5). Scoped,
/// unlike <see cref="SanctionPolicy"/>: this is only ever called from a request handler, never from the
/// singleton matchmaking path, so it can take a normal injected <see cref="KiriChessDbContext"/>.
/// Validation failures throw <see cref="ArgumentException"/> or <see cref="InvalidOperationException"/>
/// for the endpoint to turn into a 400, matching how <c>GameEndpoints.BuildCustomConfig</c> is caught -
/// there is no exception hierarchy specific to moderation.
/// </summary>
public sealed class SanctionService
{
    private readonly KiriChessDbContext _db;
    private readonly RefreshTokenService _refreshTokens;
    private readonly ApiTokenService _apiTokens;

    public SanctionService(KiriChessDbContext db, RefreshTokenService refreshTokens, ApiTokenService apiTokens)
    {
        _db = db;
        _refreshTokens = refreshTokens;
        _apiTokens = apiTokens;
    }

    /// <summary>
    /// Issues a sanction. Returns null when <paramref name="targetUserId"/> does not exist. Does not
    /// deduplicate and does not supersede an existing sanction - two overlapping bans is a moderator's
    /// problem to see and lift, not a silent merge; <see cref="SanctionPolicy.BlockingSanctionAsync"/>
    /// already picks the strongest one when enforcing.
    /// </summary>
    public async Task<UserSanction?> IssueAsync(
        Guid targetUserId, string typeRaw, string reason, string? moderatorNote,
        DateTimeOffset? expiresAt, Guid? reportId, Guid issuedByUserId, CancellationToken ct = default)
    {
        if (!await _db.Users.AnyAsync(u => u.Id == targetUserId, ct))
            return null;

        if (!Enum.TryParse<SanctionType>(typeRaw, ignoreCase: true, out var type))
            throw new ArgumentException(
                $"Unknown sanction type '{typeRaw}'. Known: {string.Join(", ", Enum.GetNames<SanctionType>())}");

        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("Reason is required.");
        var trimmedReason = reason.Trim();
        if (trimmedReason.Length > 500)
            throw new ArgumentException("Reason must be 500 characters or fewer.");

        if (expiresAt is { } expiry && expiry <= DateTimeOffset.UtcNow)
            throw new ArgumentException("ExpiresAt must be in the future.");

        var note = moderatorNote?.Trim();
        if (note is { Length: 0 }) note = null;
        if (note is not null && note.Length > 2000)
            throw new ArgumentException("ModeratorNote must be 2000 characters or fewer.");

        var sanction = new UserSanction
        {
            UserId = targetUserId,
            Type = type,
            Reason = trimmedReason,
            ModeratorNote = note,
            IssuedByUserId = issuedByUserId,
            ExpiresAt = expiresAt,
            ReportId = reportId
        };
        _db.UserSanctions.Add(sanction);
        await _db.SaveChangesAsync(ct);

        // AccountBan must bite now, not in up to Jwt:ExpiryMinutes: revoking every refresh token
        // forces the next /refresh to hit SanctionPolicy, which is where a live access token's ride
        // finally ends (see the comment on AuthEndpoints' /refresh handler).
        if (type == SanctionType.AccountBan)
        {
            await _refreshTokens.RevokeAllForUserAsync(targetUserId, ct);
            await _apiTokens.RevokeAllForUserAsync(targetUserId, ct);
        }

        return sanction;
    }

    /// <summary>
    /// Lifts a sanction. Returns null when <paramref name="sanctionId"/> does not exist. The row is
    /// never deleted - lifting only sets the three Lift* columns, because the row history is the
    /// audit record of the moderator's own action (see the UserSanction class doc; S-3-3 corrected
    /// this comment, which used to call it an appeal path). Lifting an already-expired sanction is allowed:
    /// it has no further effect, but is still worth recording who closed it out and why.
    /// </summary>
    public async Task<UserSanction?> LiftAsync(
        Guid sanctionId, Guid liftedByUserId, string reason, CancellationToken ct = default)
    {
        var sanction = await _db.UserSanctions.FirstOrDefaultAsync(s => s.Id == sanctionId, ct);
        if (sanction is null) return null;

        if (sanction.LiftedAt is not null)
            throw new InvalidOperationException("This sanction has already been lifted.");

        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("Reason is required.");
        if (reason.Length > 500)
            throw new ArgumentException("Reason must be 500 characters or fewer.");

        sanction.LiftedAt = DateTimeOffset.UtcNow;
        sanction.LiftedByUserId = liftedByUserId;
        sanction.LiftReason = reason.Trim();
        await _db.SaveChangesAsync(ct);
        return sanction;
    }

    /// <summary>The moderation sanction list, newest first, optionally filtered to one user and/or active-only.</summary>
    public async Task<List<UserSanction>> ListAsync(
        bool activeOnly, Guid? userId, int take, int skip, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var query = _db.UserSanctions.AsQueryable();
        if (userId is { } uid) query = query.Where(s => s.UserId == uid);
        if (activeOnly) query = query.Where(s => s.LiftedAt == null && (s.ExpiresAt == null || s.ExpiresAt > now));

        return await query
            .OrderByDescending(s => s.IssuedAt)
            .Skip(skip).Take(take)
            .ToListAsync(ct);
    }

    /// <summary>Every sanction ever issued against one user, newest first - the dossier and the "my-sanctions" source.</summary>
    public async Task<List<UserSanction>> ForUserAsync(Guid userId, CancellationToken ct = default) =>
        await _db.UserSanctions
            .Where(s => s.UserId == userId)
            .OrderByDescending(s => s.IssuedAt)
            .ToListAsync(ct);

    /// <summary>
    /// Whether <paramref name="userId"/> currently holds the Admin role, read straight off Identity's
    /// own UserRoles/Roles tables rather than through <c>UserManager</c> - this is a scoped service with
    /// its own <see cref="KiriChessDbContext"/> already open, and a second round trip through
    /// UserManager would just be the same query wrapped.
    /// </summary>
    public Task<bool> IsAdminAsync(Guid userId, CancellationToken ct = default) =>
        _db.UserRoles
            .Join(_db.Roles, ur => ur.RoleId, r => r.Id, (ur, r) => new { ur.UserId, r.Name })
            .AnyAsync(x => x.UserId == userId && x.Name == ModerationRoles.Admin, ct);
}
