#!/usr/bin/env node
/**
 * Generates every downstream copy of the KiriChess rulebook from config/kirichess.rules.json:
 *
 *   ui/lib/rules.generated.ts   the web UI's rule variables + the derived tables the rules page renders
 *   docs/RULES.md               the human-readable rules reference
 *
 * The engine reads the same JSON directly (embedded into KiriChess.Engine), so backend, frontend and
 * docs cannot disagree on a number. All the arithmetic that turns rule variables into published
 * tables lives here, once. The rules page renders the output rather than recomputing it.
 *
 * Wording in docs/RULES.md is the English /rules page (ui/messages/en.json, namespace "rules"),
 * in the same section order as ui/app/[locale]/rules/page.tsx. The page is the source of truth
 * for what the manual says; this script only lays that page out as Markdown and fills its
 * placeholders with the derived numbers.
 *
 *   node scripts/gen-rules.mjs            write the outputs
 *   node scripts/gen-rules.mjs --check    exit 1 if any output is stale (CI)
 */

import { readFileSync, writeFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join, relative } from "node:path";

const HERE = dirname(fileURLToPath(import.meta.url));
const REPO = join(HERE, "..", "..");
const SOURCE = join(REPO, "config", "kirichess.rules.json");
const TS_OUT = join(REPO, "ui", "lib", "rules.generated.ts");
const MD_OUT = join(REPO, "docs", "RULES.md");

const PIECES = ["pawn", "knight", "bishop", "rook", "queen", "king"];
const CAPTURABLE = ["pawn", "knight", "bishop", "rook", "queen"];
const PIECE_LABEL = {
  pawn: "Pawn",
  knight: "Knight",
  bishop: "Bishop",
  rook: "Rook",
  queen: "Queen",
  king: "King",
};
const PIECE_GLYPH = {
  pawn: "♟",
  knight: "♞",
  bishop: "♝",
  rook: "♜",
  queen: "♛",
  king: "♚",
};

// ---------------------------------------------------------------------------------------------
// Load + resolve
// ---------------------------------------------------------------------------------------------

const doc = JSON.parse(readFileSync(SOURCE, "utf8"));

/** Strips the `$comment` annotations the JSON carries for human readers. */
function strip(obj) {
  if (obj === null || typeof obj !== "object") return obj;
  if (Array.isArray(obj)) return obj.map(strip);
  return Object.fromEntries(
    Object.entries(obj)
      .filter(([k]) => k !== "$comment")
      .map(([k, v]) => [k, strip(v)]),
  );
}

const defaults = strip(doc.defaults);
const docModel = strip(doc.docModel);

/** A pool's complete rule set: the defaults with its overrides written over the top. */
function resolve(overrides) {
  return { ...defaults, ...strip(overrides) };
}

const pools = doc.pools.map((p) => ({
  key: p.key,
  label: p.label,
  rated: p.rated,
  tagline: p.tagline,
  duration: strip(p.duration),
  config: resolve(p.overrides),
}));

// ---------------------------------------------------------------------------------------------
// Rule arithmetic, mirrors KiriChessEngine.ApplyEconomy
// ---------------------------------------------------------------------------------------------

/** Seconds transferred/burned when `piece` is captured. */
const captureAmount = (c, piece) => c.pieceValues[piece] * c.captureMult * c.economyScale;

/** Seconds the mover pays to move `piece` a Chebyshev distance of `dist`. */
function toll(c, piece, dist = 1) {
  const base =
    c.moveCostMode === "None"
      ? 0
      : c.moveCostMode === "Flat"
        ? c.moveCostFlat
        : c.moveCostMode === "PerPiece"
          ? c.moveCostPerPiece[piece]
          : c.moveCostMode === "Distance"
            ? c.moveCostPerSquare * dist
            : c.moveCostPerPiece[piece] * dist;
  return base * c.economyScale;
}

const castleCost = (c) => (c.moveCostMode === "None" ? 0 : c.castleCostFlat * c.economyScale);
const checkGain = (c) => c.checkCheckerGain * c.economyScale;
const checkLoss = (c) => c.checkVictimLoss * c.economyScale;
const promotionCost = (c, piece) => c.promotionCostMult * c.pieceValues[piece] * c.economyScale;

/** "steal" (transfer), "bounty" (captor gains, nobody loses) or "burn" (victim loses, captor gains nothing). */
function economyOf(c) {
  if (c.captureCaptorGainEnabled && c.captureVictimLossEnabled) return "steal";
  if (c.captureCaptorGainEnabled) return "bounty";
  return "burn";
}

/**
 * The pool's dial label. C-n scales captures only (tolls and chips stay at base); F-n is full
 * economy scaling, which the Second Edition reserves for custom games.
 */
function multiplierLabel(c) {
  const capture = c.captureMult / defaults.captureMult;
  if (c.economyScale === 1) return `C${capture}`;
  if (capture === 1) return `F${c.economyScale}`;
  return `C${capture}-F${c.economyScale}`;
}

const timeControl = (c) => `${round(c.startTimeSeconds / 60)}+${c.baseIncrement}`;

const round = (n, dp = 1) => Number(n.toFixed(dp));

