using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using KiriChess.Server.Games;
using KiriChess.Server.Infrastructure;
using KiriChess.Server.Infrastructure.OpenApi;
using KiriChess.Server.Ratings;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KiriChess.Server.Tests;

/// <summary>
/// Ties 9c.5's Better Stack configuration to the instruments it actually reads.
///
/// <c>infra/betterstack/main.tf</c> hard-codes four Prometheus wire names in its <c>locals</c> block,
/// and every chart and threshold alert in the account is written against them. Those names are not
/// the names in <see cref="KiriChessMetrics"/>: the OpenTelemetry Prometheus exporter mangles them
/// (dots to underscores, <c>{annotation}</c> units dropped, real units appended as a suffix,
/// <c>_total</c> appended to counters), so they were originally derived by reasoning about the
/// exporter's rules and shipped marked "likely" and "unverified" - one alert
/// (<c>flagfall_sweep_slow</c>) shipped <c>paused</c> for exactly that reason.
///
/// A wrong name there does not fail anything. It produces a chart that reads "no data" and an alert
/// that can never fire, which is indistinguishable from a healthy system and is the specific way an
/// alerting layer rots into decoration. This test scrapes the real exporter and asserts every name
/// the Terraform depends on is really emitted, so renaming an instrument in C# fails the build here
/// rather than silently blinding the monitoring.
///
/// Counters and histograms are recorded to first, deliberately: an instrument with no recorded value
/// is not emitted at all (see <see cref="MetricsEndpointTests"/>), so scraping a freshly booted app
/// would prove nothing about the two names that are not gauges.
/// </summary>
public sealed class MetricsContractTests
{
    private const string ScrapeToken = "metrics-contract-test-token";

    /// <summary>Matches the `metric_* = "..."` lines in the Terraform locals block.</summary>
    private static readonly Regex TerraformMetricNameRegex =
        new(@"^\s*metric_\w+\s*=\s*""([^""]+)""", RegexOptions.Compiled | RegexOptions.Multiline);

