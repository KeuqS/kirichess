using System.Collections.Concurrent;
using KiriChess.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace KiriChess.Server.Infrastructure;

/// <summary>
/// Phase 10c.4: the "IOptionsMonitor over a small config table" the item asks for, reading
/// <see cref="Data.Entities.KillSwitchState"/> from Postgres and caching it for
/// <see cref="CacheDuration"/> so a check at the top of a hot path (register, JoinQueue,
/// JoinAsSpectator, GET /api/seeks) costs a dictionary lookup rather than a round trip on every call.
/// A write goes to the database <b>and</b> the cache in the same call, so the moderator who just
/// flipped a switch sees it take effect immediately on this instance; every other instance picks it
/// up within one cache window, which is the entire cost of not building a push-based mechanism for
/// four booleans.
///
/// Registered as a singleton with its own scope factory, the same shape as
/// <see cref="Games.LiveGamePersister"/>: the gated call sites must not each carry a scoped
/// <see cref="KiriChessDbContext"/> dependency just to ask a yes/no question that changes rarely.
///
/// This does not want a feature-flag vendor (the item's own text): that would be a sixth third
/// party and a new DPA for four booleans, when a table this app already owns says the same thing.
/// </summary>
public sealed class KillSwitchService
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(10);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<KillSwitchService> _logger;

    private readonly ConcurrentDictionary<string, bool> _cache = new();
    private DateTimeOffset _cacheFilledAt = DateTimeOffset.MinValue;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    public KillSwitchService(IServiceScopeFactory scopeFactory, ILogger<KillSwitchService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>
    /// True unless a moderator has explicitly disabled this switch. A row that has never been seen
    /// (migration not yet applied, or a read that has never succeeded) reads as enabled, the same as
    /// every one of these features behaved before this item existed - the switch can only take
    /// something away, never grant something the app did not already do.
    /// </summary>
    public async Task<bool> IsEnabledAsync(KillSwitch which, CancellationToken ct = default)
    {
        await EnsureFreshAsync(ct);
        return !_cache.TryGetValue(which.ToString(), out var enabled) || enabled;
    }

    /// <summary>All four switches, for the admin GET. Always reports all four even if a row is missing, defaulting the missing ones to enabled for the same reason <see cref="IsEnabledAsync"/> does.</summary>
    public async Task<IReadOnlyDictionary<string, bool>> GetAllAsync(CancellationToken ct = default)
    {
        await EnsureFreshAsync(ct, force: true);
        return Enum.GetValues<KillSwitch>().ToDictionary(
            k => k.ToString(),
            k => !_cache.TryGetValue(k.ToString(), out var enabled) || enabled);
    }

    public async Task SetEnabledAsync(KillSwitch which, bool enabled, Guid updatedByUserId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();

        var name = which.ToString();
        var row = await db.KillSwitches.FirstOrDefaultAsync(k => k.Name == name, ct);
        if (row is null)
        {
            row = new Data.Entities.KillSwitchState { Name = name };
            db.KillSwitches.Add(row);
        }
        row.Enabled = enabled;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        row.UpdatedByUserId = updatedByUserId;
        await db.SaveChangesAsync(ct);

        // Read-your-own-write on this instance, ahead of the cache window rather than waiting on it.
        _cache[name] = enabled;
        _logger.LogWarning("Kill switch {Switch} set to {Enabled} by {UserId}.", name, enabled, updatedByUserId);
    }

    private async Task EnsureFreshAsync(CancellationToken ct, bool force = false)
    {
        if (!force && DateTimeOffset.UtcNow - _cacheFilledAt < CacheDuration) return;

        await _refreshLock.WaitAsync(ct);
        try
        {
            // Re-check inside the lock: a concurrent caller may have just refreshed while this one
            // was waiting, and a forced admin GET must not still pay for two round trips back to back.
            if (!force && DateTimeOffset.UtcNow - _cacheFilledAt < CacheDuration) return;

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<KiriChessDbContext>();
            var rows = await db.KillSwitches.AsNoTracking().ToListAsync(ct);
            foreach (var row in rows) _cache[row.Name] = row.Enabled;
            _cacheFilledAt = DateTimeOffset.UtcNow;
        }
        catch (Exception ex)
        {
            // Fail open, deliberately: a database blip must not ALSO disable every gated feature on
            // top of whatever it is already causing. The stale (or, on a cold start, empty) cache is
            // kept, and an empty cache reads as "everything enabled" via the fallback in
            // IsEnabledAsync/GetAllAsync above - exactly today's pre-10c.4 behaviour.
            _logger.LogError(ex, "Failed to refresh kill switch state from the database; keeping the last known values.");
        }
        finally
        {
            _refreshLock.Release();
        }
    }
}
