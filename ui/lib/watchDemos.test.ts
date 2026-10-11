import { describe, expect, it } from "vitest";
import { parseBoard } from "./fen";
import { isPresetKey, PRESETS } from "./presets";
import { WATCH_DEMO_GAMES } from "./watchDemos";

describe("WATCH_DEMO_GAMES", () => {
  it("fills the watch grid with twelve distinct boards", () => {
    expect(WATCH_DEMO_GAMES).toHaveLength(12);
    const ids = WATCH_DEMO_GAMES.map((g) => g.gameId);
    expect(new Set(ids).size).toBe(ids.length);
  });

  it("gives every card a position that MiniBoard can draw", () => {
    for (const game of WATCH_DEMO_GAMES) {
      expect(game.fen).toBeTruthy();
      expect(Object.keys(parseBoard(game.fen!))).toHaveLength(64);
    }
  });

  it("keeps remaining clocks inside the pool's start time", () => {
    for (const game of WATCH_DEMO_GAMES) {
      const start =
        game.pool && isPresetKey(game.pool)
          ? PRESETS[game.pool].startTimeSeconds
          : 300;
      expect(game.whiteTime).toBeGreaterThanOrEqual(0);
      expect(game.blackTime).toBeGreaterThanOrEqual(0);
      expect(game.whiteTime).toBeLessThanOrEqual(start);
      expect(game.blackTime).toBeLessThanOrEqual(start);
    }
  });
});
