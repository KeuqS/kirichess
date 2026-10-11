using System.Globalization;
using KiriChess.Engine.Config;

namespace KiriChess.Simulator;

public enum MovePickerKind
{
    Stockfish,
    Random,
}

public sealed record SimulatorOptions(
    IReadOnlyList<string> Pools,
    int Games,
    int Elo,
    int? WhiteElo,
    int? BlackElo,
    MovePickerKind Picker,
    string? StockfishPath,
    bool Realtime,
    int? ThinkMs,
    int Seed,
    int MaxPlies,
    int Concurrency,
    bool Verbose,
    string? OutPath,
    string? PgnDir)
{
    public int ResolvedWhiteElo => WhiteElo ?? Elo;
    public int ResolvedBlackElo => BlackElo ?? Elo;

    public static SimulatorOptions Parse(string[] args)
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

        if (map.ContainsKey("help") || map.ContainsKey("h"))
            throw new HelpRequestedException();

        string Str(string key, string def) => map.TryGetValue(key, out var v) ? v : def;
        int Int(string key, int def) => map.TryGetValue(key, out var v)
            ? int.Parse(v, CultureInfo.InvariantCulture)
            : def;
        int? IntOpt(string key) => map.TryGetValue(key, out var v)
            ? int.Parse(v, CultureInfo.InvariantCulture)
            : null;
        bool Flag(string key) => map.TryGetValue(key, out var v)
            && (v is "true" or "True" or "" || bool.TryParse(v, out var b) && b);

        var games = Int("games", 1);
        if (games <= 0) throw new ArgumentException("--games must be a positive integer.");

        var elo = Int("elo", 2100);
        var whiteElo = IntOpt("white-elo");
        var blackElo = IntOpt("black-elo");
        var thinkMs = IntOpt("think-ms");
        if (thinkMs is < 0) throw new ArgumentException("--think-ms must be >= 0.");

        var maxPlies = Int("max-plies", GameRunner.DefaultMaxPlies);
        if (maxPlies <= 0) throw new ArgumentException("--max-plies must be a positive integer.");

        var concurrency = Int("concurrency", 1);
        if (concurrency <= 0) throw new ArgumentException("--concurrency must be a positive integer.");

        var pickerRaw = Str("picker", "stockfish").Trim().ToLowerInvariant();
        var picker = pickerRaw switch
        {
            "stockfish" or "sf" => MovePickerKind.Stockfish,
            "random" => MovePickerKind.Random,
            _ => throw new ArgumentException("--picker must be 'stockfish' or 'random'."),
        };

        var poolRaw = Str("pool", "all");
        var pools = ResolvePools(poolRaw);

        var outPath = Str("out", "");
        var pgnDir = Str("pgn", "");

        return new SimulatorOptions(
            Pools: pools,
            Games: games,
            Elo: elo,
            WhiteElo: whiteElo,
            BlackElo: blackElo,
            Picker: picker,
            StockfishPath: string.IsNullOrWhiteSpace(Str("stockfish", "")) ? null : Str("stockfish", ""),
            Realtime: Flag("realtime"),
            ThinkMs: thinkMs,
            Seed: Int("seed", 20260824),
            MaxPlies: maxPlies,
            Concurrency: concurrency,
            Verbose: Flag("verbose"),
            OutPath: string.IsNullOrWhiteSpace(outPath) ? null : outPath,
            PgnDir: string.IsNullOrWhiteSpace(pgnDir) ? null : pgnDir);
    }

    public static IReadOnlyList<string> ResolvePools(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Equals("all", StringComparison.OrdinalIgnoreCase))
            return KiriChessConfig.PresetOrder.ToList();

        var keys = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (keys.Length == 0) throw new ArgumentException("--pool must name at least one pool, or 'all'.");

        var known = new HashSet<string>(KiriChessConfig.PresetOrder, StringComparer.OrdinalIgnoreCase);
        foreach (var key in keys)
        {
            if (!known.Contains(key))
                throw new ArgumentException(
                    $"Unknown pool '{key}'. Known: {string.Join(", ", KiriChessConfig.PresetOrder)}.");
        }

        // Preserve declaration order, not the user's comma order, so summaries line up with RULES.md.
        return KiriChessConfig.PresetOrder
            .Where(k => keys.Any(u => string.Equals(u, k, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    public static string HelpText() => """
        KiriChess game-mode simulator - Stockfish (or random legal) vs itself through the real engine,
        spending think time the way live bots do (ui/lib/botClock.ts).

        Usage:
          dotnet run --project simulator/KiriChess.Simulator -- [options]

        Options:
          --pool <keys>       Pool key, comma list, or 'all' (default: all).
          --games <n>         Games per pool (default: 1).
          --elo <n>           Both sides (default: 2100, master clock + Stockfish depth).
          --white-elo <n>     Override White.
          --black-elo <n>     Override Black.
          --picker <name>     stockfish (default) or random.
          --stockfish <path>  Stockfish binary. Else $STOCKFISH, else PATH.
          --think-ms <n>      Constant think delay in ms, instead of the bot clock policy.
          --realtime          Actually wait the think delay (slow; matches a live bot game).
          --seed <n>          Base seed (default: 20260824).
          --max-plies <n>     Abort a game after this many plies (default: 2000).
          --concurrency <n>   Parallel games; each worker gets its own Stockfish (default: 1).
          --verbose           Print every ply.
          --out <path>        Write a JSON summary.
          --pgn <dir>         Write one PGN per game.
          --help              This text.

        Fast by default: think time is charged as whole seconds without waiting. Use --realtime
        to watch games unfold at bot speed.

        Examples:
          dotnet run --project simulator/KiriChess.Simulator -- --pool steal1 --games 10 --elo 2100
          dotnet run --project simulator/KiriChess.Simulator -- --pool all --games 5 --picker random
        """;
}

public sealed class HelpRequestedException : Exception;
