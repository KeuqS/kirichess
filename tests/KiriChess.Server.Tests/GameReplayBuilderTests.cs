using System.Diagnostics.Metrics;
using KiriChess.Engine;
using KiriChess.Engine.Config;
using KiriChess.Engine.Core;
using KiriChess.Server.Data.Entities;
using KiriChess.Server.Games;
using KiriChess.Server.Infrastructure;
using Xunit;

namespace KiriChess.Server.Tests;

/// <summary>
/// Pure (no Docker) coverage of <see cref="GameReplayBuilder.Build"/>, the logic behind the public
/// replay endpoint (Phase 8.2). These are the tests that actually prove the reconstruction, since the
/// Docker-gated <see cref="ReplayEndpointTests"/> only exercises routing and skip without a daemon.
/// </summary>
public sealed class GameReplayBuilderTests
{
    private static KiriChessConfig CreateFlagfallConfig() => KiriChessConfig.Presets["steal1"].Clone();

    private static Game CreateEntity(KiriChessConfig config, List<MoveRecord> moves,
        GameResult result = GameResult.WhiteWins, TerminationReason termination = TerminationReason.Checkmate,
        string? startFen = null) => new()
    {
        WhitePlayerId = Guid.NewGuid(),
        BlackPlayerId = Guid.NewGuid(),
        Result = result,
        Termination = termination,
        RatingPool = "steal1",
        IsRated = true,
        StartFen = startFen ?? KiriChessEngine.StartFen,
        ConfigSnapshot = config,
        Moves = moves,
    };

    [Fact]
    public void Every_ply_gets_a_distinct_well_formed_fen_after_it()
    {
        var engine = new KiriChessEngine();
        var config = CreateFlagfallConfig();
        var entity = CreateEntity(config, new List<MoveRecord>
        {
            new(1, "e2e4", Side.White, 172, 180, -8, 0, "Ongoing"),
            new(2, "e7e5", Side.Black, 172, 172, 0, -8, "Ongoing"),
        });

        var response = GameReplayBuilder.Build(engine, entity, "Ada", "Bo");

        Assert.Equal(2, response.Plies.Count);
        Assert.Null(response.TruncatedAtPly);
        Assert.Equal(
            "rnbqkbnr/pppppppp/8/8/4P3/8/PPPP1PPP/RNBQKBNR b KQkq e3 0 1",
            response.Plies[0].FenAfter);
        // Distinct FENs, the second ply is not a re-emission of the first.
        Assert.NotEqual(response.Plies[0].FenAfter, response.Plies[1].FenAfter);
    }

    [Fact]
    public void Clocks_and_deltas_come_from_the_stored_record_not_the_zero_think_time_replay()
    {
        // A think-time-0 replay of a single steal1 pawn push leaves White's clock close to its
        // starting value (only the movement toll is charged). The stored record instead claims 100,
        // a number no replay of this config would ever produce, so if the builder is ever
        // "simplified" into reading result.NewPosition.WhiteTimeSeconds instead of the record, this
        // assertion fails.
        var engine = new KiriChessEngine();
        var config = CreateFlagfallConfig();
        var entity = CreateEntity(config, new List<MoveRecord>
        {
            new(1, "e2e4", Side.White, 100, 360, -8, 0, "Ongoing"),
        });

        var response = GameReplayBuilder.Build(engine, entity, "Ada", "Bo");

        var ply = Assert.Single(response.Plies);
        Assert.Equal(100, ply.WhiteTimeAfter);
        Assert.Equal(360, ply.BlackTimeAfter);
        Assert.Equal(-8, ply.WhiteDelta);
        Assert.Equal(0, ply.BlackDelta);
    }

