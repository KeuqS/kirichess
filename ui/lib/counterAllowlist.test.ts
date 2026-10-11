import { readFileSync } from "node:fs";
import { join } from "node:path";
import { describe, expect, it } from "vitest";

/**
 * M-4-7 (cross-ref C-9): `ui/lib/analytics.ts`'s `COUNTER_KEYS` and `ui/lib/api.ts`'s
 * `COUNTER_SOURCES` are hand-mirrors of `src/KiriChess.Server/Analytics/CounterKeys.cs`'s
 * `CounterKeys.All` and `CounterSources.All`. Both TS files already carry a "keep them in step"
 * comment, but nothing checked it: `CounterEndpoints.cs:65` answers `204` for an unrecognised key
 * (README scope decision 11, so a client sending a stale or renamed key fails silently, never a
 * test, never a 500) and `CounterKeys.NormalizeSource` falls back to `direct` for an unrecognised
 * source rather than rejecting it. Both are the generated-vs-hand-written drift class the repo's
 * contract tests exist to catch, just one where the failure mode is "the count quietly goes
 * missing" instead of an error.
 *
 * The fix picked here is the "read-and-assert" alternative from the finding, not "a tiny JSON both
 * sides import": a new shared JSON would need the C# side to read it too to actually close the
 * gap (per the finding's own caveat), and that means either a second new C# test file or editing
 * `CounterKeys.cs`/an existing C# test, both outside what this pass touches. Reading the real
 * `CounterKeys.cs` source as text and asserting the TS sets are subsets of it needs nothing on the
 * C# side and reads the actual source of truth rather than a third copy that could itself drift.
 *
 * Subset, not equality, is the right direction: `CounterKeys.All` also holds server-only keys
 * (`QueueJoin`, `QueueMatch`, `RematchOffer`, `RematchAccept`) that no client-side call fires, and
 * `CounterSources.All` holds `direct`, which the client never needs to send since it's the
 * server's own fallback. A TS value not in the C# allowlist is the actual bug (a renamed or
 * retired key silently dropping counts); the reverse is normal and not checked.
 */

const ROOT = join(__dirname, "..", "..");
const ANALYTICS_TS = join(ROOT, "ui", "lib", "analytics.ts");
const API_TS = join(ROOT, "ui", "lib", "api.ts");
const COUNTER_KEYS_CS = join(
  ROOT,
  "src",
  "KiriChess.Server",
  "Analytics",
  "CounterKeys.cs",
);

/** Index of the `close` that balances the `open` at `openIdx`. */
function findMatchingClose(
  text: string,
  openIdx: number,
  open: string,
  close: string,
): number {
  let depth = 0;
  for (let i = openIdx; i < text.length; i++) {
    if (text[i] === open) depth++;
    else if (text[i] === close) {
      depth--;
      if (depth === 0) return i;
    }
  }
  return -1;
}

/** The source of one `public static class <name> { ... }` block, braces excluded. */
function classBody(source: string, className: string): string {
  const marker = `class ${className}`;
  const markerIdx = source.indexOf(marker);
  if (markerIdx === -1) {
    throw new Error(`Could not find "${marker}" in ${COUNTER_KEYS_CS}`);
  }
  const openIdx = source.indexOf("{", markerIdx);
  const closeIdx = findMatchingClose(source, openIdx, "{", "}");
  if (openIdx === -1 || closeIdx === -1) {
    throw new Error(
      `Could not find the body of "${marker}" in ${COUNTER_KEYS_CS}`,
    );
  }
  return source.slice(openIdx + 1, closeIdx);
}

/** Every `public const string X = "value";` value declared directly in a class body. */
function constStringValues(body: string): string[] {
  const values: string[] = [];
  const re = /public const string \w+\s*=\s*"([^"]*)";/g;
  let m: RegExpExecArray | null;
  while ((m = re.exec(body)) !== null) {
    values.push(m[1]);
  }
  return values;
}

/** Every string value in `export const COUNTER_KEYS = { ... } as const;`. */
function tsCounterKeys(): string[] {
  const source = readFileSync(ANALYTICS_TS, "utf8");
  const startIdx = source.indexOf("export const COUNTER_KEYS");
  const openIdx = source.indexOf("{", startIdx);
  const closeIdx = findMatchingClose(source, openIdx, "{", "}");
  const body = source.slice(openIdx + 1, closeIdx);
  return [...body.matchAll(/:\s*"([^"]+)"/g)].map((m) => m[1]);
}

/** Every string value in `const COUNTER_SOURCES = new Set([ ... ]);` in api.ts. */
function tsCounterSources(): string[] {
  const source = readFileSync(API_TS, "utf8");
  const startIdx = source.indexOf("const COUNTER_SOURCES");
  const openIdx = source.indexOf("[", startIdx);
  const closeIdx = findMatchingClose(source, openIdx, "[", "]");
  const body = source.slice(openIdx + 1, closeIdx);
  return [...body.matchAll(/"([^"]+)"/g)].map((m) => m[1]);
}

describe("client counter allowlists are a subset of the server's", () => {
  const csSource = readFileSync(COUNTER_KEYS_CS, "utf8");
  const csKeys = constStringValues(classBody(csSource, "CounterKeys"));
  const csSources = constStringValues(classBody(csSource, "CounterSources"));

  it("found a plausible number of C# constants (parser sanity floor)", () => {
    // Guards against the class-body/regex extraction silently matching nothing, which would make
    // both subset assertions below pass for the wrong reason (comparing against an empty set).
    expect(csKeys.length).toBeGreaterThanOrEqual(10);
    expect(csSources.length).toBeGreaterThanOrEqual(10);
  });

  it("ui/lib/analytics.ts's COUNTER_KEYS are all recognised by CounterKeys.cs", () => {
    const csKeySet = new Set(csKeys);
    const missing = tsCounterKeys().filter((k) => !csKeySet.has(k));
    expect(
      missing,
      missing.length === 0
        ? undefined
        : `ui/lib/analytics.ts's COUNTER_KEYS has a value CounterKeys.cs does not declare: ` +
            `${missing.join(", ")}. The server answers 204 (silently drops the count) for an ` +
            `unrecognised key, so this is a renamed or retired key the client never noticed.`,
    ).toEqual([]);
  });

  it("ui/lib/api.ts's COUNTER_SOURCES are all recognised by CounterSources.cs", () => {
    const csSourceSet = new Set(csSources);
    const missing = tsCounterSources().filter((s) => !csSourceSet.has(s));
    expect(
      missing,
      missing.length === 0
        ? undefined
        : `ui/lib/api.ts's COUNTER_SOURCES has a value CounterSources.cs does not declare: ` +
            `${missing.join(", ")}. Not fatal server-side (NormalizeSource falls back to "direct"), ` +
            `but it means every visit tagged with this source silently loses its attribution.`,
    ).toEqual([]);
  });
});
