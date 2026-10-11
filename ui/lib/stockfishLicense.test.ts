import { createHash } from "node:crypto";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { describe, expect, it } from "vitest";

const UI_ROOT = join(__dirname, "..");

/** Digests published on the nmrugg/stockfish.js v19.0.0 release (2026-09-15). */
const RELEASE = {
  tag: "v19.0.0",
  url: "https://github.com/nmrugg/stockfish.js/releases/tag/v19.0.0",
  commit: "edb0d9db6731067ec50ce619ff372b463bc4dd5d",
  files: [
    {
      name: "stockfish-19-lite-single.js",
      bytes: 21415,
      sha256:
        "d3344124ab067fb0b90ee77873bb8e9fbf5fc01bc525fe714b0f942581e889e6",
    },
    {
      name: "stockfish-19-lite-single.wasm",
      bytes: 1787571,
      sha256:
        "57ac2d72312aba346760e3f173f687a8c211208e97a87268436f7f0e10bb5387",
    },
  ],
} as const;

describe("vendored Stockfish release (L-10-2)", () => {
  const license = readFileSync(
    join(UI_ROOT, "public/STOCKFISH.LICENSE"),
    "utf8",
  );

  it("records the release tag, upstream commit, and both file hashes", () => {
    expect(license).toContain(RELEASE.tag);
    expect(license).toContain(RELEASE.url);
    expect(license).toContain(RELEASE.commit);
    for (const file of RELEASE.files) {
      expect(license).toContain(file.name);
      expect(license).toContain(file.sha256);
    }
  });

  it("matches the published bytes of that release", () => {
    for (const file of RELEASE.files) {
      const bytes = readFileSync(join(UI_ROOT, "public", file.name));
      expect(bytes.length).toBe(file.bytes);
      expect(createHash("sha256").update(bytes).digest("hex")).toBe(file.sha256);
    }
  });

  it("loads that worker, which fetches the sibling wasm by name", () => {
    const bot = readFileSync(join(UI_ROOT, "lib/bot.ts"), "utf8");
    expect(bot).toContain('new Worker("/stockfish-19-lite-single.js")');
    // Production CSP has no unsafe-eval. Without this token the worker cannot
    // compile the wasm module and every bot game falls back to a random move.
    const nextConfig = readFileSync(join(UI_ROOT, "next.config.ts"), "utf8");
    expect(nextConfig).toContain("'wasm-unsafe-eval'");
  });
});
