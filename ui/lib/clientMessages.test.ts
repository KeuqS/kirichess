import { readdirSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import {
  CLIENT_MESSAGE_NAMESPACES,
  LEARN_CLIENT_NAMESPACES,
  pickMessages,
} from "./clientMessages";

const UI_ROOT = join(__dirname, "..");

function walkSources(dir: string, acc: string[] = []): string[] {
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    const path = join(dir, entry.name);
    if (entry.isDirectory()) {
      if (entry.name === "node_modules" || entry.name === ".next") continue;
      walkSources(path, acc);
    } else if (
      (entry.name.endsWith(".ts") || entry.name.endsWith(".tsx")) &&
      !entry.name.endsWith(".test.ts") &&
      !entry.name.endsWith(".test.tsx") &&
      !entry.name.endsWith(".d.ts") &&
      !entry.name.endsWith(".generated.ts")
    ) {
      acc.push(path);
    }
  }
  return acc;
}

describe("pickMessages (P-7-3)", () => {
  it("keeps only requested namespaces", () => {
    const picked = pickMessages(
      { home: { a: "1" }, rules: { b: "2" }, leftover: { c: "3" } },
      ["home"],
    );
    expect(picked).toEqual({ home: { a: "1" } });
  });

  it("adds rules only on the learn extra list", () => {
    expect(CLIENT_MESSAGE_NAMESPACES).not.toContain("rules");
    expect(LEARN_CLIENT_NAMESPACES).toContain("rules");
  });
});

describe("CLIENT_MESSAGE_NAMESPACES covers every useTranslations call", () => {
  it("lists every namespace a non-test client component or hook reads", () => {
    const allowed = new Set<string>(LEARN_CLIENT_NAMESPACES);
    const missing: string[] = [];
    const used = new Set<string>();
    // Hooks in `lib/*.ts` call useTranslations too. A .tsx-only walk misses them,
    // and a missing namespace then renders as the raw key.
    for (const file of [
      ...walkSources(join(UI_ROOT, "app")),
      ...walkSources(join(UI_ROOT, "components")),
      ...walkSources(join(UI_ROOT, "lib")),
    ]) {
      const source = readFileSync(file, "utf8");
      for (const match of source.matchAll(
        /useTranslations\(\s*["']([^"']+)["']\s*\)/g,
      )) {
        const ns = match[1];
        used.add(ns);
        if (!allowed.has(ns)) {
          missing.push(`${relativePosix(file)}: ${ns}`);
        }
      }
    }
    expect(used.has("challenge"), "challenge page is a client tree").toBe(true);
    expect(
      missing,
      missing.join("\n") || "ok",
    ).toEqual([]);
  });
});

function relativePosix(file: string): string {
  return file.slice(UI_ROOT.length + 1).replaceAll("\\", "/");
}
