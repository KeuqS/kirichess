using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using KiriChess.Engine.Core;

namespace KiriChess.Engine.Config;

public enum MoveCostMode
{
    None,
    Flat,
    PerPiece,
    Distance,
    PerPieceXDistance
}

/// <summary>
/// A complete set of KiriChess rule variables. Every default and every rated pool comes from
/// <c>config/kirichess.rules.json</c> via <see cref="RulesDocument"/>, this class holds no literal
/// rule numbers of its own, so the engine, the web UI and the docs cannot drift apart.
/// </summary>
public class KiriChessConfig
{
    /// <summary>A config preloaded with the engine defaults from <c>config/kirichess.rules.json</c>.</summary>
    public KiriChessConfig() : this(applyDefaults: true) { }

    /// <summary>
    /// The blank baseline. Only <see cref="RulesDocument"/> uses it, to build the defaults themselves
    /// without recursing through the public constructor.
    /// </summary>
    internal KiriChessConfig(bool applyDefaults)
    {
        if (applyDefaults)
            RulesDocument.Instance.Defaults.ApplyTo(this);
    }

    // ------------------------------------------------------------------------------
    // BASE CLOCK
    // ------------------------------------------------------------------------------
    public int StartTimeSeconds { get; set; }
    public int BaseIncrement { get; set; }

    // ------------------------------------------------------------------------------
    // ECONOMY SCALING
    // ------------------------------------------------------------------------------
    public int EconomyScale { get; set; } = 1;

    // ------------------------------------------------------------------------------
    // RULE 1 - CAPTURE ECONOMY
    // ------------------------------------------------------------------------------
    public Dictionary<PieceType, int> PieceValues { get; set; } = new();

    public int CaptureMult { get; set; }
    public bool CaptureCaptorGainEnabled { get; set; }
    public bool CaptureVictimLossEnabled { get; set; }

    // ------------------------------------------------------------------------------
    // RULE 2 - MOVEMENT TOLL
    // ------------------------------------------------------------------------------
    public MoveCostMode MoveCostMode { get; set; }

    public Dictionary<PieceType, int> MoveCostPerPiece { get; set; } = new();

    public int CastleCostFlat { get; set; }
    public int MoveCostFlat { get; set; }
    public int MoveCostPerSquare { get; set; }

    // ------------------------------------------------------------------------------
    // RULE 4 - REPETITION GUARD
    // ------------------------------------------------------------------------------
    public bool RepeatedPositionNullifiesTime { get; set; }
    public bool ThreefoldRepetitionIsDraw { get; set; }

    // ------------------------------------------------------------------------------
    // RULE 3 - CHECK CHIP
    // ------------------------------------------------------------------------------
    public int CheckCheckerGain { get; set; }
    public int CheckVictimLoss { get; set; }
    public int DoubleCheckChipMultiplier { get; set; }

    // ------------------------------------------------------------------------------
    // OTHER PRICED EVENTS
    // ------------------------------------------------------------------------------
    public int PromotionCostMult { get; set; }

    // ------------------------------------------------------------------------------
    // GAME END
    // ------------------------------------------------------------------------------
    public bool FlagFallLoses { get; set; }
    public bool FlagVsInsufficientMaterialIsDraw { get; set; }

    // ------------------------------------------------------------------------------
    // CEILINGS
    // ------------------------------------------------------------------------------