// ---------------------------------------------------------------------------------------------
// Derived tables
// ---------------------------------------------------------------------------------------------

const ratedPools = pools.filter((p) => p.rated);

/** True when every rated pool prices moves and checks identically: what makes the tables universal. */
const sameAcrossPools = (fn) => {
  const first = JSON.stringify(fn(ratedPools[0].config));
  return ratedPools.every((p) => JSON.stringify(fn(p.config)) === first);
};

const tollsAreUniversal = sameAcrossPools((c) => PIECES.map((p) => toll(c, p)).concat(castleCost(c)));
const chipsAreUniversal = sameAcrossPools((c) => [checkGain(c), checkLoss(c)]);

/** Rule 1 at base scale: what each piece is worth when captured, under each capture personality. */
const captureTable = CAPTURABLE.map((piece) => {
  const amount = captureAmount(defaults, piece);
  return {
    piece,
    label: PIECE_LABEL[piece],
    glyph: PIECE_GLYPH[piece],
    points: defaults.pieceValues[piece],
    amount,
    steal: `±${amount}s`,
    bounty: `+${amount}s`,
    burn: `−${amount}s`,
  };
});

/** Rule 2 at base scale: the per-move toll, plus castling's flat fee. */
const tollTable = [
  ...PIECES.map((piece) => ({
    piece,
    label: PIECE_LABEL[piece],
    glyph: PIECE_GLYPH[piece],
    toll: toll(defaults, piece),
  })),
  { piece: "castle", label: "Castling (either side)", glyph: "⟲", toll: castleCost(defaults) },
];

/**
 * Rule 3 profitability: a check costs the checker its toll and pays the chip, so the cheaper the
 * checking piece the better the trade. Pieces are grouped where their tolls are equal.
 */
const checkProfitTable = (() => {
  const gain = checkGain(defaults);
  const loss = checkLoss(defaults);
  const groups = new Map();
  for (const piece of ["queen", "rook", "knight", "bishop", "pawn"]) {
    const t = toll(defaults, piece);
    if (!groups.has(t)) groups.set(t, []);
    groups.get(t).push(PIECE_LABEL[piece]);
  }
  return [...groups.entries()]
    .sort((a, b) => b[0] - a[0])
    .map(([t, labels]) => {
      const yourClock = gain - t;
      const gap = yourClock + loss;
      return {
        piece: labels.join(" / "),
        breakdown: `−${t} (toll) +${gain} (chip) = ${yourClock > 0 ? "+" : yourClock < 0 ? "−" : ""}${Math.abs(yourClock)}`,
        yourClock,
        theirClock: -loss,
        gap,
        verdict: gap === 0 ? "±0" : gap > 0 ? `+${gap}` : `${gap}`,
      };
    });
})();

/** What an equal knight trade does to each clock, per capture personality. */
const tradeMathTable = (() => {
  const amount = captureAmount(defaults, "knight");
  return [
    {
      economy: "steal",
      label: `Steal (${defaults.captureMult}, ${defaults.captureMult})`,
      you: `−${amount} +${amount} = 0`,
      them: "0",
      effect: "unchanged",
    },
    {
      economy: "bounty",
      label: `Bounty (${defaults.captureMult}, 0)`,
      you: `+${amount}`,
      them: `+${amount}`,
      effect: "both profit",
    },
    {
      economy: "burn",
      label: `Burn (0, ${defaults.captureMult})`,
      you: `−${amount}`,
      them: `−${amount}`,
      effect: "both drain",
    },
  ];
})();

/** One row per pool: time control, dial, economy, everything the pool table shows. */
const poolTable = pools.map((p) => ({
  key: p.key,
  label: p.label,
  tagline: p.tagline,
  rated: p.rated,
  time: timeControl(p.config),
  startTimeSeconds: p.config.startTimeSeconds,
  multiplier: multiplierLabel(p.config),
  economy: economyOf(p.config),
  tag: `${timeControl(p.config)} - ${capitalize(economyOf(p.config))} - ${multiplierLabel(p.config)}`,
}));

function capitalize(s) {
  return s[0].toUpperCase() + s.slice(1);
}

/**
 * The real per-move thinking time: the pool minus the toll bill, plus or minus what the capture
 * economy injects or destroys, divided over a typical game.
 */
const thinkingBudgetTable = pools.map((p) => {
  const c = p.config;
  const moves = docModel.movesPerPlayer;
  const avgToll = PIECES.reduce((sum, piece) => sum + docModel.moveFrequency[piece] * toll(c, piece), 0);
  const tollDrain = Math.round(avgToll * moves);
  const economy = economyOf(c);
  const magnitude = docModel.materialTradedPoints.value * c.captureMult * c.economyScale;
  const economyDrain = economy === "steal" ? 0 : economy === "bounty" ? magnitude : -magnitude;
  const perMove = (c.startTimeSeconds - tollDrain + economyDrain) / moves;

  // Bounty is the one economy that puts time back, so it reads as a subtraction from the drain.
  const drain =
    economyDrain > 0
      ? `~${tollDrain}s tolls − ~${economyDrain}s bounty`
      : economyDrain < 0
        ? `~${tollDrain}s tolls + ~${Math.abs(economyDrain)}s burn`
        : `~${tollDrain}s tolls`;

  return {
    key: p.key,
    label: p.label,
    poolSeconds: c.startTimeSeconds,
    tollDrain,
    economyDrain,
    drain,
    perMove: round(perMove),
    feelsLike: p.duration.feelsLike,
  };
});

