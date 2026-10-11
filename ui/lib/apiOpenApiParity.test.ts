import { readFileSync } from "node:fs";
import { join } from "node:path";
import { describe, expect, it } from "vitest";

/**
 * C-9-1, which is also the whole of C-11-4: `ui/lib/api.ts` has ~70 hand-written `/api/...`
 * path literals and HTTP methods, and nothing checks them against `docs/api/openapi.json`.
 * `api.generated.ts` is generated and gated by `npm run gen:api:check`, but `api.ts` imports it
 * for TYPES only, every actual fetch is a string literal path and a string literal method.
 * C-11-4's own note is that a generated request client adds nothing beyond this test, so one
 * was not introduced. A route rename, a verb change, or a query
 * param rename that keeps the response DTO shape identical passes `OpenApiContractTests`,
 * `gen:api:check` and `tsc --noEmit`, and only fails at runtime as a 404/400.
 *
 * This extracts every `/api/...` literal and its HTTP method from `api.ts` (a small hand-rolled
 * scanner, not a TS parser: it walks template literals with balanced-brace matching so a nested
 * template like `` `/api/games/mine${qs ? `?${qs}` : ""}` `` resolves correctly, and it walks
 * balanced parens to find the `method: "X"` that belongs to the call the literal is actually an
 * argument of) and asserts each `(path, method)` pair resolves to an operation in
 * `docs/api/openapi.json`, with path parameters normalised so `${id}` and `{id}` compare equal
 * regardless of the parameter's name on either side.
 *
 * Scope, deliberately: this checks the path and the verb, not headers or query parameters.
 * `getSessions()` (`api.ts`) sends the caller's refresh token as `X-Refresh-Token`, a header
 * `docs/api/openapi.json` does not declare (see `AccountEndpoints.cs:107-111`). That is real, and
 * documented, but it is a header-contract gap, not a path/method mismatch, so it does not appear
 * as a failure or an allowlist entry here. A future pass that wants to check headers too should
 * extend this file rather than repurpose the PATH_ALLOWLIST below for something it doesn't cover.
 *
 * PATH_ALLOWLIST is for a literal that intentionally does not resolve as a plain (path, method)
 * pair, the `CiWorkflowDriftTests` idiom: every entry carries a one-line reason. It is empty
 * today; nothing in `api.ts` needed it when this test was written.
 */

const ROOT = join(__dirname, "..", "..");
const API_TS = join(ROOT, "ui", "lib", "api.ts");
const OPENAPI_JSON = join(ROOT, "docs", "api", "openapi.json");

/** A (path, method) pair that is known and deliberate, with the reason it doesn't need to match. */
interface AllowlistEntry {
  path: string;
  method: string;
  reason: string;
}

const PATH_ALLOWLIST: readonly AllowlistEntry[] = [];

// ---- Minimal source scanner -------------------------------------------------------------------
//
// Deliberately not a full TS/JS parser. It only needs to (a) find every string/template literal in
// the file, (b) resolve a template literal's interpolations well enough to normalise a path, and
// (c) find the `method:` key inside the call a literal is the first argument of. All three are
// doable with balanced-bracket scanning because api.ts's call sites are single-expression,
// non-recursive `request<T>(literal, { ... })` / `fetch(literal, { ... })` forms.

/** Strip line comments and block comments so a doc comment quoting a path (e.g. line 104's
 * backtick-quoted `/api/auth/refresh`) is never mistaken for a real literal. The `(?<!:)` guards
 * `http://`/`https://` inside an actual string literal (e.g. `API_BASE`'s default) from being read
 * as a comment start. */
function stripComments(rawSource: string): string {
  // CRLF first: `.` in the per-line regex below doesn't match "\r", so a line ending in "\r"
  // (this file is CRLF) would leave that character after `.*`, and `$` (no `m` flag) then never
  // matches there, silently skipping the whole line's comment strip.
  const source = rawSource.replace(/\r\n/g, "\n");
  const noBlocks = source.replace(/\/\*[\s\S]*?\*\//g, (m) =>
    m.replace(/[^\n]/g, " "),
  );
  return noBlocks
    .split("\n")
    .map((line) => line.replace(/(?<!:)\/\/.*$/, ""))
    .join("\n");
}

