import { readFileSync } from "node:fs";
import { join } from "node:path";
import { describe, expect, it } from "vitest";

const UI_ROOT = join(__dirname, "..");
const REPO_ROOT = join(UI_ROOT, "..");

describe("P-7 source guards", () => {
  it("caches the Stockfish worker and wasm as immutable assets (P-7-2)", () => {
    const sw = readFileSync(join(UI_ROOT, "public/sw.js"), "utf8");
    const nextConfig = readFileSync(join(UI_ROOT, "next.config.ts"), "utf8");
    for (const path of [
      "/stockfish-19-lite-single.js",
      "/stockfish-19-lite-single.wasm",
    ]) {
      expect(sw).toContain(`pathname === "${path}"`);
      expect(nextConfig).toContain(`"${path}"`);
    }
    expect(nextConfig).toMatch(/max-age=31536000, immutable/);
  });

  it("does not statically import the Sentry client SDK (P-7-4)", () => {
    const instrumentation = readFileSync(
      join(UI_ROOT, "instrumentation-client.ts"),
      "utf8",
    );
    expect(instrumentation).not.toMatch(
      /^import\s+\*\s+as\s+Sentry\s+from\s+"@sentry\/nextjs"/m,
    );
    const nextConfig = readFileSync(join(UI_ROOT, "next.config.ts"), "utf8");
    expect(nextConfig).toMatch(
      /export default process\.env\.NEXT_PUBLIC_SENTRY_DSN/,
    );
    const deploy = readFileSync(
      join(REPO_ROOT, "docs/ops/DEPLOYMENT.md"),
      "utf8",
    );
    expect(deploy).toMatch(/NEXT_PUBLIC_SENTRY_DSN/);
  });

  it("keeps route-level loading.tsx on the data-heavy pages (I-1-7 / AS-10)", () => {
    const routes = [
      "app/[locale]/leaderboards/loading.tsx",
      "app/[locale]/analytics/loading.tsx",
      "app/[locale]/replay/[id]/loading.tsx",
      "app/[locale]/u/[id]/loading.tsx",
      "app/[locale]/watch/loading.tsx",
      "app/[locale]/watch/[id]/loading.tsx",
      "app/[locale]/game/[id]/loading.tsx",
      "app/[locale]/challenge/[token]/loading.tsx",
      "app/[locale]/profile/loading.tsx",
    ];
    for (const file of routes) {
      const source = readFileSync(join(UI_ROOT, file), "utf8");
      expect(source, file).toMatch(/RouteLoading/);
    }
  });

  it("chains a bundle-budget assertion from npm run build (P-7-5)", () => {
    const pkg = JSON.parse(
      readFileSync(join(UI_ROOT, "package.json"), "utf8"),
    ) as { scripts: { build: string } };
    expect(pkg.scripts.build).toMatch(/assert-bundle-budget\.mjs/);
  });
});
