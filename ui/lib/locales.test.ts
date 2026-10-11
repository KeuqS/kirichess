import { readFileSync } from "node:fs";
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import { LOCALE_LABELS, routing } from "../i18n/routing";
// A plain .mjs generator module: dependency-free on purpose, and shared with CI so the
// script and the guard can never disagree about what counts as a placeholder.
import { structuralTokens } from "../scripts/icu-protect.mjs";

/**
 * Phase 9f.4. The drift guard for the two places that mirror the locale list but cannot import it.
 *
 * `public/sw.js` is served straight out of public/ with no build step, and `messages/*.json` is
 * data. Both are the shape rules/testing.md calls out: a hand-written file mirroring a generated or
 * canonical source. Breaking one of these means fixing the mirror, never loosening the test.
 */

const root = join(__dirname, "..");

function readSwLocalePrefixes(): string[] {
  const sw = readFileSync(join(root, "public/sw.js"), "utf8");
  const match = sw.match(/const LOCALE_PREFIXES = \[([^\]]*)\]/);
  if (!match) throw new Error("sw.js no longer declares LOCALE_PREFIXES");
  return match[1]
    .split(",")
    .map((entry) => entry.trim().replace(/^"|"$/g, ""))
    .filter((entry, index, all) => entry !== "" || index === all.indexOf(""))
    .map((entry) => entry);
}

/** One locale's catalogue, flattened to dotted keys, which is the shape every check below wants. */
function readCatalogue(locale: string): Record<string, string> {
  const raw = JSON.parse(
    readFileSync(join(root, "messages", `${locale}.json`), "utf8"),
  );
  const out: Record<string, string> = {};
  const walk = (value: unknown, prefix: string) => {
    if (typeof value === "string") {
      out[prefix] = value;
      return;
    }
    for (const [k, v] of Object.entries(value as Record<string, unknown>)) {
      walk(v, prefix ? `${prefix}.${k}` : k);
    }
  };
  walk(raw, "");
  return out;
}

