import { describe, expect, it } from "vitest";
import { readFileSync } from "node:fs";
import { join } from "node:path";

/**
 * Regression guard for P-1-1: `/leaderboards`, `/watch` and `/u/[id]` each used to carry a
 * `const SOME_FLAG: boolean = true;` demo/mock fixture that shipped enabled, rendering
 * fabricated data (a leaderboard of grandmaster names, "live" games that weren't live, a fake
 * public profile) to every visitor. The fix removed the fixtures and the flags entirely; this
 * test is what stops one of the three from quietly coming back as `= true` in a future change,
 * the same way `styling.test.ts` guards `.claude/rules/styling.md`.
 *
 * Deliberately narrow: it only checks that these three specific flag names are not declared as a
 * truthy `boolean` literal in their known home files. It does not try to catch a fourth,
 * differently-named fixture flag - that needs a human reading the diff, same as any other new
 * form of drift this mechanical check cannot anticipate.
 */

const UI_ROOT = join(__dirname, "..");

const FIXTURE_FLAGS: { file: string; flag: string }[] = [
  { file: "app/[locale]/leaderboards/page.tsx", flag: "USE_DUMMY_ROWS" },
  { file: "app/[locale]/watch/page.tsx", flag: "SAMPLE" },
  { file: "app/[locale]/u/[id]/page.tsx", flag: "USE_DUMMY_NAKAMURA" },
];

describe("P-1-1: no demo/mock fixture flag ships enabled", () => {
  for (const { file, flag } of FIXTURE_FLAGS) {
    it(`${file} does not declare ${flag} = true`, () => {
      const source = readFileSync(join(UI_ROOT, file), "utf8");
      // Matches `const FLAG: boolean = true;`, `const FLAG = true;`, `let FLAG = true`, with or
      // without the type annotation and with any amount of whitespace - narrow to this exact
      // declaration shape rather than a bare `/true/` scan, which would flag unrelated code.
      const truthyDeclaration = new RegExp(
        `\\b(?:const|let)\\s+${flag}\\s*(?::\\s*boolean\\s*)?=\\s*true\\b`,
      );
      expect(
        truthyDeclaration.test(source),
        `${file} declares ${flag} = true - P-1-1's demo/mock fixture is enabled again. ` +
          `Flip it to false, or better, remove the flag and its dead branch entirely.`,
      ).toBe(false);
    });
  }

  it("still finds the files it is supposed to be scanning", () => {
    // Guards against a rename or a moved file silently making every check above pass for the
    // wrong reason (a missing file throws on readFileSync, which is also a failure, but this
    // makes the intent explicit).
    for (const { file } of FIXTURE_FLAGS) {
      expect(() => readFileSync(join(UI_ROOT, file), "utf8")).not.toThrow();
    }
  });
});
