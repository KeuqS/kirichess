using System.Diagnostics;
using KiriChess.LoadTest;

LoadOptions options;
try
{
    options = LoadOptions.Parse(args);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Bad arguments: {ex.Message}");
    return 2;
}

Console.WriteLine($"KiriChess load harness - label={options.Label} games={options.Games} pool={options.Pool} " +
                   $"think={options.ThinkMs}±{options.JitterMs}ms warmup={options.WarmupSeconds}s window={options.WindowSeconds}s " +
                   $"presence={options.Presence} seed={options.Seed} pinnedCpus={options.PinnedCpus}");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
var ct = cts.Token;

var handler = new SocketsHttpHandler
{
    MaxConnectionsPerServer = 100_000,
    PooledConnectionLifetime = TimeSpan.FromMinutes(10),
};
using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(180) };

var startedAt = DateTimeOffset.UtcNow;
var coordinator = new GameCoordinator();
BotPlayer[] bots = [];

try
{
    // Step 1: never Task.Delay past a condition you can observe.
    Console.WriteLine("Waiting for /health/ready...");
    await WaitForReadyAsync(http, options.Api, options.ReadyTimeoutSeconds, ct);
    Console.WriteLine("Backend is ready.");

    // Step 2: register + connect 2*games clients, ramped in batches.
    var total = options.Games * 2;
    bots = Enumerable.Range(options.BotOffset, total).Select(i => new BotPlayer(i, http, options, coordinator)).ToArray();
    Console.WriteLine($"Registering and connecting {total} clients in batches of {options.RampBatchSize}...");
    var rampedSoFar = 0;
    foreach (var batch in Chunk(bots, options.RampBatchSize))
    {
        await Task.WhenAll(batch.Select(async b =>
        {
            await b.RegisterAsync(ct);
            await b.ConnectAsync(ct);
        }));
        rampedSoFar += batch.Length;
        Console.WriteLine($"  ramped {rampedSoFar}/{total}");
    }

    // Step 3: all clients JoinQueue, then wait for kirichess.games.live to converge.
    Console.WriteLine("All clients joining queue...");
    await Task.WhenAll(bots.Select(b => b.JoinQueueAsync(ct)));

    Console.WriteLine("Waiting for matchmaking to converge...");
    var pairSw = Stopwatch.StartNew();
    var target = 0.95 * options.Games;
    double lastLive = 0;
    var converged = false;
    while (pairSw.Elapsed < TimeSpan.FromSeconds(options.PairTimeoutSeconds))
    {
        var snap = await MetricsScraper.ScrapeAsync(http, options.Api, options.ScrapeToken, ct);
        lastLive = MetricsScraper.GaugeValue(snap, "kirichess_games_live");
        Console.WriteLine($"  live={lastLive:F0} target={options.Games} ({lastLive / options.Games:P0})");
        if (lastLive >= target) { converged = true; break; }
        await Task.Delay(TimeSpan.FromSeconds(2), ct);
    }
    if (!converged)
        Console.WriteLine($"WARNING: pairing did not converge within {options.PairTimeoutSeconds}s; " +
                           $"observed {lastLive:F0}/{options.Games} live games. Proceeding anyway - this is itself a finding.");

    // Step 4: warm-up, measuring nothing.
    Console.WriteLine($"Warm-up for {options.WarmupSeconds}s...");
    await Task.Delay(TimeSpan.FromSeconds(options.WarmupSeconds), ct);

    // Step 5: the measurement window.
    Console.WriteLine($"Opening measurement window for {options.WindowSeconds}s...");
    var windowOpenedAt = DateTimeOffset.UtcNow;
    var snapStart = await MetricsScraper.ScrapeAsync(http, options.Api, options.ScrapeToken, ct);
    coordinator.ResetLatencySamples();
    var coordAtWindowStart = coordinator.Snapshot();

    var gaugeTimes = new List<double>();
    var gaugeLive = new List<double>();
    var gaugePending = new List<double>();
    var gaugeQueueDepth = new List<double>();
    var gaugeOnline = new List<double>();
    var containerSamples = new Dictionary<string, List<ContainerSample>>();
    var harnessCpu = new HarnessCpuSampler();
    harnessCpu.Start();

    var windowSw = Stopwatch.StartNew();
    while (windowSw.Elapsed < TimeSpan.FromSeconds(options.WindowSeconds))
    {
        var tickStart = DateTimeOffset.UtcNow;
        try
        {
            var snap = await MetricsScraper.ScrapeAsync(http, options.Api, options.ScrapeToken, ct);
            gaugeTimes.Add(windowSw.Elapsed.TotalSeconds);
            gaugeLive.Add(MetricsScraper.GaugeValue(snap, "kirichess_games_live"));
            gaugePending.Add(MetricsScraper.GaugeValue(snap, "kirichess_persistence_pending_snapshots"));
            gaugeQueueDepth.Add(MetricsScraper.GaugeValue(snap, "kirichess_matchmaking_queue_depth"));
            gaugeOnline.Add(MetricsScraper.GaugeValue(snap, "kirichess_presence_online_users"));
        }
        catch (Exception ex) { Console.Error.WriteLine($"  metrics sample failed: {ex.Message}"); }

        try
        {
            var samples = await ContainerStats.SampleOnceAsync([options.BackendContainer, options.DbContainer], ct);
            foreach (var s in samples)
            {
                if (!containerSamples.TryGetValue(s.Name, out var list))
                    containerSamples[s.Name] = list = new List<ContainerSample>();
                list.Add(s);
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"  docker stats sample failed: {ex.Message}"); }

        harnessCpu.Sample();

        var tickElapsed = DateTimeOffset.UtcNow - tickStart;
        var remaining = TimeSpan.FromSeconds(options.SampleIntervalSeconds) - tickElapsed;
        if (remaining > TimeSpan.Zero) await Task.Delay(remaining, ct);
    }

    var snapEnd = await MetricsScraper.ScrapeAsync(http, options.Api, options.ScrapeToken, ct);
    var windowClosedAt = DateTimeOffset.UtcNow;
    Console.WriteLine("Window closed. Computing results...");

    // --- Server-side deltas and histogram window stats ---
    var movesApplied = MetricsScraper.CounterDelta(snapStart, snapEnd, "kirichess_moves_applied_total");
    var gamesStarted = MetricsScraper.CounterDelta(snapStart, snapEnd, "kirichess_games_started_total");
    var gamesEnded = MetricsScraper.CounterDelta(snapStart, snapEnd, "kirichess_games_ended_total");
    var hubErrors = MetricsScraper.CounterDelta(snapStart, snapEnd, "kirichess_hub_errors_total");
    var rlHub = MetricsScraper.CounterDelta(snapStart, snapEnd, "kirichess_ratelimit_rejected_total",
        l => l.TryGetValue("surface", out var s) && s == "hub");
    var rlHttp = MetricsScraper.CounterDelta(snapStart, snapEnd, "kirichess_ratelimit_rejected_total",
        l => l.TryGetValue("surface", out var s) && s == "http");
    var snapshotFailures = MetricsScraper.CounterDelta(snapStart, snapEnd, "kirichess_persistence_snapshot_failures_total");
    var flagfallDetected = MetricsScraper.CounterDelta(snapStart, snapEnd, "kirichess_flagfall_detected_total");

    var moveApplyHist = MetricsScraper.HistogramWindow(snapStart, snapEnd, "kirichess_move_apply_duration_milliseconds");
    var flagfallHist = MetricsScraper.HistogramWindow(snapStart, snapEnd, "kirichess_flagfall_sweep_duration_milliseconds");

    var gamesLiveStats = SeriesStats(gaugeLive);
    var pendingStats = SeriesStats(gaugePending);
    var queueDepthStats = SeriesStats(gaugeQueueDepth);
    var onlineStats = SeriesStats(gaugeOnline);

    // --- Client-side ---
    var coordSnap = coordinator.Snapshot();
    var latencies = coordSnap.MoveLatenciesMs.OrderBy(x => x).ToArray();
    double? ClientPercentile(double p)
    {
        if (latencies.Length == 0) return null;
        var idx = (int)Math.Ceiling(p * latencies.Length) - 1;
        idx = Math.Clamp(idx, 0, latencies.Length - 1);
        return latencies[idx];
    }

    // --- Container aggregates ---
    var containerStatsList = containerSamples.Select(kv =>
    {
        var cpus = kv.Value.Select(s => s.CpuPercent).ToArray();
        var mems = kv.Value.Select(s => s.MemUsedBytes).ToArray();
        return new ContainerSeriesStats(
            kv.Key,
            cpus.Length > 0 ? cpus.Average() : 0,
            cpus.Length > 0 ? cpus.Max() : 0,
            mems.Length > 0 ? mems.Max() : 0,
            kv.Value.Count);
    }).ToArray();

    var backendStats = containerStatsList.FirstOrDefault(c => c.Container == options.BackendContainer)
        ?? new ContainerSeriesStats(options.BackendContainer, 0, 0, 0, 0);

    // --- Generator honesty ---
    // Phase 9b.3 § 3 states "Planned throughput is N x 2 / think_ms
    // moves per second." That formula assumes both sides of a game can move independently and
    // simultaneously - it does not, because chess turns strictly alternate: only one side can be on
    // the clock at a time, so one game produces at most one ply per mean think interval, not two.
    // README § Decision 2 itself specifies exactly this alternating shape ("whoever the server
    // pairs, it pairs... drive that game" with think time "per side", i.e. sequential turns).
    // Empirically, an N=10 smoke rung with ~0% harness CPU (i.e. the generator was never the
    // bottleneck) sustained 3.3 moves/s, matching N*1000/think_ms (3.33) almost exactly and roughly
    // half of the task file's literal N*2*1000/think_ms (6.67) - using the literal formula would
    // void the generator-honesty criterion on every rung of the ladder regardless of server health.
    // The physically-achievable rate below is used instead; see loadtest/README.md and RESULTS.md
    // for the full note.
    var plannedMovesPerSecond = options.Games * 1000.0 / options.ThinkMs;
    var achievedMovesPerSecond = movesApplied / options.WindowSeconds;
    var achievedFraction = plannedMovesPerSecond > 0 ? achievedMovesPerSecond / plannedMovesPerSecond : 0;
    var harnessCpuMean = harnessCpu.Samples.Count > 0 ? harnessCpu.Samples.Average() : 0;
    var harnessCpuMax = harnessCpu.Samples.Count > 0 ? harnessCpu.Samples.Max() : 0;

    // --- Criteria (README/task § Decision 6, evaluated BEFORE the run per the contract) ---
    var backendCpuMeanPctOfBudget = backendStats.CpuMeanPercent / options.PinnedCpus;
    var backendCpuMaxPctOfBudget = backendStats.CpuMaxPercent / options.PinnedCpus;

    var criteria = new List<CriterionResult>
    {
        Criteria.FlagFallSweep(flagfallHist),
        Criteria.MoveLatency(ClientPercentile(0.95), ClientPercentile(0.99)),
        Criteria.SnapshotBacklog(pendingStats, options.Games),
        Criteria.Correctness(hubErrors, rlHub, snapshotFailures),
        Criteria.Headroom(backendCpuMeanPctOfBudget, backendCpuMaxPctOfBudget),
        Criteria.GeneratorHonesty(achievedFraction, harnessCpuMax),
    };
    var overall = Criteria.Overall(criteria);

    var result = new RunResult(
        Label: options.Label,
        TargetGames: options.Games,
        Pool: options.Pool,
        ThinkMs: options.ThinkMs,
        JitterMs: options.JitterMs,
        WarmupSeconds: options.WarmupSeconds,
        WindowSeconds: options.WindowSeconds,
        Presence: options.Presence,
        Seed: options.Seed,
        StartedAtUtc: startedAt,
        WindowOpenedAtUtc: windowOpenedAt,
        WindowClosedAtUtc: windowClosedAt,
        PairingConverged: converged,
        ObservedLiveGamesAtQueueGate: lastLive,
        MovesAppliedDelta: movesApplied,
        GamesStartedDelta: gamesStarted,
        GamesEndedDelta: gamesEnded,
        HubErrorsDelta: hubErrors,
        RateLimitRejectedHubDelta: rlHub,
        RateLimitRejectedHttpDelta: rlHttp,
        SnapshotFailuresDelta: snapshotFailures,
        FlagFallDetectedDelta: flagfallDetected,
        MoveApplyDuration: new HistogramReport(moveApplyHist.Count, moveApplyHist.Mean, moveApplyHist.P50, moveApplyHist.P95, moveApplyHist.P99),
        FlagFallSweepDuration: new HistogramReport(flagfallHist.Count, flagfallHist.Mean, flagfallHist.P50, flagfallHist.P95, flagfallHist.P99),
        GamesLive: gamesLiveStats,
        PendingSnapshots: pendingStats,
        QueueDepth: queueDepthStats,
        OnlineUsers: onlineStats,
        ClientMovesSubmitted: coordSnap.MovesSubmitted - coordAtWindowStart.MovesSubmitted,
        ClientMovesReceived: coordSnap.MovesReceived - coordAtWindowStart.MovesReceived,
        ClientLatencyMeanMs: latencies.Length > 0 ? latencies.Average() : null,
        ClientLatencyP50Ms: ClientPercentile(0.50),
        ClientLatencyP95Ms: ClientPercentile(0.95),
        ClientLatencyP99Ms: ClientPercentile(0.99),
        PingsAnswered: coordSnap.PingsAnswered,
        ReconnectsOrCloses: coordSnap.ReconnectsOrCloses,
        ClientErrorsByCategory: coordSnap.ErrorsByCategory,
        Containers: containerStatsList,
        PlannedMovesPerSecond: plannedMovesPerSecond,
        AchievedMovesPerSecond: achievedMovesPerSecond,
        AchievedFractionOfPlanned: achievedFraction,
        HarnessCpuMeanPercentOfHost: harnessCpuMean,
        HarnessCpuMaxPercentOfHost: harnessCpuMax,
        LoadCpus: options.PinnedCpus.ToString("0.0"),
        LoadMemory: options.LoadMemory,
        Criteria: criteria,
        Overall: overall);

    result.PrintSummary();
    result.WriteJson(options.Out);
    Console.WriteLine($"Wrote {options.Out}");

    Console.WriteLine("Shutting down clients...");
    await ShutdownAllAsync(bots);

    return overall is Verdict.Pass or Verdict.Warn ? 0 : 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FATAL: {ex}");
    try { await ShutdownAllAsync(bots); } catch { /* best effort */ }
    return 2;
}

