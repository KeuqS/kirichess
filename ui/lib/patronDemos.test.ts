import { describe, expect, it } from "vitest";
import { PATRON_DEMOS } from "./patronDemos";

describe("PATRON_DEMOS", () => {
  it("fills the donate wall with fifty distinct names", () => {
    expect(PATRON_DEMOS).toHaveLength(50);
    const names = PATRON_DEMOS.map((p) => p.name);
    expect(new Set(names).size).toBe(names.length);
  });
});
