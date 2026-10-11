import { describe, expect, it, vi } from "vitest";
import {
  BotSearchAbandonedError,
  createBotEngine,
  getRandomMove,
  isBotSearchAbandoned,
  mapEloToSearch,
  type BotWorkerLike,
} from "./bot";

/**
 * C-10-8: the lockstep claim on mapEloToSearch's own doc comment is the thing this file exists to
 * check. Expected numbers below are computed directly from simulator/KiriChess.Simulator/BotSkill.cs's
 * BotSkill.ForElo (not re-derived from bot.ts itself, which would only prove the TypeScript agrees
 * with itself), at the four boundary values the audit packet (docs/audits/C.md, C-10-8) names:
 * 400, 799, 800, 3000.
 *
 * BotSkill.ForElo(400):  targetElo=400, randomChance = 1 - ((400-400)/400.0)   = 1,    skillLevel=0 (targetElo<800), depth: targetElo<1000 => 1
 * BotSkill.ForElo(799):  targetElo=799, randomChance = 1 - ((799-400)/400.0)   = 0.0025, skillLevel=0 (targetElo<800), depth: targetElo<1000 => 1
 * BotSkill.ForElo(800):  targetElo=800, randomChance = 0 (targetElo>=800),     skillLevel = floor(((800-800)/2200)*20) = 0, depth: targetElo<1000 => 1
 * BotSkill.ForElo(3000): targetElo=3000, randomChance = 0 (targetElo>=800),    skillLevel = floor(((3000-800)/2200)*20) = 20, depth: default => 15
 */
describe("mapEloToSearch matches BotSkill.ForElo at the named boundaries", () => {
  it("400 (floor, all-random)", () => {
    const result = mapEloToSearch(400);
    expect(result.targetElo).toBe(400);
    expect(result.randomChance).toBe(1);
    expect(result.skillLevel).toBe(0);
    expect(result.depth).toBe(1);
  });

  it("799 (just under the random-blend cutoff)", () => {
    const result = mapEloToSearch(799);
    expect(result.targetElo).toBe(799);
    expect(result.randomChance).toBeCloseTo(0.0025, 10);
    expect(result.skillLevel).toBe(0);
    expect(result.depth).toBe(1);
  });

  it("800 (random blend turns off, Skill Level engages)", () => {
    const result = mapEloToSearch(800);
    expect(result.targetElo).toBe(800);
    expect(result.randomChance).toBe(0);
    expect(result.skillLevel).toBe(0);
    expect(result.depth).toBe(1);
  });

  it("3000 (ceiling, maximum skill and depth)", () => {
    const result = mapEloToSearch(3000);
    expect(result.targetElo).toBe(3000);
    expect(result.randomChance).toBe(0);
    expect(result.skillLevel).toBe(20);
    expect(result.depth).toBe(15);
  });

  it("clamps below 400 and above 3000 to the same endpoints BotSkill.ForElo clamps to", () => {
    expect(mapEloToSearch(0).targetElo).toBe(400);
    expect(mapEloToSearch(999999).targetElo).toBe(3000);
  });
});

describe("getRandomMove", () => {
  it("returns an empty string for an empty list rather than throwing or returning undefined", () => {
    expect(getRandomMove([])).toBe("");
  });

  it("returns the only move for a single-element list", () => {
    expect(getRandomMove(["e2e4"])).toBe("e2e4");
  });
});

/**
 * A stand-in for the Stockfish worker. It records what was posted and lets a test deliver a
 * `bestmove` line at the exact moment it wants to, which is the whole point: the defect C-1-7
 * describes is an ordering one, and a real worker cannot be asked to reply late.
 */
class FakeWorker implements BotWorkerLike {
  readonly posted: string[] = [];
  terminated = 0;
  private listener: ((event: { data: unknown }) => void) | null = null;

  postMessage(message: string) {
    this.posted.push(message);
  }

  terminate() {
    this.terminated += 1;
  }

  addEventListener(
    _type: "message",
    listener: (event: { data: unknown }) => void,
  ) {
    this.listener = listener;
  }

  /** Delivers one line from the engine, exactly as `onmessage` would. */
  emit(line: string) {
    this.listener?.({ data: line });
  }
}

/**
 * C-1-7. The worker used to be a module singleton with one `resolveBestMove` slot, so a second
 * search simply overwrote the first one's resolver and whichever `bestmove` arrived next resolved
 * whatever promise happened to be sitting there. Across a route change that meant one game's search
 * answering another game's turn, and within a game it meant a move computed for the previous
 * position being played into the current one.
 */
