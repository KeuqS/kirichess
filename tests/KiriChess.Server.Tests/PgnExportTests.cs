using System.Diagnostics.Metrics;
using System.Text.RegularExpressions;
using KiriChess.Engine;
using KiriChess.Engine.Config;
using KiriChess.Engine.Core;
using KiriChess.Server.Data.Entities;
using KiriChess.Server.Games;
using KiriChess.Server.Infrastructure;
using Xunit;

namespace KiriChess.Server.Tests;

/// <summary>
/// Pure (no Docker) coverage of <see cref="PgnBuilder.Build"/>, the logic behind
/// <c>GET /api/games/{id}/pgn</c> (Phase 9e.2). Modelled on <see cref="GameReplayBuilderTests"/>: a
/// real <see cref="KiriChessEngine"/> and a real preset, and "compute the expected economy by running
/// the engine rather than guessing a number" wherever a number needs computing.
///
/// The format under test is an external contract (docs/game/PGN.md, Phase 9e.2 "The
/// format"); these tests exist to catch an accidental shape change before it ships to a file already
/// sitting on someone's disk.
/// </summary>
public sealed class PgnExportTests
{
    private static readonly DateTimeOffset FixedCreatedAt = new(2026, 8, 15, 19, 4, 11, TimeSpan.Zero);

    private static KiriChessConfig CreateSteal1Config() => KiriChessConfig.Presets["steal1"].Clone();

    private static Game CreateEntity(KiriChessConfig config, List<MoveRecord> moves,
        GameResult result = GameResult.WhiteWins, TerminationReason termination = TerminationReason.Checkmate,
        string? startFen = null, string? ratingPool = "steal1", bool isRated = true) => new()
    {
        WhitePlayerId = Guid.NewGuid(),
        BlackPlayerId = Guid.NewGuid(),
        Result = result,
        Termination = termination,
        RatingPool = ratingPool,
        IsRated = isRated,
        StartFen = startFen ?? KiriChessEngine.StartFen,
        ConfigSnapshot = config,
        Moves = moves,
        CreatedAt = FixedCreatedAt,
    };

    /// <summary>The blank line between the header block and the movetext (Decision 3/4): everything
    /// after it, minus the file's own trailing blank line, is the movetext under test.</summary>
    private static string MovetextOf(string pgn)
    {
        var separatorIndex = pgn.IndexOf("\n\n", StringComparison.Ordinal);
        Assert.True(separatorIndex >= 0, "PGN has no blank line between the header block and the movetext.");
        return pgn[(separatorIndex + 2)..].TrimEnd('\n');
    }

    [Fact]
    public void Header_block_is_complete_and_in_order_for_a_rated_flagfall_game()
    {
        var engine = new KiriChessEngine();
        var config = CreateSteal1Config();
        var entity = CreateEntity(config, new List<MoveRecord>
        {
            new(1, "e2e4", Side.White, 179, 180, -1, 0, "Ongoing"),
        });

        var pgn = PgnBuilder.Build(engine, entity, "Ada", "Bo", 1523, 1487, "https://kirichess.app");

        var expectedHeader =
            "[Event \"KiriChess rated " + KiriChessConfig.PoolLabel("steal1") + " game\"]\n" +
            $"[Site \"https://kirichess.app/replay/{entity.Id}\"]\n" +
            "[Date \"2026.08.15\"]\n" +
            "[Round \"-\"]\n" +
            "[White \"Ada\"]\n" +
            "[Black \"Bo\"]\n" +
            "[Result \"1-0\"]\n" +
            "[UTCDate \"2026.08.15\"]\n" +
            "[UTCTime \"19:04:11\"]\n" +
            "[WhiteElo \"1523\"]\n" +
            "[BlackElo \"1487\"]\n" +
            $"[TimeControl \"{config.StartTimeSeconds}+{config.BaseIncrement}\"]\n" +
            "[Termination \"Normal\"]\n" +
            "[KiriChessFormat \"1\"]\n" +
            $"[KiriChessId \"{entity.Id}\"]\n" +
            "[KiriChessPool \"steal1\"]\n" +
            "[KiriChessRated \"true\"]\n" +
            "[KiriChessTermination \"Checkmate\"]\n";

        Assert.StartsWith(expectedHeader, pgn);
        // No [Variant] tag and, for a standard start, no [FEN]/[SetUp]: Decision 3's closest call.
        Assert.DoesNotContain("[Variant", pgn);
        Assert.DoesNotContain("[FEN", pgn);
        Assert.DoesNotContain("[SetUp", pgn);
    }