/**
 * The per-pool prices the prose quotes inline. Same arithmetic as the tables above, exposed as
 * plain numbers so the rules page's sentences interpolate them instead of spelling a retune's
 * worth of seconds into ui/messages/*.json, where nothing would catch them going stale.
 */
const poolEconomyTable = pools.map((p) => {
  const c = p.config;
  const economy = economyOf(c);
  const amount = Object.fromEntries(CAPTURABLE.map((piece) => [piece, captureAmount(c, piece)]));

  // What one capture does to the *gap* between the two clocks. Steal moves it twice over (the
  // captor gains exactly what the victim loses); Bounty and Burn only ever move one clock.
  const swing = Object.fromEntries(
    CAPTURABLE.map((piece) => [piece, economy === "steal" ? amount[piece] * 2 : amount[piece]]),
  );

  const tollDrain = thinkingBudgetTable.find((b) => b.key === p.key).tollDrain;
  const bestCheckGap = Math.max(...checkProfitTable.map((r) => r.gap));

  return {
    key: p.key,
    label: p.label,
    minutes: round(c.startTimeSeconds / 60),
    /** Seconds a single point of material is worth in this pool. */
    perPoint: c.captureMult * c.economyScale,
    /** How many times the base capture multiplier this pool runs at: the n in "Cn". */
    multiplierRatio: c.captureMult / defaults.captureMult,
    amount,
    swing,
    /** Share of the starting clock the toll bill eats over a modelled game. */
    tollSharePct: Math.round((tollDrain / c.startTimeSeconds) * 100),
    /** Share of the starting clock a single queen capture swings. */
    queenSwingSharePct: Math.round((swing.queen / c.startTimeSeconds) * 100),
    /** Profitable pawn checks needed to claw back a lost queen: comeback power against the stakes. */
    checksToRecoverQueen: Math.round(swing.queen / bestCheckGap),
  };
});

/** How long a game actually runs. Nominal is the clock; the rest are playtested observations. */
const durationTable = pools.map((p) => ({
  key: p.key,
  label: p.label,
  nominalMinutes: round(p.config.startTimeSeconds / 60),
  ceilingMinutes: p.duration.ceilingMinutes,
  beginner: p.duration.beginner,
  intermediate: p.duration.intermediate,
  advanced: p.duration.advanced,
}));

/** The toll cost of converting a won endgame, priced from the toll table and a modelled move mix. */
const endgameTable = docModel.endgames.map((e) => {
  const moves = Object.values(e.mix).reduce((a, b) => a + b, 0);
  const bill = Object.entries(e.mix).reduce((sum, [piece, n]) => sum + n * toll(defaults, piece), 0);
  return {
    name: e.name,
    work: e.work,
    moves,
    bill,
    mix: Object.entries(e.mix).map(([piece, n]) => `${n}× ${PIECE_LABEL[piece].toLowerCase()}`).join(", "),
  };
});

/** The priced events that are not moves, captures or checks. */
const pricedEvents = {
  promotionIsFree: defaults.promotionCostMult === 0,
  promotionQueenCost: promotionCost(defaults, "queen"),
  pawnToll: toll(defaults, "pawn"),
  pawnCapture: captureAmount(defaults, "pawn"),
  castleCost: castleCost(defaults),
  kingToll: toll(defaults, "king"),
  rookToll: toll(defaults, "rook"),
  doubleCheckGain: checkGain(defaults) * defaults.doubleCheckChipMultiplier,
  doubleCheckLoss: checkLoss(defaults) * defaults.doubleCheckChipMultiplier,
  checkGain: checkGain(defaults),
  checkLoss: checkLoss(defaults),
};

/** How games end, gated on the flags that actually govern each ending. */
const endingRules = {
  flagFallLoses: defaults.flagFallLoses,
  flagVsInsufficientMaterialIsDraw: defaults.flagVsInsufficientMaterialIsDraw,
  threefoldRepetitionIsDraw: defaults.threefoldRepetitionIsDraw,
  repeatedPositionNullifiesTime: defaults.repeatedPositionNullifiesTime,
};

// ---------------------------------------------------------------------------------------------
// TypeScript emitter
// ---------------------------------------------------------------------------------------------

const lit = (v) => JSON.stringify(v, null, 2);
const union = (values) => values.map((v) => JSON.stringify(v)).join(" | ");

