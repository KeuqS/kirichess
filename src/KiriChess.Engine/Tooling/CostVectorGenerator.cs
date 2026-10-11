using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using KiriChess.Engine.Config;
using KiriChess.Engine.Core;

namespace KiriChess.Engine.Tooling;

/// <summary>
/// Roadmap 10b.1: golden (position, move) -&gt; economy vectors that pin
/// <see cref="KiriChessEngine.Apply"/>'s deterministic economy output (capture economy, movement
/// toll/castling, promotion cost, and the base increment - the same subset
/// <c>ui/lib/costPreview.ts</c>'s <c>computeCostPreview</c> mirrors, deliberately excluding
/// check-chip amount and repetition nullification). Reused by the <c>--export-cost-vectors</c> CLI mode in
/// <c>KiriChess.Server/Program.cs</c> and by <c>CostVectorConformanceTests</c>, so both agree on the
/// exact output by construction - the same sharing <c>OpenApiExport</c> uses for the OpenAPI document
/// (Phase 9d.7).
/// </summary>
public static class CostVectorGenerator
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly string[] Comment =
    [
        "Golden (position, move) -> economy vectors for Phase 10b.1: conformance between",
        "KiriChessEngine.ApplyEconomy (C#) and computeCostPreview (ui/lib/costPreview.ts).",
        "",
        "Generated, never hand-edited. Regenerate after any change to the economy rules:",
        "  dotnet run --project src/KiriChess.Server -- --export-cost-vectors config/cost-vectors.generated.json",
        "",
        "Consumed by tests/KiriChess.Engine.Tests/CostVectorConformanceTests.cs (guards this file is",
        "fresh relative to the engine) and by ui/lib/costPreview.test.ts (replays every vector through",
        "computeCostPreview and asserts it agrees).",
    ];

    // Fixed rather than derived from the pool key's own hash: string.GetHashCode is randomized per
    // process in .NET, so a derived seed would make two regeneration runs on two machines produce two
    // different (and equally valid) files - the exact non-determinism a "generated, checked-in,
    // CI-gated" artifact cannot tolerate.
    private static readonly Dictionary<string, int> SelfPlaySeeds = new()
    {
        ["bounty1"] = 101,
        ["burn1"] = 202,
        ["steal2"] = 303,
        ["steal1"] = 404,
        ["burn2"] = 505,
        ["steal3"] = 606,
    };

    private const int PliesPerPreset = 24;

    public static IReadOnlyList<VectorJson> GenerateVectors()
    {
        var engine = new KiriChessEngine();
        var vectors = new List<VectorJson>();

        // Self-play through every rated preset: broad, generated coverage of ordinary play, rather
        // than only the handful of positions a person would think to hand-write.
        foreach (var presetKey in KiriChessConfig.PresetOrder)
        {
            var config = KiriChessConfig.Presets[presetKey].Clone();
            var seed = SelfPlaySeeds.GetValueOrDefault(presetKey, 1);
            SelfPlay(engine, config, presetKey, seed, PliesPerPreset, vectors);
        }

        AddSyntheticVectors(engine, vectors);

        return vectors;
    }

    private static void SelfPlay(
        KiriChessEngine engine,
        KiriChessConfig config,
        string presetKey,
        int seed,
        int plies,
        List<VectorJson> vectors)
    {
        var rng = new Random(seed);
        var position = engine.NewGame(config);

        for (var ply = 0; ply < plies; ply++)
        {
            // Sorted before indexing: the underlying move generator's own return order is not a
            // guaranteed-stable thing to seed a reproducible RNG pick against (confirmed empirically -
            // two runs of the same seed against the same position picked different moves), and an
            // artifact that is supposed to regenerate byte-identical on every machine cannot leave that
            // order unpinned.
            var legal = engine.LegalMoves(position)
                .OrderBy(m => m.ToString(), StringComparer.Ordinal)
                .ToList();
            if (legal.Count == 0) break;

            var move = legal[rng.Next(legal.Count)];
            vectors.Add(BuildVector(engine, config, $"{presetKey} self-play, ply {ply + 1}", position.Fen, move));

            var applied = engine.Apply(position, move, config);
            position = applied.NewPosition;
            if (applied.IsGameOver) break;
        }
    }

    // The six rated pools all share MoveCostMode=PerPiece, PromotionCostMult=0 and BaseIncrement=0
    // (see config/kirichess.rules.json), so self-play alone never exercises the other four toll
    // modes, a nonzero promotion cost, a nonzero base increment, castling under MoveCostMode=None,
    // or the "check chip not configured" / "capture economy fully off" edges computeCostPreview.ts
    // still has to answer correctly. Each plays exactly one curated move so the formula under test
    // is unambiguous from the label alone.
    private static void AddSyntheticVectors(KiriChessEngine engine, List<VectorJson> vectors)
    {
        var baseConfig = KiriChessConfig.Presets["steal1"].Clone();

        const string startFen = KiriChessEngine.StartFen;
        // The standard "both sides can castle both ways" test position (perft position 2's rook/king
        // placement), used here only for its castling rights, not for any tactics.
        const string castleFen = "r3k2r/8/8/8/8/8/8/R3K2R w KQkq - 0 1";
        // A lone pawn one step from promoting, with nothing else on the board to complicate it.
        const string promoteFen = "8/P6k/8/8/8/8/7K/8 w - - 0 1";
        // White e5 pawn, black just played d7-d5: e5xd6 is a legal en passant capture.
        const string enPassantFen = "4k3/8/8/3pP3/8/8/8/4K3 w - d6 0 1";
        // tests/KiriChess.Engine.Tests/Rule1CaptureEconomyTests.cs's own PawnTradeFen: e4xd5.
        const string pawnTradeFen = "4k3/8/2p5/3p4/4P3/8/8/4K3 w - - 0 1";

        var noneConfig = baseConfig.Clone();
        noneConfig.MoveCostMode = MoveCostMode.None;
        vectors.Add(BuildVector(engine, noneConfig,
            "synthetic: moveCostMode=None charges no movement toll", startFen, new Move("g1", "f3")));
        vectors.Add(BuildVector(engine, noneConfig,
            "synthetic: moveCostMode=None also waives the castling fee", castleFen, new Move("e1", "g1")));

        var flatConfig = baseConfig.Clone();
        flatConfig.MoveCostMode = MoveCostMode.Flat;
        flatConfig.MoveCostFlat = 7;
        vectors.Add(BuildVector(engine, flatConfig,
            "synthetic: moveCostMode=Flat charges moveCostFlat regardless of piece or distance",
            startFen, new Move("g1", "f3")));

        var distanceConfig = baseConfig.Clone();
        distanceConfig.MoveCostMode = MoveCostMode.Distance;
        distanceConfig.MoveCostPerSquare = 2;
        vectors.Add(BuildVector(engine, distanceConfig,
            "synthetic: moveCostMode=Distance charges moveCostPerSquare * chebyshev distance",
            startFen, new Move("d2", "d4")));

        var perPieceXDistanceConfig = baseConfig.Clone();
        perPieceXDistanceConfig.MoveCostMode = MoveCostMode.PerPieceXDistance;
        vectors.Add(BuildVector(engine, perPieceXDistanceConfig,
            "synthetic: moveCostMode=PerPieceXDistance charges moveCostPerPiece[piece] * distance",
            startFen, new Move("d2", "d4")));

        var promoConfig = baseConfig.Clone();
        promoConfig.PromotionCostMult = 3;
        vectors.Add(BuildVector(engine, promoConfig,
            "synthetic: promotion to queen prices promotionCostMult * queen's piece value",
            promoteFen, new Move("a7", "a8", PieceType.Queen)));
        vectors.Add(BuildVector(engine, promoConfig,
            "synthetic: under-promotion to knight prices the promoted piece, not a queen",
            promoteFen, new Move("a7", "a8", PieceType.Knight)));

        vectors.Add(BuildVector(engine, baseConfig,
            "synthetic: en passant is priced as a capture of the passed pawn",
            enPassantFen, new Move("e5", "d6")));

        var noCheckChipConfig = baseConfig.Clone();
        noCheckChipConfig.CheckCheckerGain = 0;
        noCheckChipConfig.CheckVictimLoss = 0;
        vectors.Add(BuildVector(engine, noCheckChipConfig,
            "synthetic: hasCheckChip is false when both check amounts are zero",
            startFen, new Move("g1", "f3")));

        var captureFlagsOffConfig = baseConfig.Clone();
        captureFlagsOffConfig.CaptureCaptorGainEnabled = false;
        captureFlagsOffConfig.CaptureVictimLossEnabled = false;
        vectors.Add(BuildVector(engine, captureFlagsOffConfig,
            "synthetic: capture and victimType are still reported when both capture-economy flags are off",
            pawnTradeFen, new Move("e4", "d5")));

        // Rated pools leave BaseIncrement at 0, so without this vector the golden file would
        // carry the field and still never count it. The increment is whole seconds, not scaled.
        var incrementConfig = baseConfig.Clone();
        incrementConfig.BaseIncrement = 3;
        vectors.Add(BuildVector(engine, incrementConfig,
            "synthetic: base increment is credited on top of the movement toll",
            startFen, new Move("g1", "f3")));

        // C-10-7: every rule variable simultaneously at the ceiling KiriChessConfig.Validate() now
        // enforces, on the most expensive shape of move (a capture that also pays a distance-scaled
        // toll). Two things are pinned at once. The config passes Validate(), because BuildVector
        // goes through FromFen, so the ceilings admit their own maximum rather than sitting one off
        // it. And the priced result is an ordinary five-digit number rather than a wrapped negative,
        // which is the failure the ceilings exist to prevent, checked against computeCostPreview.ts
        // as well as against the engine.
        var nearCeilingConfig = baseConfig.Clone();
        nearCeilingConfig.StartTimeSeconds = 7200;
        nearCeilingConfig.EconomyScale = KiriChessConfig.MaxEconomyScale;
        nearCeilingConfig.CaptureMult = KiriChessConfig.MaxCaptureMult;
        nearCeilingConfig.CaptureCaptorGainEnabled = true;
        nearCeilingConfig.CaptureVictimLossEnabled = true;
        nearCeilingConfig.MoveCostMode = MoveCostMode.PerPieceXDistance;
        nearCeilingConfig.CastleCostFlat = KiriChessConfig.MaxMoveCost;
        nearCeilingConfig.MoveCostFlat = KiriChessConfig.MaxMoveCost;
        nearCeilingConfig.MoveCostPerSquare = KiriChessConfig.MaxMoveCost;
        nearCeilingConfig.CheckCheckerGain = KiriChessConfig.MaxCheckChip;
        nearCeilingConfig.CheckVictimLoss = KiriChessConfig.MaxCheckChip;
        nearCeilingConfig.DoubleCheckChipMultiplier = KiriChessConfig.MaxDoubleCheckChipMultiplier;
        nearCeilingConfig.PromotionCostMult = KiriChessConfig.MaxPromotionCostMult;
        foreach (var piece in nearCeilingConfig.PieceValues.Keys.ToList())
            nearCeilingConfig.PieceValues[piece] = KiriChessConfig.MaxPieceValue;
        foreach (var piece in nearCeilingConfig.MoveCostPerPiece.Keys.ToList())
            nearCeilingConfig.MoveCostPerPiece[piece] = KiriChessConfig.MaxMoveCost;
        vectors.Add(BuildVector(engine, nearCeilingConfig,
            "synthetic: a config at every ceiling still prices a capture inside int",
            pawnTradeFen, new Move("e4", "d5")));
    }

    private static VectorJson BuildVector(
        KiriChessEngine engine, KiriChessConfig config, string label, string fen, Move move)
    {
        // Capture / victim-type detection must not depend on the config's own capture-economy flags
        // (computeCostPreview.ts reports them from board occupancy alone, see its own doc comment), so
        // it is probed through a scratch config with both flags force-enabled and a guaranteed nonzero
        // amount, never read off the real config's Apply result.
        var probeConfig = config.Clone();
        probeConfig.CaptureCaptorGainEnabled = true;
        probeConfig.CaptureVictimLossEnabled = true;
        probeConfig.CaptureMult = 1;
        foreach (var piece in probeConfig.PieceValues.Keys.ToList())
            probeConfig.PieceValues[piece] = 1;

        var probeResult = engine.Apply(engine.FromFen(probeConfig, fen), move, probeConfig);
        var captureEvent = probeResult.Events.FirstOrDefault(
            e => e.Type is ClockEventType.CaptureGain or ClockEventType.CaptureLoss);
        var capture = captureEvent is not null;
        var victimType = captureEvent?.Detail?.ToLowerInvariant();

        // The real amounts, from a position seeded fresh (RepetitionCounts holding only this one FEN)
        // so Rule 4 can never nullify a vector's economy: computeCostPreview.ts has no game history to
        // nullify against and would never agree with a nullified vector.
        var fresh = engine.FromFen(config, fen);
        var result = engine.Apply(fresh, move, config);

        var moverNet = result.Events
            .Where(e => e.Side == fresh.SideToMove && e.Type is ClockEventType.CaptureGain
                or ClockEventType.MovementToll or ClockEventType.CastleCost or ClockEventType.PromotionCost
                or ClockEventType.Increment)
            .Sum(e => e.Amount);

        var captureLossEvent = result.Events.FirstOrDefault(e => e.Type == ClockEventType.CaptureLoss);
        var opponentLoss = captureLossEvent is null ? 0 : -captureLossEvent.Amount;

        var hasCheckChip = config.CheckCheckerGain > 0 || config.CheckVictimLoss > 0;

        return new VectorJson(
            label,
            fen,
            move.From,
            move.To,
            PromotionChar(move.Promotion),
            config.ToEconomyJson(),
            new ExpectedJson(moverNet, capture, victimType, opponentLoss, hasCheckChip));
    }

    private static string? PromotionChar(PieceType? promotion) => promotion switch
    {
        null => null,
        PieceType.Queen => "q",
        PieceType.Rook => "r",
        PieceType.Bishop => "b",
        PieceType.Knight => "n",
        _ => throw new InvalidOperationException($"{promotion} is not a valid promotion piece."),
    };

    private static EconomyJson ToEconomyJson(this KiriChessConfig c) => new(
        c.StartTimeSeconds,
        c.EconomyScale,
        new PieceInts5(
            c.PieceValues[PieceType.Pawn],
            c.PieceValues[PieceType.Knight],
            c.PieceValues[PieceType.Bishop],
            c.PieceValues[PieceType.Rook],
            c.PieceValues[PieceType.Queen]),
        c.CaptureMult,
        c.CaptureCaptorGainEnabled,
        c.CaptureVictimLossEnabled,
        c.MoveCostMode.ToString(),
        new PieceInts6(
            c.MoveCostPerPiece[PieceType.Pawn],
            c.MoveCostPerPiece[PieceType.Knight],
            c.MoveCostPerPiece[PieceType.Bishop],
            c.MoveCostPerPiece[PieceType.Rook],
            c.MoveCostPerPiece[PieceType.Queen],
            c.MoveCostPerPiece[PieceType.King]),
        c.CastleCostFlat,
        c.MoveCostFlat,
        c.MoveCostPerSquare,
        c.PromotionCostMult,
        c.CheckCheckerGain,
        c.CheckVictimLoss,
        c.DoubleCheckChipMultiplier,
        c.BaseIncrement,
        c.RepeatedPositionNullifiesTime,
        c.ThreefoldRepetitionIsDraw,
        c.FlagFallLoses,
        c.FlagVsInsufficientMaterialIsDraw);

    /// <summary>The committed JSON text: deterministic key order, LF line endings, one trailing
    /// newline, matching the convention <c>OpenApiExport.SerializeAsync</c> uses for the same
    /// reason.</summary>
    public static string Serialize(IReadOnlyList<VectorJson> vectors)
    {
        var file = new CostVectorFile(Comment, vectors);
        var json = JsonSerializer.Serialize(file, JsonOptions);
        return json.Replace("\r\n", "\n").TrimEnd('\n') + "\n";
    }

    /// <summary>
    /// Writes <see cref="GenerateVectors"/>'s output to <paramref name="path"/>. A relative path is
    /// resolved against the repository root (<see cref="FindRepoRoot"/>), not
    /// <see cref="Environment.CurrentDirectory"/> - see <c>OpenApiExport.WriteAsync</c>'s doc comment
    /// for why that distinction matters under <c>dotnet run --project</c>.
    /// </summary>
    public static async Task WriteAsync(string path)
    {
        var json = Serialize(GenerateVectors());
        var resolvedPath = Path.IsPathRooted(path) ? path : Path.Combine(FindRepoRoot(), path);
        var directory = Path.GetDirectoryName(Path.GetFullPath(resolvedPath));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        // `path` is a CLI argument the developer running this generator supplies directly (see
        // OpenApiExport.WriteAsync's identical comment) - not data from a request, so there is no
        // path-traversal actor here to sanitize `Path.GetFileName` against.
        // nosemgrep: csharp.lang.security.filesystem.unsafe-path-combine.unsafe-path-combine
        await File.WriteAllTextAsync(resolvedPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>Walks up from <see cref="AppContext.BaseDirectory"/> until a directory containing
    /// <c>KiriChess.slnx</c> is found. A private copy of <c>OpenApiExport.FindRepoRoot</c>'s logic
    /// rather than a shared reference to it: this project has no dependency on KiriChess.Server (and
    /// must not gain one just for a five-line path helper), so <c>CostVectorConformanceTests</c> in
    /// KiriChess.Engine.Tests can call this without pulling in the web host.</summary>
    public static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "KiriChess.slnx")))
            dir = dir.Parent;

        return dir?.FullName
            ?? throw new InvalidOperationException(
                $"Could not locate KiriChess.slnx by walking up from {AppContext.BaseDirectory}.");
    }
}