    [Theory]
    [InlineData(GameResult.WhiteWins, "1-0")]
    [InlineData(GameResult.BlackWins, "0-1")]
    [InlineData(GameResult.Draw, "1/2-1/2")]
    [InlineData(GameResult.Aborted, "*")]
    [InlineData(GameResult.Ongoing, "*")]
    public void Result_maps_to_the_standard_PGN_token(GameResult result, string expectedToken)
    {
        var engine = new KiriChessEngine();
        var config = CreateSteal1Config();
        var entity = CreateEntity(config, new List<MoveRecord>(), result: result);

        var pgn = PgnBuilder.Build(engine, entity, "Ada", "Bo", null, null, null);

        Assert.Contains($"[Result \"{expectedToken}\"]", pgn);
        var movetext = MovetextOf(pgn);
        Assert.EndsWith(expectedToken, movetext);
    }

    [Theory]
    [InlineData(TerminationReason.None, "Unterminated")]
    [InlineData(TerminationReason.Checkmate, "Normal")]
    [InlineData(TerminationReason.Flagfall, "Time forfeit")]
    [InlineData(TerminationReason.Stalemate, "Normal")]
    [InlineData(TerminationReason.Resignation, "Normal")]
    [InlineData(TerminationReason.DrawByRepetition, "Normal")]
    [InlineData(TerminationReason.DrawByInsufficientMaterial, "Normal")]
    [InlineData(TerminationReason.DrawByFiftyMove, "Normal")]
    [InlineData(TerminationReason.Abandoned, "Abandoned")]
    [InlineData(TerminationReason.DrawByAgreement, "Normal")]
    [InlineData(TerminationReason.ServerRestart, "Unterminated")]
    [InlineData(TerminationReason.NoShow, "Abandoned")]
    public void Termination_maps_to_the_standard_vocabulary_and_keeps_the_exact_reason_too(
        TerminationReason termination, string expectedStandardValue)
    {
        var engine = new KiriChessEngine();
        var config = CreateSteal1Config();
        var entity = CreateEntity(config, new List<MoveRecord>(), termination: termination);

        var pgn = PgnBuilder.Build(engine, entity, "Ada", "Bo", null, null, null);

        Assert.Contains($"[Termination \"{expectedStandardValue}\"]", pgn);
        Assert.Contains($"[KiriChessTermination \"{termination}\"]", pgn);
    }

    [Fact]
    public void Site_is_the_unknown_marker_when_no_base_url_is_configured()
    {
        var engine = new KiriChessEngine();
        var config = CreateSteal1Config();
        var entity = CreateEntity(config, new List<MoveRecord>());

        var pgn = PgnBuilder.Build(engine, entity, "Ada", "Bo", null, null, null);

        Assert.Contains("[Site \"?\"]", pgn);
    }

    [Fact]
    public void Site_is_the_replay_url_when_a_base_url_is_configured()
    {
        var engine = new KiriChessEngine();
        var config = CreateSteal1Config();
        var entity = CreateEntity(config, new List<MoveRecord>());

        var pgn = PgnBuilder.Build(engine, entity, "Ada", "Bo", null, null, "https://kirichess.app");

        Assert.Contains($"[Site \"https://kirichess.app/replay/{entity.Id}\"]", pgn);
    }

    [Fact]
    public void Elo_tags_are_absent_not_a_question_mark_when_elo_arguments_are_null()
    {
        var engine = new KiriChessEngine();
        var config = CreateSteal1Config();
        var entity = CreateEntity(config, new List<MoveRecord>());

        var pgn = PgnBuilder.Build(engine, entity, "Ada", "Bo", null, null, null);

        Assert.DoesNotContain("[WhiteElo", pgn);
        Assert.DoesNotContain("[BlackElo", pgn);
    }

    [Fact]
    public void Elo_tags_carry_the_supplied_values_when_present()
    {
        var engine = new KiriChessEngine();
        var config = CreateSteal1Config();
        var entity = CreateEntity(config, new List<MoveRecord>());

        var pgn = PgnBuilder.Build(engine, entity, "Ada", "Bo", 1500, 1400, null);

        Assert.Contains("[WhiteElo \"1500\"]", pgn);
        Assert.Contains("[BlackElo \"1400\"]", pgn);
    }

