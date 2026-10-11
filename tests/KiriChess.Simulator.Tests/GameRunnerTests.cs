using KiriChess.Engine;
using KiriChess.Engine.Config;
using KiriChess.Engine.Core;

namespace KiriChess.Simulator.Tests;

internal sealed class ScriptedMoveChooser : IMoveChooser
{
    private readonly Queue<string> _moves;
    public ScriptedMoveChooser(params string[] uci) => _moves = new Queue<string>(uci);
    public string Name => "Script";
    public Task<string> ChooseAsync(
        string fen, int elo, IReadOnlyList<string> legalUci, Random rng, CancellationToken cancellationToken)
    {
        if (_moves.Count == 0) throw new InvalidOperationException("No scripted moves left.");
        return Task.FromResult(_moves.Dequeue());
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public class GameRunnerTests
{
    [Fact]
    public async Task ScholarsMate_with_zero_think_matches_the_engine_clocks()
    {
        var config = KiriChessConfig.CreateSteal1();
        var start = config.StartTimeSeconds;
        var chooser = new ScriptedMoveChooser("e2e4", "e7e5", "f1c4", "b8c6", "d1h5", "g8f6", "h5f7");
        var game = await GameRunner.PlayAsync(
            new KiriChessEngine(),
            new GameRunRequest("steal1", config, 2100, 2100, Seed: 1, MaxPlies: 20, Realtime: false, FixedThinkMs: 0),
            chooser);

        Assert.Equal(GameStatus.Checkmate, game.Status);
        Assert.Equal(Side.White, game.Winner);
        Assert.Equal(7, game.PlyCount);
        Assert.Equal(start - 6, game.Final.WhiteTimeSeconds);
        Assert.Equal(start - 10, game.Final.BlackTimeSeconds);
        Assert.Equal(0, game.ThinkSecondsTotal);
    }

    [Fact]
    public async Task Fixed_think_ms_is_floored_onto_the_mover_clock()
    {
        var config = KiriChessConfig.CreateSteal1();
        var start = config.StartTimeSeconds;
        var chooser = new ScriptedMoveChooser("e2e4");
        var game = await GameRunner.PlayAsync(
            new KiriChessEngine(),
            new GameRunRequest("steal1", config, 2100, 2100, Seed: 1, MaxPlies: 1, Realtime: false, FixedThinkMs: 2500),
            chooser);

        Assert.Equal(1, game.PlyCount);
        Assert.Equal(2, game.Plies[0].ThinkSeconds);
        Assert.Equal(start - 3, game.Final.WhiteTimeSeconds);
        Assert.Equal(start, game.Final.BlackTimeSeconds);
    }

    [Fact]
    public async Task Random_picker_plays_every_rated_pool_without_throwing()
    {
        var chooser = new RandomMoveChooser();
        foreach (var key in KiriChessConfig.PresetOrder)
        {
            var game = await GameRunner.PlayAsync(
                new KiriChessEngine(),
                new GameRunRequest(
                    key,
                    KiriChessConfig.Presets[key].Clone(),
                    WhiteElo: 1500,
                    BlackElo: 1500,
                    Seed: GameRunner.SeedFor(20260824, key, 0),
                    MaxPlies: 16,
                    Realtime: false,
                    FixedThinkMs: 0),
                chooser);

            Assert.InRange(game.PlyCount, 1, 16);
            if (game.Status == GameStatus.Ongoing) Assert.Equal(16, game.PlyCount);
            Assert.True(game.Final.WhiteTimeSeconds >= 0);
            Assert.True(game.Final.BlackTimeSeconds >= 0);
        }
    }

    [Fact]
    public void SeedFor_is_stable_across_calls()
    {
        var a = GameRunner.SeedFor(20260824, "steal1", 3);
        var b = GameRunner.SeedFor(20260824, "steal1", 3);
        var c = GameRunner.SeedFor(20260824, "steal1", 4);
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }
}

public class SimulatorOptionsTests
{
    [Fact]
    public void Resolves_all_pools_in_declaration_order() =>
        Assert.Equal(KiriChessConfig.PresetOrder, SimulatorOptions.ResolvePools("all"));

    [Fact]
    public void Resolves_a_subset_in_declaration_order_not_csv_order()
    {
        var keys = SimulatorOptions.ResolvePools("steal3,bounty1");
        Assert.Equal("bounty1", keys[0]);
        Assert.Equal("steal3", keys[1]);
        Assert.Equal(2, keys.Count);
    }

    [Fact]
    public void Rejects_an_unknown_pool() =>
        Assert.Throws<ArgumentException>(() => SimulatorOptions.ResolvePools("not-a-pool"));

    [Fact]
    public void Parse_defaults_to_master_clock_and_stockfish()
    {
        var o = SimulatorOptions.Parse(["--games", "3", "--pool", "steal1"]);
        Assert.Equal(3, o.Games);
        Assert.Equal(["steal1"], o.Pools);
        Assert.Equal(2100, o.Elo);
        Assert.Equal(MovePickerKind.Stockfish, o.Picker);
        Assert.False(o.Realtime);
        Assert.Null(o.ThinkMs);
    }

    [Fact]
    public void Parse_accepts_random_picker_and_fixed_think()
    {
        var o = SimulatorOptions.Parse(["--picker", "random", "--think-ms", "1500", "--realtime"]);
        Assert.Equal(MovePickerKind.Random, o.Picker);
        Assert.Equal(1500, o.ThinkMs);
        Assert.True(o.Realtime);
    }
}

public class PgnWriterTests
{
    [Fact]
    public async Task Emits_a_loadable_SAN_game_with_pool_tags()
    {
        var script = new ScriptedMoveChooser("e2e4", "e7e5", "f1c4", "b8c6", "d1h5", "g8f6", "h5f7");
        var game = await GameRunner.PlayAsync(
            new KiriChessEngine(),
            new GameRunRequest("steal1", KiriChessConfig.CreateSteal1(), 2100, 2100, 1, 20, false, 0),
            script);

        var pgn = PgnWriter.Build(game, new DateTimeOffset(2026, 8, 24, 18, 0, 0, TimeSpan.Zero));
        Assert.Contains("[Result \"1-0\"]", pgn, StringComparison.Ordinal);
        Assert.Contains("[KiriChessPool \"steal1\"]", pgn, StringComparison.Ordinal);
        Assert.Contains("[KiriChessTermination \"Checkmate\"]", pgn, StringComparison.Ordinal);
        Assert.Contains("e4", pgn, StringComparison.Ordinal);
        Assert.Contains("Qxf7", pgn, StringComparison.Ordinal);
        Assert.Contains("1-0", pgn, StringComparison.Ordinal);
        Assert.EndsWith("\n\n", pgn.Replace("\r\n", "\n"));
    }
}

public class StockfishProtocolTests
{
    [Theory]
    [InlineData("bestmove e2e4 ponder e7e5", "e2e4")]
    [InlineData("bestmove e7e8q", "e7e8q")]
    [InlineData("bestmove (none)", "")]
    [InlineData("bestmove", "")]
    public void ParseBestMove_reads_the_uci_token(string line, string expected) =>
        Assert.Equal(expected, StockfishMoveChooser.ParseBestMove(line));
}
