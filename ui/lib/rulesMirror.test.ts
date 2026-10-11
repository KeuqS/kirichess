import { readFileSync, readdirSync } from "node:fs";
import { join } from "node:path";
import { describe, expect, it } from "vitest";

/**
 * C-9-2: nothing else keeps `.claude/rules/*.md` and `.cursor/rules/*.mdc` in sync. Both trees
 * are checked into git as the project conventions. See `.claude/rules/project.md`'s
 * "Conventions location" section and `.claude/rules/styling.md`'s own closing paragraph, which
 * already tells an author to update both in the same commit but had nothing checking that they
 * actually did.
 *
 * This lives in its own file rather than folded into `styling.test.ts`: that file's `sourceFiles()`
 * walks `ui/app` and `ui/components` for Tailwind class strings, a different root and a different
 * kind of check than diffing two directories of Markdown outside `ui/` entirely. Keeping the two
 * separate means a failure here can't be mistaken for a styling regression.
 *
 * The `.mdc` side is not a byte-identical copy of the `.md` source. Every pair adds:
 *   1. a YAML frontmatter block (`description`, `globs`, `alwaysApply`),
 *   2. a `"> Mirrors [...] ... keep both in sync when editing either."` line,
 *   3. a same-directory cross-link rewrite: `[foo.md](foo.md)` (or `[bar/foo.md](foo.md)`) becomes
 *      a plain `` `foo.mdc` ``, since the `.mdc` copies don't hyperlink each other, they just
 *      name the sibling file.
 * `.claude/rules/security.md` has one further wrinkle: its one link into `.claude/config/` is
 * `../config/...` (one level up from `.claude/rules/`), but from `.cursor/rules/` the same target
 * needs `../../.claude/config/...` (two levels up to the repo root, then back down into `.claude`).
 * That is a path-depth consequence of the same link-rewrite family, not a fourth kind of drift.
 *
 * This test normalises exactly those things and then requires the two bodies to match verbatim,
 * so an actual content difference, the two files saying something different, fails
 * loudly and names the file and the first line where they diverge, not just "not equal".
 */

const ROOT = join(__dirname, "..", "..");
const CLAUDE_DIR = join(ROOT, ".claude", "rules");
const CURSOR_DIR = join(ROOT, ".cursor", "rules");

/** Every `.claude/rules/*.md` basename that is expected to have a `.cursor/rules/*.mdc` mirror. */
function pairNames(): string[] {
  return readdirSync(CLAUDE_DIR)
    .filter((f) => f.endsWith(".md"))
    .map((f) => f.slice(0, -".md".length))
    .sort();
}

/** Same-directory cross-links, e.g. `[realtime.md](realtime.md)` or `[rules/project.md](project.md)`,
 * become a bare `` `name.mdc` `` in the .mdc mirror, matched on the paren target, not the bracket text. */
function rewriteSelfLinks(md: string, names: string[]): string {
  let out = md;
  for (const name of names) {
    const linkRe = new RegExp(`\\[[^\\]]+\\]\\(${name}\\.md\\)`, "g");
    out = out.replace(linkRe, `\`${name}.mdc\``);
  }
  return out;
}

/** The one link whose relative depth changes because its target lives inside `.claude/` itself. */
function rewriteClaudeInternalLinks(md: string): string {
  return md.replace(/\]\(\.\.\/config\//g, "](../../.claude/config/");
}

function normalizeMd(raw: string, names: string[]): string {
  return rewriteClaudeInternalLinks(rewriteSelfLinks(raw, names))
    .replace(/\r\n/g, "\n")
    .trim();
}

// The mirror line always reads `> Mirrors [.claude/rules/X.md](...) <dash> keep both in sync when
// editing either.` immediately after the frontmatter. The separator may be either an em dash or a
// normal hyphen.
const DASH_SEPARATOR = "(?:\\u2014|-)";
const MIRROR_LINE = new RegExp(
  `^\\n*> Mirrors \\[.*?\\]\\(.*?\\)\\s*${DASH_SEPARATOR}\\s*keep both in sync when editing either\\.\\n*`,
);

function normalizeMdc(raw: string): string {
  let body = raw.replace(/\r\n/g, "\n");
  // Strip the YAML frontmatter block.
  body = body.replace(/^---\n[\s\S]*?\n---\n/, "");
  // Strip the "> Mirrors ..." line plus the blank lines around it.
  body = body.replace(MIRROR_LINE, "\n");
  return body.trim();
}

/** Index of the first line where two line arrays diverge, or -1 if they are identical. */
function firstDivergence(a: string[], b: string[]): number {
  const max = Math.max(a.length, b.length);
  for (let i = 0; i < max; i++) {
    if (a[i] !== b[i]) return i;
  }
  return -1;
}

describe(".claude/rules and .cursor/rules stay in sync", () => {
  const names = pairNames();

  it("found the eight rule pairs this test is supposed to cover", () => {
    // Guards the discovery step itself: a wrong CLAUDE_DIR or an empty directory would make every
    // assertion below pass for the wrong reason (nothing to compare).
    expect(names.length).toBeGreaterThanOrEqual(8);
  });

  for (const name of pairNames()) {
    it(`${name}.md and ${name}.mdc bodies match after normalisation`, () => {
      const mdRaw = readFileSync(join(CLAUDE_DIR, `${name}.md`), "utf8");
      const mdcRaw = readFileSync(join(CURSOR_DIR, `${name}.mdc`), "utf8");

      const md = normalizeMd(mdRaw, names);
      const mdc = normalizeMdc(mdcRaw);

      const mdLines = md.split("\n");
      const mdcLines = mdc.split("\n");
      const diffAt = firstDivergence(mdLines, mdcLines);

      expect(
        diffAt,
        diffAt === -1
          ? undefined
          : `.claude/rules/${name}.md and .cursor/rules/${name}.mdc diverge at line ${diffAt + 1} ` +
              `(after stripping frontmatter, the mirror line, and the known link rewrites):\n` +
              `  .claude/rules/${name}.md:  ${JSON.stringify(mdLines[diffAt] ?? "<file ends here>")}\n` +
              `  .cursor/rules/${name}.mdc: ${JSON.stringify(mdcLines[diffAt] ?? "<file ends here>")}\n` +
              `Fix the .mdc mirror to match the .claude original unless the change was meant to land ` +
              `in .claude/rules/${name}.md too, in which case update both in the same commit.`,
      ).toBe(-1);
    });
  }
});
