import { describe, expect, it } from "vitest";
import { clockSyncFromState, displayClocks } from "./liveClock";

describe("clockSyncFromState", () => {
  it("subtracts the charged fraction from the side to move only", () => {
    const sync = clockSyncFromState(
      {
        whiteTime: 59,
        blackTime: 60,
        sideToMove: "White",
        activeClockOffsetMs: 900,
      },
      1_000,
    );

    expect(sync.time).toBe(1_000);
    expect(sync.whiteTime).toBeCloseTo(58.1);
    expect(sync.blackTime).toBe(60);
  });

  it("leaves both clocks whole when nothing has been charged yet", () => {
    const sync = clockSyncFromState(
      {
        whiteTime: 60,
        blackTime: 60,
        sideToMove: "Black",
        activeClockOffsetMs: 0,
      },
      0,
    );

    expect(sync.whiteTime).toBe(60);
    expect(sync.blackTime).toBe(60);
  });
});

describe("displayClocks", () => {
  it("counts the synced fraction down with monotonic time", () => {
    const sync = clockSyncFromState(
      {
        whiteTime: 59,
        blackTime: 60,
        sideToMove: "White",
        activeClockOffsetMs: 900,
      },
      0,
    );

    const shown = displayClocks(sync, 500, true);
    expect(shown.white).toBeCloseTo(57.6);
    expect(shown.black).toBe(60);
  });

  it("does not add time when the tick is older than the sync", () => {
    const sync = clockSyncFromState(
      {
        whiteTime: 10,
        blackTime: 9,
        sideToMove: "Black",
        activeClockOffsetMs: 250,
      },
      5_000,
    );

    const shown = displayClocks(sync, 4_900, true);
    expect(shown.black).toBeCloseTo(8.75);
    expect(shown.white).toBe(10);
  });

  it("stops at the synced clocks once the game is over", () => {
    const sync = clockSyncFromState(
      {
        whiteTime: 4,
        blackTime: 0,
        sideToMove: "Black",
        activeClockOffsetMs: 0,
      },
      0,
    );

    const shown = displayClocks(sync, 10_000, false);
    expect(shown.white).toBe(4);
    expect(shown.black).toBe(0);
  });
});