    private static WebApplicationFactory<Program> BuildFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = "Host=127.0.0.1;Port=1;Database=nope;Username=nope;Password=nope;Timeout=1;Command Timeout=1",
                ["Jwt:Key"] = "metrics-contract-test-signing-key-at-least-32-bytes",
                ["RunMigrationsOnStartup"] = "false",
                ["Cors:AllowedOrigins:0"] = "https://example.test",
                ["Metrics:ScrapeToken"] = ScrapeToken
            }));
        });

    /// <summary>
    /// Puts every instrument under test into a state where the exporter will actually emit it.
    /// Five of the seven need this, for two different reasons, and both reasons are worth knowing
    /// before reading a Better Stack chart:
    ///
    /// <list type="bullet">
    /// <item>A <b>counter or histogram</b> with no recorded value is not emitted at all. That covers
    /// <c>kirichess.hub.errors</c>, <c>kirichess.flagfall.sweep.duration</c>, and I-5-1's two
    /// persister instruments, <c>kirichess.persistence.snapshot_failures</c> and
    /// <c>kirichess.persistence.snapshot_batch.duration</c>.</item>
    /// <item><b>kirichess.matchmaking.queue.depth</b> is an observable gauge that emits one
    /// measurement <i>per pool that currently has someone waiting</i>
    /// (<c>GameMetricsObserver.ObserveQueueDepth</c> projects <c>MatchQueue.Depths()</c>), so with an
    /// empty queue, the normal state, the series does not exist on the wire. That is not a bug and it
    /// does not break the <c>queue_stuck</c> alert (an absent series cannot exceed a threshold), but
    /// it does mean the chart reads "no data" rather than "0" whenever nobody is queuing, and it is
    /// why this test has to enqueue somebody to check the name at all.</item>
    /// </list>
    ///
    /// <c>kirichess.games.live</c> and <c>kirichess.persistence.pending_snapshots</c> are both
    /// single-value observable gauges with no per-tag fan-out, so they emit a real <c>0</c> on a
    /// freshly booted app and need no exercise here.
    ///
    /// <c>TryPair</c> with an empty queue leaves the entrant waiting and returns null, which is
    /// exactly the state wanted here, and it needs no database.
    /// </summary>
    private static void Exercise(WebApplicationFactory<Program> factory)
    {
        var metrics = factory.Services.GetRequiredService<KiriChessMetrics>();
        metrics.HubError("ContractTest");
        metrics.FlagFallSweepCompleted(12.5);
        metrics.SnapshotFailed();
        metrics.SnapshotBatchWritten(1, 42.0);
        // C-8-3: not Better-Stack-declared today (the assertion below is unchanged), but exercised
        // here so the day it is added to infra/betterstack/main.tf this test already proves it is
        // exportable, the same discipline every other counter/histogram in this list follows.
        metrics.ReplayTruncated("replay");

        var queue = factory.Services.GetRequiredService<MatchmakingService>().Queue;
        var pool = RatingConstants.OfficialPools[0];
        Assert.Null(queue.TryPair(pool, new Waiting(Guid.NewGuid(), "metrics-contract-test", 1500)));
    }

    private static async Task<string> ScrapeAsync(WebApplicationFactory<Program> factory)
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ScrapeToken);
        return await client.GetStringAsync("/metrics");
    }

    [Fact]
    public async Task Every_metric_name_better_stack_alerts_on_is_really_exported()
    {
        var terraform = await File.ReadAllTextAsync(
            Path.Combine(OpenApiExport.FindRepoRoot(), "infra", "betterstack", "main.tf"));
        var declaredNames = TerraformMetricNameRegex.Matches(terraform)
            .Select(m => m.Groups[1].Value)
            .ToList();

        // Four today. A lower bound rather than an equality so adding a fifth metric to the dashboard
        // is covered automatically, but deleting the locals block (or reformatting it out of this
        // regex's reach) fails instead of vacuously passing.
        Assert.True(declaredNames.Count >= 4,
            $"Expected at least 4 metric_* names in infra/betterstack/main.tf, found {declaredNames.Count}. "
            + "The locals block moved or was reformatted; fix this regex, do not delete the assertion.");

        using var factory = BuildFactory();

        // The two non-gauges must record before they exist on the wire. Values are arbitrary; only
        // the emitted names are under test here.
        Exercise(factory);

        var body = await ScrapeAsync(factory);

        var missing = declaredNames.Where(name => !body.Contains(name, StringComparison.Ordinal)).ToList();
        Assert.True(missing.Count == 0,
            $"infra/betterstack/main.tf alerts on {missing.Count} metric name(s) the exporter does not emit: "
            + string.Join(", ", missing)
            + ". Better Stack would show these as \"no data\" forever, and their alerts could never fire. "
            + "Either the instrument was renamed in KiriChessMetrics/GameMetricsObserver, or the exporter's "
            + "name mangling differs from what the Terraform assumed.");
    }

    /// <summary>
    /// The exporter's suffix rules are the part that was guessed rather than known, so they are
    /// asserted explicitly: a future exporter upgrade that changes them would otherwise only surface
    /// as the test above failing with no indication of why.
    /// </summary>
    [Fact]
    public async Task Counter_and_histogram_suffixes_are_what_the_terraform_assumes()
    {
        using var factory = BuildFactory();
        Exercise(factory);

        var body = await ScrapeAsync(factory);

        // Counter: dots to underscores, "{error}" annotation unit dropped, "_total" appended.
        Assert.Contains("kirichess_hub_errors_total", body);
        // Histogram with a real "ms" unit: exported as "_milliseconds" plus the usual bucket/sum/count
        // series. This is the one the flagfall_sweep_slow alert was paused over.
        Assert.Contains("kirichess_flagfall_sweep_duration_milliseconds", body);
        // Observable gauges, both with annotation units that must not appear in the wire name.
        Assert.Contains("kirichess_games_live", body);
        Assert.Contains("kirichess_matchmaking_queue_depth", body);
    }
}
