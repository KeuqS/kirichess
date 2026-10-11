using System.IO;
using KiriChess.Engine.Config;
using KiriChess.Engine.Core;

namespace KiriChess.Engine.Tests;

public class ConfigLoadingTests
{
    [Fact]
    public void FromDictionary_OverridesScalarsAndPieceTables()
    {
        var values = new Dictionary<string, string>
        {
            ["CLOCK_START_SECONDS"] = "300",
            ["CAPTURE_MULT"] = "7",
            ["CAPTURE_VICTIM_LOSS_ENABLED"] = "false",
            ["MOVE_COST_MODE"] = "Distance",
            ["MOVE_COST_PER_SQUARE"] = "2",
            ["PIECE_TIME_VALUES"] = "1, 3, 3, 5, 10",
            ["MOVE_COST_PER_PIECE"] = "1, 5, 2, 3, 4, 1",
            ["THREEFOLD_REPETITION_IS_DRAW"] = "false"
        };

        var config = KiriChessConfig.FromDictionary(values);

        Assert.Equal(300, config.StartTimeSeconds);
        Assert.Equal(7, config.CaptureMult);
        Assert.False(config.CaptureVictimLossEnabled);
        Assert.Equal(MoveCostMode.Distance, config.MoveCostMode);
        Assert.Equal(2, config.MoveCostPerSquare);
        Assert.Equal(10, config.PieceValues[PieceType.Queen]);
        Assert.Equal(5, config.MoveCostPerPiece[PieceType.Knight]);
        Assert.False(config.ThreefoldRepetitionIsDraw);
    }

    [Fact]
    public void FromDictionary_CanSelectAndOverrideAPreset()
    {
        var values = new Dictionary<string, string>
        {
            ["PRESET"] = "burn2",
            ["CLOCK_START_SECONDS"] = "120"
        };

        var config = KiriChessConfig.FromDictionary(values);

        // Mainspring = capture gain false, victim loss true, with our override to the start clock on top.
        Assert.False(config.CaptureCaptorGainEnabled);
        Assert.True(config.CaptureVictimLossEnabled);
        Assert.Equal(120, config.StartTimeSeconds);
    }

    [Fact]
    public void UnknownKey_Throws()
    {
        var values = new Dictionary<string, string> { ["NONSENSE_KEY"] = "1" };
        Assert.Throws<InvalidOperationException>(() => KiriChessConfig.FromDictionary(values));
    }

