using Microsoft.Extensions.Options;

namespace KiriChess.Server.Retention;

/// <summary>
/// Phase 8a.3. Runs RetentionSweep on a timer. Registered unconditionally and reads
/// RetentionOptions.Enabled at tick time rather than at registration, so a WebApplicationFactory test
/// can turn it off through configuration - the opposite of the fail-closed registration used for
/// Sentry, email and Google sign-in, which gate on whether a third party EXISTS. Nothing external is
/// involved here; the switch is purely operational.
///
/// The first sweep waits InitialDelaySeconds rather than firing at boot, so it is not competing with
/// migrations, GameRecoveryService and the leaderboard warmup for the same connection pool. It also
/// means most tests finish before a sweep ever runs.
/// </summary>
public sealed class RetentionSweeper : BackgroundService
{
    private readonly IOptionsMonitor<RetentionOptions> _options;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RetentionSweeper> _logger;

    public RetentionSweeper(
        IOptionsMonitor<RetentionOptions> options, IServiceScopeFactory scopeFactory, ILogger<RetentionSweeper> logger)
    {
        _options = options;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.CurrentValue.Enabled)
        {
            _logger.LogInformation("Retention sweeps are disabled (Retention:Enabled = false).");
            return;
        }

        var initialDelaySeconds = Math.Max(0, _options.CurrentValue.InitialDelaySeconds);
        await Task.Delay(TimeSpan.FromSeconds(initialDelaySeconds), stoppingToken);

        var intervalHours = Math.Max(1, _options.CurrentValue.SweepIntervalHours);
        using var timer = new PeriodicTimer(TimeSpan.FromHours(intervalHours));

        // Sweep once before the first WaitForNextTickAsync, not after: waiting for the first tick
        // means the first sweep is a full interval after boot, and a process that restarts daily
        // (or more often, during early deploys) would then never sweep at all.
        await RunOnceAsync(stoppingToken);

        while (await timer.WaitForNextTickAsync(stoppingToken))
            await RunOnceAsync(stoppingToken);
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var sweep = scope.ServiceProvider.GetRequiredService<RetentionSweep>();
            // The only place UtcNow appears in this task - RetentionSweep.RunAsync itself takes `now`
            // as a parameter so RetentionSweepTests never has to sleep.
            await sweep.RunAsync(DateTimeOffset.UtcNow, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // shutdown, not a failed sweep - let it propagate out of the loop
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Retention sweep failed; will try again next tick.");
        }
    }
}