static async Task WaitForReadyAsync(HttpClient http, string api, int timeoutSeconds, CancellationToken ct)
{
    var sw = Stopwatch.StartNew();
    Exception? last = null;
    while (sw.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
    {
        try
        {
            using var resp = await http.GetAsync($"{api}/health/ready", ct);
            if (resp.IsSuccessStatusCode) return;
        }
        catch (Exception ex) { last = ex; }
        await Task.Delay(TimeSpan.FromSeconds(1), ct);
    }
    throw new TimeoutException($"{api}/health/ready did not report healthy within {timeoutSeconds}s.", last);
}

static async Task ShutdownAllAsync(BotPlayer[] bots)
{
    const int batchSize = 100;
    foreach (var batch in Chunk(bots, batchSize))
        await Task.WhenAll(batch.Select(b => b.ShutdownAsync()));
}

static GaugeSeriesStats SeriesStats(IReadOnlyList<double> values)
{
    if (values.Count == 0) return new GaugeSeriesStats(0, 0, 0, 0, 0, 0);
    var n = values.Count;
    var thirdSize = Math.Max(1, n / 3);
    var first = values.Take(thirdSize).Average();
    var last = values.Skip(Math.Max(0, n - thirdSize)).Average();
    return new GaugeSeriesStats(values.Min(), values.Average(), values.Max(), first, last, n);
}

static IEnumerable<T[]> Chunk<T>(IReadOnlyList<T> source, int size)
{
    for (var i = 0; i < source.Count; i += size)
        yield return source.Skip(i).Take(size).ToArray();
}
