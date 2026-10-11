import { test, expect } from "@playwright/test";

/**
 * Phase 9d.6: SEO & social preview cards. `next dev`-only, ad-hoc checks caught a real bug (the
 * replay metadata's error-fallback branch losing its openGraph/twitter override to the parent's
 * during merge) before this suite existed; these are what keep it caught. Not a full axe-style
 * audit, just: the two file-convention routes are actually served, the home page's card is
 * populated, and the metadata-generating `/replay/[id]` route degrades to a real fallback (not a
 * 500) for a game that doesn't exist, since no CI fixture produces a finished game to check the
 * populated-card path against (same limitation 9d.4's replay-board check hit).
 */

test("robots.txt allows the public site and points at the sitemap", async ({
  request,
}) => {
  const res = await request.get("/robots.txt");
  expect(res.status()).toBe(200);
  const body = await res.text();
  expect(body).toContain("Sitemap: http://localhost:3000/sitemap.xml");
  expect(body).toContain("Disallow: /moderation");
  expect(body).toContain("Disallow: /replay/");
  // M-1-3: both carry a live token in the URL (an unsubscribe token that fires on load, an invite
  // token in the challenge path) and were missing from this list before the fix.
  expect(body).toContain("Disallow: /unsubscribe");
  expect(body).toContain("Disallow: /challenge/");
});

// M-1-3: robots.txt is advisory, so a bot that fetches the URL anyway should still see an explicit
// noindex rather than relying on the disallow line alone.
test("/unsubscribe sets a noindex robots meta tag", async ({ page }) => {
  await page.goto("/unsubscribe");
  await expect(page.locator('meta[name="robots"]')).toHaveAttribute(
    "content",
    /noindex/,
  );
});

test("sitemap.xml lists the growth/ranking pages", async ({ request }) => {
  const res = await request.get("/sitemap.xml");
  expect(res.status()).toBe(200);
  const body = await res.text();
  for (const path of [
    // The home entry is the bare origin, no trailing slash: sitemap.ts strips it from "/" on
    // purpose (a slash would make the non-default locales read as "/nl/", a different URL), and
    // SITE_URL itself carries no trailing slash, so this is the canonical form the rest of the
    // metadata uses too.
    "<loc>http://localhost:3000</loc>",
    "/rules</loc>",
    "/learn</loc>",
    "/leaderboards</loc>",
  ]) {
    expect(body).toContain(path);
  }
});

test("home page carries a populated openGraph/twitter card", async ({
  page,
}) => {
  await page.goto("/");
  await expect(page.locator('meta[property="og:title"]')).toHaveAttribute(
    "content",
    "KiriChess",
  );
  await expect(page.locator('meta[property="og:description"]')).toHaveAttribute(
    "content",
    /./,
  );
  await expect(page.locator('meta[property="og:image"]')).toHaveAttribute(
    "content",
    /.+/,
  );
  await expect(page.locator('meta[name="twitter:card"]')).toHaveAttribute(
    "content",
    "summary_large_image",
  );
});

test("rules and learn pages have distinct titles", async ({ page }) => {
  await page.goto("/rules");
  await expect(page).toHaveTitle(/Rules/);
  await page.goto("/learn");
  await expect(page).toHaveTitle(/Learn/);
});

// M-1-1: before the fix, every page below inherited the root layout's whole openGraph/twitter
// object (title "KiriChess", url "/"), so a shared /rules link previewed as the homepage. M-1-2:
// each also gets a self-referencing canonical instead of none at all.
test("/rules carries its own openGraph card and canonical, not the homepage's", async ({
  page,
}) => {
  await page.goto("/rules");
  await expect(page.locator('meta[property="og:title"]')).not.toHaveAttribute(
    "content",
    "KiriChess",
  );
  await expect(page.locator('meta[property="og:url"]')).toHaveAttribute(
    "content",
    "http://localhost:3000/rules",
  );
  await expect(page.locator('link[rel="canonical"]')).toHaveAttribute(
    "href",
    "http://localhost:3000/rules",
  );
});

// M-1-4: a WebSite + Organization graph should exist somewhere in the document, minimally.
test("home page carries a JSON-LD WebSite/Organization graph", async ({
  page,
}) => {
  await page.goto("/");
  const script = page.locator('script[type="application/ld+json"]');
  await expect(script).toHaveCount(1);
  const raw = await script.textContent();
  const data = JSON.parse(raw ?? "{}");
  const types = (data["@graph"] ?? []).map(
    (node: { "@type": string }) => node["@type"],
  );
  expect(types).toContain("WebSite");
  expect(types).toContain("Organization");
});

test("a nonexistent replay degrades to a real fallback card, not a 500", async ({
  page,
  request,
}) => {
  const res = await page.goto("/replay/00000000-0000-0000-0000-000000000000");
  expect(res?.status()).toBe(200);
  await expect(page.locator('meta[property="og:title"]')).toHaveAttribute(
    "content",
    "Replay",
  );
  await expect(page.locator('meta[property="og:description"]')).toHaveAttribute(
    "content",
    /isn't available/,
  );

  const imageRes = await request.get(
    "/replay/00000000-0000-0000-0000-000000000000/opengraph-image",
  );
  expect(imageRes.status()).toBe(200);
  expect(imageRes.headers()["content-type"]).toBe("image/png");
});