function emitTypeScript() {
  const poolKeys = pools.map((p) => p.key);
  const byKey = Object.fromEntries(pools.map((p) => [p.key, p]));

  return `// GENERATED FILE, DO NOT EDIT.
//
// Source of truth: config/kirichess.rules.json
// Regenerate:      npm run gen:rules   (npm run gen:rules:check verifies it in CI)
//
// The same JSON is embedded into KiriChess.Engine, so every number below is byte-for-byte the
// economy the server actually runs. Change the config, not this file.

export type PoolKey = ${union(poolKeys)};

export type MoveCostMode =
  | "None"
  | "Flat"
  | "PerPiece"
  | "Distance"
  | "PerPieceXDistance";

export type Economy = "steal" | "burn" | "bounty";

export interface PieceInts5 {
  pawn: number;
  knight: number;
  bishop: number;
  rook: number;
  queen: number;
}

export interface PieceInts6 extends PieceInts5 {
  king: number;
}

/** A complete set of rule variables, mirrors \`KiriChessConfig\` field for field. */
export interface RuleValues {
  startTimeSeconds: number;
  baseIncrement: number;
  economyScale: number;
  pieceValues: PieceInts5;
  captureMult: number;
  captureCaptorGainEnabled: boolean;
  captureVictimLossEnabled: boolean;
  moveCostMode: MoveCostMode;
  moveCostPerPiece: PieceInts6;
  castleCostFlat: number;
  moveCostFlat: number;
  moveCostPerSquare: number;
  checkCheckerGain: number;
  checkVictimLoss: number;
  doubleCheckChipMultiplier: number;
  promotionCostMult: number;
  repeatedPositionNullifiesTime: boolean;
  threefoldRepetitionIsDraw: boolean;
  flagFallLoses: boolean;
  flagVsInsufficientMaterialIsDraw: boolean;
}

export interface PoolDuration {
  ceilingMinutes: number;
  beginner: string;
  intermediate: string;
  advanced: string;
  feelsLike: string;
}

export interface PoolDefinition {
  key: PoolKey;
  label: string;
  rated: boolean;
  tagline: string;
  /** The pool's fully resolved rule set (defaults with its overrides applied). */
  config: RuleValues;
  duration: PoolDuration;
}

export const RULES_SCHEMA_VERSION = ${doc.schemaVersion};
export const EDITION = ${JSON.stringify(doc.edition)};

/** Engine defaults, and the starting point for the custom-game builder. */
export const DEFAULT_RULES: RuleValues = ${lit(defaults)};

/** Pool keys in the order the config declares them, the order the UI presents them in. */
export const POOL_KEYS: PoolKey[] = ${lit(poolKeys)};

/** Keys of the pools that carry a rating. Any other config is unrated. */
export const RATED_POOL_KEYS: PoolKey[] = ${lit(pools.filter((p) => p.rated).map((p) => p.key))};

export const POOLS: Record<PoolKey, PoolDefinition> = ${lit(byKey)};

// -------------------------------------------------------------------------------------------
// Derived tables, computed from the rule variables above by scripts/gen-rules.mjs, so they
// recompute themselves whenever a rule changes instead of going quietly stale.
// -------------------------------------------------------------------------------------------

/** True when every rated pool prices moves identically (what makes the toll table universal). */
export const TOLLS_ARE_UNIVERSAL = ${tollsAreUniversal};

/** True when every rated pool prices checks identically (what makes the check table universal). */
export const CHIPS_ARE_UNIVERSAL = ${chipsAreUniversal};

export const CAPTURE_TABLE = ${lit(captureTable)} as const;

export const TOLL_TABLE = ${lit(tollTable)} as const;

export const CHECK_PROFIT_TABLE = ${lit(checkProfitTable)} as const;

export const TRADE_MATH_TABLE = ${lit(tradeMathTable)} as const;

export const POOL_TABLE = ${lit(poolTable)} as const;

/** Per-pool prices, for the sentences that quote one inline. */
export const POOL_ECONOMY_TABLE = ${lit(poolEconomyTable)} as const;

export const THINKING_BUDGET_TABLE = ${lit(thinkingBudgetTable)} as const;

export const DURATION_TABLE = ${lit(durationTable)} as const;

export const ENDGAME_TABLE = ${lit(endgameTable)} as const;

/** Prices for the events that are neither an ordinary move, a capture, nor a check. */
export const PRICED_EVENTS = ${lit(pricedEvents)} as const;

/** The flags that govern how games end. */
export const ENDING_RULES = ${lit(endingRules)} as const;

/** Modelling assumptions behind the explanatory tables (not engine rules). */
export const DOC_MODEL = ${lit(docModel)} as const;
`;
}

// ---------------------------------------------------------------------------------------------
// Markdown emitter
//
// docs/RULES.md mirrors the English /rules page. Wording comes from ui/messages/en.json
// (namespace "rules"); every number comes from the derived tables above, the same ones
// ui/app/[locale]/rules/page.tsx renders. Section order follows that page. A new section
// on the page has to be added here, or the manual will not mention it.
// ---------------------------------------------------------------------------------------------

const enRules = JSON.parse(readFileSync(join(REPO, "ui", "messages", "en.json"), "utf8")).rules;

const EMPHASIS_TAGS = new Set(["strong", "gains", "loses", "toll", "val", "cost"]);

/**
 * Fill one English rules-page string the way the page's translator does, then turn its
 * rich-text tags into Markdown. Throws if a placeholder has no value, so a new `{name}`
 * on the page cannot ship as a literal brace in the manual.
 */
