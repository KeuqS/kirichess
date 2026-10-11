using System.Collections.Concurrent;
using KiriChess.Server.Infrastructure.Redis;
using Microsoft.Extensions.DependencyInjection;

namespace KiriChess.Server.Games.Distributed;

/// <summary>
/// Everything <see cref="GameRegistry"/> has to tell the rest of the cluster about a game's lifecycle
/// (Phase 8.4). One type rather than four injected services, so the registry, which is otherwise pure
/// bookkeeping, gains a single seam instead of a distributed-systems dependency list. Every method is
/// a no-op when Redis is disabled.
///
/// Also caches each game's resolved display names in-process. The README's Step 2 explicitly forbids
/// <see cref="LiveGameIndex.PublishAsync"/> from doing its own DB round-trip because it runs on the
/// move path; <see cref="OnGameChangedAsync"/> runs on that same move path (see
/// <c>GameActionExecutor.SubmitMoveLocalAsync</c>), so it cannot re-resolve names on every move either.
/// Names are resolved once, at creation, or lazily on first use by a game this instance did not create
/// (a takeover or a boot-time recovery), and are stable enough for a live-list row that a mid-game
/// display-name change going unnoticed until the next game is an acceptable trade for never hitting
/// Postgres on a move.
/// </summary>
public sealed class GameDistribution
{
    private readonly IRedisConnection _connection;
    private readonly GameOwnership _ownership;
    private readonly LiveGameIndex _liveIndex;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<GameDistribution> _logger;
    private readonly ConcurrentDictionary<Guid, (string White, string Black, string WhitePatronTier, string BlackPatronTier)> _names = new();
    // I-1-3: SeatPlayersAsync is once per process per game, not once per move. Recovery and a
    // lease reclaim are start-like and must seat even though CreateAsync already ran.
    private readonly ConcurrentDictionary<Guid, byte> _seated = new();

    public GameDistribution(
        IRedisConnection connection, GameOwnership ownership, LiveGameIndex liveIndex,
        IServiceScopeFactory scopeFactory, ILogger<GameDistribution> logger)
    {
        _connection = connection;
        _ownership = ownership;
        _liveIndex = liveIndex;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>Claims the (guaranteed-free) lease for a brand-new game and publishes its first live-state entry.</summary>
    public async Task OnGameCreatedAsync(GameSession session, CancellationToken ct = default)
    {
        if (!_connection.Enabled) return;
        try
        {
            // A brand-new Guid can never already be owned, so this claim is guaranteed to succeed
            // barring a Redis failure. If it somehow fails, the game is still fully playable on this
            // instance (GameRegistry already holds it), refusing to start it over a lease write would
            // be worse than serving it single-instance until the next successful renewal.
            if (!await _ownership.TryClaimAsync(session.Id, ct))
            {
                _logger.LogError("Failed to claim the ownership lease for newly created game {GameId}.", session.Id);
                return;
            }

            var names = await ResolveAndCacheNamesAsync(session, ct);
            // State key first, then the per-player set: PlayingGamesAsync SREMs ids whose state is
            // missing, so seating before publish would drop a game that is still being created.
            await _liveIndex.PublishAsync(session, names.White, names.Black, names.WhitePatronTier, names.BlackPatronTier, ct);
            await _liveIndex.SeatPlayersAsync(session, ct);
            _seated[session.Id] = 1;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish distributed state for newly created game {GameId}.", session.Id);
        }
    }

    /// <summary>Refreshes the published live-state entry. Called after every move, and by the ownership heartbeat.</summary>
    public async Task OnGameChangedAsync(GameSession session, CancellationToken ct = default)
    {
        if (!_connection.Enabled) return;
        try
        {
            var names = _names.TryGetValue(session.Id, out var cached)
                ? cached
                : await ResolveAndCacheNamesAsync(session, ct); // cache miss: a game this instance took over or recovered, never created
            await _liveIndex.PublishAsync(session, names.White, names.Black, names.WhitePatronTier, names.BlackPatronTier, ct);
            // Recovery and a flushed-Redis reclaim never went through OnGameCreatedAsync on this
            // process (or Redis lost the set). Seat once, then every later move only publishes.
            if (_seated.TryAdd(session.Id, 1))
                await _liveIndex.SeatPlayersAsync(session, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh live state for game {GameId}.", session.Id);
        }
    }

    /// <summary>
    /// Releases the ownership lease and removes every published index entry, in that order deliberately.
    /// Called by <see cref="GameRegistry.PersistIfOverAsync"/> after the Postgres write succeeds: Postgres
    /// is the durable record, so a crash between "row saved" and "lease released" leaves a recoverable
    /// finished-in-Postgres-but-still-leased row (harmless, the lease just expires), whereas releasing
    /// first would let another instance take a game over that is not actually safe to touch anymore.
    /// </summary>
    public async Task OnGameEndedAsync(GameSession session, CancellationToken ct = default)
    {
        _names.TryRemove(session.Id, out _);
        _seated.TryRemove(session.Id, out _);
        if (!_connection.Enabled) return;
        try
        {
            await _ownership.ReleaseAsync(session.Id, ct);
            await _liveIndex.RemoveAsync(session, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clean up distributed state for finished game {GameId}; it will expire on its own TTL.", session.Id);
        }
    }

    private async Task<(string White, string Black, string WhitePatronTier, string BlackPatronTier)> ResolveAndCacheNamesAsync(GameSession session, CancellationToken ct)
    {
        var names = await GameNameResolver.ResolveNamesAsync(_scopeFactory, session.WhitePlayerId, session.BlackPlayerId, ct);
        _names[session.Id] = names;
        return names;
    }

    /// <summary>
    /// A lease reclaim after a Redis flush must re-SADD even though this process already seated the
    /// game once. Forgetting the in-process marker lets the next <see cref="OnGameChangedAsync"/>
    /// seat again.
    /// </summary>
    public void ForgetSeat(Guid gameId) => _seated.TryRemove(gameId, out _);
}