/** Index of the `close` that balances the `open` at `openIdx` (which must itself be `open`). */
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

/** Scans the string/template literal starting at `start` (index of the opening quote/backtick). */
function scanLiteral(
  text: string,
  start: number,
): { raw: string; end: number } | null {
  const quote = text[start];
  if (quote !== '"' && quote !== "`" && quote !== "'") return null;
  let i = start + 1;
  while (i < text.length) {
    if (text[i] === "\\") {
      i += 2;
      continue;
    }
    if (quote === "`" && text[i] === "$" && text[i + 1] === "{") {
      const close = findMatchingClose(text, i + 1, "{", "}");
      if (close === -1) return null;
      i = close + 1;
      continue;
    }
    if (text[i] === quote) {
      return { raw: text.slice(start, i + 1), end: i + 1 };
    }
    i++;
  }
  return null;
}

/**
 * Resolves a scanned literal (backticks/quotes included) to a normalised `/api/...` path, or null
 * if it isn't one. `${API_BASE}`/`${base}` (the origin) are dropped; an interpolation whose
 * expression contains `?` is a `qs ? \`?${qs}\` : ""`-shaped optional query string and is dropped;
 * every other interpolation (a real path segment, e.g. `${id}`) becomes the placeholder `{}`. A
 * trailing literal `?` (a static query string, e.g. `checkDisplayName`'s `?candidate=`) is chopped
 * off last, since `docs/api/openapi.json` paths never include one.
 */
function resolvePathTemplate(raw: string): string | null {
  const quote = raw[0];
  const inner = raw.slice(1, -1);
  if (quote === '"' || quote === "'") {
    return inner.startsWith("/api/") ? inner.split("?")[0] : null;
  }
  let out = "";
  let i = 0;
  while (i < inner.length) {
    if (inner[i] === "$" && inner[i + 1] === "{") {
      const close = findMatchingClose(inner, i + 1, "{", "}");
      if (close === -1) return null;
      const expr = inner.slice(i + 2, close);
      if (expr === "API_BASE" || expr === "base") {
        // origin, not a path segment
      } else if (expr.includes("?") || expr === "qs") {
        // an optional-query-string ternary (`${qs ? \`?${qs}\` : ""}`), or (getLiveGames) a bare
        // `${qs}` referencing a query string already computed on a previous line.
      } else {
        out += "{}";
      }
      i = close + 1;
      continue;
    }
    out += inner[i];
    i++;
  }
  if (!out.startsWith("/api/")) return null;
  return out.split("?")[0];
}

interface ExtractedCall {
  path: string;
  method: string;
}

const HTTP_METHODS = ["GET", "POST", "PUT", "DELETE", "PATCH"] as const;

/**
 * True when the `/` at the end of `before` looks like it starts a regex literal rather than a
 * division operator: `api.ts` has exactly two (`/filename="?([^"; ]+)"?/i`, used to parse a
 * Content-Disposition header), and there is no division in this file, so "not preceded by an
 * identifier, number, or closing bracket/quote" is a safe enough heuristic here.
 */
function looksLikeRegexStart(before: string): boolean {
  const trimmed = before.replace(/\s+$/, "");
  if (trimmed === "") return true;
  return !/[A-Za-z0-9_$)\]"'`]$/.test(trimmed);
}

/**
 * Skips a regex literal starting at `start` (the opening `/`), so an embedded quote inside it (the
 * `"` characters in `/filename="?([^"; ]+)"?/i`, which are not JS string delimiters at all) is
 * never mistaken for the start of a string. Returns the index just past the trailing flags, or
 * null if this doesn't look like a closed regex literal after all (division, most likely).
 */
function skipRegexLiteral(text: string, start: number): number | null {
  let i = start + 1;
  let inClass = false;
  while (i < text.length && text[i] !== "\n") {
    if (text[i] === "\\") {
      i += 2;
      continue;
    }
    if (text[i] === "[") {
      inClass = true;
      i++;
      continue;
    }
    if (text[i] === "]") {
      inClass = false;
      i++;
      continue;
    }
    if (text[i] === "/" && !inClass) {
      let end = i + 1;
      while (end < text.length && /[a-zA-Z]/.test(text[end])) end++;
      return end;
    }
    i++;
  }
  return null;
}

