# Patron badge: go-live checklist

The code is on `dev` / `fix/audit-2026-08-31` (originally merged on `integrate/patron-badge`). **None
of that makes the badge work in production.** Recurring donations stay off (`MONTHLY_ENABLED =
false`, TERMS §12); this go-live is the one-off badge only.

**Checked 2026-09-04.** Production `kirichess-api` v17 was a `workflow_dispatch` of `main`.
`AddPatronTiers` is **not** an ancestor of `origin/main`, so the schema is not there.
Live Stripe destination `KiriChess production` exists (`https://api.kirichess.com/api/webhooks/stripe`,
four events). `Stripe__WebhookSecret` is **not** on Fly yet - set it on the deploy that ships the
new webhook handler, not before. TERMS §12 and the worktree-hook false-positive fix are already in
the tree. `$1-1` (API-version 400) and `$1-3` (grant on unpaid checkout) are fixed in
`StripeWebhookEndpoints.cs`.

Do the remaining steps in this order. Step 1 is the remaining blocker. Steps 2–5 are done
(Fly secret rides the deploy).

| # | Step | Who | Blocking? | State |
| --- | --- | --- | --- | --- |
| 1 | [Apply the migration](#1-apply-the-migration) | you (deploy from a branch that contains it) | yes, the app will not start correctly against an unmigrated DB | **not applied** - not on `main` |
| 2 | [Configure the Stripe webhook](#2-configure-the-stripe-webhook) | you (Stripe dashboard) | yes, without it no badge is ever granted or revoked | **done 2026-09-04** - live destination, four events; Fly secret waits on step 1 |
| 3 | [Write the TERMS donations section](#3-write-the-terms-donations-section) | done 2026-08-23/24 | before the donate page is public | **done** - TERMS §12, version `2026-08-24` |
| 4 | [Deactivate the monthly payment links](#5-deactivate-the-monthly-payment-links) | done 2026-09-04 | yes, TERMS §12 is one-off only | **done** - five monthly links deactivated |

---

## 1. Apply the migration

The migration was generated but deliberately never applied. It is
`src/KiriChess.Server/Data/Migrations/20260823050623_AddPatronTiers.cs`.

### What it does

Additive only, no data loss, and it has a complete `Down`:

- Adds five columns to `AspNetUsers`: `PatronTier` and `PatronKind` (both `varchar(16)`, `NOT NULL`,
  default `'None'`), `PatronExpiresAt`, `StripeSubscriptionId` and `StripeCustomerId` (all nullable).
- Adds table `DonationReferences` (`Id`, `UserId`, `Reference`, `CreatedAt`, `ExpiresAt`,
  `ConsumedAt`) with a cascade FK to `AspNetUsers`.
- Adds a unique index on `DonationReferences.Reference` and a plain index on
  `AspNetUsers.StripeSubscriptionId` (the whole revoke path is a lookup on that column).
  `UniqueStripeSubscriptionId` later replaces that index with a unique partial index
  (`WHERE "StripeSubscriptionId" IS NOT NULL`). This migration still creates the plain one.

### Running it

**Do not apply this by hand against production.** `AddPatronTiers` is additive, but production
migrations go through Fly's `release_command` (`dotnet KiriChess.Server.dll --migrate`) on a deploy
from `main`. The patron code is on `dev` and not on `origin/main` (verified 2026-09-04:
`git merge-base --is-ancestor 129a359 origin/main` is false). Merging it onto `main` and dispatching
Deploy is what applies it. A `dotnet ef database update` from a laptop against Neon is the 3am
incident path [MIGRATIONS.md](MIGRATIONS.md) exists to avoid.

Review the SQL first if you want to see exactly what the release machine will run:

```bash
# See the exact SQL without executing it.
dotnet ef migrations script --idempotent --project src/KiriChess.Server
```

### Existing accounts

There is no back-fill and none is needed: the old webhook could never fire (no payment link ever sent
a `client_reference_id`), so no row has `IsPatron = true`. If any row somehow does, it will read as
tier `None` and show no badge, which is consistent rather than broken.

---

## 2. Configure the Stripe webhook

**Done 2026-09-04 (dashboard).** Live destination `KiriChess production` points at
`https://api.kirichess.com/api/webhooks/stripe` and listens to the four events below. Do **not**
`fly secrets set Stripe__WebhookSecret` until step 1 has deployed the new handler; today's `main`
image 400s on an API-version mismatch and Stripe will disable the endpoint.

The endpoint is `POST /api/webhooks/stripe`. It is anonymous by design and verifies Stripe's
signature instead. The rest of this section is the record of what was configured.

### 2a. Subscribe to all three events

In the Stripe dashboard: **Developers -> Webhooks -> your endpoint -> Update details -> Select
events.**

| Event | What it does here | Consequence if missing |
| --- | --- | --- |
| `checkout.session.completed` | grants the badge | no badge is ever granted |
| `customer.subscription.deleted` | revokes when a subscription ends | **badges are granted and never removed** |
| `customer.subscription.updated` | revokes on `canceled` / `unpaid` / `incomplete_expired` | a subscription that dies without a delete event keeps its badge |

Your endpoint most likely subscribes to `checkout.session.completed` only, because that is the single
event the old code handled. **Adding the other two is the entire point of this work.** A grant path
without a revoke path is worse than no badge, because it silently accumulates permanent badges for
people who cancelled.

Note `past_due` is deliberately NOT revoked: Stripe is still retrying the card, and it sends the
delete event once retries are exhausted. Revoking on `past_due` would strip badges off people whose
payment is about to succeed.

### 2b. Set the signing secret

Copy the endpoint's signing secret (`whsec_...`) and set it as `Stripe__WebhookSecret` (double
underscore is the .NET config separator for the `Stripe:WebhookSecret` key).

```bash
fly secrets set --app kirichess-api Stripe__WebhookSecret="whsec_..."
```

If it is unset the endpoint logs an error and returns 500 to every delivery, which Stripe will retry
and eventually disable the endpoint for. There is no dev fallback, on purpose. Confirmed unset on
production 2026-09-04 (`fly secrets list --app kirichess-api` has no `Stripe__*` row).

**API version.** Stripe.net 52.3.0 pins `2026-07-29.dahlia`. A dashboard endpoint carries the
*account's* default API version, which is almost never that string. The handler now passes
`throwOnApiVersionMismatch: false` and logs a warning of its own (`Stripe webhook API version …
differs from Stripe.net …`) instead of sharing the signature-failure line. You do **not** need to
pin the endpoint to `2026-07-29.dahlia`. If you still see a 400 whose log is `Stripe Webhook error.`
with no API-version warning, that is a bad `whsec_`, not a version skew ($1-1).

### 2c. Optional: how long a one-off badge lasts

Defaults to 1 month, which is what the donate page states. Nothing needs to be set unless you want
to change it.

```bash
fly secrets set --app kirichess-api Patron__OneTimeGrantMonths="1"
```

The value is clamped to 1..120. A longer flat life was the earlier default: `docs/audits/L.md`
(L-7-1) wants the badge to read as a thank-you rather than a purchase, and a 50 EUR one-off donor
should not end worse off than a 5 EUR monthly one. Raise the secret if that is the life you want;
the donate page has to say the same number.

### 2d. Test it before trusting it

Use Stripe's CLI against a local server rather than a real card:

```bash
stripe listen --forward-to localhost:5053/api/webhooks/stripe
# In another shell, using the whsec_ that `stripe listen` prints:
stripe trigger checkout.session.completed
stripe trigger checkout.session.async_payment_succeeded
stripe trigger customer.subscription.deleted
```

A triggered event carries no real `client_reference_id`, so the grant will log
"Checkout completed with no donation reference; nothing to grant." and return 200. That is the
correct behaviour and proves signature verification and routing work. To test an actual grant, do a
real test-mode checkout while signed in (see step 6).

---

## 3. Write the TERMS donations section

**Done 2026-08-23/24.** `docs/legal/TERMS.md` §12 ("Donations and patron badges"), mirrored on
`ui/app/[locale]/terms/page.tsx`, version `2026-08-24` in all five `LegalDocumentDriftTests` places.
§7 no longer says "non-commercial"; it points at §12. Recurring donations were turned off in the
same version bump, which is why this go-live is the badge half only (FS.4a, not FS.4).

If anyone edits TERMS again: this is a five-place edit, not one file. `LegalDocumentDriftTests`
fails the build unless all of these move together - `TermsVersion.Current`, the `Last updated` /
`Version` line in `docs/legal/TERMS.md` and `docs/legal/PRIVACY.md`, and the two hand-maintained
pages at `ui/app/[locale]/terms/page.tsx` and `ui/app/[locale]/privacy/page.tsx`. Append numbered
sections (`## 12. …`); a "§7a" heading matches neither of the test's regexes.

---

## 5. Deactivate the monthly payment links

**Done 2026-09-04.** Owner deactivated the five monthly Stripe payment links in the dashboard.
That is the real control: `MONTHLY_ENABLED = false` only hides the toggle. TERMS §12 is one-off
only, so those five links must stay inactive. Reactivating them is an L-7-1 / FS.4 decision, not a
UI flip.

**Client bundle ($2-3).** The five monthly URLs were also removed from
`ui/components/DonationTiers.tsx`, so they no longer ship in the public JS bundle. A Vitest case
fails if any of those five strings reappear, and another fails if `MONTHLY_ENABLED` is false while
TERMS §12 no longer says donations are one-off only. Putting the URLs back is the same FS.4
decision as flipping the flag.

---

## 6. Verify end to end

Once steps 1 and 2 are done, prove the whole chain with a real test-mode donation:

1. Sign in to the site. **Signing in is what connects a donation to an account**; a signed-out donor
   gets no badge by design, and the donate page says so.
2. Open `/donate` and click a tier. The outgoing URL must carry `?client_reference_id=<32 hex chars>`.
   If it does not, the browser never got a reference and the webhook has nothing to match. Check that
   `POST /api/donations/reference` returned 200.
3. Complete a test-mode checkout.
4. Confirm the delivery succeeded in the Stripe dashboard, and check the server log for
   `Granted patron badge to user … tier … kind …`.
5. Load `/profile`. The badge should render next to your display name as a bounty heart
   (outline for a one-off, filled for monthly) and a "lapses on" date for a one-off.
6. For a monthly tier, cancel the subscription in the Stripe dashboard, then reload `/profile`. The
   badge should be gone. **This is the half that has never existed before, so it is the half worth
   actually testing.**

### Where the badge appears

Own profile, leaderboards, the `/watch` live-games list, `/watch/[id]`, `/replay/[id]`, and the
public profile `/u/[id]`.

`/game/[id]` deliberately shows no badge: `GameStateResponse` carries only player Guids, with no
display names to sit beside.

### What is deliberately not built

- **A live patron wall.** `/donate` still shows 50 dummy names from `ui/lib/patronDemos.ts`.
  Publishing who paid you money needs an opt-in consent flag that does not exist; shipping it without
  one would be a privacy problem.
- **A "manage my subscription" link.** That needs a Stripe billing-portal session, which needs a
  Stripe API key. This design deliberately uses only the webhook secret.
- **Refunds, chargebacks, disputes and proration.** A refunded donation keeps its badge until it
  lapses.
- **Anonymous donations getting a badge.** There is no account to attach one to.

---

## Rollback

The feature fails closed. If you need to stop it without redeploying, **remove the four events from
the Stripe webhook subscription**: no event, no grant, no revoke, and nothing else in the app depends
on them. Existing badges stay as they are.

To roll the schema back, `Down` on `AddPatronTiers` drops the five columns and the table cleanly.
