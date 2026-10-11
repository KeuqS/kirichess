import { describe, expect, it } from "vitest";
import { gapFromClocks } from "./GapMeter";

describe("gapFromClocks", () => {
  it("matches floored clock faces, not a rounded difference", () => {
    // Clocks show 1:00 and 0:55 (floor 60.9 and 55.1); round(5.8) would be 6.
    expect(gapFromClocks(60.9, 55.1)).toBe(5);
  });

  it("clamps negative remaining at zero like Clock", () => {
    expect(gapFromClocks(-1.2, 4.9)).toBe(-4);
  });
});