    [Fact]
    public void FEN_and_SetUp_appear_together_and_the_first_move_numbers_from_the_FEN()
    {
        var engine = new KiriChessEngine();
        var config = CreateSteal1Config();
        // Fullmove 5, Black to move: the FEN's own fields 6 and 2 (1-indexed), not move 1, must
        // drive the first move-number token.
        var startFen = "rnbqkbnr/pppp1ppp/8/4p3/4P3/8/PPPP1PPP/RNBQKBNR b KQkq - 0 5";
        var entity = CreateEntity(config, new List<MoveRecord>
        {
            new(1, "b8c6", Side.Black, 179, 180, -1, 0, "Ongoing"),
        }, startFen: startFen);

        var pgn = PgnBuilder.Build(engine, entity, "Ada", "Bo", null, null, null);

        Assert.Contains($"[FEN \"{startFen}\"]", pgn);
        Assert.Contains("[SetUp \"1\"]", pgn);
        var movetext = MovetextOf(pgn);
        Assert.Contains("5... Nc6", movetext);
        Assert.DoesNotContain("1... Nc6", movetext);
    }

    [Fact]
    public void Movetext_shape_carries_the_four_MoveRecord_numbers_verbatim()
    {
        var engine = new KiriChessEngine();
        var config = CreateSteal1Config();
        // Numbers a zero-think-time replay of this ply could never produce on its own (mirrors
        // GameReplayBuilderTests.Clocks_and_deltas_come_from_the_stored_record_not_the_zero_think_time_replay):
        // proves the movetext reads MoveRecord, never the replay's own recomputed clock.
        var entity = CreateEntity(config, new List<MoveRecord>
        {
            new(1, "e2e4", Side.White, 100, 360, -8, 0, "Ongoing"),
        });

        var pgn = PgnBuilder.Build(engine, entity, "Ada", "Bo", null, null, null);
        var movetext = MovetextOf(pgn);

        Assert.Contains("1. e4 { [%clk 0:01:40] [%kc 100,360,-8,0] }", movetext);
    }

    [Fact]
    public void Movetext_repeats_blacks_move_number_with_the_ellipsis_form()
    {
        var engine = new KiriChessEngine();
        var config = CreateSteal1Config();
        var afterE4 = engine.Apply(engine.NewGame(config), Move.Parse("e2e4"), config, 0);
        var entity = CreateEntity(config, new List<MoveRecord>
        {
            new(1, "e2e4", Side.White, 179, 180, afterE4.WhiteTimeDelta, 0, "Ongoing"),
            new(2, "e7e5", Side.Black, 179, 179, 0, -1, "Ongoing"),
        });

        var pgn = PgnBuilder.Build(engine, entity, "Ada", "Bo", null, null, null);
        var movetext = MovetextOf(pgn);

        // Not asserting the comment sits on the same physical line as "1... e5": the 80-column wrap
        // (WrapTokens) can legitimately break the line between a move token and its comment token,
        // and did in this exact case, since only whitespace ever separates tokens either way.
        Assert.Contains("1. e4 {", movetext);
        Assert.Contains("1... e5", movetext);
        Assert.DoesNotContain("1. e5", movetext);
    }

