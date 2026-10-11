using KiriChess.Server.Infrastructure.Redis;
using StackExchange.Redis;

namespace KiriChess.Server.Games.Distributed;

/// <summary>
/// The safety net <see cref="GameOwnershipHeartbeat"/> and <see cref="GameActionExecutor"/>'s
/// on-demand takeover cannot provide (Phase 8.4): those only fire when somebody touches a game. If the
/// owning instance dies while both players are idle, nothing does, no flag-fall monitor sweeps a game
/// nobody local holds, so the clock visibly runs to zero on both clients and the game never ends. This
/// hosted service walks <c>{p}games:live</c>, oldest first, and takes over any game whose owner key has
/// actually expired. Registered only when Redis is enabled (see <c>Program.cs</c>), nothing to sweep,
/// and no live index to read, in single-instance mode.
/// </summary>
public sealed class OrphanedGameSweeper : BackgroundService
{
    // Bounds the work done per tick rather than sweeping the whole live index at once, a huge
    // backlog after a multi-instance outage must not turn one sweep into a multi-second stall.
    private const int MaxPerTick = 50;

    private readonly IRedisConnection _connection;
    private readonly GameRegistry _registry;
    private readonly GameOwnership _ownership;
    private readonly GameActionExecutor _actions;
    private readonly RedisOptions _options;
    private readonly ILogger<OrphanedGameSweeper> _logger;

    public OrphanedGameSweeper(
        IRedisConnection connection, GameRegistry registry, GameOwnership ownership,
        GameActionExecutor actions, RedisOptions options, ILogger<OrphanedGameSweeper> logger)
    {
        _connection = connection;
        _registry = registry;
        _ownership = ownership;
        _actions = actions;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Same cadence as the instance heartbeat: there is no reason for this sweep to run any more
        // or less often than the interval that decides whether an owner even looks alive.
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.HeartbeatSeconds));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Orphaned-game sweep failed; will try again next tick.");
            }
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        if (!_connection.Enabled) return;

        var ids = await _connection.Db.SortedSetRangeByScoreAsync(
            _connection.Key("games:live"), order: Order.Ascending, take: MaxPerTick).WaitAsync(ct);

        foreach (var idValue in ids)
        {
            if (!Guid.TryParse((string?)idValue, out var gameId)) continue;
            if (_registry.Get(gameId) is not null) continue; // this instance already holds it, nothing to do

            var owner = await _ownership.GetOwnerAsync(gameId, ct);
            if (owner is not null) continue; // still leased somewhere; that instance's own heartbeat covers it

            if (await _actions.TryTakeoverAsync(gameId, ct: ct))
                _logger.LogInformation("Orphaned-game sweep took over game {GameId}: its owner's lease had expired with nobody touching it.", gameId);
        }
    }
}
