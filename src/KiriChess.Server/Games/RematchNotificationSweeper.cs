namespace KiriChess.Server.Games;

/// <summary>
/// Runs <see cref="RematchNotificationSweep"/> on a timer. Mirrors <c>RatingInactivitySweeper</c>,
/// including why the first run waits: it must not compete with migrations, GameRecoveryService and
/// the leaderboard warm-up for the same connection pool at boot. The delay is the tick interval,
/// not the mail delay: <c>Rematch:NotifyDelaySeconds</c> is 90s and the offer window is an hour, so
/// a 15s tick is prompt enough after a deploy and cheap (one indexed-enough query on a tiny table).
///
/// Tests that need the sweep call <see cref="RematchNotificationService.FlushOwedAsync"/> directly
/// and do not wait for this timer.
/// </summary>
public sealed class RematchNotificationSweeper : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RematchNotificationSweeper> _logger;

    public RematchNotificationSweeper(IServiceScopeFactory scopeFactory, ILogger<RematchNotificationSweeper> logger)
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
            var sweep = scope.ServiceProvider.GetRequiredService<RematchNotificationSweep>();
            await sweep.RunAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // shutdown, not a failed sweep - let it propagate out of the loop
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Rematch notification sweep failed; will try again next tick.");
        }
    }
}
