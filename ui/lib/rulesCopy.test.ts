import { readFileSync } from "node:fs";
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import { routing } from "../i18n/routing";

/**
 * The rules page is the manual for an economy the engine actually runs, and every number on it is
 * generated from config/kirichess.rules.json (see .claude/rules/engine.md). The page itself obeys
 * that: it reads `@/lib/rules.generated`. The catalogue is the hole, since a price written into a
 * sentence in messages/*.json is invisible to `gen:rules:check` and survives any retune.
 *
 * So: a priced quantity in the copy has to arrive as an ICU placeholder. Breaking this means
 * threading the value through `poolNumbers()` in app/[locale]/rules/page.tsx (or the equivalent
 * for `/learn`), never widening the pattern below. Modelling observations that are not engine
 * values (how many moves a beginner's game runs) are prose and carry no unit, which is why the
 * match is on units, not on digits.
 *
 * C-9-3: this guarded only the `rules` namespace, but `/learn` teaches the same clock economy
 * (capture presets, move tolls, check chip, net clock effect) from the same catalogue, under the
 * `learn` namespace, and a hardcoded number there would survive a retune exactly the same way. The
 * fix is NOT to blindly scan the whole catalogue: a check of every namespace turned up a hit
 * outside `rules`/`learn`, in `custom` (`startTimeMinError`'s "1 second"), and it is not a
 * `config/kirichess.rules.json` value: it is a UI form-validation floor hardcoded in
 * `app/[locale]/custom/page.tsx` (`c.startTimeSeconds < 1`). Scanning
 * the whole catalogue would make this guard fail on namespaces it was never meant to police and
 * that don't fit its "config could move this" premise. So: iterate the specific namespaces that
 * quote the engine's own economy, `rules` and `learn`, the same way the fix note in C-9-3 suggests
 * ("`learn` at minimum").
 */

const root = join(__dirname, "..");

/** A number immediately qualified by a clock or share unit, in either locale's wording. */
const PRICED =
  /(?<![\w{])\d+(?:[.,]\d+)?\s*(?:s\b|seconds?\b|seconden?\b|%|-?\s?minutes?\b|-?\s?minuten\b|-?\s?minuut\b)/gi;

/** Namespaces in messages/*.json that describe the engine's clock economy in prose. */
const ECONOMY_NAMESPACES = ["rules", "learn"] as const;

function namespaceCopy(
  locale: string,
  namespace: (typeof ECONOMY_NAMESPACES)[number],
): Record<string, string> {
  const catalogue = JSON.parse(
    readFileSync(join(root, "messages", `${locale}.json`), "utf8"),
  );
  return Object.fromEntries(
    Object.entries(catalogue[namespace] as Record<string, unknown>).filter(
      (entry): entry is [string, string] => typeof entry[1] === "string",
    ),
  );
}

describe("rules copy prices", () => {
  for (const locale of routing.locales) {
    for (const namespace of ECONOMY_NAMESPACES) {
      it(`${locale} ${namespace} quotes no price the config could move`, () => {
        const offenders: string[] = [];
        for (const [key, value] of Object.entries(
          namespaceCopy(locale, namespace),
        )) {
          // Placeholders are the sanctioned way to carry a price, so blank them before matching.
          const hits = value.replace(/\{[^}]*\}/g, "@").match(PRICED);
          if (hits) offenders.push(`${namespace}.${key}: ${hits.join(", ")}`);
        }
        expect(offenders).toEqual([]);
      });
    }
  }
});
