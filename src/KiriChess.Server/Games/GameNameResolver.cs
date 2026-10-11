using KiriChess.Server.Account;
using KiriChess.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KiriChess.Server.Games;

/// <summary>
/// Resolves both players' display names and patron tiers for a session. Lifted out of what used to be
/// <c>GameHub.ResolveNamesAsync</c> (Phase 8.1) so <c>GameDistribution</c> does not run a second copy
/// of the same query to build the Redis live-state publication (Phase 8.4, see
/// Phase 8.4 Step 2). Falls back to "Unknown"/"None" rather than throwing:
/// a spectator page or a live-list row with one blank name is a far better outcome than a hub call or a
/// publish failing because a user row went missing.
/// </summary>
public static class GameNameResolver
{
    public static async Task<(string White, string Black, string WhitePatronTier, string BlackPatronTier)> ResolveNamesAsync(
        IServiceScopeFactory scopeFactory, Guid whitePlayerId, Guid blackPlayerId, CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
        var ids = new[] { whitePlayerId, blackPlayerId };
        // Off IGameClock rather than DateTimeOffset.UtcNow so a test can drive badge expiry with
        // FakeGameClock, the same as every other clock-sensitive path. This is an instant compared
        // against a stored instant, not an interval, so the wall clock is the correct reading here
        // (see .claude/rules/realtime.md); the injection is about testability, not about stepping.
        var now = scope.ServiceProvider.GetRequiredService<IGameClock>().UtcNow;
        var rows = await db.Users
            .Where(u => ids.Contains(u.Id))
            .Select(u => new { u.Id, u.DisplayName, u.IsPatron, u.PatronTier, u.PatronKind, u.PatronExpiresAt })
            .ToDictionaryAsync(u => u.Id, ct);

        // Same rule as PatronBadge.For, duplicated here because rows is an anonymous projection
        // rather than an ApplicationUser; see Account/PatronBadge.cs for the definition, including
        // why a Monthly badge ignores the expiry column.
        string TierFor(Guid id) =>
            rows.TryGetValue(id, out var r) && r.IsPatron
            && (r.PatronKind == PatronKind.Monthly || r.PatronExpiresAt is null || r.PatronExpiresAt > now)
                ? r.PatronTier.ToString()
                : nameof(PatronTier.None);

        return (rows.TryGetValue(whitePlayerId, out var w) ? w.DisplayName : "Unknown",
                rows.TryGetValue(blackPlayerId, out var b) ? b.DisplayName : "Unknown",
                TierFor(whitePlayerId), TierFor(blackPlayerId));
    }
}
