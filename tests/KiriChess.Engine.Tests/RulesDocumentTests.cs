using System.IO;
using System.Linq;
using System.Text.Json;
using KiriChess.Engine.Config;
using KiriChess.Engine.Core;

namespace KiriChess.Engine.Tests;

/// <summary>
/// Guards the Phase 8.6 single-source-of-truth contract: <c>config/kirichess.rules.json</c> is the
/// only place a rule variable is written down, and the engine runs exactly what it says.
/// </summary>
public class RulesDocumentTests
{
    /// <summary>Walks up from the test binary to the repo root (the folder holding KiriChess.slnx).</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "KiriChess.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string OnDiskConfigPath() =>
        Path.Combine(RepoRoot(), "config", "kirichess.rules.json");

    [Fact]
    public void EmbeddedConfig_MatchesTheFileInTheRepo()
    {
        // If the csproj ever stops embedding the config, the engine would silently keep running an
        // older rulebook compiled into the assembly. This is the test that catches that.
        var onDisk = RulesDocument.Parse(File.ReadAllText(OnDiskConfigPath()));
        var embedded = RulesDocument.Instance;

        Assert.Equal(onDisk.SchemaVersion, embedded.SchemaVersion);
        Assert.Equal(onDisk.PoolKeys, embedded.PoolKeys);

        foreach (var key in onDisk.PoolKeys)
        {
            var expected = onDisk.ToConfigs()[key];
            var actual = embedded.ToConfigs()[key];
            AssertSameConfig(expected, actual, $"pool '{key}'");
        }
    }

    [Fact]
    public void Presets_ComeFromTheConfig_NotFromCode()
    {
        var doc = RulesDocument.Parse(File.ReadAllText(OnDiskConfigPath()));

        Assert.Equal(doc.PoolKeys.Count, KiriChessConfig.Presets.Count);
        foreach (var pool in doc.Pools)
        {
            Assert.True(KiriChessConfig.Presets.ContainsKey(pool.Key), $"Preset '{pool.Key}' is missing.");
            AssertSameConfig(doc.ToConfig(pool), KiriChessConfig.Presets[pool.Key], $"pool '{pool.Key}'");
        }
    }

    [Fact]
    public void DefaultConfig_MatchesTheConfigDefaults()
    {
        var doc = RulesDocument.Parse(File.ReadAllText(OnDiskConfigPath()));
        AssertSameConfig(doc.ToDefaultConfig(), new KiriChessConfig(), "defaults");
    }

    [Fact]
    public void RatedPoolKeys_AreDeclaredInTheConfig()
    {
        var doc = RulesDocument.Parse(File.ReadAllText(OnDiskConfigPath()));
        Assert.Equal(doc.RatedPoolKeys, KiriChessConfig.RatedPoolKeys);
        Assert.NotEmpty(KiriChessConfig.RatedPoolKeys);
    }

    /// <summary>
    /// The Second Edition's defining property: a rated pool scales captures only, so the toll table
    /// and the check chip are identical everywhere. This is what makes the manual's check table and
    /// conversion bill universal, if a pool ever sets <c>economyScale</c> above 1, they aren't.
    /// </summary>
    [Fact]
    public void RatedPools_ScaleCapturesOnly()
    {
        var reference = KiriChessConfig.Presets[KiriChessConfig.RatedPoolKeys[0]];

        foreach (var key in KiriChessConfig.RatedPoolKeys)
        {
            var config = KiriChessConfig.Presets[key];

            Assert.True(
                config.EconomyScale == 1,
                $"Pool '{key}' has EconomyScale {config.EconomyScale}. Rated pools must scale captures via " +
                "CaptureMult only (C-mode); EconomyScale also multiplies tolls, chips and promotion (F-mode), " +
                "which breaks the universal check table and conversion bill the rules page documents.");

            Assert.Equal(reference.MoveCostPerPiece, config.MoveCostPerPiece);
            Assert.Equal(reference.CastleCostFlat, config.CastleCostFlat);
            Assert.Equal(reference.CheckCheckerGain, config.CheckCheckerGain);
            Assert.Equal(reference.CheckVictimLoss, config.CheckVictimLoss);
        }
    }

    /// <summary>
    /// Promotion is free in the Second Edition, the pawn already paid its march tolls and the new
    /// queen is priced by Rule 1. The fee survives only as a custom-game dial.
    /// </summary>
    [Fact]
    public void RatedPools_DoNotChargeForPromotion()
    {
        foreach (var key in KiriChessConfig.RatedPoolKeys)
            Assert.Equal(0, KiriChessConfig.Presets[key].PromotionCostMult);
    }

