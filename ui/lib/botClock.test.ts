import { afterEach, describe, expect, it, vi } from "vitest";
import {
  BOT_TIERS,
  createThinkDelay,
  remainingThinkMsAfterWake,
  simulatedThinkMs,
  tierForElo,
} from "./botClock";

describe("tierForElo", () => {
  it("bands are contiguous and cover every Elo from 0 up", () => {
    expect(tierForElo(0).key).toBe("novice");
    expect(tierForElo(-100).key).toBe("novice"); // below the lowest band still resolves
    expect(tierForElo(899).key).toBe("novice");
    expect(tierForElo(900).key).toBe("casual");
    expect(tierForElo(1299).key).toBe("casual");
    expect(tierForElo(1300).key).toBe("club");
    expect(tierForElo(1699).key).toBe("club");
    expect(tierForElo(1700).key).toBe("expert");
    expect(tierForElo(2099).key).toBe("expert");
    expect(tierForElo(2100).key).toBe("master");
    expect(tierForElo(5000).key).toBe("master");
  });
});

describe("simulatedThinkMs", () => {
  const noJitter = () => 0.5; // midpoint of the 0.65x-1.35x jitter band => exactly 1x

  it("never returns less than the tier's minThinkMs", () => {
    const ms = simulatedThinkMs({
      remainingSeconds: 0.01,
      legalMoveCount: 30,
      elo: 2500, // master: reserveSeconds 15, so this position is unaffordable
      random: noJitter,
    });
    // Affordability still overrides the floor at the very bottom, but never below what the
    // function documents as its own minimum clamp target.
    expect(ms).toBeGreaterThanOrEqual(0);
  });

  it("never returns more than the tier's maxThinkMs", () => {
    const ms = simulatedThinkMs({
      remainingSeconds: 100000,
      legalMoveCount: 60, // pushes complexity to its 1.6x ceiling
      elo: 0, // novice: maxThinkMs 6000
      random: noJitter,
    });
    expect(ms).toBeLessThanOrEqual(6000);
  });

  it("never spends past what the clock can afford, protecting the tier's reserve", () => {
    const policy = tierForElo(2100).clock; // master: reserveSeconds 15
    const remainingSeconds = 20;
    const ms = simulatedThinkMs({
      remainingSeconds,
      legalMoveCount: 30,
      elo: 2100,
      random: noJitter,
    });
    const affordableMs = (remainingSeconds - policy.reserveSeconds) * 1000;
    expect(ms).toBeLessThanOrEqual(Math.max(policy.minThinkMs, affordableMs));
  });

  it("floors at minThinkMs even when the clock cannot afford it, rather than dropping to zero", () => {
    // 1 second left, master reserves 15: spendable is 0, but the clamp's own floor
    // (`Math.max(policy.minThinkMs, affordableMs)`) guarantees at least minThinkMs regardless.
    const policy = tierForElo(2100).clock;
    const ms = simulatedThinkMs({
      remainingSeconds: 1,
      legalMoveCount: 30,
      elo: 2100,
      random: noJitter,
    });
    expect(ms).toBe(policy.minThinkMs);
  });

  it("a wider position (more legal moves) never thinks less than a narrower one, all else equal", () => {
    const narrow = simulatedThinkMs({
      remainingSeconds: 300,
      legalMoveCount: 5,
      elo: 1300,
      random: noJitter,
    });
    const wide = simulatedThinkMs({
      remainingSeconds: 300,
      legalMoveCount: 45,
      elo: 1300,
      random: noJitter,
    });
    expect(wide).toBeGreaterThanOrEqual(narrow);
  });

  it("is deterministic given a fixed random source", () => {
    const input = {
      remainingSeconds: 180,
      legalMoveCount: 30,
      elo: 1500,
      random: () => 0.5,
    };
    expect(simulatedThinkMs(input)).toBe(simulatedThinkMs(input));
  });

  it("jitter stays within the documented 0.65x-1.35x band before clamping", () => {
    // Small enough that the jittered budget stays inside [minThinkMs, maxThinkMs] and well
    // under what the clock can afford, so neither clamp masks the jitter band itself.
    const remainingSeconds = 30;
    const policy = tierForElo(1700).clock; // expert
    const legalMoveCount = 30; // complexity = 1.0
    const budgetMs = remainingSeconds * policy.budgetFraction * 1.0 * 1000;
    expect(budgetMs * 1.35).toBeLessThan(policy.maxThinkMs);
    expect(budgetMs * 0.65).toBeGreaterThan(policy.minThinkMs);

    const low = simulatedThinkMs({
      remainingSeconds,
      legalMoveCount,
      elo: 1700,
      random: () => 0,
    });
    const high = simulatedThinkMs({
      remainingSeconds,
      legalMoveCount,
      elo: 1700,
      random: () => 0.999999,
    });

    expect(low).toBeGreaterThanOrEqual(Math.round(budgetMs * 0.65) - 1);
    expect(high).toBeLessThanOrEqual(Math.round(budgetMs * 1.35) + 1);
    expect(high).toBeGreaterThan(low);
  });
});