    [Fact]
    public void The_kcfg_comment_round_trips_to_the_entitys_config_snapshot()
    {
        var engine = new KiriChessEngine();
        var config = CreateSteal1Config();
        var entity = CreateEntity(config, new List<MoveRecord>
        {
            new(1, "e2e4", Side.White, 179, 180, -1, 0, "Ongoing"),
        });

        var pgn = PgnBuilder.Build(engine, entity, "Ada", "Bo", null, null, null);

        // Pulled out with a regex, the same way a third-party reader would have to: nothing in this
        // codebase parses PGN back (README.md, "Out of scope"), so this is the whole reader half of
        // the format, existing only as this assertion.
        var match = Regex.Match(pgn, @"\[%kcfg ([^\]]*)\]");
        Assert.True(match.Success, "No %kcfg comment found in the exported PGN.");

        var dictionary = match.Groups[1].Value
            .Split(';')
            .Select(entry =>
            {
                var eq = entry.IndexOf('=');
                return (Key: entry[..eq], Value: entry[(eq + 1)..]);
            })
            .ToDictionary(p => p.Key, p => p.Value);

        var reconstructed = KiriChessConfig.FromDictionary(dictionary);

        // This is the load-bearing test of the whole phase: it is what proves an exported file is
        // enough to reconstruct the economy, not merely enough to look plausible.
        Assert.Equal(entity.ConfigSnapshot.StartTimeSeconds, reconstructed.StartTimeSeconds);
        Assert.Equal(entity.ConfigSnapshot.BaseIncrement, reconstructed.BaseIncrement);
        Assert.Equal(entity.ConfigSnapshot.EconomyScale, reconstructed.EconomyScale);
        Assert.Equal(entity.ConfigSnapshot.PieceValues, reconstructed.PieceValues);
        Assert.Equal(entity.ConfigSnapshot.CaptureMult, reconstructed.CaptureMult);
        Assert.Equal(entity.ConfigSnapshot.CaptureCaptorGainEnabled, reconstructed.CaptureCaptorGainEnabled);
        Assert.Equal(entity.ConfigSnapshot.CaptureVictimLossEnabled, reconstructed.CaptureVictimLossEnabled);
        Assert.Equal(entity.ConfigSnapshot.MoveCostMode, reconstructed.MoveCostMode);
        Assert.Equal(entity.ConfigSnapshot.MoveCostPerPiece, reconstructed.MoveCostPerPiece);
        Assert.Equal(entity.ConfigSnapshot.CastleCostFlat, reconstructed.CastleCostFlat);
        Assert.Equal(entity.ConfigSnapshot.MoveCostFlat, reconstructed.MoveCostFlat);
        Assert.Equal(entity.ConfigSnapshot.MoveCostPerSquare, reconstructed.MoveCostPerSquare);
        Assert.Equal(entity.ConfigSnapshot.RepeatedPositionNullifiesTime, reconstructed.RepeatedPositionNullifiesTime);
        Assert.Equal(entity.ConfigSnapshot.ThreefoldRepetitionIsDraw, reconstructed.ThreefoldRepetitionIsDraw);
        Assert.Equal(entity.ConfigSnapshot.CheckCheckerGain, reconstructed.CheckCheckerGain);
        Assert.Equal(entity.ConfigSnapshot.CheckVictimLoss, reconstructed.CheckVictimLoss);
        Assert.Equal(entity.ConfigSnapshot.DoubleCheckChipMultiplier, reconstructed.DoubleCheckChipMultiplier);
        Assert.Equal(entity.ConfigSnapshot.PromotionCostMult, reconstructed.PromotionCostMult);
        Assert.Equal(entity.ConfigSnapshot.FlagFallLoses, reconstructed.FlagFallLoses);
        Assert.Equal(entity.ConfigSnapshot.FlagVsInsufficientMaterialIsDraw, reconstructed.FlagVsInsufficientMaterialIsDraw);
    }

    [Fact]
    public void A_corrupt_move_log_truncates_instead_of_throwing()
    {
        var engine = new KiriChessEngine();
        var config = CreateSteal1Config();
        // The second "e2e4" is illegal after the first (no white pawn left on e2, and it is Black to
        // move): the stored log and config disagree, a corrupt row, which must truncate, not throw.
        var entity = CreateEntity(config, new List<MoveRecord>
        {
            new(1, "e2e4", Side.White, 179, 180, -1, 0, "Ongoing"),
            new(2, "e2e4", Side.White, 179, 180, -1, 0, "Ongoing"),
        });

        var pgn = PgnBuilder.Build(engine, entity, "Ada", "Bo", null, null, null);
        var movetext = MovetextOf(pgn);

        Assert.Contains("1. e4", movetext);
        Assert.DoesNotContain("2.", movetext);
    }

    /// <summary>Tiny local stand-in for a real <see cref="IMeterFactory"/>, mirroring
    /// KiriChessMetricsTests's own so this file can prove a truncation is actually recorded.</summary>
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
        // C-8-3, PGN half: mirrors GameReplayBuilderTests's identical assertion for the JSON replay.
        var engine = new KiriChessEngine();
        var config = CreateSteal1Config();
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

        var pgn = PgnBuilder.Build(engine, entity, "Ada", "Bo", null, null, null, logger: logger, metrics: metrics);
        var movetext = MovetextOf(pgn);