describe("locale configuration", () => {
  it("declares the same locales in sw.js as in the routing config", () => {
    // The default locale is unprefixed under `localePrefix: "as-needed"`, so it is the empty
    // string in sw.js rather than "/en".
    const expected = routing.locales.map((locale) =>
      locale === routing.defaultLocale ? "" : `/${locale}`,
    );
    expect(readSwLocalePrefixes().sort()).toEqual([...expected].sort());
  });

  it("ships a message catalogue for every declared locale", () => {
    for (const locale of routing.locales) {
      const path = join(root, "messages", `${locale}.json`);
      expect(
        () => readFileSync(path, "utf8"),
        `messages/${locale}.json`,
      ).not.toThrow();
    }
  });

  it("gives every declared locale a switcher label", () => {
    expect(Object.keys(LOCALE_LABELS).sort()).toEqual(
      [...routing.locales].sort(),
    );
  });

  // Placeholder and tag parity, which is the half a translation engine breaks and a reader cannot
  // see. `{count}` silently renamed, or a `<gains>` tag dropped, is not a bad sentence: next-intl
  // throws on a missing tag and prints the literal `{count}` for a missing value, so it renders as
  // a crash or as debris. scripts/gen-locale.mjs refuses to write a draft that fails this, and this
  // is the same check standing guard over hand edits afterwards.
  //
  // It shares the scanner with that script rather than re-deriving it with a regex, because the
  // regex version is subtly wrong: it counts the branch bodies of `{count, plural, one {code}
  // other {codes}}` as placeholders, and so rejects every locale that translates them.
  it("keeps every ICU placeholder and rich-text tag that English has", () => {
    const english = readCatalogue(routing.defaultLocale);
    for (const locale of routing.locales) {
      if (locale === routing.defaultLocale) continue;
      const translated = readCatalogue(locale);
      for (const [key, value] of Object.entries(english)) {
        expect(
          structuralTokens(translated[key] ?? ""),
          `${locale}: ${key}`,
        ).toEqual(structuralTokens(value));
      }
    }
  });

  // ICU well-formedness. The plural blocks are the ones at risk: `one` and `other` are keywords
  // that look like ordinary English words to a translator, and `#` looks like punctuation, so a
  // mangled branch is both easy to produce and invisible until a Dutch reader hits a page with a
  // count on it. Brace balance is checked by hand rather than with @formatjs's parser, which is
  // only a transitive dependency here and not something CI should rest on.
  //
  // The brace walk collects offending keys and asserts once per locale rather than calling
  // `expect` per character. That is not a style preference: at two locales the per-character
  // version was ~200k assertions and passed, and at six it was ~600k and timed out at 5s. The
  // invariant is identical and a failure still names the exact key.
  it("leaves every ICU plural and select block well formed", () => {
    for (const locale of routing.locales) {
      const unopened: string[] = [];
      const unclosed: string[] = [];
      const noOther: string[] = [];

      for (const [key, value] of Object.entries(readCatalogue(locale))) {
        let depth = 0;
        let wentNegative = false;
        for (const ch of value) {
          if (ch === "{") depth++;
          else if (ch === "}" && --depth < 0) wentNegative = true;
        }
        if (wentNegative) unopened.push(key);
        else if (depth !== 0) unclosed.push(key);

        for (const match of value.matchAll(
          /\{\s*[A-Za-z0-9_]+\s*,\s*(plural|selectordinal)\s*,/g,
        )) {
          if (!/\bother\s*\{/.test(value.slice(match.index))) noOther.push(key);
        }
      }

      expect(unopened, `${locale}: closes a brace it never opened`).toEqual([]);
      expect(unclosed, `${locale}: leaves a brace open`).toEqual([]);
      expect(noOther, `${locale}: plural block lost its "other" branch`).toEqual(
        [],
      );
    }
  });

  // The reason 9f.4 refuses machine translation is that a missing or invented key reads to a
  // player as a broken rule rather than a bad string. A structural comparison is the part of that
  // a test can actually hold: every locale answers exactly the keys English does.
  it("gives every locale the same key set as English", () => {
    const keysOf = (value: unknown, prefix = ""): string[] => {
      if (typeof value !== "object" || value === null) return [prefix];
      return Object.entries(value as Record<string, unknown>).flatMap(
        ([k, v]) => keysOf(v, prefix ? `${prefix}.${k}` : k),
      );
    };
    const read = (locale: string) =>
      keysOf(
        JSON.parse(
          readFileSync(join(root, "messages", `${locale}.json`), "utf8"),
        ),
      ).sort();

    // `read(locale)` re-reads and re-parses the file, so it is hoisted out of the filters and the
    // lookups go through a Set. Called inside `filter` it was one parse plus one linear scan per
    // key: fine at two locales, and 5s+ at six.
    const english = read(routing.defaultLocale);
    const englishSet = new Set(english);
    for (const locale of routing.locales) {
      if (locale === routing.defaultLocale) continue;
      const translated = read(locale);
      const translatedSet = new Set(translated);
      const missing = english.filter((k) => !translatedSet.has(k));
      const extra = translated.filter((k) => !englishSet.has(k));
      expect({ locale, missing, extra }).toEqual({
        locale,
        missing: [],
        extra: [],
      });
    }
  });

  // L-7-5 / P-9-1: TERMS §7 says the project is not a registered non-profit. These four strings
  // used to claim the opposite on every page (footer), on /about, and twice on /donate.
  it("does not claim non-profit in the copy TERMS §7 disclaims", () => {
    const banned =
      /non-profit|non profit|sin ánimo de lucro|à but non lucratif|gemeinnützig|sem fins lucrativos/i;
    const keys = [
      "Footer.tagline",
      "about.projectPara",
      "donate.metaDescription",
      "donate.intro",
    ];
    for (const locale of routing.locales) {
      const cat = readCatalogue(locale);
      for (const key of keys) {
        expect(
          cat[key],
          `${locale} ${key} still claims a non-profit legal form`,
        ).not.toMatch(banned);
      }
    }
  });

  // P-9-2: /about used to render literal "[TODO: …]" editor notes. Keep every catalogue string
  // free of that marker so a stub cannot ship to visitors again.
  it("does not ship bracketed TODO placeholders in the catalogue", () => {
    for (const locale of routing.locales) {
      const cat = readCatalogue(locale);
      for (const [key, value] of Object.entries(cat)) {
        expect(value, `${locale} ${key}`).not.toMatch(/\[TODO\b/i);
      }
    }
  });
});
