using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using KiriChess.Engine.Core;

namespace KiriChess.Engine.Config;

/// <summary>
/// The parsed <c>config/kirichess.rules.json</c>, the single source of truth for every rule
/// variable in KiriChess. It is embedded into this assembly at build time, so the engine always
/// carries the exact rulebook that is in the repo, and the web UI generates its own copy from the
/// same file (<c>ui/lib/rules.generated.ts</c>). Neither side hardcodes a rule number.
/// </summary>
public sealed class RulesDocument
{
    private const string ResourceName = "KiriChess.Engine.kirichess.rules.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; init; }
    [JsonPropertyName("edition")] public string Edition { get; init; } = "";
    [JsonPropertyName("defaults")] public RuleValues Defaults { get; init; } = new();
    [JsonPropertyName("pools")] public IReadOnlyList<PoolDefinition> Pools { get; init; } = Array.Empty<PoolDefinition>();

    /// <summary>The embedded rulebook, parsed once per process.</summary>
    public static RulesDocument Instance { get; } = LoadEmbedded();

    private static RulesDocument LoadEmbedded()
    {
        using var stream = typeof(RulesDocument).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded rules config '{ResourceName}' is missing. The build must embed config/kirichess.rules.json.");
        return Parse(stream);
    }

    /// <summary>Parses a rules document from a stream. Exposed so tests can read the on-disk file directly.</summary>
    public static RulesDocument Parse(Stream stream)
    {
        var doc = JsonSerializer.Deserialize<RulesDocument>(stream, JsonOptions)
            ?? throw new InvalidOperationException("Rules config deserialized to null.");
        doc.Validate();
        return doc;
    }

    /// <inheritdoc cref="Parse(Stream)"/>
    public static RulesDocument Parse(string json)
    {
        var doc = JsonSerializer.Deserialize<RulesDocument>(json, JsonOptions)
            ?? throw new InvalidOperationException("Rules config deserialized to null.");
        doc.Validate();
        return doc;
    }

    private void Validate()
    {
        if (SchemaVersion <= 0)
            throw new InvalidOperationException("Rules config is missing 'schemaVersion'.");
        if (Pools.Count == 0)
            throw new InvalidOperationException("Rules config declares no pools.");

        var duplicates = Pools.GroupBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToArray();
        if (duplicates.Length > 0)
            throw new InvalidOperationException($"Rules config has duplicate pool keys: {string.Join(", ", duplicates)}");

        foreach (var pool in Pools)
        {
            if (string.IsNullOrWhiteSpace(pool.Key))
                throw new InvalidOperationException("Rules config has a pool with no key.");
            // Surfaces a bad override (negative toll, unknown MoveCostMode, ...) at startup rather
            // than at the first move of the first game played in that pool.
            ToConfig(pool).Validate();
        }
    }

    /// <summary>Pool keys in the order the config declares them (the order the UI presents them in).</summary>
    public IReadOnlyList<string> PoolKeys => Pools.Select(p => p.Key).ToArray();

    /// <summary>Keys of pools that carry a rating. Any other config is unrated.</summary>
    public IReadOnlyList<string> RatedPoolKeys => Pools.Where(p => p.Rated).Select(p => p.Key).ToArray();

    /// <summary>
    /// The engine defaults, the base every pool overrides and the custom builder starts from.
    /// Builds from the blank baseline rather than <c>new KiriChessConfig()</c>: the public
    /// constructor reads <see cref="Instance"/>, which is still initializing when this runs.
    /// </summary>
    public KiriChessConfig ToDefaultConfig() => Defaults.ApplyTo(new KiriChessConfig(applyDefaults: false));

    /// <summary>Builds the full <see cref="KiriChessConfig"/> for one pool (defaults + its overrides).</summary>
    public KiriChessConfig ToConfig(PoolDefinition pool) => pool.Overrides.ApplyTo(ToDefaultConfig());

    /// <summary>Builds every pool's config, keyed by pool key (case-insensitive).</summary>
    public Dictionary<string, KiriChessConfig> ToConfigs()
    {
        var result = new Dictionary<string, KiriChessConfig>(StringComparer.OrdinalIgnoreCase);
        foreach (var pool in Pools)
            result[pool.Key] = ToConfig(pool);
        return result;
    }

    public sealed class PoolDefinition
    {
        [JsonPropertyName("key")] public string Key { get; init; } = "";
        [JsonPropertyName("label")] public string Label { get; init; } = "";
        [JsonPropertyName("rated")] public bool Rated { get; init; }
        [JsonPropertyName("tagline")] public string Tagline { get; init; } = "";
        [JsonPropertyName("overrides")] public RuleValues Overrides { get; init; } = new();
    }

