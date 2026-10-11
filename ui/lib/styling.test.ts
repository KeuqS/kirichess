import { describe, expect, it } from "vitest";
import { readdirSync, readFileSync } from "node:fs";
import { join, sep } from "node:path";

/**
 * Drift guard for `.claude/rules/styling.md`, in the same spirit as the repo's other
 * contract tests (`OpenApiContractTests`, `LegalDocumentDriftTests`): the spec is a
 * document, and a document alone does not stop the next author from re-introducing what
 * this pass just removed.
 *
 * Deliberately mechanical. It checks the bans that can be decided by looking at a class
 * string, and nothing that needs judgement: it cannot tell you an h1 is the wrong size,
 * a card the wrong elevation, or a colour the wrong tier. Those still need review.
 *
 * When one of these fails, fix the code. Loosening the rule is the wrong move unless the
 * spec itself changed, in which case update `.claude/rules/styling.md` and its
 * `.cursor/rules/styling.mdc` mirror in the same commit.
 */

const ROOTS = ["app", "components"];
const UI_ROOT = join(__dirname, "..");

function sourceFiles(): string[] {
  const out: string[] = [];
  const walk = (dir: string) => {
    for (const entry of readdirSync(dir, { withFileTypes: true })) {
      const full = join(dir, entry.name);
      if (entry.isDirectory()) walk(full);
      else if (entry.name.endsWith(".tsx") && !entry.name.endsWith(".test.tsx"))
        out.push(full);
    }
  };
  for (const r of ROOTS) walk(join(UI_ROOT, r));
  return out;
}

/** Repo-relative, posix-style, e.g. `ui/components/Card.tsx`. */
function rel(file: string): string {
  return (
    "ui/" +
    file
      .slice(UI_ROOT.length + 1)
      .split(sep)
      .join("/")
  );
}