function fill(key, values = {}, rich = {}) {
  const template = enRules[key];
  if (typeof template !== "string") {
    throw new Error(`rules.${key} is missing from ui/messages/en.json`);
  }
  const missing = [];
  const filled = template.replace(/\{(\w+)\}/g, (match, name) => {
    if (!Object.prototype.hasOwnProperty.call(values, name)) {
      missing.push(name);
      return match;
    }
    return String(values[name]);
  });
  if (missing.length > 0) {
    throw new Error(`rules.${key} is missing values for: ${missing.join(", ")}`);
  }
  return filled.replace(/<(\w+)>([\s\S]*?)<\/\1>/g, (_, tag, inner) => {
    if (tag === "pools" && rich.poolsHref) return `[${inner}](${rich.poolsHref})`;
    if (EMPHASIS_TAGS.has(tag)) return `**${inner}**`;
    return inner;
  });
}

function table(headers, rows, align = []) {
  const esc = (value) => String(value).replace(/\|/g, "\\|").replace(/\r?\n/g, " ");
  const sep = headers.map((_, i) => (align[i] === "r" ? "---:" : "---"));
  return [
    `| ${headers.map(esc).join(" | ")} |`,
    `| ${sep.join(" | ")} |`,
    ...rows.map((r) => `| ${r.map(esc).join(" | ")} |`),
  ].join("\n");
}

function headingSlug(text) {
  return text
    .toLowerCase()
    .replace(/[^\p{L}\p{N}\s-]/gu, "")
    .trim()
    .replace(/\s+/g, "-");
}

function block(parts) {
  return parts.join("\n\n");
}

function bullets(items) {
  return items.map((item) => `- ${item}`).join("\n");
}

function numbered(items) {
  return items.map((item, i) => `${i + 1}. ${item}`).join("\n");
}

