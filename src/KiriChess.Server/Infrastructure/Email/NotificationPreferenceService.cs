using KiriChess.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace KiriChess.Server.Infrastructure.Email;

/// <summary>
/// Reads and writes the per-category mail preference on <c>ApplicationUser</c> (Phase 11b.1). Scoped,
/// like <c>ChallengeService</c>: it takes a <c>KiriChessDbContext</c> directly and every caller
/// (a request handler, or another scoped service already inside one) already has a fresh scope.
/// </summary>
public sealed class NotificationPreferenceService
{
    private readonly KiriChessDbContext _db;

    public NotificationPreferenceService(KiriChessDbContext db) => _db = db;

    /// <summary>
    /// True (the default) unless the user has explicitly unsubscribed from <paramref name="category"/>,
    /// or the account no longer exists. Every sender in this codebase (<c>PairingNotificationService</c>,
    /// <c>RematchNotificationService</c>) checks this before building the mail, not after.
    ///
    /// The switch has to pick the projection expression before the query runs, not inside <c>Select</c>:
    /// EF Core's query translator cannot turn a call to an arbitrary C# helper method into SQL, only a
    /// property access it can see directly in the expression tree.
    /// </summary>
    public async Task<bool> IsSubscribedAsync(Guid userId, NotificationCategory category, CancellationToken ct = default)
    {
        var users = _db.Users.Where(u => u.Id == userId);
        DateTimeOffset? unsubscribedAt = category switch
        {
            NotificationCategory.PairingFound =>
                await users.Select(u => u.PairingFoundEmailUnsubscribedAt).FirstOrDefaultAsync(ct),
            NotificationCategory.RematchOffered =>
                await users.Select(u => u.RematchOfferedEmailUnsubscribedAt).FirstOrDefaultAsync(ct),
            _ => throw new ArgumentOutOfRangeException(nameof(category), category, "Unhandled NotificationCategory."),
        };
        return unsubscribedAt is null;
    }

    /// <summary>All categories and their current subscription state, for the profile page's toggle list.</summary>
    public async Task<IReadOnlyDictionary<NotificationCategory, bool>> GetAllAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return new Dictionary<NotificationCategory, bool>();

        return Enum.GetValues<NotificationCategory>()
            .ToDictionary(c => c, c => ColumnFor(c, user) is null);
    }

    /// <summary>
    /// Sets whether <paramref name="userId"/> receives <paramref name="category"/> mail. Idempotent:
    /// re-subscribing clears the timestamp back to null (the default state) rather than leaving a
    /// "was once unsubscribed" mark, unsubscribing twice just overwrites the same timestamp.
    /// </summary>
    public async Task SetSubscribedAsync(
        Guid userId, NotificationCategory category, bool subscribed, CancellationToken ct = default)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return;

        var value = subscribed ? null : (DateTimeOffset?)DateTimeOffset.UtcNow;
        switch (category)
        {
            case NotificationCategory.PairingFound:
                user.PairingFoundEmailUnsubscribedAt = value;
                break;
            case NotificationCategory.RematchOffered:
                user.RematchOfferedEmailUnsubscribedAt = value;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(category), category, "Unhandled NotificationCategory.");
        }
        await _db.SaveChangesAsync(ct);
    }

    private static DateTimeOffset? ColumnFor(NotificationCategory category, Data.Entities.ApplicationUser u) => category switch
    {
        NotificationCategory.PairingFound => u.PairingFoundEmailUnsubscribedAt,
        NotificationCategory.RematchOffered => u.RematchOfferedEmailUnsubscribedAt,
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, "Unhandled NotificationCategory."),
    };
}