    /// <summary>
    /// Upper bounds enforced by <see cref="Validate"/>, one per rule variable.
    /// </summary>
    /// <remarks>
    /// C-8-1 / C-10-7. Until Phase 9d.8 a custom config only ever reached its own author, so the
    /// browser's own per-control caps were treated as the boundary. A challenge carries
    /// <c>Challenge.CustomConfig</c> to a second player, and a hand-rolled
    /// <c>POST /api/challenges</c> never touches the builder at all, so the boundary has to be here:
    /// <see cref="Validate"/> runs on every <c>NewGame</c>/<c>FromFen</c>, which is the one gate a
    /// config cannot get into a game without passing.
    ///
    /// <para>Every number below is exactly the custom-game builder's own cap for that control
    /// (<c>ui/app/[locale]/custom/page.tsx</c>), so the engine refuses precisely what the builder
    /// refuses and can never refuse a config the builder was willing to offer.
    /// <see cref="MaxStartTimeSeconds"/> is the one deliberate exception, and says why on itself.</para>
    ///
    /// <para><b>Why a ceiling at all:</b> the economy arithmetic in
    /// <c>KiriChessEngine.ApplyEconomy</c> is unchecked <see langword="int"/>, so an unbounded field
    /// sign-flips a clock delta rather than failing loudly. Worst case these ceilings permit, per
    /// move: a capture at <c>50 * 100 * 20 = 100,000</c>; a promotion at <c>20 * 50 * 20 = 20,000</c>;
    /// a check chip at <c>50 * 20 * 10 = 10,000</c>; a movement toll at <c>50 * 7 * 20 = 7,000</c>
    /// (7 being the longest Chebyshev distance on a board). Four orders of magnitude clear of
    /// <see cref="int.MaxValue"/>, and the clock those accumulate onto is bounded too: the fifty-move
    /// rule is auto-detected on every position, which caps a game at roughly 12,600 plies, so even a
    /// check chip on every single ply of the longest legal game adds under 1.3e8.</para>
    /// </remarks>
    public const int MaxCaptureMult = 100;

    /// <inheritdoc cref="MaxCaptureMult"/>
    public const int MaxEconomyScale = 20;

    /// <inheritdoc cref="MaxCaptureMult"/>
    public const int MaxPieceValue = 50;

    /// <summary>Ceiling shared by every movement toll and the castling fee. <inheritdoc cref="MaxCaptureMult"/></summary>
    public const int MaxMoveCost = 50;

    /// <summary>Ceiling shared by both check-chip amounts. <inheritdoc cref="MaxCaptureMult"/></summary>
    public const int MaxCheckChip = 50;

    /// <inheritdoc cref="MaxCaptureMult"/>
    public const int MaxDoubleCheckChipMultiplier = 10;

    /// <inheritdoc cref="MaxCaptureMult"/>
    public const int MaxPromotionCostMult = 20;

    /// <inheritdoc cref="MaxCaptureMult"/>
    public const int MaxBaseIncrement = 60;

    /// <summary>
    /// One year of seconds, deliberately far more permissive than the builder's own 7200.
    /// </summary>
    /// <remarks>
    /// The only rule variable that is never a factor in a multiply: it seeds a clock that deltas are
    /// added to, so its ceiling is a sanity bound rather than an overflow bound, and a year still
    /// leaves the accumulated-gain worst case above four orders of magnitude short of
    /// <see cref="int.MaxValue"/>. It has to clear 7200 by some margin because the engine's own
    /// generative tests deliberately start a clock in the millions to take flag-fall out of an
    /// economy measurement (<c>Generative/PerftTests.cs</c>, <c>Generative/EconomyInvariantTests.cs</c>),
    /// which is a legitimate use of a config that no player will ever create.
    /// </remarks>
    public const int MaxStartTimeSeconds = 31_536_000;

