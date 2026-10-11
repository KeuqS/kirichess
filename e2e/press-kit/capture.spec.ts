import * as fs from "node:fs";
import * as path from "node:path";
import { test, expect, type Page, type Browser } from "@playwright/test";
import {
  newPlayer,
  registerAndConfirm,
  loginViaUi,
  userIdOf,
  whitePlayerIdOf,
  type Player,
} from "../fixtures/players";

/**
 * AD.1 (docs/ROADMAP.md): "nothing in this document produces a picture of [the clock swinging]
 * happening". This is that capture. A scripted, re-runnable artefact rather than a one-off screen
 * recording, so it can be regenerated whenever the board or clock UI changes instead of rotting.
 *
 * Run with a real stack up (`npm run stack:up` in e2e/), then:
 *   npx playwright test --config=playwright.presskit.config.ts
 *
 * Output lands in e2e/press-kit/output/ (gitignored scratch) AND is copied into the committed
 * press-kit/ folder at the repo root. Recapture is a desktop viewport (>=1024px, the `lg` clocks-
 * beside-board grid). After a re-record, run `node convert.mjs` in press-kit/ (ffmpeg on PATH)
 * to refresh the trimmed MP4 and GIF.
 */

const OUTPUT_DIR = path.resolve(__dirname, "output");
const PRESS_KIT_DIR = path.resolve(__dirname, "..", "..", "press-kit");

/** Desktop, same size as the rules-table shot below. AD.1's desktop recapture is not blocked by
 *  P-6-1: the stacked-clock mobile bug is irrelevant at >=1024px / `lg:`. */
const DESKTOP_VIEWPORT = { width: 1280, height: 900 };

/** steal1's display label lives in config/kirichess.rules.json. e2e/tsconfig does not include
 *  ui/, so this reads the JSON rather than importing presets.ts. */
function steal1PoolLabel(): string {
  const rulesPath = path.resolve(
    __dirname,
    "..",
    "..",
    "config",
    "kirichess.rules.json",
  );
  const rules = JSON.parse(fs.readFileSync(rulesPath, "utf8")) as {
    pools: Array<{ key: string; label: string }>;
  };
  const steal1 = rules.pools.find((p) => p.key === "steal1");
  if (!steal1?.label) {
    throw new Error(
      "config/kirichess.rules.json has no pool with key steal1; capture locators cannot resolve the label.",
    );
  }
  return steal1.label;
}

const STEAL1_LABEL = steal1PoolLabel();

/** Two clicks, not a drag. Copied from e2e/tests/critical-path.spec.ts. boardOrientation is
 *  hardcoded "white" for BOTH players, so these coordinates are absolute in either browser. */
async function move(page: Page, from: string, to: string): Promise<void> {
  await page.locator(`[data-square="${from}"]`).click();
  await page.locator(`[data-square="${to}"]`).click();
  await page.evaluate(() => window.scrollTo(0, 0));
}

/** PresetTile overlay uses catalogue aria-labels (`queueAriaLabel` / `searchingAriaLabel`),
 *  not the visible tile heading. English today: "Queue {name}" and "Searching {name}… (cancel)". */
function queueButton(page: Page, poolLabel: string) {
  return page.getByRole("button", { name: `Queue ${poolLabel}` });
}

function searchingButton(page: Page, poolLabel: string) {
  return page.getByRole("button", {
    name: `Searching ${poolLabel}… (cancel)`,
  });
}

async function freshRegisteredPage(
  browser: Browser,
  tag: string,
): Promise<{
  player: Player;
  context: Awaited<ReturnType<Browser["newContext"]>>;
}> {
  const context = await browser.newContext();
  const page = await context.newPage();
  const player = newPlayer(tag);
  await registerAndConfirm(page, player);
  await context.close();
  return { player, context };
}