/** Strip `//` and block comments so a doc comment quoting a banned pattern is not a hit. */
function withoutComments(source: string): string[] {
  return source
    .replace(/\/\*[\s\S]*?\*\//g, (m) => m.replace(/[^\n]/g, " "))
    .split("\n")
    .map((line) => (/^\s*(\/\/|\*)/.test(line) ? "" : line));
}

interface Violation {
  where: string;
  text: string;
}

/** Collect `file:line` hits for `pattern`, skipping comments and allowlisted files. */
function scan(
  pattern: RegExp,
  allow: readonly string[] = [],
  lineIsExempt: (line: string) => boolean = () => false,
): Violation[] {
  const found: Violation[] = [];
  for (const file of sourceFiles()) {
    const name = rel(file);
    if (allow.includes(name)) continue;
    withoutComments(readFileSync(file, "utf8")).forEach((line, i) => {
      const re = new RegExp(pattern.source, pattern.flags.replace("g", ""));
      if (re.test(line) && !lineIsExempt(line)) {
        found.push({
          where: `${name}:${i + 1}`,
          text: line.trim().slice(0, 120),
        });
      }
    });
  }
  return found;
}

const report = (v: Violation[]) =>
  v.map((x) => `  ${x.where}\n    ${x.text}`).join("\n");

describe("styling.md is still true of ui/", () => {
  it("writes no rounded-* classes (globals.css squares every corner)", () => {
    const hits = scan(
      /\brounded-(?:sm|md|lg|xl|2xl|3xl|full|none|\[[^\]\s]+\])/,
    );
    expect(
      hits.length,
      `styling.md section 1: the app is square-cornered, so rounded-* is inert.\n${report(hits)}`,
    ).toBe(0);
  });

  it("writes no raw hex for a colour that has a token", () => {
    // Only the tokenised palette. A domain colour with no token (ClockGraph's #ededed
    // for the white side, say) is not what this rule is about; inventing a token for it
    // is a design decision, not a drift fix.
    const TOKENISED =
      /#(?:282c34|21252b|3e4451|abb2bf|9197a3|e5c07b|98c379|e8838a)\b/i;
    const hits = scan(
      TOKENISED,
      [
        // Renders when the stylesheet may not have loaded, so it cannot use tokens.
        "ui/app/global-error.tsx",
        // Satori renders these at build time with no stylesheet: a var() here yields a
        // transparent image, and the failure is invisible in the app.
        "ui/app/[locale]/opengraph-image.tsx",
        "ui/app/[locale]/replay/[id]/opengraph-image.tsx",
      ],
      (line) =>
        // The page-shell selection pair is quoted verbatim on every page.
        /selection:(?:bg|text)-\[#/.test(line) ||
        // Browser hint, not CSS.
        /themeColor/.test(line),
    );
    expect(
      hits.length,
      `styling.md section 2: use a token. #282c34 is var(--background).\n${report(hits)}`,
    ).toBe(0);
  });

  it("does not blend --foreground to 70% or below", () => {
    const hits = scan(
      /text-\[var\(--foreground\)\]\/(?:[0-9]|[1-6][0-9]|70)\b/,
      [],
    );
    expect(
      hits.length,
      `styling.md section 2: that reads below the 4.5:1 AA floor. Use text-muted.\n${report(hits)}`,
    ).toBe(0);
  });

  it("puts no focus:ring-* on a field", () => {
    const hits = scan(/focus:ring-/, [
      // Lobby panel, one of the four reference surfaces; left alone deliberately.
      "ui/components/ChallengeFriendPanel.tsx",
    ]);
    expect(
      hits.length,
      `styling.md section 8: the field's own border plus the global outline already show focus; a ring makes it a triple frame.\n${report(hits)}`,
    ).toBe(0);
  });

  it("does not hand-roll the Card box", () => {
    const hits = scan(
      /border border-\[var\(--border\)\][^"'`]*bg-\[var\(--surface\)\]|bg-\[var\(--surface\)\][^"'`]*border border-\[var\(--border\)\]/,
      [
        // These two ARE the recipe.
        "ui/components/Card.tsx",
        "ui/components/AuthShell.tsx",
        // Own their container by design, see styling.md section 7 and the chart components.
        "ui/components/DataTable.tsx",
        "ui/components/ClockGraph.tsx",
        "ui/components/GapLadder.tsx",
        "ui/components/MoveList.tsx",
        "ui/components/MatchHistoryCard.tsx",
        "ui/components/LiveGameCard.tsx",
        // Lobby surfaces, deliberately untouched by the consistency pass.
        "ui/components/BotGameDialog.tsx",
        "ui/components/Avatar.tsx",
        // The four reference pages. They predate <Card> and hand-roll the same box, and
        // styling.md is explicit that where it and a reference page disagree the page
        // wins. This is a known exception, not a model: converting them to <Card> would
        // be a pure refactor with no visual change, and is worth doing separately.
        "ui/app/[locale]/page.tsx",
        "ui/app/[locale]/rules/page.tsx",
        "ui/app/[locale]/about/page.tsx",
        "ui/app/[locale]/leaderboards/page.tsx",
      ],
    );
    expect(
      hits.length,
      `styling.md section 5: if you are writing that class string, you are writing <Card>.\n${report(hits)}`,
    ).toBe(0);
  });

  it("does not hand-roll a modal backdrop", () => {
    const hits = scan(/fixed inset-0[^"'`]*bg-black\//, [
      // Dialog IS the shell.
      "ui/components/Dialog.tsx",
      // Lobby surface; its styling is recorded as shipped and walked in UI-CHECKLIST.
      "ui/components/BotGameDialog.tsx",
    ]);
    expect(
      hits.length,
      `styling.md section 10: use <Dialog>, which also carries role="dialog" and Escape handling.\n${report(hits)}`,
    ).toBe(0);
  });

  it("writes no raw rgba()/rgb()/hsl() in a .tsx file", () => {
    // The hex check above can't see a colour hidden inside an inline `style` object or a template
    // literal - that is exactly how the pre-9d.4 red survived in GapLadder and an off-palette amber
    // survived in Board until the STYLING-CONFORMANCE.md audit caught them by eye. This is the same
    // ban, in a form the walker can see.
    //
    // The lookbehind (not `\b`) matters: Tailwind arbitrary values join tokens with `_`
    // (`shadow-[0_0_30px_-5px_rgba(...)]`), and `_` is a word character, so `\b` does not fire
    // between it and `rgba` - that variant of the ban silently never matched.
    const hits = scan(/(?<![A-Za-z0-9])(?:rgba|rgb|hsl)\(/, [
      // Black/white text-shadow halo behind a piece glyph: a board affordance with no token.
      // Board.tsx's dest hints used to live here as raw rgba(); they are color-mix(--hue-green) now.
      "ui/components/MiniBoard.tsx",
      // Renders when the stylesheet may not have loaded, same reason as the hex check above.
      "ui/app/global-error.tsx",
      // Satori renders these at build time with no stylesheet, same reason as the hex check above.
      "ui/app/[locale]/opengraph-image.tsx",
      "ui/app/[locale]/replay/[id]/opengraph-image.tsx",
    ]);
    expect(
      hits.length,
      `styling.md section 10: rgba()/rgb()/hsl() is a raw colour the same way a hex literal is; use a token (or color-mix(in srgb, var(--token) …) the way Board.tsx's SELECTED_INSET does).\n${report(hits)}`,
    ).toBe(0);
  });

  it("does not reach for bg-black/NN or text-white/NN as a shade", () => {
    // A token at reduced alpha (border-[var(--border)]/50) is explicitly fine per section 2 - this
    // only matches the literal words `black`/`white`, never `var(--...)`.
    const hits = scan(/\b(?:bg-black|text-white)\/\d+\b/, [
      // The modal backdrop shell; already tolerated by "does not hand-roll a modal backdrop" above.
      "ui/components/Dialog.tsx",
      "ui/components/BotGameDialog.tsx",
      // Scrims over a user's uploaded photo, not a themed surface: the pixels underneath are
      // arbitrary, so a token at low alpha would tint them. styling.md section 2 carves this out
      // explicitly ("Black is still correct for a scrim over an image") - permanent, not deferred.
      "ui/components/AvatarCropper.tsx",
      "ui/components/EditableAvatar.tsx",
    ]);
    expect(
      hits.length,
      `styling.md section 10: a black/white wash is not a token, and three call sites will pick three different alphas. Use --surface or --muted.\n${report(hits)}`,
    ).toBe(0);
  });

  it("reserves space-y-* for a list-disc/list-decimal list", () => {
    const hits = scan(
      /\bspace-y-/,
      [],
      (line) => /list-disc/.test(line) || /list-decimal/.test(line),
    );
    expect(
      hits.length,
      `styling.md section 9: space-y-* is reserved for list-disc pl-5 space-y-2 inside a list; use flex flex-col gap-* otherwise.\n${report(hits)}`,
    ).toBe(0);
  });

  it("keeps shadow-* to the three tiers", () => {
    // Allowed: shadow-lg (raised), shadow-2xl (overlay), shadow-[0_0_...] (a glow, not an elevation
    // tier). Banned: shadow-sm/md/xl/inner/none, none of which is in section 5's table.
    const hits = scan(/\bshadow-(?:sm|md|xl|inner|none)\b/);
    expect(
      hits.length,
      `styling.md section 5: shadow-lg, shadow-2xl and an arbitrary shadow-[0_0_...] glow are the only three; this isn't one of them.\n${report(hits)}`,
    ).toBe(0);
  });

  it("keeps button padding on the documented size scale", () => {
    // The full six-size scale (Compact/Default/Hero/Form submit/Split row/Icon only, plus the
    // px-4 py-2 text-sm tab/chip size) can't be enforced as a strict whole-string match without
    // false positives: a button legitimately carries other classes too (mt-4, gap-2,
    // tracking-widest, whitespace-nowrap...). This instead bans the specific invented padding pairs
    // the styling-conformance audit found on btn-* elements - narrower than the spec, but it catches
    // the drift that actually happened instead of guessing at every legal combination of surrounding
    // classes.
    const hits = scan(
      // Anchored at ^ with two independent lookaheads, not one lookahead followed by a consuming
      // alternation: a plain `(?=.*btn-...)(?:BANNED)` only matches when BANNED occurs *after* the
      // position the lookahead started from, so once the scan position passes the (earlier) btn-*
      // class it can no longer see it and the whole thing silently never fires. Anchoring to line
      // start and putting the banned-padding check in its own lookahead makes both scan the full
      // line independently of which comes first.
      /^(?=.*\bbtn-(?:primary|secondary|danger(?:-fill)?)\b)(?=.*(?:\bpx-2\.5 py-1(?!\.)|\bpx-2 py-1(?!\.)|\bpx-6 py-2(?!\.)|\bpx-6 py-2\.5\b|\bpx-5 py-2\.5\b|\bpx-4 py-3\b|\bpx-3 py-2(?!\.)|\bflex-1\b[^"'`]*\bpy-2\b(?!\.)[^"'`]*\btext-sm\b|\bflex-1\b[^"'`]*\bpy-3\b(?!\.)))/,
      [
        "ui/components/BotGameDialog.tsx",
        "ui/components/ChallengeFriendPanel.tsx",
        "ui/app/global-error.tsx",
      ],
    );
    expect(
      hits.length,
      `styling.md section 6: that padding pair isn't one of the documented button sizes.\n${report(hits)}`,
    ).toBe(0);
  });

  it("keeps a split row at one height", () => {
    // "Split row applies to the whole row, not to one button in it." A precise "same row" detector
    // needs a JSX parser; this line-window heuristic instead flags a Split-row button
    // (flex-1 ... py-2.5) and a Compact button (px-3 py-1.5 text-sm) within ~28 lines of each other
    // in the same file, which in practice means the same button row.
    const WINDOW = 28;
    const SPLIT_ROW = /\bflex-1\b[^"'`]*\bpy-2\.5\b/;
    const COMPACT = /\bpx-3 py-1\.5 text-sm\b/;
    const found: Violation[] = [];
    for (const file of sourceFiles()) {
      const name = rel(file);
      const lines = withoutComments(readFileSync(file, "utf8"));
      const splitLines: number[] = [];
      const compactLines: number[] = [];
      lines.forEach((line, i) => {
        if (SPLIT_ROW.test(line)) splitLines.push(i);
        if (COMPACT.test(line)) compactLines.push(i);
      });
      for (const s of splitLines) {
        if (compactLines.some((c) => Math.abs(s - c) <= WINDOW)) {
          found.push({
            where: `${name}:${s + 1}`,
            text: lines[s].trim().slice(0, 120),
          });
        }
      }
    }
    expect(
      found.length,
      `styling.md section 6: a Compact button (px-3 py-1.5) sits within ${WINDOW} lines of a Split-row button (flex-1 py-2.5) - that pairs two different button heights in what looks like one row.\n${report(found)}`,
    ).toBe(0);
  });

  it("gives every NavBar-rendering page a main-content landmark with scroll-mt-24", () => {
    // Approximation for "every branch, not just the happy path" (section 3): counting occurrences
    // per file rather than tracing which <main> belongs to which early return. A file with three
    // `<NavBar` and only two `id="main-content"` is missing one somewhere, which is what the loading
    // and not-found branches of /game/[id], /watch/[id] and /replay/[id] did before this pass.
    const found: Violation[] = [];
    for (const file of sourceFiles()) {
      const name = rel(file);
      if (!name.startsWith("ui/app/")) continue;
      const content = withoutComments(readFileSync(file, "utf8")).join("\n");
      const navCount = (content.match(/<NavBar\b/g) || []).length;
      if (navCount === 0) continue;
      const mainCount = (content.match(/id="main-content"/g) || []).length;
      const scrollCount = (content.match(/scroll-mt-24/g) || []).length;
      if (mainCount < navCount || scrollCount < navCount) {
        found.push({
          where: name,
          text: `<NavBar>×${navCount}, id="main-content"×${mainCount}, scroll-mt-24×${scrollCount}`,
        });
      }
    }
    expect(
      found.length,
      `styling.md section 3: a branch that renders <NavBar> owes a <main id="main-content" ... scroll-mt-24>; this file has fewer of one of those than <NavBar> occurrences.\n${report(found)}`,
    ).toBe(0);
  });

  it("keeps flex flex-col wherever min-h-screen pairs with bg-[var(--background)]", () => {
    // Section 3: "min-h-screen flex flex-col ... plus flex-1 on the <main> ... [w]ithout the pair
    // the footer floats up under short content instead of sitting at the bottom." Not a verbatim
    // substring match - AuthShell legitimately reorders the shell string
    // (`flex min-h-screen w-full flex-col ...`) - so this checks that both classes are present as
    // their own tokens, not that they sit adjacent.
    const hits = scan(
      /(?=.*min-h-screen)(?=.*bg-\[var\(--background\)\])/,
      [],
      (line) => /\bflex\b(?!-)/.test(line) && /\bflex-col\b/.test(line),
    );
    expect(
      hits.length,
      `styling.md section 3: a min-h-screen bg-[var(--background)] shell also needs flex and flex-col (not necessarily adjacent), or the footer won't sit at the bottom of short content.\n${report(hits)}`,
    ).toBe(0);
  });

  it("still finds the files it is supposed to be scanning", () => {
    // Guards against the walker silently matching nothing (a wrong root, a rename),
    // which would make every assertion above pass for the wrong reason.
    expect(sourceFiles().length).toBeGreaterThan(80);
  });

  it("keeps page-board padding at py-6", () => {
    const hits = scan(
      /\bpage-board\b[^"'`\n]*\bpy-(?:10|16)\b|\bpy-(?:10|16)\b[^"'`\n]*\bpage-board\b/,
    );
    expect(
      hits.length,
      `styling.md section 3: page-board padding is py-6 on every branch.\n${report(hits)}`,
    ).toBe(0);
  });

  it("does not name the auth wordmark as an h1", () => {
    const source = readFileSync(
      join(UI_ROOT, "components", "BrandMark.tsx"),
      "utf8",
    );
    expect(
      source.includes("<h1"),
      "styling.md section 4: AuthBrandHeading is a home link, not the page h1.",
    ).toBe(false);
  });

  it("gives the home page an h1", () => {
    const source = readFileSync(
      join(UI_ROOT, "app", "[locale]", "page.tsx"),
      "utf8",
    );
    expect(
      /<h1\b/.test(source),
      "styling.md section 4 / P-10-1: the lobby owes an h1.",
    ).toBe(true);
  });

  it("routes the footer legal row to /about#licence", () => {
    const source = readFileSync(
      join(UI_ROOT, "components", "Footer.tsx"),
      "utf8",
    );
    expect(
      source.includes("/about#licence"),
      "P-10-6: the licence grant needs a site-wide footer route.",
    ).toBe(true);
  });

  it("does not dim TimeDeltaLayer labels with opacity-70", () => {
    const source = readFileSync(
      join(UI_ROOT, "components", "TimeDeltaLayer.tsx"),
      "utf8",
    );
    expect(
      source.includes("opacity-70"),
      "P-10-4: readable popup labels take full-strength colour, not an opacity wash.",
    ).toBe(false);
  });
});
