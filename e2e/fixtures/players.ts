import { expect, type Page } from "@playwright/test";
import { API_BASE, confirmationLinkFor } from "./stack";

export interface Player {
  email: string;
  password: string;
  displayName: string;
}

/**
 * A fresh account. The suffix is DIGITS ONLY on purpose: 8a.4's ContentPolicy rejects a display name
 * containing any of 42 blocked substrings, several of them three letters long, and a random base36
 * suffix can produce one. A digit run cannot. The name also has to satisfy MinLength 3 / MaxLength 24,
 * ASCII letters+digits+space/._- only, and no leading, trailing or doubled separator, and it must not
 * be a reserved name ("Admin" and 32 others), which fails INSIDE the register call rather than at your
 * assertion.
 *
 * The password needs mixed case and a digit: Identity still defaults RequireDigit / RequireLowercase
 * / RequireUppercase to true. RequireNonAlphanumeric is off (8.8a). A lowercase-only password like
 * "e2e-password-1234" is rejected with "PasswordRequiresUpper", verified against the real
 * /api/auth/register endpoint while building this harness.
 */
export function newPlayer(tag: string): Player {
  const stamp = `${String(Date.now()).slice(-8)}${Math.floor(Math.random() * 1000)}`;
  return {
    email: `e2e-${tag}-${stamp}@kirichess.test`,
    password: "E2e-password-1234",
    displayName: `Tester ${tag} ${stamp.slice(-6)}`,
  };
}

/**
 * The register tab of /login.
 *
 * `fill()` and never `pressSequentially()`: useDisplayNameCheck debounces 400 ms and then calls
 * GET /api/account/display-name/check, which carries RateLimitPolicies.Auth, 30 permits per MINUTE
 * partitioned by IP, and in CI every browser context shares one IP. `fill()` dispatches one input
 * event, so this costs one request instead of one per character. See README decision 5.
 *
 * The submit button is scoped to the form: on the login tab the tab strip and the submit button are
 * both named "Log in", which is a strict-mode violation unscoped.
 *
 * The password field is NOT selected via getByLabel: the <label> wraps both the <input> and the
 * show/hide <button aria-label="Show password">, so the input's computed accessible name is
 * "Password Show password At least 8 characters." (the whole label subtree), not "Password",
 * verified against a real page snapshot while building this harness, exactly the strict-mode/
 * accessible-name caveat the plan flagged. `form input[type="password"]` is the one password input
 * on this page, in either tab.
 */
export async function registerViaUi(page: Page, player: Player): Promise<void> {
  await page.goto("/login");
  await page.getByRole("button", { name: "Register", exact: true }).click();

  await page.getByLabel("Display name").fill(player.displayName);
  await page.getByLabel("Email", { exact: true }).fill(player.email);
  await page.locator('form input[type="password"]').fill(player.password);
  await page.getByLabel(/at least 16 years old/i).check();

  // Wait for the Turnstile token, not just for the button, before clicking. P-4-5 made the widget
  // render after an async wait for `window.turnstile`, so the token now lands roughly when this
  // fixture is ready to submit, and submit is disabled until it does. Clicking in the same frame
  // the button flips enabled loses the React submit handler: the click is dispatched, no request is
  // ever made, and the form just sits there filled, which reads as a register failure three tests
  // away from here. The hidden input is Turnstile's own output and is empty until the challenge
  // solves; the e2e stack always sets NEXT_PUBLIC_TURNSTILE_SITE_KEY (docker-compose.e2e.yml), so
  // it is always present here.
  await expect(page.locator('input[name="cf-turnstile-response"]')).not.toHaveValue("");

  const submit = page
    .locator("form")
    .getByRole("button", { name: "Create account" });
  await expect(submit).toBeEnabled();
  await submit.click();

  // Register issues a token pair immediately (it always has, independent of
  // Auth:RequireConfirmedEmail), so a successful register lands signed in on "/".
  await expect(page.getByRole("button", { name: "Log out" })).toBeVisible();
}

export async function loginViaUi(page: Page, player: Player): Promise<void> {
  await page.goto("/login");
  await page.getByLabel("Email", { exact: true }).fill(player.email);
  await page.locator('form input[type="password"]').fill(player.password);
  await page.locator("form").getByRole("button", { name: "Log in" }).click();
}

/** Register, then follow the confirmation link out of the log sink. Leaves the page signed in on "/". */
export async function registerAndConfirm(
  page: Page,
  player: Player,
): Promise<void> {
  await registerViaUi(page, player);
  const link = await confirmationLinkFor(player.email);
  await page.goto(link);
  await expect(page.getByText("Your email is confirmed.")).toBeVisible();
  await page.goto("/");
}

export async function userIdOf(page: Page): Promise<string> {
  return page.evaluate(
    () => JSON.parse(localStorage.getItem("cs_auth")!).userId as string,
  );
}

/**
 * Which player id is White in this game.
 *
 * ORCHESTRATION, NOT ASSERTION (README decision 8). MatchmakingService randomises colours, so the
 * spec has to discover which browser to drive; every actual assertion is made against the rendered
 * DOM, because "the UI was checked by reading routes, which would not catch a wrong response shape"
 * is the exact gap this whole phase exists to close. Do not grow this into a way of checking outcomes
 * over HTTP.
 *
 * GET /api/games/{id} reads GameRegistry's IN-MEMORY session, so it 404s once the game ends and is
 * evicted. Call it while the game is live.
 */
export async function whitePlayerIdOf(
  page: Page,
  gameId: string,
): Promise<string> {
  const token = await page.evaluate(
    () => JSON.parse(localStorage.getItem("cs_auth")!).token as string,
  );
  const res = await fetch(`${API_BASE}/api/games/${gameId}`, {
    headers: { Authorization: `Bearer ${token}` },
  });
  if (!res.ok)
    throw new Error(`GET /api/games/${gameId} returned ${res.status}`);
  return ((await res.json()) as { whitePlayerId: string }).whitePlayerId;
}
