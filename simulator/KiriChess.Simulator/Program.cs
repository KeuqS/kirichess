using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using KiriChess.Engine;
using KiriChess.Engine.Config;
using KiriChess.Engine.Core;
using KiriChess.Simulator;

SimulatorOptions options;
try
{
    if (args.Length == 0)
    {
        Console.WriteLine(SimulatorOptions.HelpText());
        return 0;
    }
    options = SimulatorOptions.Parse(args);
}
catch (HelpRequestedException)
{
    Console.WriteLine(SimulatorOptions.HelpText());
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Bad arguments: {ex.Message}");
    Console.Error.WriteLine();
    Console.Error.WriteLine(SimulatorOptions.HelpText());
    return 2;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

IMoveChooser[] workers;
try
{
    workers = CreateWorkers(options);
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

var started = DateTimeOffset.UtcNow;
Console.WriteLine(
    $"KiriChess simulator  pools={string.Join(",", options.Pools)} games/pool={options.Games} " +
    $"picker={options.Picker} elo=W{options.ResolvedWhiteElo}/B{options.ResolvedBlackElo} " +
    $"think={(options.ThinkMs is { } ms ? $"{ms}ms fixed" : "bot-clock")} " +
    $"realtime={options.Realtime} seed={options.Seed} concurrency={options.Concurrency}");

var poolReports = new List<PoolSummary>();

try
{
    foreach (var poolKey in options.Pools)
    {
        cts.Token.ThrowIfCancellationRequested();
        var config = KiriChessConfig.Presets[poolKey].Clone();
        var results = new ConcurrentBag<(int Index, SimulatedGame Game)>();
        var chooserPool = new BlockingCollection<IMoveChooser>(workers.Length);
        foreach (var worker in workers) chooserPool.Add(worker);

        await Parallel.ForEachAsync(Enumerable.Range(0, options.Games), new ParallelOptions
        {
            MaxDegreeOfParallelism = workers.Length,
            CancellationToken = cts.Token,
        }, async (index, token) =>
        {
            var chooser = chooserPool.Take(token);
            try
            {
                var request = new GameRunRequest(
                    PoolKey: poolKey,
                    Config: config,
                    WhiteElo: options.ResolvedWhiteElo,
                    BlackElo: options.ResolvedBlackElo,
                    Seed: GameRunner.SeedFor(options.Seed, poolKey, index),
                    MaxPlies: options.MaxPlies,
                    Realtime: options.Realtime,
                    FixedThinkMs: options.ThinkMs);

                // One engine per game: Gera.Chess parallelises inside a call, and we do not want two
                // games sharing a board object even though KiriChessEngine itself is stateless.
                var game = await GameRunner.PlayAsync(new KiriChessEngine(), request, chooser, token);
                results.Add((index, game));
                WriteGameLine(options, poolKey, index, game);
            }
            finally
            {
                chooserPool.Add(chooser);
            }
        });

        var ordered = results.OrderBy(r => r.Index).ToList();
        poolReports.Add(ReportBuilder.Summarize(poolKey, KiriChessConfig.PoolLabel(poolKey), ordered));

        if (options.PgnDir is { } pgnDir)
        {
            Directory.CreateDirectory(pgnDir);
            foreach (var (index, game) in ordered)
            {
                var name = $"{poolKey}-{index.ToString("D3", CultureInfo.InvariantCulture)}-{game.Status}.pgn";
                await File.WriteAllTextAsync(
                    Path.Combine(pgnDir, name),
                    PgnWriter.Build(game, started),
                    cts.Token);
            }
        }
    }
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Cancelled.");
    return 130;
}
finally
{
    foreach (var worker in workers)
        await worker.DisposeAsync();
}

Console.WriteLine();
Console.WriteLine(ReportBuilder.FormatTable(poolReports));
Console.WriteLine();
Console.WriteLine($"elapsed {(DateTimeOffset.UtcNow - started).TotalSeconds:0.0}s  " +
                  "(think time is charged, not waited, unless --realtime)");

if (options.OutPath is { } outPath)
{
    var dir = Path.GetDirectoryName(outPath);
    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    var report = new RunReport
    {
        Seed = options.Seed,
        Picker = options.Picker.ToString().ToLowerInvariant(),
        WhiteElo = options.ResolvedWhiteElo,
        BlackElo = options.ResolvedBlackElo,
        FixedThinkMs = options.ThinkMs,
        Realtime = options.Realtime,
        Pools = poolReports,
    };
    await File.WriteAllTextAsync(outPath, JsonSerializer.Serialize(report, ReportBuilder.JsonOptions));
    Console.WriteLine($"wrote {outPath}");
}

return 0;

static IMoveChooser[] CreateWorkers(SimulatorOptions options)
{
    if (options.Picker == MovePickerKind.Random)
        return Enumerable.Range(0, options.Concurrency)
            .Select(_ => (IMoveChooser)new RandomMoveChooser())
            .ToArray();

    var path = StockfishMoveChooser.Find(options.StockfishPath)
        ?? throw new InvalidOperationException(StockfishMoveChooser.InstallHint());

    Console.WriteLine($"Using Stockfish at {path}");
    return Enumerable.Range(0, options.Concurrency)
        .Select(_ => (IMoveChooser)new StockfishMoveChooser(path))
        .ToArray();
}

static void WriteGameLine(SimulatorOptions options, string poolKey, int index, SimulatedGame game)
{
    lock (Console.Out)
    {
        var heading =
            $"  {poolKey} {index + 1}/{options.Games}  {game.Status,-24} " +
            $"plies={game.PlyCount,3}  W={game.Final.WhiteTimeSeconds,4}s B={game.Final.BlackTimeSeconds,4}s  " +
            $"think={game.ThinkSecondsTotal}s";
        Console.WriteLine(heading);
        if (!options.Verbose) return;

        foreach (var ply in game.Plies)
        {
            var num = ply.Index / 2 + 1;
            var prefix = ply.Side == Side.White ? $"{num}." : $"{num}...";
            Console.WriteLine(
                $"    {prefix,-6} {ply.San,-8} think={ply.ThinkSeconds}s  " +
                $"W={ply.WhiteTimeAfter} B={ply.BlackTimeAfter}");
        }
    }
}
