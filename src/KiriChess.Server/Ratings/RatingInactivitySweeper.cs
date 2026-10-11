namespace KiriChess.Server.Ratings;

/// <summary>
/// Runs <see cref="RatingInactivitySweep"/> on a timer. Mirrors <c>RetentionSweeper</c> exactly,
/// including why the first run waits: it must not compete with migrations, GameRecoveryService and
/// the leaderboard warm-up for the same connection pool at boot, and the delay also means an
/// ordinary test host is disposed long before a sweep would fire.
///
/// The tick interval is not the rating period. <see cref="RatingConstants.RatingPeriod"/> is a week;
/// ticking six-hourly only decides how soon after a row crosses a period boundary the inflation is
/// applied, and the sweep itself is idempotent, so a shorter interval buys promptness and costs one
/// indexed query. Redis is irrelevant here: the sweep is driven entirely from Postgres, and the
/// leaderboard write-through it performs no-ops when Redis is disabled.
/// </summary>
public sealed class RatingInactivitySweeper : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RatingInactivitySweeper> _logger;

    public RatingInactivitySweeper(IServiceScopeFactory scopeFactory, ILogger<RatingInactivitySweeper> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(InitialDelay, stoppingToken);

            using var timer = new PeriodicTimer(Interval);
            await RunOnceAsync(stoppingToken);
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await RunOnceAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown, not a failure.
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var sweep = scope.ServiceProvider.GetRequiredService<RatingInactivitySweep>();
            // The only UtcNow in this task; RunAsync takes `now` so its tests never sleep.
            await sweep.RunAsync(DateTimeOffset.UtcNow, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // shutdown, not a failed sweep - let it propagate out of the loop
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Rating inactivity sweep failed; will try again next tick.");
        }
    }
}
