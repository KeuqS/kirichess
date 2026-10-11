import { test, expect } from "@playwright/test";
import AxeBuilder from "@axe-core/playwright";
import type { Result } from "axe-core";
import { newPlayer, registerAndConfirm } from "../fixtures/players";

/**
 * Phase 9d.4: accessibility baseline. Scans every anonymous-reachable page against the WCAG
 * 2.0/2.1 A+AA ruleset (`withTags` below), the tag set every mainstream a11y CI gate uses, and
 * what the roadmap's "verifiable with axe in CI" acceptance criterion means in practice.
 * `game/[id]`, `watch/[id]` and `replay/[id]`'s live board are deliberately NOT scanned here:
 * keyboard move entry and a screen-reader board are 9f.2's job, not this baseline's, and axe
 * cannot evaluate "can a screen-reader user actually play", only markup-level defects.
 *
 * P-5-3 / L-8-4 (2026-09-06): the page list had drifted behind the actual route list.
 * `/developers`, `/offline`, `/unsubscribe` and the 404 catch-all were reachable by a stranger and
 * unscanned, and `/reset-password` / `/verify-email` (both link-only, reachable only from an
 * email) had no coverage at all. All five are covered below; the two forms get their own test
 * with syntactically-valid dummy query params, for the reason spelled out at each one.
 *
 * Explicitly OUT of scope, decided here rather than left ambiguous: the staff-403 render inside
 * `/moderation` and `/analytics`. It is not anonymous-reachable, since it only appears for an
 * *authenticated* non-admin session hitting a staff-only route, so it does not belong in
 * `ANONYMOUS_PAGES` by the file's own definition. It would need the same signed-in-fixture
 * treatment as `/custom` below, for a page whose entire content is a generic "forbidden" notice;
 * revisit only if that notice grows real content worth scanning.
 *
 * "best-practice"-tagged rules (e.g. `landmark-one-main`, `region`) are excluded on purpose: they
 * are style guidance the axe-core project itself does not classify as WCAG conformance, and
 * gating this suite on them would fail the build over findings that are not accessibility defects.
 */
const WCAG_TAGS = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"];

async function expectNoViolations(
  page: import("@playwright/test").Page,
): Promise<void> {
  await page.waitForLoadState("networkidle");
  const results = await new AxeBuilder({ page }).withTags(WCAG_TAGS).analyze();
  expect(results.violations, describeViolations(results.violations)).toEqual(
    [],
  );
}

/** Turns axe's violation objects into a readable failure message instead of a raw JSON dump. */
function describeViolations(violations: Result[]): string {
  if (violations.length === 0) return "";
  return violations
    .map((v) => {
      const targets = v.nodes
        .map((n) => `  - ${n.target.join(" ")}`)
        .join("\n");
      return `[${v.impact}] ${v.id}: ${v.help} (${v.helpUrl})\n${targets}`;
    })
    .join("\n\n");
}

// Content-only pages, reachable logged-out, no live game state. Every one of these was audited
// and hand-fixed for Phase 9d.4 (reduced-motion, focus-visible, label/aria-label, and the
// site-wide contrast pass in globals.css), this loop is what keeps them that way.
const ANONYMOUS_PAGES = [
  "/",
  "/login",
  "/forgot-password",
  "/rules",
  "/learn",
  "/leaderboards",
  "/watch",
  "/donate",
  "/privacy",
  "/terms",
  "/about",
  "/developers",
  "/offline",
  "/unsubscribe",
];

for (const path of ANONYMOUS_PAGES) {
  test(`accessibility: ${path} has no WCAG A/AA violations`, async ({
    page,
  }) => {
    await page.goto(path);
    await expectNoViolations(page);
  });
}

// The catch-all 404 (`app/[locale]/[...rest]/page.tsx`) is reachable by any stranger who mistypes
// or follows a stale link; it renders inside the real layout (nav, footer, locale intact) rather
// than a bare error page, so it deserves the same floor as everything else in the list above.
test("accessibility: the 404 page has no WCAG A/AA violations", async ({
  page,
}) => {
  await page.goto("/this-page-does-not-exist-a11y-probe");
  await expectNoViolations(page);
});

// /reset-password and /verify-email are both link-only: nothing in the app links to them, they
// exist purely to be the target of an emailed link, so nothing has ever exercised their markup.
// The two pages want the dummy params for different reasons, so they get separate tests.
//
// /reset-password renders its form whatever the query string says (the email/token check happens
// inside handleSubmit, not at render), so the params here are realism rather than a gate. What is
// being scanned is the form itself: labelled password field, the show/hide toggle, the error
// banner's markup.
test("accessibility: /reset-password has no WCAG A/AA violations", async ({
  page,
}) => {
  await page.goto(
    "/reset-password?email=a11y-probe%40example.com&token=a11y-dummy-token",
  );
  await expectNoViolations(page);
});

// /verify-email is the one where the params change what renders: without them its mount effect
// short-circuits to the "missing userId or token" error banner. With them it calls the API, fails
// on the dummy token, and renders the API-error banner instead, which is the state a real reader
// following a stale link actually lands on.
test("accessibility: /verify-email has no WCAG A/AA violations", async ({
  page,
}) => {
  await page.goto(
    "/verify-email?userId=00000000-0000-0000-0000-000000000000&token=a11y-dummy-token",
  );
  await expectNoViolations(page);
});

// The one form gated behind auth that's cheap to reach here: register a fresh account (same
// fixture every other authed spec uses) and scan the custom-game builder, the densest form on
// the site (two dozen number/toggle/select fields) and the one 9d.4 found an unlabelled range
// input on.
test("accessibility: /custom (signed in) has no WCAG A/AA violations", async ({
  page,
}) => {
  const player = newPlayer("a11y");
  await registerAndConfirm(page, player);
  await page.goto("/custom");
  await expectNoViolations(page);
});

// A public profile needs a real account to point at, so it cannot join the static ANONYMOUS_PAGES
// loop above: register one, take its id out of the auth blob the app stores, then scan the page it
// gives every visitor. The route itself needs no session - what is being scanned here is the
// populated state, which is the one the loop could not produce.
test("accessibility: /u/[id] has no WCAG A/AA violations", async ({ page }) => {
  // Tag kept short: newPlayer builds "Tester <tag> <6 digits>", and ContentModeration:MaxLength is
  // 24, so anything past an 8-char tag makes register reject the display name before this test can
  // sign in ("a11yprofile" produced a 25-char name).
  const player = newPlayer("profile");
  await registerAndConfirm(page, player);

  const userId = await page.evaluate(() => {
    const raw = window.localStorage.getItem("cs_auth");
    return raw ? (JSON.parse(raw) as { userId: string }).userId : null;
  });
  expect(userId).not.toBeNull();

  await page.goto(`/u/${userId}`);
  await expect(
    page.getByRole("heading", { name: player.displayName }),
  ).toBeVisible();
  await expectNoViolations(page);
});