    public void Validate()
    {
        var requiredPieces = new[] { PieceType.Pawn, PieceType.Knight, PieceType.Bishop, PieceType.Rook, PieceType.Queen };
        foreach (var p in requiredPieces)
        {
            if (!PieceValues.ContainsKey(p))
                throw new InvalidOperationException($"Config missing PieceValues for {p}");
            if (PieceValues[p] < 0)
                throw new InvalidOperationException($"Config PieceValues for {p} cannot be negative");
            if (PieceValues[p] > MaxPieceValue)
                throw new InvalidOperationException($"Config PieceValues for {p} cannot exceed {MaxPieceValue}");

            if (!MoveCostPerPiece.ContainsKey(p))
                throw new InvalidOperationException($"Config missing MoveCostPerPiece for {p}");
            if (MoveCostPerPiece[p] < 0)
                throw new InvalidOperationException($"Config MoveCostPerPiece for {p} cannot be negative");
            if (MoveCostPerPiece[p] > MaxMoveCost)
                throw new InvalidOperationException($"Config MoveCostPerPiece for {p} cannot exceed {MaxMoveCost}");
        }

        if (!MoveCostPerPiece.ContainsKey(PieceType.King))
            throw new InvalidOperationException("Config missing MoveCostPerPiece for King");
        if (MoveCostPerPiece[PieceType.King] < 0)
            throw new InvalidOperationException("Config MoveCostPerPiece for King cannot be negative");
        if (MoveCostPerPiece[PieceType.King] > MaxMoveCost)
            throw new InvalidOperationException($"Config MoveCostPerPiece for King cannot exceed {MaxMoveCost}");

        // The text parser (ParseEnum, reached from ApplyKey) rejects an unknown mode name on its own
        // path, but that is not the only way a mode arrives. A KiriChessConfig is also deserialized
        // whole out of the Challenge.CustomConfig and Game.ConfigSnapshot jsonb columns, and while
        // that converter writes the enum as a name, JsonStringEnumConverter still accepts a bare
        // integer on the way back in (allowIntegerValues defaults to true), so a stored
        // "moveCostMode": 99 loads as an undefined value rather than throwing. Any in-process cast
        // does the same. Undefined then falls through MovementToll's `_ => 0` as a permanently free
        // move, which is the quiet shape of failure Validate() exists to turn into a loud one.
        if (!Enum.IsDefined(MoveCostMode))
            throw new InvalidOperationException(
                $"Config MoveCostMode must be one of [{string.Join(", ", Enum.GetNames<MoveCostMode>())}], got {(int)MoveCostMode}");

        // At least one second, matching the custom-game builder's own floor (ui/app/custom/page.tsx).
        // Zero passed here, so a direct POST could create a game whose clock is already at zero: the
        // session reports LiveRemaining(White) == 0 and flags before a single move is played, and the
        // game is persisted as a completed loss for a player who never had a turn.
        if (StartTimeSeconds < 1) throw new InvalidOperationException("StartTimeSeconds must be at least 1");
        if (BaseIncrement < 0) throw new InvalidOperationException("BaseIncrement cannot be negative");
        if (EconomyScale < 1) throw new InvalidOperationException("EconomyScale must be at least 1");
        if (CaptureMult < 0) throw new InvalidOperationException("CaptureMult cannot be negative");
        if (CastleCostFlat < 0) throw new InvalidOperationException("CastleCostFlat cannot be negative");
        if (MoveCostFlat < 0) throw new InvalidOperationException("MoveCostFlat cannot be negative");
        if (MoveCostPerSquare < 0) throw new InvalidOperationException("MoveCostPerSquare cannot be negative");
        if (CheckCheckerGain < 0) throw new InvalidOperationException("CheckCheckerGain cannot be negative");
        if (CheckVictimLoss < 0) throw new InvalidOperationException("CheckVictimLoss cannot be negative");
        if (DoubleCheckChipMultiplier < 0) throw new InvalidOperationException("DoubleCheckChipMultiplier cannot be negative");
        if (PromotionCostMult < 0) throw new InvalidOperationException("PromotionCostMult cannot be negative");

        // The other half of each floor above. See the ceiling constants for where the numbers come
        // from and for the arithmetic showing what they keep inside int.
        if (StartTimeSeconds > MaxStartTimeSeconds) throw new InvalidOperationException($"StartTimeSeconds cannot exceed {MaxStartTimeSeconds}");
        if (BaseIncrement > MaxBaseIncrement) throw new InvalidOperationException($"BaseIncrement cannot exceed {MaxBaseIncrement}");
        if (EconomyScale > MaxEconomyScale) throw new InvalidOperationException($"EconomyScale cannot exceed {MaxEconomyScale}");
        if (CaptureMult > MaxCaptureMult) throw new InvalidOperationException($"CaptureMult cannot exceed {MaxCaptureMult}");
        if (CastleCostFlat > MaxMoveCost) throw new InvalidOperationException($"CastleCostFlat cannot exceed {MaxMoveCost}");
        if (MoveCostFlat > MaxMoveCost) throw new InvalidOperationException($"MoveCostFlat cannot exceed {MaxMoveCost}");
        if (MoveCostPerSquare > MaxMoveCost) throw new InvalidOperationException($"MoveCostPerSquare cannot exceed {MaxMoveCost}");
        if (CheckCheckerGain > MaxCheckChip) throw new InvalidOperationException($"CheckCheckerGain cannot exceed {MaxCheckChip}");
        if (CheckVictimLoss > MaxCheckChip) throw new InvalidOperationException($"CheckVictimLoss cannot exceed {MaxCheckChip}");
        if (DoubleCheckChipMultiplier > MaxDoubleCheckChipMultiplier) throw new InvalidOperationException($"DoubleCheckChipMultiplier cannot exceed {MaxDoubleCheckChipMultiplier}");
        if (PromotionCostMult > MaxPromotionCostMult) throw new InvalidOperationException($"PromotionCostMult cannot exceed {MaxPromotionCostMult}");
    }

