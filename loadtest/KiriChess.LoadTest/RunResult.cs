using System.Text.Json;
using System.Text.Json.Serialization;

namespace KiriChess.LoadTest;

internal enum Verdict { Pass, Warn, Fail, Void }

internal sealed record CriterionResult(string Name, Verdict Verdict, string Detail);

internal sealed record GaugeSeriesStats(double Min, double Mean, double Max, double FirstThirdMean, double LastThirdMean, int Samples);

internal sealed record ContainerSeriesStats(string Container, double CpuMeanPercent, double CpuMaxPercent, double MemMaxBytes, int Samples);

internal sealed record HistogramReport(double Count, double? MeanMs, double? P50Ms, double? P95Ms, double? P99Ms);

internal sealed record RunResult(
    string Label,
    int TargetGames,
    string Pool,
    int ThinkMs,
    int JitterMs,
    int WarmupSeconds,
    int WindowSeconds,
    bool Presence,
    int Seed,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset WindowOpenedAtUtc,
    DateTimeOffset WindowClosedAtUtc,

    // Pairing / ramp
    bool PairingConverged,
    double ObservedLiveGamesAtQueueGate,

    // Server-side (from /metrics deltas and gauge polling across the window)
    double MovesAppliedDelta,
    double GamesStartedDelta,
    double GamesEndedDelta,
    double HubErrorsDelta,
    double RateLimitRejectedHubDelta,
    double RateLimitRejectedHttpDelta,
    double SnapshotFailuresDelta,
    double FlagFallDetectedDelta,
    HistogramReport MoveApplyDuration,
    HistogramReport FlagFallSweepDuration,
    GaugeSeriesStats GamesLive,
    GaugeSeriesStats PendingSnapshots,
    GaugeSeriesStats QueueDepth,
    GaugeSeriesStats OnlineUsers,

    // Client-side
    double ClientMovesSubmitted,
    double ClientMovesReceived,
    double? ClientLatencyMeanMs,
    double? ClientLatencyP50Ms,
    double? ClientLatencyP95Ms,
    double? ClientLatencyP99Ms,
    long PingsAnswered,
    long ReconnectsOrCloses,
    IReadOnlyDictionary<string, long> ClientErrorsByCategory,

    // Container stats
    IReadOnlyList<ContainerSeriesStats> Containers,

    // Generator honesty
    double PlannedMovesPerSecond,
    double AchievedMovesPerSecond,
    double AchievedFractionOfPlanned,
    double HarnessCpuMeanPercentOfHost,
    double HarnessCpuMaxPercentOfHost,

    // Budget context (recorded so the JSON is self-describing without the compose file next to it)
    string LoadCpus,
    string LoadMemory,

    IReadOnlyList<CriterionResult> Criteria,
    Verdict Overall)
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() }
    };

    public void WriteJson(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }

    public void PrintSummary()
    {
        Console.WriteLine();
        Console.WriteLine($"===== {Label} (N={TargetGames}, pool={Pool}) =====");
        Console.WriteLine($"Pairing converged: {PairingConverged}  observed live at gate: {ObservedLiveGamesAtQueueGate:F0}");
        Console.WriteLine($"Server moves.applied delta: {MovesAppliedDelta:F0}  games.started delta: {GamesStartedDelta:F0}  games.ended delta: {GamesEndedDelta:F0}");
        Console.WriteLine($"move.apply.duration  mean={Fmt(MoveApplyDuration.MeanMs)}  p50<={Fmt(MoveApplyDuration.P50Ms)}  p95<={Fmt(MoveApplyDuration.P95Ms)}  p99<={Fmt(MoveApplyDuration.P99Ms)}");
        Console.WriteLine($"flagfall.sweep.duration  mean={Fmt(FlagFallSweepDuration.MeanMs)}  p95<={Fmt(FlagFallSweepDuration.P95Ms)}  p99<={Fmt(FlagFallSweepDuration.P99Ms)}");
        Console.WriteLine($"client move latency  mean={Fmt(ClientLatencyMeanMs)}  p50={Fmt(ClientLatencyP50Ms)}  p95={Fmt(ClientLatencyP95Ms)}  p99={Fmt(ClientLatencyP99Ms)}  (n={ClientMovesReceived:F0})");
        Console.WriteLine($"games.live  min={GamesLive.Min:F1} mean={GamesLive.Mean:F1} max={GamesLive.Max:F1}");
        Console.WriteLine($"pending_snapshots  max={PendingSnapshots.Max:F1}  firstThird={PendingSnapshots.FirstThirdMean:F2}  lastThird={PendingSnapshots.LastThirdMean:F2}");
        foreach (var c in Containers)
            Console.WriteLine($"{c.Container}  cpu mean={c.CpuMeanPercent:F1}%  max={c.CpuMaxPercent:F1}%  memMax={c.MemMaxBytes / 1024 / 1024:F0}MiB");
        Console.WriteLine($"planned {PlannedMovesPerSecond:F2} moves/s, achieved {AchievedMovesPerSecond:F2} moves/s ({AchievedFractionOfPlanned:P0})  harness cpu mean={HarnessCpuMeanPercentOfHost:F1}% max={HarnessCpuMaxPercentOfHost:F1}%");
        Console.WriteLine($"hub.errors={HubErrorsDelta:F0}  ratelimit.rejected(hub)={RateLimitRejectedHubDelta:F0}  ratelimit.rejected(http)={RateLimitRejectedHttpDelta:F0}  snapshot_failures={SnapshotFailuresDelta:F0}");
        Console.WriteLine($"reconnects/closes={ReconnectsOrCloses}  pings answered={PingsAnswered}");
        if (ClientErrorsByCategory.Count > 0)
            Console.WriteLine("client errors: " + string.Join(", ", ClientErrorsByCategory.Select(kv => $"{kv.Key}={kv.Value}")));
        Console.WriteLine();
        Console.WriteLine("Criterion                | Verdict | Detail");
        Console.WriteLine("--------------------------|---------|-------");
        foreach (var c in Criteria)
            Console.WriteLine($"{c.Name,-26}| {c.Verdict,-7} | {c.Detail}");
        Console.WriteLine($"OVERALL: {Overall}");
        Console.WriteLine();
    }

    private static string Fmt(double? v) => v is null ? "n/a" : (double.IsPositiveInfinity(v.Value) ? "+Inf" : v.Value.ToString("F1"));
}

