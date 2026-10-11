import { test, expect, type Page } from "@playwright/test";
import {
  newPlayer,
  registerAndConfirm,
  userIdOf,
  whitePlayerIdOf,
} from "../fixtures/players";
import { POOLS } from "../../ui/lib/rules.generated";

/** The pool this spec queues into, by its stable key. Its button text is the pool's display name,
 *  which config/kirichess.rules.json owns, so read the label rather than typing it: a rebrand should
 *  not redden an end-to-end test that is about pairing, not about naming. */
const QUEUE_POOL = POOLS.bounty1.label;

/** Two clicks, not a drag: Board.tsx implements select-then-move via onSquareClick, and dragging
 *  goes through react-dnd-touch-backend with delayTouchStart plus a 300 ms board animation, the
 *  single flakiest interaction available here. boardOrientation is hardcoded "white" for BOTH
 *  players, so these coordinates are absolute in either browser. */
async function move(page: Page, from: string, to: string): Promise<void> {
  await page.locator(`[data-square="${from}"]`).click();
  await page.locator(`[data-square="${to}"]`).click();
}

/** The PoolRatingCard rating for a named pool on `/profile`. Sibling of the heading's wrapper in
 *  the card header row; provisional ratings carry a trailing `?`, which the caller strips. */
function poolRating(page: Page, poolLabel: string) {
  return page
    .getByRole("heading", { name: poolLabel, exact: true })
    .locator("xpath=../../*[last()]");
}

test("register → confirm → queue → pair → play → flag-fall → rating change", async ({
  browser,
}) => {
  const contextA = await browser.newContext();
  const contextB = await browser.newContext();
  const pageA = await contextA.newPage();
  const pageB = await contextB.newPage();

  const alpha = newPlayer("A");
  const beta = newPlayer("B");
  await registerAndConfirm(pageA, alpha);
  await registerAndConfirm(pageB, beta);

  // --- Queue and pair -------------------------------------------------------------------------
  // Sequenced deliberately: MatchQueue pairs an ENTRANT against someone already waiting, so A has to
  // be visibly in the queue before B enters or both just enqueue and neither is matched.
  await pageA.goto("/");
  await pageA.getByRole("button", { name: `Queue ${QUEUE_POOL}` }).click();
  await expect(
    pageA.getByRole("button", { name: new RegExp(`Searching ${QUEUE_POOL}`) }),
  ).toBeVisible();

  await pageB.goto("/");
  await pageB.getByRole("button", { name: `Queue ${QUEUE_POOL}` }).click();

  // MatchFound routes both browsers to the same game. Asserting they agree on the id is the whole
  // pairing contract: two players who clicked Play found EACH OTHER, not two separate games.
  const gameUrl = /\/game\/[0-9a-f-]{36}\?preset=bounty1/;
  await expect(pageA).toHaveURL(gameUrl);
  await expect(pageB).toHaveURL(gameUrl);
  const gameId = new URL(pageA.url()).pathname.split("/").pop()!;
  expect(new URL(pageB.url()).pathname).toContain(gameId);

  // Both boards actually mounted (Board is a dynamic ssr:false import behind a "Loading board…").
  await expect(pageA.locator('[data-square="e2"]')).toBeVisible();
  await expect(pageB.locator('[data-square="e2"]')).toBeVisible();

  // --- Who is White ---------------------------------------------------------------------------
  const whiteId = await whitePlayerIdOf(pageA, gameId);
  const white = (await userIdOf(pageA)) === whiteId ? pageA : pageB;
  const black = white === pageA ? pageB : pageA;

  // --- Play -----------------------------------------------------------------------------------
  // One real move each, submitted through the board over SignalR. Asserting the move on the
  // OPPONENT's page is what proves the MovePlayed broadcast landed, not just the local optimistic
  // render, the wire contract 8.5 said reading routes could not check.
  await move(white, "e2", "e4");
  await expect(white.getByText(/e2e4/)).toBeVisible();
  await expect(black.getByText(/e2e4/)).toBeVisible();

  await move(black, "e7", "e5");
  await expect(white.getByText(/e7e5/)).toBeVisible();
  await expect(black.getByText(/e7e5/)).toBeVisible();

  // --- Flag-fall ------------------------------------------------------------------------------
  // It is White's turn and White simply stops. bounty1 starts at 60 s (config/kirichess.rules.json),
  // the clock is authoritative server-side, and FlagFallMonitor sweeps once a second and broadcasts
  // GameEnded to the group. This is a real 60 seconds and that is the design, not an oversight,
  // README decision 1. There is no sleep here: the wait IS the assertion.
  const flagFall = { timeout: 90_000 };
  await expect(white.getByRole("heading", { name: "Flag fell" })).toBeVisible(
    flagFall,
  );
  await expect(black.getByRole("heading", { name: "Flag fell" })).toBeVisible(
    flagFall,
  );
  // `exact` matters since 9f.2: the result is now also announced to screen readers through the
  // assertive live region, whose text is the whole sentence ("Flag fell. Black wins."). A substring
  // match therefore resolves to two elements and trips Playwright's strict mode. The overlay's own
  // paragraph is exactly "Black wins", so an exact match keeps this assertion about what is drawn
  // on screen, which is what it was always testing.
  await expect(white.getByText("Black wins", { exact: true })).toBeVisible();
  await expect(black.getByText("Black wins", { exact: true })).toBeVisible();

  // --- Rating change --------------------------------------------------------------------------
  // GameRegistry.PersistIfOverAsync applies Glicko-2 and commits BEFORE FlagFallMonitor broadcasts
  // GameEnded, so the overlay above already implies the rating rows exist. Profile fetches
  // getMyRatings() on mount; read the bounty1 card there (the pool this game was rated in).
  //
  // Direction only, never a number: both accounts start at 1500/RD350 and one decisive game moves
  // them a long way in opposite directions, but Glicko-2's exact output is not this phase's contract.
  await white.goto("/profile");
  await black.goto("/profile");
  await expect(poolRating(white, QUEUE_POOL)).toBeVisible();
  await expect(poolRating(black, QUEUE_POOL)).toBeVisible();

  const readRating = async (page: Page) =>
    Number((await poolRating(page, QUEUE_POOL).innerText()).replace("?", ""));

  expect(await readRating(black)).toBeGreaterThan(1500); // winner
  expect(await readRating(white)).toBeLessThan(1500); // loser, flagged

  await contextA.close();
  await contextB.close();
});