    /// <summary>
    /// A set of rule variables. Every field is nullable so the same type serves both the complete
    /// <c>defaults</c> block and a pool's sparse <c>overrides</c> block: applying a set only writes
    /// the fields it actually declares.
    /// </summary>
    public sealed class RuleValues
    {
        [JsonPropertyName("startTimeSeconds")] public int? StartTimeSeconds { get; init; }
        [JsonPropertyName("baseIncrement")] public int? BaseIncrement { get; init; }
        [JsonPropertyName("economyScale")] public int? EconomyScale { get; init; }
        [JsonPropertyName("pieceValues")] public PieceValueSet? PieceValues { get; init; }
        [JsonPropertyName("captureMult")] public int? CaptureMult { get; init; }
        [JsonPropertyName("captureCaptorGainEnabled")] public bool? CaptureCaptorGainEnabled { get; init; }
        [JsonPropertyName("captureVictimLossEnabled")] public bool? CaptureVictimLossEnabled { get; init; }
        [JsonPropertyName("moveCostMode")] public string? MoveCostMode { get; init; }
        [JsonPropertyName("moveCostPerPiece")] public PieceValueSet? MoveCostPerPiece { get; init; }
        [JsonPropertyName("castleCostFlat")] public int? CastleCostFlat { get; init; }
        [JsonPropertyName("moveCostFlat")] public int? MoveCostFlat { get; init; }
        [JsonPropertyName("moveCostPerSquare")] public int? MoveCostPerSquare { get; init; }
        [JsonPropertyName("checkCheckerGain")] public int? CheckCheckerGain { get; init; }
        [JsonPropertyName("checkVictimLoss")] public int? CheckVictimLoss { get; init; }
        [JsonPropertyName("doubleCheckChipMultiplier")] public int? DoubleCheckChipMultiplier { get; init; }
        [JsonPropertyName("promotionCostMult")] public int? PromotionCostMult { get; init; }
        [JsonPropertyName("repeatedPositionNullifiesTime")] public bool? RepeatedPositionNullifiesTime { get; init; }
        [JsonPropertyName("threefoldRepetitionIsDraw")] public bool? ThreefoldRepetitionIsDraw { get; init; }
        [JsonPropertyName("flagFallLoses")] public bool? FlagFallLoses { get; init; }
        [JsonPropertyName("flagVsInsufficientMaterialIsDraw")] public bool? FlagVsInsufficientMaterialIsDraw { get; init; }

        internal KiriChessConfig ApplyTo(KiriChessConfig target)
        {
            if (StartTimeSeconds is { } startTime) target.StartTimeSeconds = startTime;
            if (BaseIncrement is { } increment) target.BaseIncrement = increment;
            if (EconomyScale is { } scale) target.EconomyScale = scale;
            if (PieceValues is { } values) target.PieceValues = values.ToDictionary(includeKing: false);
            if (CaptureMult is { } captureMult) target.CaptureMult = captureMult;
            if (CaptureCaptorGainEnabled is { } captorGain) target.CaptureCaptorGainEnabled = captorGain;
            if (CaptureVictimLossEnabled is { } victimLoss) target.CaptureVictimLossEnabled = victimLoss;
            if (MoveCostMode is { } mode)
            {
                if (!Enum.TryParse<MoveCostMode>(mode, ignoreCase: true, out var parsed))
                    throw new InvalidOperationException(
                        $"Rules config has unknown moveCostMode '{mode}'. Known: {string.Join(", ", Enum.GetNames<MoveCostMode>())}");
                target.MoveCostMode = parsed;
            }
            if (MoveCostPerPiece is { } costs) target.MoveCostPerPiece = costs.ToDictionary(includeKing: true);
            if (CastleCostFlat is { } castle) target.CastleCostFlat = castle;
            if (MoveCostFlat is { } flat) target.MoveCostFlat = flat;
            if (MoveCostPerSquare is { } perSquare) target.MoveCostPerSquare = perSquare;
            if (CheckCheckerGain is { } checkerGain) target.CheckCheckerGain = checkerGain;
            if (CheckVictimLoss is { } checkLoss) target.CheckVictimLoss = checkLoss;
            if (DoubleCheckChipMultiplier is { } doubleCheck) target.DoubleCheckChipMultiplier = doubleCheck;
            if (PromotionCostMult is { } promotion) target.PromotionCostMult = promotion;
            if (RepeatedPositionNullifiesTime is { } nullifies) target.RepeatedPositionNullifiesTime = nullifies;
            if (ThreefoldRepetitionIsDraw is { } threefold) target.ThreefoldRepetitionIsDraw = threefold;
            if (FlagFallLoses is { } flagFall) target.FlagFallLoses = flagFall;
            if (FlagVsInsufficientMaterialIsDraw is { } insufficient) target.FlagVsInsufficientMaterialIsDraw = insufficient;
            return target;
        }
    }

    /// <summary>A per-piece integer table. <c>king</c> is absent from capture values (a king is never captured).</summary>
    public sealed class PieceValueSet
    {
        [JsonPropertyName("pawn")] public int Pawn { get; init; }
        [JsonPropertyName("knight")] public int Knight { get; init; }
        [JsonPropertyName("bishop")] public int Bishop { get; init; }
        [JsonPropertyName("rook")] public int Rook { get; init; }
        [JsonPropertyName("queen")] public int Queen { get; init; }
        [JsonPropertyName("king")] public int? King { get; init; }

        internal Dictionary<PieceType, int> ToDictionary(bool includeKing)
        {
            var result = new Dictionary<PieceType, int>
            {
                { PieceType.Pawn, Pawn },
                { PieceType.Knight, Knight },
                { PieceType.Bishop, Bishop },
                { PieceType.Rook, Rook },
                { PieceType.Queen, Queen }
            };
            if (includeKing)
            {
                result[PieceType.King] = King
                    ?? throw new InvalidOperationException("Rules config: moveCostPerPiece must include 'king'.");
            }
            return result;
        }
    }
}
