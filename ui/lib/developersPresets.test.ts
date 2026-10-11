import { readFileSync } from "node:fs";
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import { RATED_POOL_KEYS } from "./rules.generated";

/**
 * M-6-1: `/developers` once told bot authors to `JoinQueue` with "an official preset (bounty1,
 * flagfall, ...)" - `flagfall` has never been a real pool, so a bot author who copied the example
 * hit `HubException` with nothing pointing at the preset name as the cause (the one real preset
 * named, `bounty1`, made the error look intermittent rather than a copy-paste mistake).
 *
 * This guards the fix from drifting the same way again: every preset id named in
 * `developers.playPara` (English is the source of the fact; the other five catalogues carry the
 * same literal ids inside their own translated sentence) must be exactly the set of real rated
 * pools, `RATED_POOL_KEYS` - generated from `config/kirichess.rules.json` and already gated by
 * `npm run gen:rules:check` (see .claude/rules/engine.md). A pool renamed or added in config and
 * not carried into this sentence fails here instead of silently teaching a wrong name again.
 */

const root = join(__dirname, "..");

function playPara(locale: string): string {
  const raw = JSON.parse(
    readFileSync(join(root, "messages", `${locale}.json`), "utf8"),
  );
  return raw.developers.playPara as string;
}

/** Pulls the comma-separated preset ids out of the first parenthesised list in the sentence. */
function namedPresets(text: string): string[] {
  const match = text.match(/\(([^)]*)\)/);
  if (!match) throw new Error("playPara names no parenthesised preset list");
  return match[1]
    .split(",")
    .map((s) => s.trim())
    .filter((s) => s.length > 0 && s !== "…" && s !== "...");
}

describe("developers page preset names", () => {
  it("names every real rated pool and nothing else, in English", () => {
    const named = namedPresets(playPara("en")).sort();
    const real = [...RATED_POOL_KEYS].sort();
    expect(named).toEqual(real);
  });
});