function emitMarkdown() {
  const poolNames = Object.fromEntries(pools.map((p) => [p.key, p.label]));
  const numberWords = [
    "zero",
    "one",
    "two",
    "three",
    "four",
    "five",
    "six",
    "seven",
    "eight",
    "nine",
    "ten",
  ];
  const ratedCountWord = numberWords[ratedPools.length] ?? String(ratedPools.length);
  const poolsHeading = fill("poolsH2", { count: ratedCountWord });
  const baseMultiplier = `C${defaults.captureMult / defaults.captureMult}`;

  // Same formulas as the constants at the top of ui/app/[locale]/rules/page.tsx.
  const exactAvgToll = PIECES.reduce(
    (sum, piece) => sum + docModel.moveFrequency[piece] * defaults.moveCostPerPiece[piece],
    0,
  );
  const avgToll = Math.round(exactAvgToll * 10) / 10;
  const queenToll = defaults.moveCostPerPiece.queen;
  const rookToll = pricedEvents.rookToll;
  const bishopToll = defaults.moveCostPerPiece.bishop;
  const undiscountedQueenToll = defaults.moveCostPerPiece.rook + bishopToll;
  const heavyMoveShare = Math.round((docModel.moveFrequency.queen + docModel.moveFrequency.rook) * 100);
  const heavyTollShare = Math.round(
    ((docModel.moveFrequency.queen * queenToll + docModel.moveFrequency.rook * rookToll) / exactAvgToll) * 100,
  );
  const tollDrain = thinkingBudgetTable[0].tollDrain;
  const bestCheckGap = Math.max(...checkProfitTable.map((r) => r.gap));
  const billOf = (name) => {
    const row = endgameTable.find((e) => e.name === name);
    if (!row) throw new Error(`docModel.endgames has no "${name}", which the rules page quotes`);
    return row.bill;
  };
  const equalTrade = captureTable.find((r) => r.piece === "knight").amount;
  const basePool = poolEconomyTable.find((p) => p.key === "steal1");
  if (!basePool) {
    throw new Error("the rules page compares comebacks against pool key steal1, which is missing");
  }

  const highMultiplierSteal = poolTable
    .filter(
      (p) => p.rated && p.economy === "steal" && p.multiplier !== baseMultiplier && p.multiplier.startsWith("C"),
    )
    .map((p) => {
      const budget = thinkingBudgetTable.find((b) => b.key === p.key);
      return `${p.label}'s "${budget.perMove}s"`;
    });

  function numbersFor(key) {
    const economy = poolEconomyTable.find((p) => p.key === key);
    const budget = thinkingBudgetTable.find((b) => b.key === key);
    const dial = poolTable.find((p) => p.key === key).multiplier;
    return {
      minutes: economy.minutes,
      dial,
      chip: pricedEvents.checkGain,
      perPoint: economy.perPoint,
      pawnSwing: economy.swing.pawn,
      bishopSwing: economy.swing.bishop,
      rookSwing: economy.swing.rook,
      queenSwing: economy.swing.queen,
      queenAmount: economy.amount.queen,
      queenSwingShare: economy.queenSwingSharePct,
      tollShare: economy.tollSharePct,
      // The First Edition's full-scale dial, kept so the design note stays derived. Mirrors LEGACY_F4_SCALE on the page.
      tollShareAtF4: economy.tollSharePct * 4,
      scaledChip: pricedEvents.checkGain * economy.multiplierRatio,
      checksForQueen: economy.checksToRecoverQueen,
      baseChecksForQueen: basePool.checksToRecoverQueen,
      perMove: Math.floor(budget.perMove),
      ceiling: durationTable.find((d) => d.key === key).ceilingMinutes,
    };
  }

  function withPool(key) {
    return { ...poolNames, ...numbersFor(key) };
  }

  const endings = [];
  const pushEnding = (name, verdict, description) => {
    endings.push(`**${name}** - ${verdict}. ${description}`);
  };
  pushEnding(fill("endingCheckmateName"), fill("verdictWin"), fill("endingCheckmateDescription"));
  if (endingRules.flagFallLoses) {
    pushEnding(
      fill("endingFlagFallName"),
      endingRules.flagVsInsufficientMaterialIsDraw ? fill("verdictWinStar") : fill("verdictWin"),
      endingRules.flagVsInsufficientMaterialIsDraw
        ? fill("endingFlagFallDescriptionWithDraw")
        : fill("endingFlagFallDescriptionNoDraw"),
    );
  }
  if (endingRules.flagFallLoses && endingRules.flagVsInsufficientMaterialIsDraw) {
    pushEnding(
      fill("endingFlagVsBareKingName"),
      fill("verdictDraw"),
      fill("endingFlagVsBareKingDescription"),
    );
  }
  pushEnding(fill("endingStalemateName"), fill("verdictDraw"), fill("endingStalemateDescription"));
  if (endingRules.threefoldRepetitionIsDraw) {
    pushEnding(fill("endingThreefoldName"), fill("verdictDraw"), fill("endingThreefoldDescription"));
  }
  pushEnding(fill("endingFiftyMoveName"), fill("verdictDraw"), fill("endingFiftyMoveDescription"));
  pushEnding(fill("endingInsufficientName"), fill("verdictDraw"), fill("endingInsufficientDescription"));
  pushEnding(fill("endingAgreementName"), fill("verdictDraw"), fill("endingAgreementDescription"));
  pushEnding(fill("endingResignationName"), fill("verdictLoss"), fill("endingResignationDescription"));
  pushEnding(fill("endingAbandonedName"), fill("verdictLoss"), fill("endingAbandonedDescription"));
  if (endingRules.flagFallLoses) {
    pushEnding(
      fill("endingTimeoutName"),
      fill("verdictLoss"),
      endingRules.flagVsInsufficientMaterialIsDraw
        ? fill("endingTimeoutDescriptionWithDraw")
        : fill("endingTimeoutDescriptionNoDraw"),
    );
  }

  const promotionTitle = pricedEvents.promotionIsFree
    ? fill("promotionFreeTitle")
    : fill("promotionPricedTitle");
  const promotionBody = pricedEvents.promotionIsFree
    ? fill("promotionFreeBody", { pawnToll: pricedEvents.pawnToll })
    : fill("promotionPricedBody", {
        pawnToll: pricedEvents.pawnToll,
        mult: defaults.promotionCostMult,
        queenCost: pricedEvents.promotionQueenCost,
      });

  const glossary = [
    [fill("glossaryClockGapTerm"), fill("glossaryClockGapDef")],
    [
      fill("glossaryFlagfallTerm"),
      endingRules.flagVsInsufficientMaterialIsDraw
        ? fill("glossaryFlagfallDef")
        : fill("glossaryFlagfallDefNoDraw"),
    ],
    [fill("glossaryTollTerm"), fill("glossaryTollDef")],
    [
      fill("glossaryChipTerm"),
      fill("glossaryChipDef", {
        chip: pricedEvents.checkGain,
        neverScales: tollsAreUniversal && chipsAreUniversal ? fill("glossaryNeverScales") : "",
      }),
    ],
    [fill("glossaryStealBurnBountyTerm"), fill("glossaryStealBurnBountyDef")],
    [fill("glossaryGuardTerm"), fill("glossaryGuardDef")],
    [fill("glossaryCaptureMultiplierTerm"), fill("glossaryCaptureMultiplierDef")],
    [fill("glossaryFModeTerm"), fill("glossaryFModeDef")],
    [fill("glossaryThinkingBudgetTerm"), fill("glossaryThinkingBudgetDef")],
    [fill("glossarySelfFinancingTerm"), fill("glossarySelfFinancingDef")],
    [
      fill("glossaryConversionBillTerm"),
      fill("glossaryConversionBillDef", {
        kq: billOf("K+Q vs K"),
        kr: billOf("K+R vs K"),
        everyPool: tollsAreUniversal ? fill("glossaryInEveryPool") : "",
      }),
    ],
    [fill("glossaryDeadCapitalTerm"), fill("glossaryDeadCapitalDef")],
  ];

  const poolDetails = poolTable
    .filter((p) => p.rated)
    .map((p) => {
      const values = withPool(p.key);
      return block([
        `### ${p.label}`,
        `*${p.tag}*`,
        fill(`${p.key}Para1`, values),
        fill(`${p.key}Para2`, values),
        `**${fill("rewardsLabel")}** ${fill(`${p.key}Rewards`, poolNames)}`,
        `**${fill("punishesLabel")}** ${fill(`${p.key}Punishes`, poolNames)}`,
      ]);
    })
    .join("\n\n");

  const banner =
    "<!-- GENERATED FILE, DO NOT EDIT. Wording: the /rules page (`ui/messages/en.json`). Numbers: `config/kirichess.rules.json`. Run `npm --prefix ui run gen:rules`. -->";

  return [
    banner,
    `# ${fill("conceptH1")}`,
    block([
      `## ${fill("changesHeading")}`,
      fill("changesPara1"),
      fill("changesPara2"),
      fill("changesPara3"),
      fill("changesPara4"),
      fill("changesPara5"),
    ]),
    block([`## ${fill("unchangedHeading")}`, fill("unchangedPara1"), fill("unchangedPara2")]),
    block([
      `## ${fill("rulesHeading")}`,
      fill("rulesIntro"),
      `### 1. ${fill("capturesH3")}`,
      fill("capturesIntro"),
      `**${fill("stealLabel")}.** ${fill("stealCardBody")}`,
      `**${fill("bountyLabel")}.** ${fill("bountyCardBody")}`,
      `**${fill("burnLabel")}.** ${fill("burnCardBody")}`,
      table(
        [fill("captureTableColPiece"), fill("stealLabel"), fill("bountyLabel"), fill("burnLabel")],
        captureTable.map((r) => [`${r.glyph} ${r.label}`, r.steal, r.bounty, r.burn]),
        ["", "r", "r", "r"],
      ),
      fill(
        "captureFootnote",
        { multiplier: baseMultiplier, poolCount: poolTable.length },
        { poolsHref: `#${headingSlug(poolsHeading)}` },
      ),
      `### 2. ${fill("tollsH3")}`,
      fill("tollsIntro"),
      table(
        [fill("tollTableColPiece"), fill("tollTableColToll")],
        tollTable.map((r) => [`${r.glyph} ${r.label}`, `${r.toll}s`]),
        ["", "r"],
      ),
      `**${fill("tollsNeverScaleTitle")}.** ${fill("tollsNeverScaleBody")}`,
      `**${fill("castlingDiscountTitle")}.** ${fill("castlingDiscountBody", {
        kingToll: pricedEvents.kingToll,
        rookToll,
        unbundled: pricedEvents.kingToll + rookToll,
        castleCost: pricedEvents.castleCost,
      })}`,
      `**${fill("zugzwangTitle")}.** ${fill("zugzwangBody")}`,
      `**${fill("avgMoveCostTitle", { avgToll })}.** ${fill("avgMoveCostBody", {
        tollDrain,
        movesPerPlayer: docModel.movesPerPlayer,
        heavyMoveShare,
        heavyTollShare,
      })}`,
      `### 3. ${fill("checkChipH3")}`,
      fill("checkChipIntro", {
        chip: pricedEvents.checkGain,
        multiplier: defaults.doubleCheckChipMultiplier,
        doubleChip: pricedEvents.doubleCheckLoss,
      }),
      `> ${fill("checkChipFormula", { chip: pricedEvents.checkGain })}`,
      table(
        [fill("checkTableColCheckBy"), fill("checkTableColGapSwing")],
        checkProfitTable.map((r) => [r.piece, r.verdict]),
        ["", "r"],
      ),
      fill("checkChipReadNote"),
      `### 4. ${fill("repetitionH3")}`,
      fill("repetitionIntro"),
      bullets([fill("repetitionLi1"), fill("repetitionLi2"), fill("repetitionLi3")]),
      fill("repetitionFootnote"),
      `### ${fill("otherEventsH3")}`,
      `**${promotionTitle}.** ${promotionBody}`,
      `**${fill("enPassantTitle")}.** ${fill("enPassantBody", { pawnCapture: pricedEvents.pawnCapture })}`,
      `### ${fill("endingH3")}`,
      endings.join("\n\n"),
      fill("endingFootnote"),
    ]),
    block([
      `## ${fill("economyH2")}`,
      fill("economyIntro"),
      `### ${fill("victoryH3")}`,
      fill("victoryIntro"),
      table(
        [fill("mateTableColEndgame"), fill("mateTableColMoves"), fill("mateTableColBill")],
        endgameTable.map((r) => [`${r.name} (${r.work})`, `~${r.moves}`, `~${r.bill}s`]),
        ["", "r", "r"],
      ),
      fill("mateTableFootnote"),
      fill("conversionBillPara", { ...poolNames, bill: billOf("K+R vs K") }),
      numbered([fill("mateLi1"), fill("mateLi2"), fill("mateLi3")]),
      `### ${fill("tradeMathH3")}`,
      fill("tradeMathIntro"),
      table(
        [
          fill("tradeMathColEconomy"),
          fill("tradeMathColYou"),
          fill("tradeMathColThem"),
          fill("tradeMathColGap"),
        ],
        tradeMathTable.map((r) => [r.label, r.you, r.them, r.effect]),
        ["", "r", "r", ""],
      ),
      fill("tradeMathFootnote", { equalTrade }),
      `### ${fill("queenCostH3", { queenToll, undiscounted: undiscountedQueenToll })}`,
      fill("queenCostPara", {
        undiscounted: undiscountedQueenToll,
        rookToll,
        bishopToll,
        tenXUndiscounted: 10 * undiscountedQueenToll,
        queenToll,
      }),
    ]),
    block([
      `## ${poolsHeading}`,
      fill("poolsIntro"),
      table(
        [
          fill("poolTableColPool"),
          fill("poolTableColTime"),
          fill("poolTableColMultiplier"),
          fill("poolTableColEconomy"),
        ],
        poolTable.map((r) => [r.label, r.time, r.multiplier, capitalize(r.economy)]),
        ["", "r", "r", "r"],
      ),
      fill("poolTableFootnote"),
      poolDetails,
    ]),
    block([
      `## ${fill("tempoH2")}`,
      `### ${fill("budgetH3")}`,
      fill("budgetIntro", { moves: docModel.movesPerPlayer }),
      table(
        [
          fill("budgetTableColPool"),
          fill("budgetTableColPoolPlayer"),
          fill("budgetTableColEconomyDrain"),
          fill("budgetTableColThinkingMove"),
          fill("budgetTableColFeelsLike"),
        ],
        thinkingBudgetTable.map((r) => [
          r.label,
          `${r.poolSeconds}s`,
          r.drain,
          `~${r.perMove}s`,
          r.feelsLike,
        ]),
        ["", "r", "", "r", "r"],
      ),
      bullets([
        fill("thinkingBudgetLi1", { tollDrain }),
        fill("thinkingBudgetLi2", {
          ladder: thinkingBudgetTable.map((r) => r.perMove).join(" \u2192 "),
        }),
        fill("thinkingBudgetLi3", { pools: highMultiplierSteal.join(" and ") }),
        fill("thinkingBudgetLi4", poolNames),
      ]),
      `### ${fill("durationH3")}`,
      fill("durationIntro"),
      table(
        [
          fill("durationTableColPool"),
          fill("durationTableColNominal"),
          fill("durationTableColCeiling"),
          fill("durationTableColBeginner"),
          fill("durationTableColIntermediate"),
          fill("durationTableColAdvanced"),
        ],
        durationTable.map((r) => [
          r.label,
          `${r.nominalMinutes} min`,
          `~${r.ceilingMinutes} min`,
          r.beginner,
          r.intermediate,
          r.advanced,
        ]),
        ["", "r", "r", "r", "r", "r"],
      ),
      bullets([
        fill("durationLi1", poolNames),
        fill("durationLi2", poolNames),
        fill("durationLi3", poolNames),
        fill("durationLi4", poolNames),
      ]),
    ]),
    block([
      `## ${fill("designH2")}`,
      `### ${fill("captureOnlyH3")}`,
      fill("captureOnlyPara1", poolNames),
      fill("captureOnlyPara2", {
        pawnCheck: bestCheckGap,
        kq: billOf("K+Q vs K"),
        kr: billOf("K+R vs K"),
      }),
      fill("captureOnlyPara3"),
      `### ${fill("promotionFreeH3")}`,
      fill("promotionFreePara"),
      `### ${fill("openDialsH3")}`,
      fill("openDial1", withPool("steal3")),
      fill("openDial2", withPool("steal2")),
      fill("openDial3", withPool("steal3")),
      fill("designFootnote"),
    ]),
    block([
      `## ${fill("glossaryH2")}`,
      glossary.map(([term, def]) => `**${term}.** ${def}`).join("\n\n"),
    ]),
    block([
      `## ${fill("licenceH2")}`,
      `${fill("licenceBody")} ${fill("licenceAttribution")} [${fill("licenceLinkLabel")}](https://creativecommons.org/licenses/by/4.0/).`,
    ]),
  ].join("\n\n") + "\n";
}

// ---------------------------------------------------------------------------------------------
// Write / check
// ---------------------------------------------------------------------------------------------

const outputs = [
  { path: TS_OUT, content: emitTypeScript() },
  { path: MD_OUT, content: emitMarkdown() },
];

const check = process.argv.includes("--check");
let stale = false;

for (const { path, content } of outputs) {
  const rel = relative(REPO, path).replace(/\\/g, "/");
  let current = null;
  try {
    current = readFileSync(path, "utf8");
  } catch {
    /* not generated yet */
  }
  const normalize = (s) => s.replace(/\r\n/g, "\n");

  if (current !== null && normalize(current) === normalize(content)) {
    if (check) console.log(`  ok    ${rel}`);
    continue;
  }

  if (check) {
    stale = true;
    console.error(`  STALE ${rel}`);
    continue;
  }

  writeFileSync(path, content, "utf8");
  console.log(`  wrote ${rel}`);
}

if (check && stale) {
  console.error(
    "\nGenerated rule files are out of date with config/kirichess.rules.json or the English /rules page.\n" +
      "Run `npm --prefix ui run gen:rules` and commit the result.",
  );
  process.exit(1);
}
