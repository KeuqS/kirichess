import { describe, expect, it } from "vitest";
import {
  analyzeClockSwings,
  dominantClockEvent,
  swingThreshold,
} from "./clockAnalysis";
import { DEFAULT_RULES } from "./rules.generated";
import type { ClockEventDto, ReplayPlyDto } from "./types";

/**
 * Builds a minimal, valid ReplayPlyDto fixture. Fields irrelevant to clockAnalysis.ts (fenAfter,
 * whiteTimeAfter, blackTimeAfter) get harmless placeholder values; only ply/san/uci/mover/
 * whiteDelta/blackDelta/events are ever varied per test.
 */
function makePly(
  overrides: Partial<ReplayPlyDto> & { ply: number },
): ReplayPlyDto {
  return {
    uci: "e2e4",
    san: "e4",
    mover: "White",
    fenAfter: "startpos",
    whiteTimeAfter: 0,
    blackTimeAfter: 0,
    whiteDelta: 0,
    blackDelta: 0,
    events: [],
    ...overrides,
  };
}

function ev(
  type: ClockEventDto["type"],
  side: ClockEventDto["side"],
  amount: number,
): ClockEventDto {
  return { type, side, amount, detail: null };
}

describe("swingThreshold", () => {
  it("swingThreshold(60) === 4 (floor wins over round(60*0.05)=3)", () => {
    expect(swingThreshold(60)).toBe(4);
  });

  it("swingThreshold(360) === 18", () => {
    expect(swingThreshold(360)).toBe(18);
  });

  it("swingThreshold(900) === 45", () => {
    expect(swingThreshold(900)).toBe(45);
  });

  it("swingThreshold(80) === 4 (round(80*0.05)=4, same as the floor)", () => {
    expect(swingThreshold(80)).toBe(4);
  });

  it("the 1-4s toll range quoted in swingThreshold's doc comment still matches the config (C-3-7)", () => {
    // Not a swingThreshold behaviour test - swingThreshold never reads moveCostPerPiece, the number
    // is only ever used as prose justifying the constant. This exists so a future
    // config/kirichess.rules.json edit that moves the per-piece toll range fails a test here instead
    // of quietly leaving the doc comment quoting a stale edition, which is exactly how it went stale
    // the first time (First Edition's 1-7 survived into the Second Edition's 1-4 unnoticed).
    const tolls = Object.values(DEFAULT_RULES.moveCostPerPiece);
    expect(Math.min(...tolls)).toBe(1);
    expect(Math.max(...tolls)).toBe(4);
  });
});

describe("dominantClockEvent", () => {
  it("returns null for an empty array", () => {
    expect(dominantClockEvent([])).toBeNull();
  });

  it("returns the single event's type when there is only one", () => {
    const events = [ev("MovementToll", "White", -2)];
    expect(dominantClockEvent(events)).toBe("MovementToll");
  });

  it("picks the largest |amount| regardless of order or sign: a -25 loss beats a +3 gain", () => {
    const events = [
      ev("CaptureGain", "White", 3),
      ev("CaptureLoss", "Black", -25),
      ev("MovementToll", "White", -1),
    ];
    expect(dominantClockEvent(events)).toBe("CaptureLoss");
  });

  it("still picks the largest |amount| when the biggest event comes first", () => {
    const events = [
      ev("CaptureLoss", "Black", -25),
      ev("CaptureGain", "White", 3),
    ];
    expect(dominantClockEvent(events)).toBe("CaptureLoss");
  });

  it("RepetitionNullified always wins even alongside a larger-magnitude event", () => {
    const events: ClockEventDto[] = [
      { type: "Increment", side: "White", amount: 10, detail: null },
      { type: "RepetitionNullified", side: "White", amount: 0, detail: null },
    ];
    expect(dominantClockEvent(events)).toBe("RepetitionNullified");
  });
});

describe("analyzeClockSwings: filtering (startTimeSeconds 360, threshold 18)", () => {
  it("excludes a ply whose gap swing is just under threshold (17)", () => {
    const plies = [
      makePly({ ply: 1, whiteDelta: -1, blackDelta: -18 }), // gapDelta = 17
    ];
    const result = analyzeClockSwings(plies, 360);
    expect(result.swings).toEqual([]);
  });

  it("includes a ply whose gap swing is exactly at threshold (18)", () => {
    const plies = [
      makePly({ ply: 1, whiteDelta: -1, blackDelta: -19 }), // gapDelta = 18
    ];
    const result = analyzeClockSwings(plies, 360);
    expect(result.swings).toHaveLength(1);
    expect(result.swings[0].magnitude).toBe(18);
  });

  it("includes a ply whose gap swing is above threshold (25)", () => {
    const plies = [
      makePly({ ply: 1, whiteDelta: 5, blackDelta: -20 }), // gapDelta = 25
    ];
    const result = analyzeClockSwings(plies, 360);
    expect(result.swings).toHaveLength(1);
    expect(result.swings[0].magnitude).toBe(25);
  });
});