internal static class Criteria
{
    public static CriterionResult FlagFallSweep(HistogramStats s)
    {
        if (s.P99 is null) return new("flagfall-sweep-p99", Verdict.Warn, "no sweep observations in window");
        var p99 = s.P99.Value;
        var v = p99 < 250 ? Verdict.Pass : p99 < 1000 ? Verdict.Warn : Verdict.Fail;
        return new("flagfall-sweep-p99", v, $"p99<={p99:F0}ms (warn>=250, fail>=1000)");
    }

    public static CriterionResult MoveLatency(double? p95, double? p99)
    {
        if (p95 is null || p99 is null) return new("move-latency", Verdict.Warn, "no client move-latency samples in window");
        var v = p95 < 250 && p99 < 1000 ? Verdict.Pass : p99 < 1000 ? Verdict.Warn : Verdict.Fail;
        return new("move-latency", v, $"p95={p95:F0}ms p99={p99:F0}ms (pass: p95<250 & p99<1000)");
    }

    public static CriterionResult SnapshotBacklog(GaugeSeriesStats g, int n)
    {
        var threshold = 0.1 * n;
        var trendingUp = g.LastThirdMean > g.FirstThirdMean * 1.2 && g.LastThirdMean > threshold * 0.5;
        var everReturnedLow = g.Min < threshold || g.LastThirdMean < threshold;
        Verdict v;
        string detail;
        if (trendingUp)
        {
            v = Verdict.Fail;
            detail = $"trending up: firstThird={g.FirstThirdMean:F2} lastThird={g.LastThirdMean:F2} (threshold {threshold:F1})";
        }
        else if (g.Max >= threshold)
        {
            v = Verdict.Warn;
            detail = $"spiked to {g.Max:F1} (threshold {threshold:F1}) but no sustained trend";
        }
        else
        {
            v = Verdict.Pass;
            detail = $"max={g.Max:F1} stayed under threshold {threshold:F1}";
        }
        _ = everReturnedLow;
        return new("snapshot-backlog", v, detail);
    }

    public static CriterionResult Correctness(double hubErrors, double ratelimitHub, double snapshotFailures)
    {
        var ok = hubErrors == 0 && ratelimitHub == 0 && snapshotFailures == 0;
        return new("correctness", ok ? Verdict.Pass : Verdict.Fail,
            $"hub.errors={hubErrors:F0} ratelimit.rejected(hub)={ratelimitHub:F0} snapshot_failures={snapshotFailures:F0}");
    }

    public static CriterionResult Headroom(double meanCpuPercentOfBudget, double maxCpuPercentOfBudget)
    {
        Verdict v = meanCpuPercentOfBudget < 80 ? (maxCpuPercentOfBudget < 95 ? Verdict.Pass : Verdict.Warn) : Verdict.Fail;
        return new("backend-cpu-headroom", v, $"mean={meanCpuPercentOfBudget:F0}% max={maxCpuPercentOfBudget:F0}% of pinned budget (fail>=80% mean)");
    }

    public static CriterionResult GeneratorHonesty(double achievedFraction, double harnessCpuMaxPercentOfHost)
    {
        var ok = achievedFraction >= 0.90 && harnessCpuMaxPercentOfHost < 70;
        return new("generator-honesty", ok ? Verdict.Pass : Verdict.Void,
            $"achieved={achievedFraction:P0} of planned (need>=90%), driver cpu max={harnessCpuMaxPercentOfHost:F0}% of host (need<70%)");
    }

    public static Verdict Overall(IEnumerable<CriterionResult> criteria)
    {
        var list = criteria.ToArray();
        if (list.Any(c => c.Verdict == Verdict.Void)) return Verdict.Void;
        if (list.Any(c => c.Verdict == Verdict.Fail)) return Verdict.Fail;
        if (list.Any(c => c.Verdict == Verdict.Warn)) return Verdict.Warn;
        return Verdict.Pass;
    }
}