    [Fact]
    public void EveryPool_HasAUniqueKeyAndAValidConfig()
    {
        var keys = KiriChessConfig.PresetOrder;
        Assert.Equal(keys.Distinct(StringComparer.OrdinalIgnoreCase).Count(), keys.Count);

        foreach (var key in keys)
        {
            var config = KiriChessConfig.Presets[key];
            config.Validate();
            Assert.True(config.StartTimeSeconds > 0, $"Pool '{key}' has a non-positive clock.");
        }
    }

    [Fact]
    public void Presets_AreClonedBeforeUse_SoTheSharedCopyIsNeverMutated()
    {
        var before = KiriChessConfig.Presets["steal1"].StartTimeSeconds;

        var mine = KiriChessConfig.CreateSteal1();
        mine.StartTimeSeconds = 1;
        mine.MoveCostPerPiece[PieceType.Queen] = 99;

        Assert.Equal(before, KiriChessConfig.Presets["steal1"].StartTimeSeconds);
        Assert.NotEqual(99, KiriChessConfig.Presets["steal1"].MoveCostPerPiece[PieceType.Queen]);

        // C-3-4: the name of this test claims the property for every route into a preset, but it
        // only ever exercised the CreateSteal1() factory. FromDictionary's baseConfig parameter is
        // the other route, and it took the caller's object by reference and wrote ApplyKey straight
        // into it, dictionaries included. The obvious future caller is already designed: ToKeyValues
        // documents %kcfg as the PGN round-trip vocabulary, and an importer reading a pool's config
        // back would naturally reach for FromDictionary(kcfg, Presets[pool]).
        var beforePawnValue = KiriChessConfig.Presets["steal1"].PieceValues[PieceType.Pawn];
        var derived = KiriChessConfig.FromDictionary(
            new Dictionary<string, string>
            {
                ["CLOCK_START_SECONDS"] = "7",
                ["PIECE_TIME_VALUES"] = "9,9,9,9,9"
            },
            KiriChessConfig.Presets["steal1"]);

        Assert.Equal(7, derived.StartTimeSeconds);
        Assert.Equal(9, derived.PieceValues[PieceType.Pawn]);
        Assert.NotSame(KiriChessConfig.Presets["steal1"], derived);
        Assert.Equal(before, KiriChessConfig.Presets["steal1"].StartTimeSeconds);
        Assert.Equal(beforePawnValue, KiriChessConfig.Presets["steal1"].PieceValues[PieceType.Pawn]);
    }

    /// <summary>
    /// C-1-5: the preset table itself, not just the configs in it. It used to be a public static
    /// <c>Dictionary</c>, so any code in the server, the simulator or a sibling test could add a
    /// pool, remove one, or point an existing pool key at a different rule set, and every game
    /// created afterwards in that process would run under the replacement with nothing thrown and
    /// nothing logged. Asserted through a cast to <see cref="IDictionary{TKey, TValue}"/> because
    /// that is the loophole a read-only declared type on a still-mutable dictionary would leave
    /// open: the wrapper has to refuse the write, not merely fail to offer it.
    /// </summary>
    [Fact]
    public void Presets_CannotBeMutatedThroughTheDictionary()
    {
        Assert.IsNotType<Dictionary<string, KiriChessConfig>>(KiriChessConfig.Presets);

        var asMutable = KiriChessConfig.Presets as IDictionary<string, KiriChessConfig>;
        Assert.NotNull(asMutable);

        // Order is load-bearing. The Add of a throwaway key comes first, so on a red run (a real
        // Dictionary, which accepts it) Assert.Throws fails right there and the two destructive
        // assertions below - which would delete or replace the steal1 preset every other test in
        // this assembly reads - are never reached.
        const string intruder = "c-1-5-intruder";
        try
        {
            Assert.Throws<NotSupportedException>(() => asMutable!.Add(intruder, new KiriChessConfig()));
            Assert.Throws<NotSupportedException>(() => asMutable!.Remove("steal1"));
            Assert.Throws<NotSupportedException>(() => asMutable!["steal1"] = new KiriChessConfig());
        }
        finally
        {
            // Only reachable while this guard is red: a mutable table would really have taken the
            // Add above, and every later test in this assembly would then see the extra pool.
            try { asMutable!.Remove(intruder); } catch (NotSupportedException) { }
        }

        Assert.DoesNotContain(intruder, KiriChessConfig.Presets.Keys);
    }

