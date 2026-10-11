import { readFileSync } from "node:fs";
import { join } from "node:path";
import { describe, expect, it } from "vitest";

/**
 * Source pins for B29 product-surface fixes that live in App Router pages
 * (the components vitest project does not load `app/**`).
 */

const UI_ROOT = join(__dirname, "..");

function readUi(rel: string): string {
  return readFileSync(join(UI_ROOT, rel), "utf8");
}

describe("C-10-9: auth callback uses shared safeNextPath", () => {
  const source = readUi("app/[locale]/auth/callback/page.tsx");

  it("imports safeNextPath from challengeLink and does not define a local copy", () => {
    expect(source).toMatch(
      /import\s*\{[^}]*\bsafeNextPath\b[^}]*\}\s*from\s*"@\/lib\/challengeLink"/,
    );
    expect(source).not.toMatch(/function\s+safe(?:Next|Return)Path\s*\(/);
  });
});

describe("P-1-6: public profile metadata and loading h1", () => {
  it("ships a layout that generateMetadata-titles from the display name", () => {
    const layout = readUi("app/[locale]/u/[id]/layout.tsx");
    expect(layout).toMatch(/export async function generateMetadata/);
    expect(layout).toMatch(/getPublicProfile/);
    expect(layout).toMatch(/profile\.displayName/);
  });

  it("gives the loading branch an sr-only h1", () => {
    const page = readUi("app/[locale]/u/[id]/page.tsx");
    expect(page).toMatch(/loadingTitle/);
    expect(page).toMatch(/<h1 className="sr-only">/);
  });
});

describe("P-1-7: unsubscribe success links to profile preferences", () => {
  it("points the success state at /profile#notifications", () => {
    const page = readUi("app/[locale]/unsubscribe/page.tsx");
    expect(page).toMatch(/href="\/profile#notifications"/);
    expect(page).toMatch(/managePreferences/);
  });

  it("anchors the preferences card so that hash lands on it", () => {
    const card = readUi("components/NotificationPreferencesCard.tsx");
    expect(card).toMatch(/id="notifications"/);
    expect(card).toMatch(/scroll-mt-24/);
  });
});

describe("P-1-8: board-page early returns keep the page shell", () => {
  const files = [
    "app/[locale]/game/[id]/page.tsx",
    "app/[locale]/watch/[id]/page.tsx",
    "app/[locale]/replay/[id]/page.tsx",
  ];

  for (const file of files) {
    it(`${file} has a main landmark and an h1 on every NavBar branch`, () => {
      const source = readUi(file);
      const navCount = (source.match(/<NavBar\b/g) || []).length;
      const mainCount = (source.match(/id="main-content"/g) || []).length;
      const h1Count = (source.match(/<h1\b/g) || []).length;
      const emptyH1 = (source.match(/headingLevel=\{1\}/g) || []).length;
      expect(navCount).toBeGreaterThan(0);
      expect(mainCount).toBeGreaterThanOrEqual(navCount);
      expect(h1Count + emptyH1).toBeGreaterThanOrEqual(navCount);
    });
  }
});
