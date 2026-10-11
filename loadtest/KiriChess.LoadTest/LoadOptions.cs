using System.Globalization;

namespace KiriChess.LoadTest;

/// <summary>The parsed run configuration. See loadtest/README.md for the full CLI reference.</summary>
internal sealed record LoadOptions(
    int Games,
    string Pool,
    string Api,
    string ScrapeToken,
    int ThinkMs,
    int JitterMs,
    int WarmupSeconds,
    int WindowSeconds,
    bool Presence,
    int Seed,
    string Label,
    string Out,
    int RampBatchSize,
    string BackendContainer,
    string DbContainer,
    int SampleIntervalSeconds,
    int ReadyTimeoutSeconds,
    int PairTimeoutSeconds,
    double PinnedCpus,
    string LoadMemory,
    int BotOffset)
{
    public static LoadOptions Parse(string[] args)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal)) continue;
            var key = args[i][2..];
            var value = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[++i]
                : "true";
            map[key] = value;
        }

        string Str(string key, string def) => map.TryGetValue(key, out var v) ? v : def;
        int Int(string key, int def) => map.TryGetValue(key, out var v) ? int.Parse(v, CultureInfo.InvariantCulture) : def;
        bool Bool(string key, bool def) => map.TryGetValue(key, out var v) ? bool.Parse(v) : def;
        double Dbl(string key, double def) => map.TryGetValue(key, out var v) ? double.Parse(v, CultureInfo.InvariantCulture) : def;

        var games = Int("games", 0);
        if (games <= 0) throw new ArgumentException("--games must be a positive integer.");

        return new LoadOptions(
            Games: games,
            Pool: Str("pool", "steal3"),
            Api: Str("api", "http://localhost:8080").TrimEnd('/'),
            ScrapeToken: Str("scrape-token", ""),
            ThinkMs: Int("think-ms", 3000),
            JitterMs: Int("jitter-ms", 1000),
            WarmupSeconds: Int("warmup-seconds", 90),
            WindowSeconds: Int("window-seconds", 180),
            Presence: Bool("presence", true),
            Seed: Int("seed", 20260808),
            Label: Str("label", "run"),
            Out: Str("out", "loadtest/results/run.json"),
            RampBatchSize: Int("ramp-batch-size", 50),
            BackendContainer: Str("backend-container", "kirichess-backend"),
            DbContainer: Str("db-container", "kirichess-db"),
            SampleIntervalSeconds: Int("sample-interval-seconds", 5),
            ReadyTimeoutSeconds: Int("ready-timeout-seconds", 120),
            PairTimeoutSeconds: Int("pair-timeout-seconds", 180),
            PinnedCpus: Dbl("pinned-cpus", 1.0),
            LoadMemory: Str("load-memory", "512m"),
            BotOffset: Int("bot-offset", 0));
    }
}