/** Finds every `/api/...` literal in `source` and the HTTP method of the call it belongs to. */
function extractApiCalls(rawSource: string): ExtractedCall[] {
  const source = stripComments(rawSource);
  const calls: ExtractedCall[] = [];
  let i = 0;
  while (i < source.length) {
    const ch = source[i];
    if (ch === "/" && looksLikeRegexStart(source.slice(0, i))) {
      const skipped = skipRegexLiteral(source, i);
      if (skipped !== null) {
        i = skipped;
        continue;
      }
    }
    if (ch !== '"' && ch !== "`" && ch !== "'") {
      i++;
      continue;
    }
    const scanned = scanLiteral(source, i);
    if (!scanned) {
      i++;
      continue;
    }
    const path = resolvePathTemplate(scanned.raw);
    if (path === null) {
      i = scanned.end;
      continue;
    }

    // Method comes from the enclosing `request<T>(` or `fetch(` call, if the literal is that
    // call's first argument; otherwise (a plain `return \`...\`` URL builder) it's an implicit GET.
    const before = source.slice(0, i).replace(/\s+$/, "");
    let method = "GET";
    if (before.endsWith("(")) {
      const isRequestCall = /request<[^()]*>\($/.test(before);
      const isFetchCall = /\bfetch\($/.test(before);
      if (isRequestCall || isFetchCall) {
        const openIdx = before.length - 1;
        const closeIdx = findMatchingClose(source, openIdx, "(", ")");
        if (closeIdx !== -1) {
          const optionsSpan = source.slice(scanned.end, closeIdx);
          const methodMatch = optionsSpan.match(
            /method:\s*["'](GET|POST|PUT|DELETE|PATCH)["']/,
          );
          if (methodMatch) method = methodMatch[1];
        }
      }
    }

    calls.push({ path, method });
    i = scanned.end;
  }
  return calls;
}

// ---- OpenAPI side -------------------------------------------------------------------------------

/** `{anything}` -> `{}`, so a param's name doesn't matter, only its position. */
function normalizePath(path: string): string {
  return path.replace(/\{[^}]*\}/g, "{}");
}

function loadOpenApiOperations(): Set<string> {
  const doc = JSON.parse(readFileSync(OPENAPI_JSON, "utf8")) as {
    paths: Record<string, Record<string, unknown>>;
  };
  const ops = new Set<string>();
  for (const [path, methods] of Object.entries(doc.paths)) {
    const normalizedPath = normalizePath(path);
    for (const method of Object.keys(methods)) {
      if ((HTTP_METHODS as readonly string[]).includes(method.toUpperCase())) {
        ops.add(`${method.toUpperCase()} ${normalizedPath}`);
      }
    }
  }
  return ops;
}

describe("ui/lib/api.ts paths and methods resolve in docs/api/openapi.json", () => {
  const apiTsSource = readFileSync(API_TS, "utf8");
  const calls = extractApiCalls(apiTsSource);
  const operations = loadOpenApiOperations();
  const allowlistKeys = new Set(
    PATH_ALLOWLIST.map((e) => `${e.method} ${normalizePath(e.path)}`),
  );

  it("extracted a plausible number of call sites (parser sanity floor)", () => {
    // Guards against the scanner silently matching nothing (a rewrite of api.ts, a regex that
    // stopped firing), which would make the assertion below pass for the wrong reason.
    expect(calls.length).toBeGreaterThan(60);
  });

  it("every (path, method) pair in api.ts resolves in docs/api/openapi.json", () => {
    const seen = new Set<string>();
    const missing: string[] = [];
    for (const { path, method } of calls) {
      const normalizedPath = normalizePath(path);
      const key = `${method} ${normalizedPath}`;
      if (seen.has(key) || allowlistKeys.has(key)) continue;
      seen.add(key);
      if (!operations.has(key)) missing.push(key);
    }
    expect(
      missing,
      missing.length === 0
        ? undefined
        : `ui/lib/api.ts calls a (method, path) pair docs/api/openapi.json does not declare:\n` +
            missing.map((m) => `  ${m}`).join("\n") +
            `\nThis is either genuine drift (fix the server route, the client call, or regenerate ` +
            `the OpenAPI document) or a deliberate exception that belongs in PATH_ALLOWLIST above, ` +
            `with a one-line reason.`,
    ).toEqual([]);
  });
});
