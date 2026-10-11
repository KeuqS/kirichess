import { readFileSync } from "node:fs";
import { join } from "node:path";
import { describe, expect, it } from "vitest";

/**
 * P-8-5. The three files that skipped the i18n migration must stay on useTranslations.
 * `react/jsx-no-literals` in eslint.config.mjs is the AST-level half; this pins the
 * player-facing English that used to ship as JSX text so a disable comment cannot sneak it back.
 */

const UI_ROOT = join(__dirname, "..");

const FILES = [
  "components/AccessibleBoard.tsx",
  "components/TwoFactorPrompt.tsx",
  "app/[locale]/error.tsx",
] as const;

const FORBIDDEN = [
  "Two-factor authentication",
  "Something went wrong",
  "Accessible chess board. You are playing",
  "This page hit an unexpected error",
];

describe("P-8-5: the former hardcoded-English files stay in the catalogue", () => {
  for (const file of FILES) {
    it(`${file} calls useTranslations and has none of the old JSX English`, () => {
      const source = readFileSync(join(UI_ROOT, file), "utf8");
      expect(source).toMatch(/useTranslations\(/);
      for (const needle of FORBIDDEN) {
        expect(source, needle).not.toContain(needle);
      }
    });
  }

  it("enables react/jsx-no-literals on those three files", () => {
    const config = readFileSync(join(UI_ROOT, "eslint.config.mjs"), "utf8");
    expect(config).toMatch(/react\/jsx-no-literals/);
    expect(config).toMatch(/AccessibleBoard\.tsx/);
    expect(config).toMatch(/TwoFactorPrompt\.tsx/);
    expect(config).toMatch(/error\.tsx/);
  });
});
