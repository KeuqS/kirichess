import type {
  ClockEventDto,
  ClockEventType,
  ReplayPlyDto,
  Side,
} from "./types";

/**
 * How much a game's clock has to swing in one ply before it counts as a moment worth naming (Phase
 * 9e.3). Scaled to the game's own time control rather than a flat number of seconds: an ordinary
 * quiet move costs 1-4s in toll in every pool (movement cost is pool-invariant - see
 * DEFAULT_RULES.moveCostPerPiece in rules.generated.ts, pawn/king 1 up to queen 4 in the shipped
 * Second Edition table), while captureMult and startTimeSeconds both vary by pool, so a flat
 * cutoff either flags routine moves in a short game or misses real swings in a long one. See Phase
 * 9e.3 "Decision 2" for the full reasoning and the pool-by-pool numbers this was checked against.
 * (C-3-7: this used to quote the First Edition's 1-7 range, one edition out of date with the config
 * it was justifying a constant against - see clockAnalysis.test.ts for a test that keeps the two in
 * step.)
 */
export function swingThreshold(startTimeSeconds: number): number {
  return Math.max(4, Math.round(startTimeSeconds * 0.05));
}

export type ClockCategory = "tolls" | "captures" | "checks" | "increment";

/** Display order for the category-totals table. Kept as an explicit list rather than relying on
 * object key order. */
export const CLOCK_CATEGORIES: readonly ClockCategory[] = [
  "tolls",
  "captures",
  "checks",
  "increment",
];

const CATEGORY_OF: Partial<Record<ClockEventType, ClockCategory>> = {
  MovementToll: "tolls",
  CastleCost: "tolls",
  PromotionCost: "tolls",
  CaptureGain: "captures",
  CaptureLoss: "captures",
  CheckGain: "checks",
  CheckLoss: "checks",
  Increment: "increment",
};

export interface ClockSwing {
  ply: number;
  san: string;
  uci: string;
  mover: Side;
  /** whiteDelta - blackDelta for this ply. Positive = the gap moved toward White. */
  gapDelta: number;
  magnitude: number;
  favoredSide: Side;
  burdenedSide: Side;
  /** The single event judged to explain the swing. See README.md "Decision 3". Always present for
   * a swing that clears the threshold, since a nonzero gapDelta always traces to at least one event. */
  dominantEvent: ClockEventType | null;
}

export type ClockCategoryTotals = Record<Side, Record<ClockCategory, number>>;

export interface ClockAnalysis {
  swings: ClockSwing[];
  categoryTotals: ClockCategoryTotals;
  swingThresholdSeconds: number;
}

/**
 * Picks the one event that explains a flagged ply. RepetitionNullified always wins when present
 * (the economy was cancelled outright, not merely dominated by one line); otherwise the largest
 * |amount| across both sides wins. See README.md "Decision 3".
 */
export function dominantClockEvent(
  events: readonly ClockEventDto[],
): ClockEventType | null {
  if (events.length === 0) return null;
  const nullified = events.find((e) => e.type === "RepetitionNullified");
  if (nullified) return nullified.type;
  return events.reduce((best, e) =>
    Math.abs(e.amount) > Math.abs(best.amount) ? e : best,
  ).type;
}

function emptyCategoryTotals(): Record<ClockCategory, number> {
  return { tolls: 0, captures: 0, checks: 0, increment: 0 };
}

function computeCategoryTotals(
  plies: readonly ReplayPlyDto[],
): ClockCategoryTotals {
  const white = emptyCategoryTotals();
  const black = emptyCategoryTotals();
  for (const ply of plies) {
    for (const ev of ply.events) {
      const category = CATEGORY_OF[ev.type];
      if (!category) continue;
      const bucket = ev.side === "White" ? white : black;
      bucket[category] += ev.amount;
    }
  }
  return { White: white, Black: black };
}

/**
 * The whole-game clock analysis (Phase 9e.3): the biggest gap-moving moments (up to `maxSwings`,
 * sorted largest first) and the seconds-by-category breakdown for the full game. Pure function of
 * the replay's own plies, no new data (README.md fact 1).
 */
export function analyzeClockSwings(
  plies: readonly ReplayPlyDto[],
  startTimeSeconds: number,
  maxSwings = 3,
): ClockAnalysis {
  const threshold = swingThreshold(startTimeSeconds);

  const swings: ClockSwing[] = plies
    .map((p): ClockSwing => {
      const gapDelta = p.whiteDelta - p.blackDelta;
      return {
        ply: p.ply,
        san: p.san,
        uci: p.uci,
        mover: p.mover,
        gapDelta,
        magnitude: Math.abs(gapDelta),
        favoredSide: gapDelta > 0 ? "White" : "Black",
        burdenedSide: gapDelta > 0 ? "Black" : "White",
        dominantEvent: dominantClockEvent(p.events),
      };
    })
    .filter((s) => s.magnitude >= threshold)
    .sort((a, b) => b.magnitude - a.magnitude)
    .slice(0, maxSwings);

  return {
    swings,
    categoryTotals: computeCategoryTotals(plies),
    swingThresholdSeconds: threshold,
  };
}
