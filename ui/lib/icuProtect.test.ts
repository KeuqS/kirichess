import { readFileSync } from "node:fs";
import { join } from "node:path";
import { describe, expect, it } from "vitest";
// A plain .mjs generator module: dependency-free on purpose, and shared with CI so the
// script and the guard can never disagree about what counts as a placeholder.
import {
  deconstruct,
  reconstruct,
  structuralTokens as tokens,
} from "../scripts/icu-protect.mjs";

/**
 * Phase 9f.4. The one load-bearing correctness claim of scripts/gen-locale.mjs.
 *
 * That script sends 989 strings to a translation engine unattended. Whatever the engine does to the
 * words is a review problem, and a human catches it. What a human does NOT catch, reading a
 * language they are checking for tone, is `{count}` quietly renamed or a `<gains>` tag dropped:
 * next-intl throws on a missing tag and prints the literal `{count}` for a missing value, so a
 * mangled placeholder renders as a crash or as debris rather than as a clumsy sentence.
 *
 * So the property tested here is not "the translation is good", which no test can hold. It is
 * "structure survives regardless of what comes back", checked against the real catalogue rather
 * than against hand-picked examples, and against a deliberately hostile translator.
 */

const root = join(__dirname, "..");
const english: Record<string, unknown> = JSON.parse(
  readFileSync(join(root, "messages", "en.json"), "utf8"),
);

function flatten(obj: unknown, prefix = "", out: Record<string, string> = {}) {
  for (const [k, v] of Object.entries(obj as Record<string, unknown>)) {
    const key = prefix ? `${prefix}.${k}` : k;
    if (typeof v === "string") out[key] = v;
    else flatten(v, key, out);
  }
  return out;
}

const catalogue = flatten(english);
const TERMS = ["KiriChess", "Steal", "Burn", "Bounty", "Elo"];

type Segment = { verbatim?: string; translate?: string; spans?: string[] };

/** Runs a message through the pipeline with a caller-supplied stand-in for the engine. */
function roundTrip(message: string, engine: (run: string) => string): string {
  const segments: Segment[] = deconstruct(message, TERMS);
  const translated = segments
    .filter((s) => s.translate !== undefined)
    .map((s) => engine(s.translate as string));
  return reconstruct(segments, translated);
}

describe("ICU protection (scripts/icu-protect.mjs)", () => {
  // The baseline: an engine that changes nothing must produce the input back, byte for byte. Any
  // failure here is the pipeline losing text on its own, before a translator is even involved.
  it("round-trips every string in the catalogue through an identity translator", () => {
    for (const [key, value] of Object.entries(catalogue)) {
      expect(
        roundTrip(value, (run) => run),
        `${key} did not survive an identity round-trip`,
      ).toBe(value);
    }
  });

  // The realistic one: a translator returns different words, of different length. Every sentinel
  // must come back as the span it stood for.
  //
  // The rewrite deliberately does NOT reorder words. An earlier version reversed them, which is a
  // fair thing for a translator to do but makes this assertion lie: reversing "# move" inside a
  // plural branch yields "{MOVE #}", and a regex counting `{name` cannot tell that from a real
  // `{MOVE}` placeholder. Placeholder movement is covered by its own test below, on a message
  // where the distinction is unambiguous.
  it("keeps every placeholder and tag when the translator rewrites the words", () => {
    // Rewrites the words between sentinels and leaves the sentinels alone, which is what
    // `tag_handling=xml` with `ignore_tags` guarantees. Rewriting them too would only prove the
    // test can break its own markers.
    const rewrite = (run: string) =>
      run
        .split(/(<x>\d+<\/x>)/g)
        .map((part) =>
          /^<x>\d+<\/x>$/.test(part)
            ? part
            : part.replace(/[A-Za-z]+/g, (w) => `${w.toUpperCase()}ZZ`),
        )
        .join("");

    for (const [key, value] of Object.entries(catalogue)) {
      const out = roundTrip(value, rewrite);
      expect(tokens(out), `${key} lost or gained a token`).toEqual(
        tokens(value),
      );
    }
  });

  // Word order genuinely differs between languages, so an engine may legitimately return the
  // sentinels in a different order than it received them. Restoration is by index, not position,
  // so the values must follow the markers rather than the slots.
  it("restores placeholders correctly when the translator moves them", () => {
    const message = "{winner} beat {loser} in {moves} moves";
    const segments: Segment[] = deconstruct(message, TERMS);
    const flip = (run: string) => {
      const found = [...run.matchAll(/<x>\d+<\/x>/g)].map((m) => m[0]);
      let i = found.length;
      return run.replace(/<x>\d+<\/x>/g, () => found[--i]);
    };
    const out = reconstruct(
      segments,
      segments
        .filter((s) => s.translate !== undefined)
        .map((s) => flip(s.translate as string)),
    );
    expect(out).toBe("{moves} beat {loser} in {winner} moves");
  });

  // The sentinel name is reserved. If the catalogue ever gains a real `<x>` rich-text tag, the
  // protection passes start eating each other's markers, which is the bug this module was rebuilt
  // around. Cheaper to fail here than to debug it in a generated locale.
  it("reserves <x> as the sentinel name, so no message may use it", () => {
    for (const [key, value] of Object.entries(catalogue)) {
      expect(value, `${key} uses the reserved <x> tag`).not.toMatch(/<\/?x>/);
    }
  });

  // The specific trap this whole module exists for. `one` and `other` are ICU keywords that read
  // as ordinary English words, and `#` reads as punctuation, so an engine given the raw block
  // returns something that no longer compiles. They must never reach it.
  it("never sends ICU plural keywords or # to the translator", () => {
    const plurals = Object.entries(catalogue).filter(([, v]) =>
      /\{[^}]*,\s*(plural|select|selectordinal)/.test(v),
    );
    expect(plurals.length).toBeGreaterThan(0);

    for (const [key, value] of plurals) {
      const segments: Segment[] = deconstruct(value, TERMS);
      const sent = segments
        .filter((s) => s.translate !== undefined)
        .map((s) => s.translate as string)
        .join(" ");
      expect(sent, `${key} sent a bare #`).not.toMatch(/#/);
      expect(sent, `${key} sent a plural branch keyword`).not.toMatch(
        /\b(one|other|zero|two|few|many)\s*\{/,
      );
    }
  });

  it("never sends a protected term to the translator", () => {
    for (const [key, value] of Object.entries(catalogue)) {
      const segments: Segment[] = deconstruct(value, TERMS);
      for (const seg of segments) {
        if (seg.translate === undefined) continue;
        for (const term of TERMS) {
          expect(
            seg.translate,
            `${key} exposed "${term}" to the translator`,
          ).not.toMatch(
            new RegExp(`(?<![A-Za-z0-9_])${term}(?![A-Za-z0-9_])`, "i"),
          );
        }
      }
    }
  });

  // A run with no words in it is quota spent on inviting the engine to "correct" something that is
  // pure structure, e.g. "{label}. {resultText}." or "{level} Elo".
  it("does not send runs that contain no translatable words", () => {
    for (const [key, value] of Object.entries(catalogue)) {
      const segments: Segment[] = deconstruct(value, TERMS);
      for (const seg of segments) {
        if (seg.translate === undefined) continue;
        const words = seg.translate.replace(/<x>\d+<\/x>/g, "");
        expect(words, `${key} sent a wordless run`).toMatch(/[A-Za-z]/);
      }
    }
  });
});
