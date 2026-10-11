/**
 * Appends `?client_reference_id=<reference>` to a Stripe payment link so the webhook can connect
 * the checkout back to the KiriChess account that started it, without Stripe ever receiving an
 * account id directly. The reference is a random single-use row, not the account Guid.
 *
 * Uses `URL`/`URLSearchParams` rather than string concatenation: `STRIPE_CUSTOM_AMOUNT` and some
 * tier links may already carry a query string, and naive concatenation would produce a broken
 * second `?`.
 *
 * Returns `href` unchanged when `reference` is null, or when `href` isn't a parseable absolute URL.
 * A donation must never be blocked by this: `/donate` still renders the plain payment link when
 * the mint has not landed, and shows a visible warning when it failed ($1-4).
 */
export function appendReference(
  href: string,
  reference: string | null,
): string {
  if (!reference) return href;
  try {
    const url = new URL(href);
    url.searchParams.set("client_reference_id", reference);
    return url.toString();
  } catch {
    return href;
  }
}