    [Fact]
    public void Parse_RejectsAnUnknownMoveCostMode()
    {
        var json = """
        {
          "schemaVersion": 1,
          "defaults": { "moveCostMode": "Telepathy" },
          "pools": [ { "key": "x", "label": "X", "rated": false, "overrides": {} } ]
        }
        """;
        var ex = Assert.Throws<InvalidOperationException>(() => RulesDocument.Parse(json));
        Assert.Contains("Telepathy", ex.Message);
    }

    [Fact]
    public void Parse_RejectsDuplicatePoolKeys()
    {
        var json = """
        {
          "schemaVersion": 1,
          "defaults": {},
          "pools": [
            { "key": "dup", "label": "A", "rated": true, "overrides": {} },
            { "key": "dup", "label": "B", "rated": true, "overrides": {} }
          ]
        }
        """;
        var ex = Assert.Throws<InvalidOperationException>(() => RulesDocument.Parse(json));
        Assert.Contains("dup", ex.Message);
    }

    [Fact]
    public void Parse_RejectsAPoolWhoseOverridesAreInvalid()
    {
        var json = """
        {
          "schemaVersion": 1,
          "defaults": {},
          "pools": [ { "key": "bad", "label": "Bad", "rated": true, "overrides": { "captureMult": -1 } } ]
        }
        """;
        Assert.Throws<InvalidOperationException>(() => RulesDocument.Parse(json));
    }

    /// <summary>
    /// The web UI generates ui/lib/rules.generated.ts from this same JSON. That generator reads the
    /// keys by name, so a rename here silently produces a UI missing a rule. Pin the field names.
    /// </summary>
    [Fact]
    public void ConfigFile_DeclaresEveryTunableRuleVariable()
    {
        using var json = JsonDocument.Parse(File.ReadAllText(OnDiskConfigPath()));
        var defaults = json.RootElement.GetProperty("defaults");

        string[] required =
        [
            "startTimeSeconds", "baseIncrement", "economyScale", "pieceValues", "captureMult",
            "captureCaptorGainEnabled", "captureVictimLossEnabled", "moveCostMode", "moveCostPerPiece",
            "castleCostFlat", "moveCostFlat", "moveCostPerSquare", "checkCheckerGain", "checkVictimLoss",
            "doubleCheckChipMultiplier", "promotionCostMult", "repeatedPositionNullifiesTime",
            "threefoldRepetitionIsDraw", "flagFallLoses", "flagVsInsufficientMaterialIsDraw"
        ];

        foreach (var field in required)
            Assert.True(defaults.TryGetProperty(field, out _), $"config defaults is missing '{field}'.");
    }

    private static void AssertSameConfig(KiriChessConfig expected, KiriChessConfig actual, string because)
    {
        Assert.Equal(expected.StartTimeSeconds, actual.StartTimeSeconds);
        Assert.Equal(expected.BaseIncrement, actual.BaseIncrement);
        Assert.Equal(expected.EconomyScale, actual.EconomyScale);
        Assert.Equal(expected.PieceValues, actual.PieceValues);
        Assert.Equal(expected.CaptureMult, actual.CaptureMult);
        Assert.Equal(expected.CaptureCaptorGainEnabled, actual.CaptureCaptorGainEnabled);
        Assert.Equal(expected.CaptureVictimLossEnabled, actual.CaptureVictimLossEnabled);
        Assert.Equal(expected.MoveCostMode, actual.MoveCostMode);
        Assert.Equal(expected.MoveCostPerPiece, actual.MoveCostPerPiece);
        Assert.Equal(expected.CastleCostFlat, actual.CastleCostFlat);
        Assert.Equal(expected.MoveCostFlat, actual.MoveCostFlat);
        Assert.Equal(expected.MoveCostPerSquare, actual.MoveCostPerSquare);
        Assert.Equal(expected.CheckCheckerGain, actual.CheckCheckerGain);
        Assert.Equal(expected.CheckVictimLoss, actual.CheckVictimLoss);
        Assert.Equal(expected.DoubleCheckChipMultiplier, actual.DoubleCheckChipMultiplier);
        Assert.Equal(expected.PromotionCostMult, actual.PromotionCostMult);
        Assert.Equal(expected.RepeatedPositionNullifiesTime, actual.RepeatedPositionNullifiesTime);
        Assert.Equal(expected.ThreefoldRepetitionIsDraw, actual.ThreefoldRepetitionIsDraw);
        Assert.Equal(expected.FlagFallLoses, actual.FlagFallLoses);
        Assert.Equal(expected.FlagVsInsufficientMaterialIsDraw, actual.FlagVsInsufficientMaterialIsDraw);
        Assert.True(true, because);
    }
}