    // ------------------------------------------------------------------------------
    // LOADING (.env / config)
    // ------------------------------------------------------------------------------

    /// <summary>
    /// Reads a <c>.env</c>-style file (KEY=VALUE per line, <c>#</c> comments, blank lines ignored)
    /// on top of a starting config. If <paramref name="baseConfig"/> is null the "steal1" preset
    /// is used as the baseline, so a file only needs to specify what it overrides.
    /// </summary>
    public static KiriChessConfig LoadFromEnvFile(string path, KiriChessConfig? baseConfig = null)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Config file not found: {path}", path);
        return FromDictionary(ParseEnv(File.ReadAllLines(path)), baseConfig);
    }

    /// <summary>Applies a set of KEY=VALUE overrides on top of a starting config (default: "steal1").</summary>
    public static KiriChessConfig FromDictionary(
        IReadOnlyDictionary<string, string> values,
        KiriChessConfig? baseConfig = null)
    {
        // Cloned, so this method is pure with respect to its argument. ApplyKey writes straight into
        // the object it is given, including into its PieceValues and MoveCostPerPiece dictionaries,
        // and Presets is a process-wide static of shared master copies: FromDictionary(values,
        // Presets["flagfall"]) used to leave the global preset holding the caller's overrides and
        // return that same object, so every later game in that pool ran under corrupted rules. The
        // null path and the PRESET= path already cloned; the baseConfig parameter did not, and it is
        // the one a %kcfg PGN importer would naturally reach for.
        var cfg = (baseConfig ?? CreateSteal1()).Clone();

        // Allow selecting a named preset as the baseline via PRESET=burn etc.
        if (values.TryGetValue("PRESET", out var presetName))
        {
            if (!Presets.TryGetValue(presetName, out var preset))
                throw new InvalidOperationException($"Unknown PRESET '{presetName}'. Known: {string.Join(", ", Presets.Keys)}");
            cfg = preset.Clone();
        }

        foreach (var (rawKey, rawValue) in values)
        {
            var key = rawKey.Trim().ToUpperInvariant();
            var value = rawValue.Trim();
            if (key == "PRESET") continue;
            cfg.ApplyKey(key, value);
        }

        cfg.Validate();
        return cfg;
    }

    /// <summary>
    /// Every rule variable as an ordered KEY=VALUE list, in exactly the vocabulary
    /// <see cref="ApplyKey"/> reads, so feeding <see cref="ToConfigString"/> back through
    /// <see cref="FromDictionary"/> reproduces this config field by field. This is the writer half of
    /// a vocabulary that previously only had a reader: the key order here is an external contract
    /// (docs/game/PGN.md, the <c>%kcfg</c> comment command a KiriChess PGN carries its rule config in), not
    /// an implementation detail, because a file already downloaded has this exact order baked in as
    /// text. Keys may be appended for a future rule, but never reordered or renamed.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, string>> ToKeyValues()
    {
        static string I(int v) => v.ToString(CultureInfo.InvariantCulture);
        static string B(bool v) => v ? "true" : "false";
        string Pieces(Dictionary<PieceType, int> map, bool includeKing)
        {
            var order = includeKing
                ? new[] { PieceType.Pawn, PieceType.Knight, PieceType.Bishop, PieceType.Rook, PieceType.Queen, PieceType.King }
                : new[] { PieceType.Pawn, PieceType.Knight, PieceType.Bishop, PieceType.Rook, PieceType.Queen };
            return string.Join(",", order.Select(p => I(map[p])));
        }

        return new[]
        {
            new KeyValuePair<string, string>("CLOCK_START_SECONDS", I(StartTimeSeconds)),
            new KeyValuePair<string, string>("BASE_INCREMENT", I(BaseIncrement)),
            new KeyValuePair<string, string>("ECONOMY_SCALE", I(EconomyScale)),
            new KeyValuePair<string, string>("PIECE_TIME_VALUES", Pieces(PieceValues, includeKing: false)),
            new KeyValuePair<string, string>("CAPTURE_MULT", I(CaptureMult)),
            new KeyValuePair<string, string>("CAPTURE_CAPTOR_GAIN_ENABLED", B(CaptureCaptorGainEnabled)),
            new KeyValuePair<string, string>("CAPTURE_VICTIM_LOSS_ENABLED", B(CaptureVictimLossEnabled)),
            new KeyValuePair<string, string>("MOVE_COST_MODE", MoveCostMode.ToString()),
            new KeyValuePair<string, string>("MOVE_COST_PER_PIECE", Pieces(MoveCostPerPiece, includeKing: true)),
            new KeyValuePair<string, string>("MOVE_COST_FLAT", I(MoveCostFlat)),
            new KeyValuePair<string, string>("MOVE_COST_PER_SQUARE", I(MoveCostPerSquare)),
            new KeyValuePair<string, string>("CASTLE_COST_FLAT", I(CastleCostFlat)),
            new KeyValuePair<string, string>("CHECK_CHECKER_GAIN", I(CheckCheckerGain)),
            new KeyValuePair<string, string>("CHECK_VICTIM_LOSS", I(CheckVictimLoss)),
            new KeyValuePair<string, string>("DOUBLE_CHECK_CHIP_MULTIPLIER", I(DoubleCheckChipMultiplier)),
            new KeyValuePair<string, string>("PROMOTION_COST_MULT", I(PromotionCostMult)),
            new KeyValuePair<string, string>("REPEATED_POSITION_NULLIFIES_TIME", B(RepeatedPositionNullifiesTime)),
            new KeyValuePair<string, string>("THREEFOLD_REPETITION_IS_DRAW", B(ThreefoldRepetitionIsDraw)),
            new KeyValuePair<string, string>("FLAG_FALL_LOSES", B(FlagFallLoses)),
            new KeyValuePair<string, string>("FLAG_VS_INSUFFICIENT_MATERIAL_IS_DRAW", B(FlagVsInsufficientMaterialIsDraw)),
        };
    }

    /// <summary>The <see cref="ToKeyValues"/> list as one <c>KEY=VALUE;KEY=VALUE</c> string, the exact
    /// text a KiriChess PGN's <c>%kcfg</c> comment carries. No value in this list needs PGN escaping:
    /// every one is an integer, a lowercase boolean, an enum name, or a comma list, never a quote or
    /// a backslash.</summary>
    public string ToConfigString() =>
        string.Join(";", ToKeyValues().Select(p => $"{p.Key}={p.Value}"));

    private void ApplyKey(string key, string value)
    {
        switch (key)
        {
            case "CLOCK_START_SECONDS": StartTimeSeconds = ParseInt(key, value); break;
            case "BASE_INCREMENT": BaseIncrement = ParseInt(key, value); break;

            case "ECONOMY_SCALE": EconomyScale = ParseInt(key, value); break;

            case "PIECE_TIME_VALUES": ApplyPieceArray(key, value, PieceValues, false); break;
            case "CAPTURE_MULT": CaptureMult = ParseInt(key, value); break;
            case "CAPTURE_CAPTOR_GAIN_ENABLED": CaptureCaptorGainEnabled = ParseBool(key, value); break;
            case "CAPTURE_VICTIM_LOSS_ENABLED": CaptureVictimLossEnabled = ParseBool(key, value); break;

            case "MOVE_COST_MODE": MoveCostMode = ParseEnum<MoveCostMode>(key, value); break;
            case "MOVE_COST_PER_PIECE": ApplyPieceArray(key, value, MoveCostPerPiece, true); break;
            case "MOVE_COST_FLAT": MoveCostFlat = ParseInt(key, value); break;
            case "MOVE_COST_PER_SQUARE": MoveCostPerSquare = ParseInt(key, value); break;
            case "CASTLE_COST_FLAT": CastleCostFlat = ParseInt(key, value); break;

            case "CHECK_CHECKER_GAIN": CheckCheckerGain = ParseInt(key, value); break;
            case "CHECK_VICTIM_LOSS": CheckVictimLoss = ParseInt(key, value); break;
            case "DOUBLE_CHECK_CHIP_MULTIPLIER": DoubleCheckChipMultiplier = ParseInt(key, value); break;

            case "PROMOTION_COST_MULT": PromotionCostMult = ParseInt(key, value); break;

            case "REPEATED_POSITION_NULLIFIES_TIME": RepeatedPositionNullifiesTime = ParseBool(key, value); break;
            case "THREEFOLD_REPETITION_IS_DRAW": ThreefoldRepetitionIsDraw = ParseBool(key, value); break;

            case "FLAG_FALL_LOSES": FlagFallLoses = ParseBool(key, value); break;
            case "FLAG_VS_INSUFFICIENT_MATERIAL_IS_DRAW": FlagVsInsufficientMaterialIsDraw = ParseBool(key, value); break;

            default:
                throw new InvalidOperationException($"Unknown config key '{key}'.");
        }
    }

    private static void ApplyPieceArray(string key, string value, Dictionary<PieceType, int> target, bool includesKing)
    {
        var parts = value.Split(',').Select(p => p.Trim()).ToArray();
        var expectedLength = includesKing ? 6 : 5;
        if (parts.Length != expectedLength)
            throw new FormatException($"Config key '{key}' expects {expectedLength} comma-separated values, got {parts.Length}.");

        target[PieceType.Pawn] = ParseInt(key, parts[0]);
        target[PieceType.Knight] = ParseInt(key, parts[1]);
        target[PieceType.Bishop] = ParseInt(key, parts[2]);
        target[PieceType.Rook] = ParseInt(key, parts[3]);
        target[PieceType.Queen] = ParseInt(key, parts[4]);
        if (includesKing)
        {
            target[PieceType.King] = ParseInt(key, parts[5]);
        }
    }

    private static Dictionary<string, string> ParseEnv(IEnumerable<string> lines)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
            var eq = trimmed.IndexOf('=');
            if (eq <= 0) throw new FormatException($"Malformed config line (expected KEY=VALUE): '{line}'");
            var key = trimmed[..eq].Trim();
            var val = trimmed[(eq + 1)..].Trim().Trim('"');
            result[key] = val;
        }
        return result;
    }

    private static int ParseInt(string key, string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)
            ? i
            : throw new FormatException($"Config key '{key}' expects an integer, got '{value}'.");

    private static bool ParseBool(string key, string value) => value.ToLowerInvariant() switch
    {
        "true" or "1" or "yes" or "on" => true,
        "false" or "0" or "no" or "off" => false,
        _ => throw new FormatException($"Config key '{key}' expects a boolean, got '{value}'.")
    };

    private static T ParseEnum<T>(string key, string value) where T : struct, Enum =>
        Enum.TryParse<T>(value, ignoreCase: true, out var v)
            ? v
            : throw new FormatException($"Config key '{key}' expects one of [{string.Join(", ", Enum.GetNames(typeof(T)))}], got '{value}'.");

    /// <summary>Deep copy, so mutating a clone never touches a shared preset's dictionaries.</summary>
    public KiriChessConfig Clone() => new()
    {
        StartTimeSeconds = StartTimeSeconds,
        BaseIncrement = BaseIncrement,
        EconomyScale = EconomyScale,
        PieceValues = new Dictionary<PieceType, int>(PieceValues),
        CaptureMult = CaptureMult,
        CaptureCaptorGainEnabled = CaptureCaptorGainEnabled,
        CaptureVictimLossEnabled = CaptureVictimLossEnabled,
        MoveCostMode = MoveCostMode,
        MoveCostPerPiece = new Dictionary<PieceType, int>(MoveCostPerPiece),
        CastleCostFlat = CastleCostFlat,
        MoveCostFlat = MoveCostFlat,
        MoveCostPerSquare = MoveCostPerSquare,
        RepeatedPositionNullifiesTime = RepeatedPositionNullifiesTime,
        ThreefoldRepetitionIsDraw = ThreefoldRepetitionIsDraw,
        CheckCheckerGain = CheckCheckerGain,
        CheckVictimLoss = CheckVictimLoss,
        DoubleCheckChipMultiplier = DoubleCheckChipMultiplier,
        PromotionCostMult = PromotionCostMult,
        FlagFallLoses = FlagFallLoses,
        FlagVsInsufficientMaterialIsDraw = FlagVsInsufficientMaterialIsDraw
    };

    // ------------------------------------------------------------------------------
    // PRESETS - every one of them read from config/kirichess.rules.json
    // ------------------------------------------------------------------------------

    /// <summary>
    /// The pools declared in <c>config/kirichess.rules.json</c>, keyed case-insensitively. These are
    /// the shared master copies: callers that intend to mutate one must <see cref="Clone"/> it first.
    /// </summary>
    /// <remarks>
    /// C-1-5: read-only, and wrapped rather than merely typed that way, so the guarantee holds
    /// against a cast as well as against a compile. This was a public static mutable dictionary of
    /// the rule sets every rated game in the process is created from, which means any code anywhere
    /// in the server, the simulator or a test could add a pool, drop one, or swap the config behind
    /// a pool key, and every game started afterwards would run under the replacement with nothing
    /// logged and nothing failing. The dictionary is the half that can be closed cheaply; the
    /// configs it holds are still mutable objects, which is why every caller clones (the
    /// <c>Presets_AreClonedBeforeUse</c> tests in <c>RulesDocumentTests</c> are what keep that
    /// convention honest) and why <see cref="FromDictionary"/> clones its <c>baseConfig</c>.
    /// </remarks>
    public static readonly IReadOnlyDictionary<string, KiriChessConfig> Presets =
        new ReadOnlyDictionary<string, KiriChessConfig>(RulesDocument.Instance.ToConfigs());

    /// <summary>Pool keys in declaration order, the order the UI presents them in.</summary>
    public static IReadOnlyList<string> PresetOrder => RulesDocument.Instance.PoolKeys;

    /// <summary>Keys of the pools that carry a rating. Any other config is unrated.</summary>
    public static IReadOnlyList<string> RatedPoolKeys => RulesDocument.Instance.RatedPoolKeys;

    /// <summary>
    /// A pool's display name for the given storage key, or the key itself if the config no longer
    /// declares that pool.
    ///
    /// Pool keys are neutral ids (<c>steal1</c>, <c>bounty1</c>) precisely so a rebrand never has to
    /// touch stored data, which means a key is never fit to show a person. Anything rendering a pool
    /// into prose a user reads (a PGN [Event] tag, an email body) must go through here; anything
    /// storing, matching or grouping stays on the key. An archived game whose pool has since been
    /// retired still renders, falling back to the key rather than throwing.
    /// </summary>
    public static string PoolLabel(string key)
    {
        foreach (var pool in RulesDocument.Instance.Pools)
        {
            if (string.Equals(pool.Key, key, StringComparison.OrdinalIgnoreCase)) return pool.Label;
        }

        return key;
    }

    private static KiriChessConfig Preset(string key) => Presets[key].Clone();

    public static KiriChessConfig CreateBounty1() => Preset("bounty1");
    public static KiriChessConfig CreateSteal1() => Preset("steal1");
    public static KiriChessConfig CreateSteal2() => Preset("steal2");
    public static KiriChessConfig CreateBurn1() => Preset("burn1");
    public static KiriChessConfig CreateSteal3() => Preset("steal3");
    public static KiriChessConfig CreateBurn2() => Preset("burn2");
}