    [Fact]
    public void Events_are_populated_and_reconcile_with_the_stored_delta_for_a_capture()
    {
        var engine = new KiriChessEngine();
        var config = CreateFlagfallConfig();

        // Compute the real economy for a capture ply (white takes the d5 pawn with e4xd5) so the
        // reconciliation assertion below is against the engine's actual output, not a guessed number.
        var afterE4 = engine.Apply(engine.NewGame(config), Move.Parse("e2e4"), config, 0);
        var afterD5 = engine.Apply(afterE4.NewPosition, Move.Parse("d7d5"), config, 0);
        var capture = engine.Apply(afterD5.NewPosition, Move.Parse("e4d5"), config, 0);

        var entity = CreateEntity(config, new List<MoveRecord>
        {
            new(1, "e2e4", Side.White, 179, 180, afterE4.WhiteTimeDelta, 0, "Ongoing"),
            new(2, "d7d5", Side.Black, 179, 179, 0, afterD5.BlackTimeDelta, "Ongoing"),
            new(3, "e4d5", Side.White, 179 + capture.WhiteTimeDelta, 179, capture.WhiteTimeDelta, capture.BlackTimeDelta, "Ongoing"),
        });

        var response = GameReplayBuilder.Build(engine, entity, "Ada", "Bo");

        var capturePly = response.Plies[2];
        Assert.Contains(capturePly.Events, e => e.Type == "CaptureGain" && e.Side == "White");
        Assert.Equal(capturePly.WhiteDelta, capturePly.Events.Where(e => e.Side == "White").Sum(e => e.Amount));
        Assert.Equal(capturePly.BlackDelta, capturePly.Events.Where(e => e.Side == "Black").Sum(e => e.Amount));
    }

    [Fact]
    public void A_corrupt_move_log_truncates_instead_of_throwing()
    {
        var engine = new KiriChessEngine();
        var config = CreateFlagfallConfig();
        // The second "e2e4" is illegal in the position after the first (no white pawn left on e2,
        // and it is Black to move), the stored log and config disagree, which must truncate, not throw.
        var entity = CreateEntity(config, new List<MoveRecord>
        {
            new(1, "e2e4", Side.White, 179, 180, -1, 0, "Ongoing"),
            new(2, "e2e4", Side.White, 179, 180, -1, 0, "Ongoing"),
        });

        var response = GameReplayBuilder.Build(engine, entity, "Ada", "Bo");

        Assert.Single(response.Plies);
    }

    [Fact]
    public void An_empty_move_list_yields_zero_plies_and_a_well_formed_header()
    {
        var engine = new KiriChessEngine();
        var config = CreateFlagfallConfig();
        var entity = CreateEntity(config, new List<MoveRecord>(), GameResult.Draw, TerminationReason.DrawByAgreement);

        var response = GameReplayBuilder.Build(engine, entity, "Ada", "Bo");

        Assert.Empty(response.Plies);
        Assert.Equal("Draw", response.Result);
        Assert.Equal("DrawByAgreement", response.Termination);
        Assert.Equal("steal1", response.RatingPool);
        Assert.True(response.IsRated);
        Assert.Equal("Ada", response.WhitePlayerName);
        Assert.Equal("Bo", response.BlackPlayerName);
    }