describe("createBotEngine search scoping", () => {
  // 3000 Elo: randomChance is 0, so every getMove below really does reach the worker and no test
  // here depends on Math.random.
  const STRONG = 3000;

  it("delivers a reply to the search it belongs to, not to whichever promise is pending", async () => {
    const worker = new FakeWorker();
    const engine = createBotEngine(() => worker);

    const first = engine.getMove("fen-one", STRONG, ["a2a3"]);
    first.catch(() => {}); // superseded below; asserted separately
    const second = engine.getMove("fen-two", STRONG, ["b2b3"]);

    // The first search's reply lands first, because it was already running when the second
    // started. It must not answer the second search.
    worker.emit("bestmove a2a3 ponder h7h6");
    worker.emit("bestmove b2b3 ponder h7h6");

    await expect(second).resolves.toBe("b2b3");
  });

  it("rejects a superseded search rather than leaving its caller waiting forever", async () => {
    const worker = new FakeWorker();
    const engine = createBotEngine(() => worker);

    const first = engine.getMove("fen-one", STRONG, ["a2a3"]);
    const second = engine.getMove("fen-two", STRONG, ["b2b3"]);
    second.catch(() => {});

    await expect(first).rejects.toBeInstanceOf(BotSearchAbandonedError);
    await expect(first.catch((e) => isBotSearchAbandoned(e))).resolves.toBe(
      true,
    );
  });

  it("stops the outstanding search before starting the next one", async () => {
    const worker = new FakeWorker();
    const engine = createBotEngine(() => worker);

    const first = engine.getMove("fen-one", STRONG, ["a2a3"]);
    first.catch(() => {});
    engine.getMove("fen-two", STRONG, ["b2b3"]).catch(() => {});

    expect(worker.posted).toContain("stop");
    // ...and not when nothing is running: the first search sent no stop of its own.
    expect(worker.posted.indexOf("stop")).toBeGreaterThan(
      worker.posted.indexOf("go depth 15"),
    );
  });

  it("terminates the worker on dispose and abandons the search in flight", async () => {
    const worker = new FakeWorker();
    const engine = createBotEngine(() => worker);

    const pending = engine.getMove("fen-one", STRONG, ["a2a3"]);
    // The assertion is attached before dispose rejects it, so a red run reports the assertion
    // that failed rather than an unhandled rejection from the one that never got a handler.
    const abandoned = expect(pending).rejects.toBeInstanceOf(
      BotSearchAbandonedError,
    );
    engine.dispose();
    await abandoned;

    expect(worker.terminated).toBe(1);

    // A late reply from a worker that has already been told to stop resolves nothing.
    worker.emit("bestmove a2a3");
    await expect(
      engine.getMove("fen-two", STRONG, ["b2b3"]),
    ).rejects.toBeInstanceOf(BotSearchAbandonedError);
    expect(worker.terminated).toBe(1); // dispose is idempotent
    engine.dispose();
    expect(worker.terminated).toBe(1);
  });

  it("gives each game its own worker", () => {
    const first = new FakeWorker();
    const second = new FakeWorker();
    const workers = [first, second];

    const engineA = createBotEngine(() => workers.shift()!);
    const engineB = createBotEngine(() => workers.shift()!);
    engineA.getMove("fen-a", STRONG, ["a2a3"]).catch(() => {});
    engineB.getMove("fen-b", STRONG, ["b2b3"]).catch(() => {});

    expect(first.posted).toContain("position fen fen-a");
    expect(first.posted).not.toContain("position fen fen-b");
    expect(second.posted).toContain("position fen fen-b");

    engineA.dispose();
    expect(first.terminated).toBe(1);
    expect(second.terminated).toBe(0);
  });

  it("answers the random-move blend without spending a search on the worker", async () => {
    const worker = new FakeWorker();
    const engine = createBotEngine(() => worker);
    const random = vi.spyOn(Math, "random").mockReturnValue(0);

    try {
      // 400 Elo is randomChance 1, so this never reaches Stockfish.
      await expect(engine.getMove("fen-one", 400, ["e2e4"])).resolves.toBe(
        "e2e4",
      );
      expect(worker.posted).toHaveLength(0);
    } finally {
      random.mockRestore();
    }
  });
});