    [Fact]
    public void LoadFromEnvFile_ParsesCommentsAndBlankLines()
    {
        var path = Path.Combine(Path.GetTempPath(), $"kirichess_{Guid.NewGuid():N}.env");
        File.WriteAllText(path,
            "# KiriChess config\n" +
            "\n" +
            "CLOCK_START_SECONDS = 90\n" +
            "CAPTURE_VICTIM_LOSS_ENABLED=false   \n" +
            "MOVE_COST_MODE = \"Flat\"\n");
        try
        {
            var config = KiriChessConfig.LoadFromEnvFile(path);
            Assert.Equal(90, config.StartTimeSeconds);
            Assert.False(config.CaptureVictimLossEnabled);
            Assert.Equal(MoveCostMode.Flat, config.MoveCostMode);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Presets_AreIndependentInstances()
    {
        // Mutating a config derived from a preset must not leak into the shared preset tables.
        // The pawn value was 99 until C-10-7 gave Validate() a ceiling of MaxPieceValue; the number
        // is arbitrary to this test, it only has to be visibly unlike the preset's own 1.
        var a = KiriChessConfig.FromDictionary(new Dictionary<string, string> { ["PRESET"] = "steal1", ["PIECE_TIME_VALUES"] = "49,3,3,5,9" });
        var b = KiriChessConfig.CreateSteal1();
        Assert.Equal(49, a.PieceValues[PieceType.Pawn]);
        Assert.Equal(1, b.PieceValues[PieceType.Pawn]);
    }

    // ------------------------------------------------------------------------------
    // ToKeyValues / ToConfigString (Phase 9e.2): the writer half of the vocabulary this whole file
    // already exercises the reader half of. Every KiriChess PGN's `%kcfg` comment is exactly
    // ToConfigString()'s output, so a round-trip failure here means an exported file can no longer
    // reconstruct the economy it was played under, the load-bearing promise of the whole format.
    // ------------------------------------------------------------------------------

    public static IEnumerable<object[]> PresetKeys =>
        KiriChessConfig.Presets.Keys.Select(k => new object[] { k });

    [Theory]
    [MemberData(nameof(PresetKeys))]
    public void ToConfigString_round_trips_through_FromDictionary_for_every_preset(string presetKey)
    {
        var original = KiriChessConfig.Presets[presetKey];

        // The exact split a third-party reader would do: split on ';' for entries, then on the
        // *first* '=' for KEY=VALUE, since PIECE_TIME_VALUES-style values never contain '=' but the
        // parse must not assume that by using a stricter split.
        var dictionary = original.ToConfigString()
            .Split(';')
            .Select(entry =>
            {
                var eq = entry.IndexOf('=');
                return (Key: entry[..eq], Value: entry[(eq + 1)..]);
            })
            .ToDictionary(p => p.Key, p => p.Value);

        var roundTripped = KiriChessConfig.FromDictionary(dictionary);

        // Field by field, covering every field Clone() copies, so this test rots (fails) the day a
        // new rule variable is added to Clone() without a matching ToKeyValues()/ApplyKey() entry.
        Assert.Equal(original.StartTimeSeconds, roundTripped.StartTimeSeconds);
        Assert.Equal(original.BaseIncrement, roundTripped.BaseIncrement);
        Assert.Equal(original.EconomyScale, roundTripped.EconomyScale);
        Assert.Equal(original.PieceValues, roundTripped.PieceValues);
        Assert.Equal(original.CaptureMult, roundTripped.CaptureMult);
        Assert.Equal(original.CaptureCaptorGainEnabled, roundTripped.CaptureCaptorGainEnabled);
        Assert.Equal(original.CaptureVictimLossEnabled, roundTripped.CaptureVictimLossEnabled);
        Assert.Equal(original.MoveCostMode, roundTripped.MoveCostMode);
        Assert.Equal(original.MoveCostPerPiece, roundTripped.MoveCostPerPiece);
        Assert.Equal(original.CastleCostFlat, roundTripped.CastleCostFlat);
        Assert.Equal(original.MoveCostFlat, roundTripped.MoveCostFlat);
        Assert.Equal(original.MoveCostPerSquare, roundTripped.MoveCostPerSquare);
        Assert.Equal(original.RepeatedPositionNullifiesTime, roundTripped.RepeatedPositionNullifiesTime);
        Assert.Equal(original.ThreefoldRepetitionIsDraw, roundTripped.ThreefoldRepetitionIsDraw);
        Assert.Equal(original.CheckCheckerGain, roundTripped.CheckCheckerGain);
        Assert.Equal(original.CheckVictimLoss, roundTripped.CheckVictimLoss);
        Assert.Equal(original.DoubleCheckChipMultiplier, roundTripped.DoubleCheckChipMultiplier);
        Assert.Equal(original.PromotionCostMult, roundTripped.PromotionCostMult);
        Assert.Equal(original.FlagFallLoses, roundTripped.FlagFallLoses);
        Assert.Equal(original.FlagVsInsufficientMaterialIsDraw, roundTripped.FlagVsInsufficientMaterialIsDraw);
    }

    [Fact]
    public void ToKeyValues_has_one_entry_per_ApplyKey_case()
    {
        // ApplyKey's switch has exactly twenty cases (excluding PRESET, which FromDictionary handles
        // before ApplyKey ever sees it); ToKeyValues must emit exactly the same twenty keys, in the
        // fixed order docs/game/PGN.md documents as an external contract.
        var expectedKeys = new[]
        {
            "CLOCK_START_SECONDS", "BASE_INCREMENT", "ECONOMY_SCALE", "PIECE_TIME_VALUES",
            "CAPTURE_MULT", "CAPTURE_CAPTOR_GAIN_ENABLED", "CAPTURE_VICTIM_LOSS_ENABLED",
            "MOVE_COST_MODE", "MOVE_COST_PER_PIECE", "MOVE_COST_FLAT", "MOVE_COST_PER_SQUARE",
            "CASTLE_COST_FLAT", "CHECK_CHECKER_GAIN", "CHECK_VICTIM_LOSS",
            "DOUBLE_CHECK_CHIP_MULTIPLIER", "PROMOTION_COST_MULT", "REPEATED_POSITION_NULLIFIES_TIME",
            "THREEFOLD_REPETITION_IS_DRAW", "FLAG_FALL_LOSES", "FLAG_VS_INSUFFICIENT_MATERIAL_IS_DRAW",
        };

        var actualKeys = KiriChessConfig.CreateSteal1().ToKeyValues().Select(kv => kv.Key).ToArray();

        Assert.Equal(expectedKeys, actualKeys);
    }

    [Theory]
    [MemberData(nameof(PresetKeys))]
    public void ToConfigString_needs_no_PGN_escaping(string presetKey)
    {
        // What makes this string safe to drop straight into a PGN brace comment with no escaping:
        // pinned as a property, not eyeballed, because a future rule variable that is free text
        // (unlikely, but not impossible) would silently break this the day it is added.
        var configString = KiriChessConfig.Presets[presetKey].ToConfigString();

        Assert.DoesNotContain('"', configString);
        Assert.DoesNotContain('\\', configString);
        Assert.DoesNotContain(' ', configString);
    }

    /// <summary>
    /// C-4-5: the custom-game builder rejects a start time under one second, Validate() accepted
    /// zero, and the two floors are supposed to be the same floor. A zero-second config produces a
    /// game that is already lost on time before either player has a turn, so the engine's own
    /// guarantee that an invalid config "fails loudly at game creation" has to cover it.
    /// </summary>
    [Fact]
    public void Validate_RejectsAZeroStartTime_MatchingTheBuildersFloor()
    {
        var config = TestSupport.AllZeroed();
        config.StartTimeSeconds = 0;

        var ex = Assert.Throws<InvalidOperationException>(() => config.Validate());
        Assert.Contains("StartTimeSeconds", ex.Message);
    }

    // ------------------------------------------------------------------------------
    // Ceilings (C-8-1 / C-10-7). Validate() had floors and no ceilings, so the browser was the only
    // thing keeping a rule variable sane, and since Phase 9d.8 a challenge carries a caller-supplied
    // config to a second player who never saw the builder. The economy multiplies are unchecked int,
    // so an unbounded CaptureMult or piece value sign-flips a clock delta instead of failing loudly.
    // ------------------------------------------------------------------------------

    /// <summary>Every field set to exactly its ceiling, which must pass.</summary>
    private static KiriChessConfig AtEveryCeiling()
    {
        var config = TestSupport.AllZeroed();
        config.StartTimeSeconds = KiriChessConfig.MaxStartTimeSeconds;
        config.BaseIncrement = KiriChessConfig.MaxBaseIncrement;
        config.EconomyScale = KiriChessConfig.MaxEconomyScale;
        config.CaptureMult = KiriChessConfig.MaxCaptureMult;
        config.CastleCostFlat = KiriChessConfig.MaxMoveCost;
        config.MoveCostFlat = KiriChessConfig.MaxMoveCost;
        config.MoveCostPerSquare = KiriChessConfig.MaxMoveCost;
        config.CheckCheckerGain = KiriChessConfig.MaxCheckChip;
        config.CheckVictimLoss = KiriChessConfig.MaxCheckChip;
        config.DoubleCheckChipMultiplier = KiriChessConfig.MaxDoubleCheckChipMultiplier;
        config.PromotionCostMult = KiriChessConfig.MaxPromotionCostMult;
        foreach (var piece in config.PieceValues.Keys.ToList())
            config.PieceValues[piece] = KiriChessConfig.MaxPieceValue;
        foreach (var piece in config.MoveCostPerPiece.Keys.ToList())
            config.MoveCostPerPiece[piece] = KiriChessConfig.MaxMoveCost;
        return config;
    }

    /// <summary>
    /// The half that matters most: a ceiling one off the highest legitimate value would fail every
    /// game in a pool at creation, and nothing else in the suite would say so.
    /// </summary>
    [Fact]
    public void Validate_AcceptsAConfigSittingOnEveryCeiling()
    {
        AtEveryCeiling().Validate();
    }

    /// <summary>
    /// The highest value each field actually reaches across the six shipped pools, all in one config.
    /// Read from the pools themselves rather than written down, so adding a pool that raises one of
    /// them past its ceiling fails here instead of at a player's first game.
    /// </summary>
    [Fact]
    public void Validate_AcceptsTheHighestValueEveryShippedPoolActuallyUses()
    {
        var pools = KiriChessConfig.PresetOrder.Select(k => KiriChessConfig.Presets[k]).ToList();
        var config = KiriChessConfig.CreateSteal1();

        config.StartTimeSeconds = pools.Max(p => p.StartTimeSeconds);
        config.BaseIncrement = pools.Max(p => p.BaseIncrement);
        config.EconomyScale = pools.Max(p => p.EconomyScale);
        config.CaptureMult = pools.Max(p => p.CaptureMult);
        config.CastleCostFlat = pools.Max(p => p.CastleCostFlat);
        config.MoveCostFlat = pools.Max(p => p.MoveCostFlat);
        config.MoveCostPerSquare = pools.Max(p => p.MoveCostPerSquare);
        config.CheckCheckerGain = pools.Max(p => p.CheckCheckerGain);
        config.CheckVictimLoss = pools.Max(p => p.CheckVictimLoss);
        config.DoubleCheckChipMultiplier = pools.Max(p => p.DoubleCheckChipMultiplier);
        config.PromotionCostMult = pools.Max(p => p.PromotionCostMult);
        foreach (var piece in config.PieceValues.Keys.ToList())
            config.PieceValues[piece] = pools.Max(p => p.PieceValues[piece]);
        foreach (var piece in config.MoveCostPerPiece.Keys.ToList())
            config.MoveCostPerPiece[piece] = pools.Max(p => p.MoveCostPerPiece[piece]);

        config.Validate();
    }

    /// <summary>One case per ceiling: the ceiling plus one must be refused, and by name.</summary>
    [Fact]
    public void Validate_RejectsEveryFieldOneAboveItsCeiling()
    {
        var cases = new (string Field, Action<KiriChessConfig> PushOverTheCeiling)[]
        {
            ("StartTimeSeconds", c => c.StartTimeSeconds = KiriChessConfig.MaxStartTimeSeconds + 1),
            ("BaseIncrement", c => c.BaseIncrement = KiriChessConfig.MaxBaseIncrement + 1),
            ("EconomyScale", c => c.EconomyScale = KiriChessConfig.MaxEconomyScale + 1),
            ("CaptureMult", c => c.CaptureMult = KiriChessConfig.MaxCaptureMult + 1),
            ("CastleCostFlat", c => c.CastleCostFlat = KiriChessConfig.MaxMoveCost + 1),
            ("MoveCostFlat", c => c.MoveCostFlat = KiriChessConfig.MaxMoveCost + 1),
            ("MoveCostPerSquare", c => c.MoveCostPerSquare = KiriChessConfig.MaxMoveCost + 1),
            ("CheckCheckerGain", c => c.CheckCheckerGain = KiriChessConfig.MaxCheckChip + 1),
            ("CheckVictimLoss", c => c.CheckVictimLoss = KiriChessConfig.MaxCheckChip + 1),
            ("DoubleCheckChipMultiplier", c => c.DoubleCheckChipMultiplier = KiriChessConfig.MaxDoubleCheckChipMultiplier + 1),
            ("PromotionCostMult", c => c.PromotionCostMult = KiriChessConfig.MaxPromotionCostMult + 1),
            ("PieceValues", c => c.PieceValues[PieceType.Queen] = KiriChessConfig.MaxPieceValue + 1),
            ("MoveCostPerPiece", c => c.MoveCostPerPiece[PieceType.Rook] = KiriChessConfig.MaxMoveCost + 1),
            ("MoveCostPerPiece", c => c.MoveCostPerPiece[PieceType.King] = KiriChessConfig.MaxMoveCost + 1),
        };

        foreach (var (field, pushOverTheCeiling) in cases)
        {
            // From the all-ceilings config, so each case differs from a passing config in exactly the
            // one field it is about: a throw here cannot be some other field's doing.
            var config = AtEveryCeiling();
            pushOverTheCeiling(config);

            var ex = Assert.Throws<InvalidOperationException>(() => config.Validate());
            Assert.Contains(field, ex.Message);
        }
    }

    /// <summary>
    /// The text parser guards its own path (ParseEnum rejects an unknown name), but a
    /// KiriChessConfig also arrives deserialized whole out of the Challenge.CustomConfig and
    /// Game.ConfigSnapshot jsonb columns, whose JsonStringEnumConverter accepts a bare integer on
    /// the way in. An undefined mode used to fall through MovementToll's <c>_ =&gt; 0</c> as a
    /// permanently free move.
    /// </summary>
    [Fact]
    public void Validate_RejectsAnUndefinedMoveCostMode()
    {
        var config = TestSupport.AllZeroed();
        config.MoveCostMode = (MoveCostMode)99;

        var ex = Assert.Throws<InvalidOperationException>(() => config.Validate());
        Assert.Contains("MoveCostMode", ex.Message);
    }

    /// <summary>
    /// The engine must never refuse a config the custom-game builder was willing to offer, so the
    /// two sets of numbers are the same numbers. This can only pin the engine half (the builder's
    /// caps are JSX attributes in another language), which is the point: it makes the pairing
    /// greppable, so changing a ceiling here without visiting the builder is a deliberate act.
    /// </summary>
    [Fact]
    public void Ceilings_MatchTheCustomBuildersOwnPerControlCaps()
    {
        // ui/app/[locale]/custom/page.tsx's own per-control max attributes, transcribed. If a cap
        // there is raised past a ceiling here, the builder starts minting configs the server 400s.
        Assert.True(7200 <= KiriChessConfig.MaxStartTimeSeconds);
        Assert.Equal(60, KiriChessConfig.MaxBaseIncrement);
        Assert.Equal(20, KiriChessConfig.MaxEconomyScale);
        Assert.Equal(100, KiriChessConfig.MaxCaptureMult);
        Assert.Equal(50, KiriChessConfig.MaxPieceValue);
        Assert.Equal(50, KiriChessConfig.MaxMoveCost);
        Assert.Equal(50, KiriChessConfig.MaxCheckChip);
        Assert.Equal(10, KiriChessConfig.MaxDoubleCheckChipMultiplier);
        Assert.Equal(20, KiriChessConfig.MaxPromotionCostMult);
    }
}
