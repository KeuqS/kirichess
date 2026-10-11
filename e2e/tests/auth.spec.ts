import { test, expect } from "@playwright/test";
import { newPlayer, registerViaUi, loginViaUi } from "../fixtures/players";
import { confirmationLinkFor } from "../fixtures/stack";

test("email confirmation gates sign-in, and the mailed link clears it", async ({
  page,
}) => {
  const player = newPlayer("auth");

  await registerViaUi(page, player);

  // Signed in from register, but the account is unconfirmed. Auth__RequireConfirmedEmail is true in
  // the E2E stack, so the moment this session ends, sign-in is refused, which is what makes the
  // confirm step load-bearing rather than decorative.
  await page.getByRole("button", { name: "Log out" }).click();
  await expect(page).toHaveURL(/\/login/);

  await loginViaUi(page, player);
  await expect(page.getByText("Email not confirmed.")).toBeVisible();

  // The URL the mail would have carried, read off the backend's stdout.
  await page.goto(await confirmationLinkFor(player.email));
  await expect(page.getByText("Your email is confirmed.")).toBeVisible();

  await loginViaUi(page, player);
  await expect(page.getByRole("button", { name: "Log out" })).toBeVisible();
});