        Assert.DoesNotContain("2.", movetext);
        Assert.Contains(logProvider.Messages, m => m.Contains(entity.Id.ToString()) && m.Contains("truncated"));
        Assert.Contains(measurements, m => m.Name == "kirichess.replay.truncated"
            && m.Tags.Any(t => t.Key == "artifact" && Equals(t.Value, "pgn")));
    }

    [Fact]
    public void A_config_snapshot_that_fails_Validate_is_caught_and_a_headers_only_file_is_returned_instead_of_throwing()
    {
        // C-8-6: mirrors GameReplayBuilderTests's identical assertion. engine.FromFen calls
        // config.Validate() before ChessBoard.LoadFromFen, which throws InvalidOperationException for
        // a jsonb row missing a since-added rule key; this call used to sit outside the corrupt-row
        // guard entirely and 500 a public download permanently.
        var engine = new KiriChessEngine();
        var config = CreateSteal1Config();
        config.MoveCostPerPiece.Remove(PieceType.King);
        var entity = CreateEntity(config, new List<MoveRecord>
        {
            new(1, "e2e4", Side.White, 179, 180, -1, 0, "Ongoing"),
        });

        var pgn = PgnBuilder.Build(engine, entity, "Ada", "Bo", null, null, null);
        var movetext = MovetextOf(pgn);

        Assert.Contains($"[KiriChessId \"{entity.Id}\"]", pgn);
        // ConfigStringOrFallback's placeholder, since ToConfigString() would hit the same missing
        // King key ToKeyValues indexes directly (no ContainsKey guard there, unlike Validate).
        Assert.Contains("{ [%kcfg CORRUPT=true] }", movetext);
        Assert.EndsWith(ResultTokenFor(entity), movetext);
    }

    private static string ResultTokenFor(Game entity) => entity.Result switch
    {
        GameResult.WhiteWins => "1-0",
        GameResult.BlackWins => "0-1",
        GameResult.Draw => "1/2-1/2",
        _ => "*"
    };

    [Fact]
    public void The_clk_comment_uses_the_stored_Mover_field_not_the_FEN_alternation()
    {
        // C-8-8: Ply.Mover is captured from MoveRecord.Mover and then never read - the movetext used
        // to derive whose clock to print from isBlackToMove, seeded from the FEN and toggled per ply.
        // Stamping Mover as Black on White's actual first move proves %clk now follows the stored
        // field: MoveRecord.Mover is metadata the engine's move-legality check never consults, so this
        // move still replays correctly even though the label disagrees with the true side to move.
        var engine = new KiriChessEngine();
        var config = CreateSteal1Config();
        var entity = CreateEntity(config, new List<MoveRecord>
        {
            new(1, "e2e4", Side.Black, 179, 42, -1, 0, "Ongoing"),
        });

        var pgn = PgnBuilder.Build(engine, entity, "Ada", "Bo", null, null, null);
        var movetext = MovetextOf(pgn);

        Assert.Contains("[%clk 0:00:42]", movetext); // BlackTimeAfter, because Mover says Black
        Assert.DoesNotContain("[%clk 0:02:59]", movetext); // WhiteTimeAfter (179s), the old behaviour
    }

    [Fact]
    public void An_empty_move_list_still_produces_a_valid_file()
    {
        var engine = new KiriChessEngine();
        var config = CreateSteal1Config();
        var entity = CreateEntity(config, new List<MoveRecord>(), GameResult.Draw, TerminationReason.DrawByAgreement);

        var pgn = PgnBuilder.Build(engine, entity, "Ada", "Bo", null, null, null);
        var movetext = MovetextOf(pgn);

        Assert.Contains("[KiriChessFormat \"1\"]", pgn);
        Assert.StartsWith("{ [%kcfg ", movetext);
        Assert.EndsWith("1/2-1/2", movetext);
    }

    [Fact]
    public void No_line_except_a_kcfg_line_exceeds_80_characters_and_there_is_no_carriage_return()
    {
        var engine = new KiriChessEngine();
        var config = CreateSteal1Config();
        var afterE4 = engine.Apply(engine.NewGame(config), Move.Parse("e2e4"), config, 0);
        var entity = CreateEntity(config, new List<MoveRecord>
        {
            new(1, "e2e4", Side.White, 179, 180, afterE4.WhiteTimeDelta, 0, "Ongoing"),
        });

        var pgn = PgnBuilder.Build(engine, entity, "Ada", "Bo", 1500, 1400, "https://kirichess.app");

        Assert.DoesNotContain("\r", pgn);
        foreach (var line in pgn.Split('\n'))
        {
            if (line.Contains("%kcfg")) continue;
            Assert.True(line.Length <= 80, $"Line exceeds 80 characters: '{line}'");
        }
    }

    [Fact]
    public void A_display_name_containing_quote_and_backslash_is_escaped()
    {
        var engine = new KiriChessEngine();
        var config = CreateSteal1Config();
        var entity = CreateEntity(config, new List<MoveRecord>());

        var pgn = PgnBuilder.Build(engine, entity, "Ada \"The\\Rook\" Smith", "Bo", null, null, null);

        Assert.Contains("[White \"Ada \\\"The\\\\Rook\\\" Smith\"]", pgn);

        var whiteLine = pgn.Split('\n').Single(l => l.StartsWith("[White "));
        // Exactly two quote characters are not preceded by a backslash: the delimiters that open
        // and close the tag value. Every other quote in the value has been escaped.
        var unescapedQuotes = Regex.Matches(whiteLine, "(?<!\\\\)\"").Count;
        Assert.Equal(2, unescapedQuotes);
    }
}
