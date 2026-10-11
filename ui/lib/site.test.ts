import { readFileSync } from "node:fs";
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import { WEEKLY_MEETUP } from "./site";

describe("WEEKLY_MEETUP", () => {
  it("matches PresenceIndicator.playHour in en.json (11a.4)", () => {
    const en = JSON.parse(
      readFileSync(join(__dirname, "../messages/en.json"), "utf8"),
    ) as { PresenceIndicator: { playHour: string } };
    expect(en.PresenceIndicator.playHour).toBe(WEEKLY_MEETUP);
  });
});