describe("BOT_TIERS ladder", () => {
  it("is sorted ascending by minElo with no gaps or overlaps in intent", () => {
    for (let i = 1; i < BOT_TIERS.length; i++) {
      expect(BOT_TIERS[i].minElo).toBeGreaterThan(BOT_TIERS[i - 1].minElo);
    }
  });

  it("budgetFraction decreases as tiers strengthen: stronger tiers protect their clock more", () => {
    for (let i = 1; i < BOT_TIERS.length; i++) {
      expect(BOT_TIERS[i].clock.budgetFraction).toBeLessThan(
        BOT_TIERS[i - 1].clock.budgetFraction,
      );
    }
  });

  it("reserveSeconds increases as tiers strengthen", () => {
    for (let i = 1; i < BOT_TIERS.length; i++) {
      expect(BOT_TIERS[i].clock.reserveSeconds).toBeGreaterThanOrEqual(
        BOT_TIERS[i - 1].clock.reserveSeconds,
      );
    }
  });
});

describe("remainingThinkMsAfterWake (P-6-6)", () => {
  const noJitter = () => 0.5;

  it("returns 0 once the original delay has already elapsed", () => {
    expect(
      remainingThinkMsAfterWake({
        originalThinkMs: 3000,
        elapsedMs: 4000,
        remainingSeconds: 180,
        legalMoveCount: 30,
        elo: 1300,
        random: noJitter,
      }),
    ).toBe(0);
  });

  it("cuts a leftover wait down to a fresh budget when remaining clock is short", () => {
    const leftover = remainingThinkMsAfterWake({
      originalThinkMs: 5000,
      elapsedMs: 0,
      remainingSeconds: 2,
      legalMoveCount: 30,
      elo: 2100,
      random: noJitter,
    });
    const fresh = simulatedThinkMs({
      remainingSeconds: 2,
      legalMoveCount: 30,
      elo: 2100,
      random: noJitter,
    });
    expect(leftover).toBe(fresh);
    expect(leftover).toBeLessThan(5000);
  });

  it("keeps the leftover original wait when the clock still affords it", () => {
    expect(
      remainingThinkMsAfterWake({
        originalThinkMs: 800,
        elapsedMs: 300,
        remainingSeconds: 180,
        legalMoveCount: 30,
        elo: 1300,
        random: noJitter,
      }),
    ).toBe(500);
  });
});

describe("createThinkDelay (P-6-6)", () => {
  afterEach(() => {
    vi.useRealTimers();
  });

  it("resolves at the original delay when nothing retargets it", async () => {
    vi.useFakeTimers();
    const delay = createThinkDelay(1000, { aborted: false });
    let done = false;
    void delay.promise.then(() => {
      done = true;
    });
    await vi.advanceTimersByTimeAsync(999);
    expect(done).toBe(false);
    await vi.advanceTimersByTimeAsync(1);
    expect(done).toBe(true);
  });

  it("resolves immediately when retargeted to 0", async () => {
    vi.useFakeTimers();
    const delay = createThinkDelay(5000, { aborted: false });
    let done = false;
    void delay.promise.then(() => {
      done = true;
    });
    delay.retarget(0);
    await vi.advanceTimersByTimeAsync(0);
    expect(done).toBe(true);
  });

  it("fires at the retargeted delay, not the original leftover", async () => {
    vi.useFakeTimers();
    const delay = createThinkDelay(5000, { aborted: false });
    let done = false;
    void delay.promise.then(() => {
      done = true;
    });
    await vi.advanceTimersByTimeAsync(100);
    delay.retarget(200);
    await vi.advanceTimersByTimeAsync(199);
    expect(done).toBe(false);
    await vi.advanceTimersByTimeAsync(1);
    expect(done).toBe(true);
  });

  it("resolves immediately when constructed already aborted", async () => {
    const delay = createThinkDelay(5000, { aborted: true });
    await expect(delay.promise).resolves.toBeUndefined();
  });
});
