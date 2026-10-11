using System.Diagnostics;
using System.Globalization;

namespace KiriChess.LoadTest;

/// <summary>One `docker stats --no-stream` sample for one container.</summary>
internal sealed record ContainerSample(DateTimeOffset TakenAt, string Name, double CpuPercent, double MemUsedBytes);

/// <summary>
/// Shells out to `docker stats --no-stream` on a timer. CPU% is Docker's own figure, which is
/// relative to one full host CPU (100% == one core saturated) regardless of any `--cpus` limit -
/// README/task file note this explicitly: "CPU% is relative to the pinned budget - with cpus: 1.0,
/// 100% is the ceiling". That is true only because budget A pins cpus to 1.0; the report states the
/// convention used so a reader with budget B (cpus: 2.0, where 100% is only half the ceiling) is not
/// misled.
/// </summary>
internal static class ContainerStats
{
    public static async Task<IReadOnlyList<ContainerSample>> SampleOnceAsync(IEnumerable<string> containerNames, CancellationToken ct)
    {
        var names = containerNames.ToArray();
        var psi = new ProcessStartInfo
        {
            FileName = "docker",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("stats");
        psi.ArgumentList.Add("--no-stream");
        psi.ArgumentList.Add("--format");
        psi.ArgumentList.Add("{{.Name}};{{.CPUPerc}};{{.MemUsage}}");
        foreach (var n in names) psi.ArgumentList.Add(n);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start docker stats.");
        var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = await proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"docker stats exited {proc.ExitCode}: {stderr}");

        var takenAt = DateTimeOffset.UtcNow;
        var results = new List<ContainerSample>();
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Trim().Split(';');
            if (parts.Length != 3) continue;
            var name = parts[0];
            var cpu = ParsePercent(parts[1]);
            var mem = ParseMemUsed(parts[2]);
            results.Add(new ContainerSample(takenAt, name, cpu, mem));
        }
        return results;
    }

    private static double ParsePercent(string s) =>
        double.TryParse(s.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    /// <summary>"123.4MiB / 512MiB" -&gt; the used side, in bytes.</summary>
    private static double ParseMemUsed(string s)
    {
        var used = s.Split('/', 2)[0].Trim();
        return ParseByteSize(used);
    }

    private static double ParseByteSize(string s)
    {
        s = s.Trim();
        var i = 0;
        while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
        if (i == 0) return 0;
        var numberPart = s[..i];
        var unitPart = s[i..].Trim();
        if (!double.TryParse(numberPart, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return 0;

        double multiplier = unitPart.ToUpperInvariant() switch
        {
            "B" => 1,
            "KB" => 1_000,
            "KIB" => 1024,
            "MB" => 1_000_000,
            "MIB" => 1024.0 * 1024,
            "GB" => 1_000_000_000,
            "GIB" => 1024.0 * 1024 * 1024,
            _ => 1
        };
        return number * multiplier;
    }
}

/// <summary>
/// Samples the harness's own process CPU on a timer - the generator-honesty check
/// (README § Decision 6) needs "driver CPU < 70% of the host" alongside achieved moves/s, because a
/// generator that cannot keep up looks identical to a server that cannot keep up unless something
/// measures the generator too.
/// </summary>
internal sealed class HarnessCpuSampler
{
    private readonly int _processorCount = Environment.ProcessorCount;
    private TimeSpan _lastCpuTime;
    private DateTimeOffset _lastWallTime;
    private readonly List<double> _samples = new();

    public void Start()
    {
        _lastCpuTime = Process.GetCurrentProcess().TotalProcessorTime;
        _lastWallTime = DateTimeOffset.UtcNow;
        _samples.Clear();
    }

    public void Sample()
    {
        var now = DateTimeOffset.UtcNow;
        var cpuNow = Process.GetCurrentProcess().TotalProcessorTime;
        var wallDelta = (now - _lastWallTime).TotalSeconds;
        var cpuDelta = (cpuNow - _lastCpuTime).TotalSeconds;
        if (wallDelta > 0)
        {
            var pctOfHost = cpuDelta / wallDelta / _processorCount * 100.0;
            _samples.Add(pctOfHost);
        }
        _lastCpuTime = cpuNow;
        _lastWallTime = now;
    }

    public IReadOnlyList<double> Samples => _samples;
}