test("capture: a queen capture swings the clock (steal1, Steal, base C1)", async ({
  browser,
}) => {
  fs.mkdirSync(OUTPUT_DIR, { recursive: true });

  // --- Registration, off camera: an account picker and a debounced display-name check make for
  // dead footage, and the clip is about the clock, not the register form. -----------------------
  const { player: alpha } = await freshRegisteredPage(browser, "PKA");
  const { player: beta } = await freshRegisteredPage(browser, "PKB");

  // --- Two recorded contexts, one discarded: matchmaking assigns colour randomly, and only the
  // capturing player's OWN screen shows the instant, optimistic animation this clip is for. Both
  // are recorded so whichever one turns out to be White is the one kept. -------------------------
  const contextA = await browser.newContext({
    viewport: DESKTOP_VIEWPORT,
    recordVideo: { dir: OUTPUT_DIR, size: DESKTOP_VIEWPORT },
  });
  const contextB = await browser.newContext({
    viewport: DESKTOP_VIEWPORT,
    recordVideo: { dir: OUTPUT_DIR, size: DESKTOP_VIEWPORT },
  });
  const pageA = await contextA.newPage();
  const pageB = await contextB.newPage();

  // loginViaUi (unlike registerViaUi) does not itself wait for the sign-in to land: navigating
  // away before the login POST resolves aborts it client-side, which is exactly what happened the
  // first time this ran without these two lines (pageB's queue click silently redirected to
  // /login because it never had a token). Wait on the same localStorage key userIdOf() /
  // whitePlayerIdOf() already read directly.
  await loginViaUi(pageA, alpha);
  await pageA.waitForFunction(() => localStorage.getItem("cs_auth") !== null);
  await loginViaUi(pageB, beta);
  await pageB.waitForFunction(() => localStorage.getItem("cs_auth") !== null);

  // A clean shot of the tiles before anyone queues, for the press kit's second screenshot.
  await pageB.goto("/");
  await pageB.screenshot({
    path: path.join(OUTPUT_DIR, "home-pools.png"),
    fullPage: false,
  });

  // --- Queue and pair, both for steal1 (label from rules.json): 6+0, Steal, base C1. A queen
  // capture there is exactly the ±25s docs/RULES.md quotes, and Steal moves both clocks at once
  // (captor gains, victim loses), which is the more legible motion for a ten-second clip than
  // Bounty's one-sided add. ---
  //
  // The 500ms pause after each goto is RatedPoolsGrid's own race, not this script's: its mount
  // effect calls `conn.start()` passively, and clicking Queue before that resolves calls
  // `conn.start()` a second time on a connection that is already connecting, which SignalR rejects
  // with "Cannot start a HubConnection that is not in the 'Disconnected' state." Caught into the
  // tile's error line instead of joining the queue. Out of scope to fix here; waiting it out is
  // enough for a script that runs once, on demand, rather than every CI build.
  await pageA.goto("/");
  await pageA.waitForTimeout(500);
  await queueButton(pageA, STEAL1_LABEL).click();
  await expect(searchingButton(pageA, STEAL1_LABEL)).toBeVisible();

  await pageB.goto("/");
  await pageB.waitForTimeout(500);
  await queueButton(pageB, STEAL1_LABEL).click();

  const gameUrl = /\/game\/[0-9a-f-]{36}\?preset=steal1/;
  await expect(pageA).toHaveURL(gameUrl);
  await expect(pageB).toHaveURL(gameUrl);
  const gameId = new URL(pageA.url()).pathname.split("/").pop()!;

  await expect(pageA.locator('[data-square="e2"]')).toBeVisible();
  await expect(pageB.locator('[data-square="e2"]')).toBeVisible();

  const whiteId = await whitePlayerIdOf(pageA, gameId);
  const white = (await userIdOf(pageA)) === whiteId ? pageA : pageB;
  const black = white === pageA ? pageB : pageA;
  const whiteContext = white === pageA ? contextA : contextB;
  const blackContext = white === pageA ? contextB : contextA;

  // --- The featured swing: two moves each to put both queens in the open, then White takes.
  // 1. e4 e5  2. Qf3 Qf6  3. Qxf6: every square on this path is empty (only the e-pawns have
  // moved), so it is legal regardless of engine move-generation quirks, and it needs no recapture
  // for the shot: the clip ends the instant the capturing move lands. -----------------------------
  await move(white, "e2", "e4");
  await expect(black.getByText(/e2e4/)).toBeVisible();

  await move(black, "e7", "e5");
  await expect(white.getByText(/e7e5/)).toBeVisible();

  await move(white, "d1", "f3");
  await expect(black.getByText(/d1f3/)).toBeVisible();

  await move(black, "d8", "f6");
  await expect(white.getByText(/d8f6/)).toBeVisible();

  // The capture itself: recorded on White's own screen, whichever context that turned out to be.
  await move(white, "f3", "f6");
  await expect(white.getByText(/f3f6/)).toBeVisible();

  // Let the capture's time-delta popup and the clock tick land before the shot and the recording
  // both end, otherwise the last frame is mid-animation rather than the settled swing.
  await white.waitForTimeout(2_500);
  await white.evaluate(() => window.scrollTo(0, 0));
  await white.screenshot({
    path: path.join(OUTPUT_DIR, "queen-capture.png"),
    fullPage: false,
  });

  const whiteVideo = white.video();
  await blackContext.close();
  await whiteContext.close(); // finalises the .webm; video().path() only resolves after this

  const rawClipPath = whiteVideo ? await whiteVideo.path() : null;

  // --- A clean shot of the manual's capture table, for the press kit's third screenshot. Not part
  // of the game recording, so a fresh unauthenticated context is enough. --------------------------
  const rulesContext = await browser.newContext({
    viewport: DESKTOP_VIEWPORT,
  });
  const rulesPage = await rulesContext.newPage();
  await rulesPage.goto("/rules#captures");
  await rulesPage.locator("article#captures").scrollIntoViewIfNeeded();
  await rulesPage
    .locator("article#captures")
    .screenshot({ path: path.join(OUTPUT_DIR, "rules-captures-table.png") });
  await rulesContext.close();

  // --- Copy the curated set into the committed press-kit/ folder. ---------------------------------
  fs.mkdirSync(path.join(PRESS_KIT_DIR, "clip"), { recursive: true });
  fs.mkdirSync(path.join(PRESS_KIT_DIR, "screenshots"), { recursive: true });

  if (rawClipPath && fs.existsSync(rawClipPath)) {
    fs.copyFileSync(
      rawClipPath,
      path.join(PRESS_KIT_DIR, "clip", "queen-capture-raw.webm"),
    );
  }
  for (const name of [
    "home-pools.png",
    "queen-capture.png",
    "rules-captures-table.png",
  ]) {
    const src = path.join(OUTPUT_DIR, name);
    if (fs.existsSync(src)) {
      fs.copyFileSync(src, path.join(PRESS_KIT_DIR, "screenshots", name));
    }
  }

  console.log(
    `Press kit refreshed: ${path.join(PRESS_KIT_DIR, "clip", "queen-capture-raw.webm")}`,
  );
});