describe("analyzeClockSwings: direction", () => {
  it("a ply favoring White yields favoredSide White, burdenedSide Black", () => {
    const plies = [
      makePly({ ply: 1, whiteDelta: 10, blackDelta: -20 }), // gapDelta = 30
    ];
    const result = analyzeClockSwings(plies, 360);
    expect(result.swings[0].favoredSide).toBe("White");
    expect(result.swings[0].burdenedSide).toBe("Black");
    expect(result.swings[0].gapDelta).toBe(30);
  });

  it("the mirror case favors Black", () => {
    const plies = [
      makePly({ ply: 1, whiteDelta: -20, blackDelta: 10 }), // gapDelta = -30
    ];
    const result = analyzeClockSwings(plies, 360);
    expect(result.swings[0].favoredSide).toBe("Black");
    expect(result.swings[0].burdenedSide).toBe("White");
    expect(result.swings[0].gapDelta).toBe(-30);
  });
});

describe("analyzeClockSwings: ordering and maxSwings", () => {
  it("with four qualifying plies, default maxSwings (3) returns exactly the three largest, sorted descending", () => {
    const plies = [
      makePly({ ply: 1, whiteDelta: 0, blackDelta: -20 }), // magnitude 20
      makePly({ ply: 2, whiteDelta: 0, blackDelta: -40 }), // magnitude 40
      makePly({ ply: 3, whiteDelta: 0, blackDelta: -30 }), // magnitude 30
      makePly({ ply: 4, whiteDelta: 0, blackDelta: -25 }), // magnitude 25
    ];
    const result = analyzeClockSwings(plies, 360);
    expect(result.swings.map((s) => s.ply)).toEqual([2, 3, 4]);
    expect(result.swings.map((s) => s.magnitude)).toEqual([40, 30, 25]);
  });
});

describe("analyzeClockSwings: category totals", () => {
  it("buckets each event type into the right category, per side, and zeroes out RepetitionNullified/FlagFall-only plies", () => {
    const captureply = makePly({
      ply: 1,
      whiteDelta: 8,
      blackDelta: -8,
      events: [ev("CaptureGain", "White", 8), ev("CaptureLoss", "Black", -8)],
    });
    const tollPly = makePly({
      ply: 2,
      whiteDelta: -2,
      blackDelta: 0,
      events: [ev("MovementToll", "White", -2)],
    });
    const checkPly = makePly({
      ply: 3,
      whiteDelta: 0,
      blackDelta: -3,
      events: [ev("CheckGain", "White", 3), ev("CheckLoss", "Black", -3)],
    });
    const incrementPly = makePly({
      ply: 4,
      whiteDelta: 2,
      blackDelta: 2,
      events: [ev("Increment", "White", 2), ev("Increment", "Black", 2)],
    });
    const nullifiedPly = makePly({
      ply: 5,
      whiteDelta: 0,
      blackDelta: 0,
      events: [
        ev("RepetitionNullified", "White", 0),
        ev("FlagFall", "Black", 0),
      ],
    });

    const result = analyzeClockSwings(
      [captureply, tollPly, checkPly, incrementPly, nullifiedPly],
      360,
    );

    expect(result.categoryTotals.White).toEqual({
      tolls: -2,
      captures: 8,
      checks: 3,
      increment: 2,
    });
    expect(result.categoryTotals.Black).toEqual({
      tolls: 0,
      captures: -8,
      checks: -3,
      increment: 2,
    });
  });

  it("buckets CastleCost and PromotionCost into tolls", () => {
    const plies = [
      makePly({
        ply: 1,
        events: [
          ev("CastleCost", "White", -8),
          ev("PromotionCost", "Black", -12),
        ],
      }),
    ];
    const result = analyzeClockSwings(plies, 360);
    expect(result.categoryTotals.White.tolls).toBe(-8);
    expect(result.categoryTotals.Black.tolls).toBe(-12);
  });
});

describe("analyzeClockSwings: empty game", () => {
  it("returns swings: [] and all-zero categoryTotals for both sides, no throw", () => {
    const result = analyzeClockSwings([], 360);
    expect(result.swings).toEqual([]);
    expect(result.categoryTotals.White).toEqual({
      tolls: 0,
      captures: 0,
      checks: 0,
      increment: 0,
    });
    expect(result.categoryTotals.Black).toEqual({
      tolls: 0,
      captures: 0,
      checks: 0,
      increment: 0,
    });
    expect(result.swingThresholdSeconds).toBe(18);
  });
});