    [Fact]
    public void A_missing_start_fen_falls_back_to_the_standard_start_and_still_replays()
    {
        var engine = new KiriChessEngine();
        var config = CreateFlagfallConfig();
        var entity = CreateEntity(config, new List<MoveRecord>
        {
            new(1, "e2e4", Side.White, 179, 180, -1, 0, "Ongoing"),
        }, startFen: "");

        var response = GameReplayBuilder.Build(engine, entity, "Ada", "Bo");

        Assert.Equal(KiriChessEngine.StartFen, response.StartFen);
        Assert.Single(response.Plies);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_null_or_blank_display_name_becomes_unknown_player(string? name)
    {
        var engine = new KiriChessEngine();
        var config = CreateFlagfallConfig();
        var entity = CreateEntity(config, new List<MoveRecord>());

        var response = GameReplayBuilder.Build(engine, entity, name, name);

        Assert.Equal(GameReplayBuilder.UnknownPlayerName, response.WhitePlayerName);
        Assert.Equal(GameReplayBuilder.UnknownPlayerName, response.BlackPlayerName);
    }

    /// <summary>Tiny local stand-in for a real <see cref="IMeterFactory"/>, mirroring
    /// KiriChessMetricsTests's own so this file can prove a truncation is actually recorded, not just
    /// that Build tolerates an optional metrics argument.</summary>
    private sealed class DummyMeterFactory : IMeterFactory
    {
        private readonly List<Meter> _meters = new();
        public Meter Create(MeterOptions options) { var m = new Meter(options); _meters.Add(m); return m; }
        public void Dispose() { foreach (var m in _meters) m.Dispose(); }
        public bool Owns(Meter meter) => _meters.Contains(meter);
    }

    private static (MeterListener Listener, List<(string Name, KeyValuePair<string, object?>[] Tags)> Measurements) Listen(DummyMeterFactory factory)
    {
        var measurements = new List<(string, KeyValuePair<string, object?>[])>();
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == KiriChessMetrics.MeterName && factory.Owns(instrument.Meter))
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => measurements.Add((instrument.Name, tags.ToArray())));
        listener.Start();
        return (listener, measurements);
    }

    [Fact]
    public void A_corrupt_move_log_logs_a_warning_naming_the_game_and_counts_the_truncation()
    {
        // C-8-3: this used to be silent - no log line, no metric, no field on the response - so a
        // jsonb row that stopped replaying was invisible to the maintainer forever.
        var engine = new KiriChessEngine();
        var config = CreateFlagfallConfig();
        var entity = CreateEntity(config, new List<MoveRecord>
        {
            new(1, "e2e4", Side.White, 179, 180, -1, 0, "Ongoing"),
            new(2, "e2e4", Side.White, 179, 180, -1, 0, "Ongoing"),
        });
        var logProvider = new CapturingLoggerProvider();
        var logger = logProvider.CreateLogger("test");
        using var factory = new DummyMeterFactory();
        var metrics = new KiriChessMetrics(factory);
        var (listener, measurements) = Listen(factory);
        using var _ = listener;

        var response = GameReplayBuilder.Build(engine, entity, "Ada", "Bo", logger: logger, metrics: metrics);

        Assert.Single(response.Plies);
        Assert.Equal(2, response.TruncatedAtPly);
        Assert.Contains(logProvider.Messages, m => m.Contains(entity.Id.ToString()) && m.Contains("truncated"));
        Assert.Contains(measurements, m => m.Name == "kirichess.replay.truncated"
            && m.Tags.Any(t => t.Key == "artifact" && Equals(t.Value, "replay")));
    }

    [Fact]
    public void A_config_snapshot_that_fails_Validate_is_caught_and_a_headers_only_response_is_returned_instead_of_throwing()
    {
        // C-8-6: engine.FromFen calls config.Validate() before ChessBoard.LoadFromFen, and Validate()
        // throws InvalidOperationException for exactly this shape of corruption (KiriChessConfig.cs:
        // "Config missing MoveCostPerPiece for King") - a jsonb row that deserialized before a rule
        // variable was added. This call used to sit outside the corrupt-row guard entirely and 500 a
        // public route permanently for the one class of failure the rest of this method survives.
        var engine = new KiriChessEngine();
        var config = CreateFlagfallConfig();
        config.MoveCostPerPiece.Remove(PieceType.King);
        var entity = CreateEntity(config, new List<MoveRecord>
        {
            new(1, "e2e4", Side.White, 179, 180, -1, 0, "Ongoing"),
        });

        var response = GameReplayBuilder.Build(engine, entity, "Ada", "Bo");

        Assert.Empty(response.Plies);
        Assert.Equal(0, response.TruncatedAtPly);
        Assert.Equal(entity.Id, response.GameId);
        Assert.Equal("Ada", response.WhitePlayerName);
        // EconomyDtoOrDefault's zeroed fallback, not a second throw from indexing the same missing key.
        Assert.Equal(0, response.Economy.MoveCostPerPiece.King);
    }

    [Fact]
    public void Economy_matches_the_config_snapshot()
    {
        var engine = new KiriChessEngine();
        var config = CreateFlagfallConfig();
        var entity = CreateEntity(config, new List<MoveRecord>());

        var response = GameReplayBuilder.Build(engine, entity, "Ada", "Bo");

        Assert.Equal(config.StartTimeSeconds, response.Economy.StartTimeSeconds);
        Assert.Equal(config.MoveCostMode.ToString(), response.Economy.MoveCostMode);
        Assert.Equal(config.EconomyScale, response.Economy.EconomyScale);
        Assert.Equal(config.CaptureMult, response.Economy.CaptureMult);
    }
}
