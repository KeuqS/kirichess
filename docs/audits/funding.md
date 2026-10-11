# $ — Funding (patronage, not a product)

Merged packet reports for the funding category (`$1`–`$5`). File is `funding.md` because `$` is awkward in some shells. Index: [INDEX.md](INDEX.md). `$4` is sealed and has no report.

---

## $1 — Donations path
Date: 2026-08-24
Packet: docs/audits/INDEX.md

### Verdict

The donation path is better reasoned than most of this repo. The reference handshake exists so Stripe
never receives an account id, the never-downgrade rule and the `past_due` carve-out are both deliberate
and documented, and `PatronService` is tested at nine cases including the two that matter (single use,
expired reference). The risk is not in that middle layer. It sits at the two ends the C# does not own:
the Stripe dashboard, where one ordinary default can make every delivery fail with a 400 the runbook
cannot diagnose, and the `/donate` page itself, where fifty invented names are presented as this
project's patrons directly beneath the ask for money. One is a launch-day trap, the other is the only
thing on this path a stranger could fairly call dishonest. Everything else here is small.

### Findings

#### $1-1 — Stripe.net throws on an API-version mismatch by default, so a normally configured endpoint 400s every delivery
- Severity: **High** (trigger: the Stripe account's default API version is not `2026-07-29.dahlia`, which is the ordinary case)
- Where: `src/KiriChess.Server/Account/StripeWebhookEndpoints.cs:42`
- Impact: the call uses the three-argument `EventUtility.ConstructEvent(json, signatureHeader, webhookSecret)`
  overload, whose `throwOnApiVersionMismatch` parameter defaults to `true`. Stripe.net 52.3.0
  (`src/KiriChess.Server/KiriChess.Server.csproj:29`) expects API version `2026-07-29.dahlia`; the string
  is in the assembly's own mismatch message, together with Stripe's recommended escape. A webhook endpoint
  created in the dashboard carries the account's default API version, so unless those two match exactly,
  every delivery raises `StripeException`, is caught at `:71`, logged as the generic `"Stripe Webhook error."`,
  and answered `400`. Stripe retries, then disables the endpoint. The symptom is "no badge is ever granted"
  beside a log line that reads like a wrong `whsec_`, which sends the owner back to step 2b instead of
  forward. `docs/ops/PATRON-BADGE-GOLIVE.md` never mentions API versions anywhere, so the runbook cannot
  get anyone out of it.
- Fix: pass `throwOnApiVersionMismatch: false`, or pin the endpoint's API version to `2026-07-29.dahlia`
  in the dashboard and say so in step 2b. Either way, log a version mismatch distinctly from a signature
  failure so the two do not share one line.

#### $1-2 — Fifty invented names are presented as this site's patrons on the page that asks for money
- Severity: **High**
- Where: `ui/lib/patronDemos.ts:9-60` (fifty names), rendered by `ui/app/donate/page.tsx:67-72` through
  `ui/components/PatronWall.tsx:24-44`, under `donate.patronsHeading` ("Patrons") and `donate.patronsIntro`
  ("A token of thanks to everyone who keeps the clocks running") in `ui/messages/en.json`. `/donate` is
  linked from the nav on every page (`ui/components/NavBar.tsx:124,184`) and the footer
  (`ui/components/Footer.tsx:128`).
- Impact: nothing in the rendered output marks these as placeholders. The disclosure lives in a source
  comment (`ui/lib/patronDemos.ts:1-4`) and a component doc comment, neither of which a visitor reads.
  What a visitor sees is a wall of fifty supporters, each with an avatar, immediately below five donate
  buttons. That is fabricated social proof attached to a solicitation. For a project whose whole
  positioning is that it says what is actually true, this costs more than any unbuilt feature does.
  `docs/ops/PATRON-BADGE-GOLIVE.md:229-232` does name the dummy wall, but frames it as "a live patron wall
  is not built"; it does not treat what ships in the meantime as an assertion.
- Fix: remove `<PatronWall>` from `/donate` until a consented, Stripe-backed list exists. The component and
  the demo file can stay for that future opt-in wall. If the section is kept, the placeholder status has to
  be in the visible copy.

#### $1-3 — A checkout that completes unpaid grants a badge, and the payment that later succeeds grants nothing
- Severity: **Medium** (trigger: any delayed-notification payment method enabled on the payment links; SEPA Direct Debit is the likely one for EUR)
- Where: `src/KiriChess.Server/Account/StripeWebhookEndpoints.cs:44-53`; consumption at
  `src/KiriChess.Server/Account/PatronService.cs:34-41`
- Impact: `checkout.session.completed` is treated as proof of payment, and `session.PaymentStatus` is never
  read. For delayed-notification methods Stripe fires that event with `payment_status: unpaid` and settles
  later through `checkout.session.async_payment_succeeded` or `async_payment_failed`, neither of which is
  handled. The gap cuts both ways: a payment that ultimately fails has already been granted a twelve-month
  badge, and because the reference is consumed at `PatronService.cs:41` on the unpaid event, the donor whose
  payment does succeed gets nothing, with no retry (the reference is single use and `/donate` offers no
  support path).
- Fix: grant only when `session.PaymentStatus` is `paid` or `no_payment_required`, and route
  `checkout.session.async_payment_succeeded` through the same `GrantAsync`. Add both to the event table in
  step 2a. If card is the only method enabled today the check costs nothing and stops this from becoming a
  live bug the day a second method is switched on.

#### $1-4 — Minting the donation reference shares a five-per-hour budget with export and erasure, and failing is silent
- Severity: **Medium**
- Where: `src/KiriChess.Server/Account/DonationEndpoints.cs:50` and `:73` both take
  `RateLimitPolicies.AccountData`, five permits per hour by default
  (`src/KiriChess.Server/Infrastructure/RateLimiting.cs:129`), shared with six account routes
  (`src/KiriChess.Server/Account/AccountEndpoints.cs:39,73,102,118,200,217`). The mint fires on every mount
  of `/donate` (`ui/components/DonationTiers.tsx:142-155`) and `/api/donations/me` on every mount of the
  profile badge (`ui/components/PatronBadge.tsx:33`).
- Impact: in one direction, a signed-in visitor who looks at their profile and `/donate` a few times in an
  hour exhausts the budget and is then refused their own data export or account deletion, which is the exact
  thing `AccountData`'s doc comment says the tightness exists to protect. In the other, a donor who trips the
  limit gets a null reference, `appendReference` silently returns the plain payment link
  (`ui/lib/donationLink.ts:19`), and they pay real money for a badge that is never granted and never
  explained. The silent fallback is the right behaviour for a network failure and the wrong one for a limit
  we impose on ourselves.
- Fix: give the two donation routes their own policy, declared in the table in `.claude/rules/security.md`.
  At minimum, stop re-minting when a reference is already in state, and show something visible when the
  reference could not be obtained, so the donor can sign in again rather than pay blind.

#### $1-5 — Consuming a reference is not concurrency-safe, so "single use" is claimed rather than enforced
- Severity: **Low** (trigger: Stripe redelivering an event while the first is in flight, or two instances after 8.4)
- Where: `src/KiriChess.Server/Account/PatronService.cs:34-41`, saved at `:84`
- Impact: the read `row.ConsumedAt is not null` and the write `row.ConsumedAt = now` are separated by two
  awaits and one `SaveChangesAsync`, with no unique-on-unconsumed constraint and no concurrency token on
  `DonationReference` (`src/KiriChess.Server/Data/KiriChessDbContext.cs:196-202`). Two concurrent deliveries
  of the same event can both pass the check and both grant, stacking a one-off expiry to twenty-four months
  through the branch at `:76-78`. The outcome is only a longer badge, which is why this is Low, but the
  single-use property `DonationReference`'s own doc comment states is not held by the schema.
- Fix: make the consume the conditional write, an `ExecuteUpdateAsync` filtered on `ConsumedAt == null`
  treating zero rows affected as already-consumed, or add an `xmin` concurrency token to the entity.

#### $1-6 — Donation references are never swept
- Severity: **Low**
- Where: `src/KiriChess.Server/Data/Entities/DonationReference.cs`, absent from
  `src/KiriChess.Server/Retention/RetentionSweep.cs:60-70` where `RefreshTokens`, `ExternalLoginCodes`,
  `TwoFactorChallenges`, `Challenges` and `ApiTokens` are all cut at the cutoff. `docs/decisions/RETENTION.md`
  contains no occurrence of the table.
- Impact: every expired and every consumed row stays forever, one per signed-in `/donate` visit per
  twenty-four hours, each carrying a `UserId`. The cascade at `KiriChessDbContext.cs:200-201` does not save
  it, because erasure anonymises to a tombstone rather than deleting the user row
  (`src/KiriChess.Server/Account/AccountDeletionService.cs`). Small in volume, but precisely the shape
  RETENTION.md §1 says a short-lived token must not have.
- Fix: one more `ExecuteDeleteAsync` in the same sweep on `ExpiresAt < cutoff || ConsumedAt < cutoff`, and a
  line in RETENTION.md.

#### $1-7 — The precondition L-1-5, L-2-4 and L-3-2 all wrote has now fired
- Severity: **Medium**
- Where: the condition those three set, "before any payment link is wired to pass `client_reference_id`"
  (`docs/audits/L.md:224`, `docs/audits/L.md:226`), is now met: `ui/lib/donationLink.ts:22` sets the
  parameter and `ui/components/DonationTiers.tsx:92,157` apply it to all eleven links.
  `docs/legal/PRIVACY.md:76` has since gained a Stripe row that describes the handshake accurately.
  `docs/legal/ROPA.md` and `docs/decisions/RETENTION.md` contain no occurrence of "Stripe", "donation" or
  "patron" at all.
- Impact: this is a state change on open findings, not a new one. Two documents are now the only ones behind
  the code, and the deadline those audits set has passed rather than approached. Worth recording in the same
  breath: the export and erasure half of **L-2-4** is closed
  (`src/KiriChess.Server/Account/AccountExportBuilder.cs:155-165`,
  `src/KiriChess.Server/Account/AccountDeletionService.cs:98-103`), so it should not be re-fixed.
- Fix: a §3.x donations activity and a Stripe row in ROPA, and a RETENTION line covering
  `DonationReferences` and the five patron columns. Stripe's Art. 28 posture remains the owner's call per
  L-3-2.

#### $1-8 — `/donate` promises an automatic badge without naming the one condition it depends on
- Severity: **Low**
- Where: `ui/messages/en.json`, `donate.patronBadgeNote` ("granted automatically once a donation goes
  through") and `donate.whereMoneyGoesA`
- Impact: the badge is granted automatically only for a donor who is signed in *and* whose reference minted
  before they clicked. `donate.signInHint` covers the signed-out case but renders above the tier grid
  (`ui/components/DonationTiers.tsx:194-204`), while `patronBadgeNote` sits in the Q&A below and states the
  promise unconditionally. A donor who pays signed out has no path to a badge and no way to ask for one from
  this page. Separately, `whereMoneyGoesA` reads "infrastructure forbetter and faster performance", a missing
  space on the page that asks for money.
- Fix: qualify `patronBadgeNote` with "if you are signed in when you donate", give the page a contact route
  for a donation that missed its badge, and fix the typo.

### Already covered

- **Refunds, chargebacks, disputes and proration.** Named as deliberately not built at
  `docs/ops/PATRON-BADGE-GOLIVE.md:236-237`; a refunded one-off keeps its badge for twelve months. Not
  re-reported. Worth revisiting only if a refund actually happens.
- **No "manage my subscription" link** and **anonymous donations get no badge**: same list, same file, both
  deliberate.
- **Stripe absent from the legal record**: **L-1-5**, **L-3-2**. Only the trigger state is new, see $1-7.
- **Patron fields in export and erasure**: **L-2-4**, now closed in code.
- **`CancellationToken` on the webhook handler**: **C-1-11** flagged `StripeWebhookEndpoints.cs:53,60`; the
  token is threaded now.
- **Donations as remuneration, VAT, the PRIVACY §1 address question**: **L-7**, **L-12**. Out of scope here.
- **Whether the badge leaks into pairing, clocks or rating**: packet **$2**, not this one.

Two things checked and not filed, because they are fine and a reader would otherwise ask:

- The webhook is reachable. The Cloudflare Access application matches the apex only, `api` having been
  removed from it on purpose (`docs/ROADMAP.md:1651-1653`), so DH.3 does not block Stripe deliveries.
- The webhook is covered by the global per-IP backstop at 600 per minute
  (`src/KiriChess.Server/Infrastructure/RateLimiting.cs:374-390`), which no plausible delivery volume
  approaches. Anonymous access is correct here; the signature is the auth.

### Action list

1. **Done 2026-09-04.** `throwOnApiVersionMismatch: false`, plus a step-2b paragraph in PATRON-BADGE-GOLIVE.md naming
   `2026-07-29.dahlia` and what the failure looks like ($1-1). Before FS.4a go-live, ROADMAP #9.
2. Remove `<PatronWall>` from `/donate` ($1-2). Before #12, where `/donate` becomes public. **Done in B28 / `788c7e3`.**
3. **Done 2026-09-04.** Gate the grant on `session.PaymentStatus` and handle `async_payment_succeeded` ($1-3).
4. Split the donation routes off `AccountData` and surface a failed mint ($1-4).
5. Conditional consume ($1-5) and a sweep line ($1-6). One batch.
6. ROPA §3.x donations activity, Stripe row, RETENTION line ($1-7). Under L-3's owner decision.
7. Copy fixes on `/donate` ($1-8).
8. **[Owner]** Decide Stripe's Art. 28 posture and accept its DPA. Still L-3-2's, unchanged by this packet.
9. **[Owner]** `.github/FUNDING.yml` does not exist. GitHub Sponsors takes no fee and is additive to Stripe;
   it costs one file once the repo is public, and nothing before then.

Test gap worth naming rather than filing as a finding: `tests/KiriChess.Server.Tests/PatronBadgeTests.cs`
covers `PatronService` well and covers `StripeWebhookEndpoints` and `DonationEndpoints` not at all. Every
finding above except $1-2 and $1-8 lives in the untested half. Feed that to **C-10**.

---

## $2 — Pay-to-win line
Date: 2026-08-24
Packet: docs/audits/INDEX.md

### Verdict

The line holds where it matters. An exhaustive sweep of every read of `IsPatron`, `PatronTier`,
`PatronKind`, `PatronExpiresAt`, `PatronSince`, `StripeSubscriptionId` and `StripeCustomerId` across
`src/` finds no patron field touched by matchmaking, by pairing, by any rating calculation, by the
engine, or by any clock path. `Games/MatchQueue.cs` and `Games/PairingNotificationService.cs` contain
the string "Patron" zero times. The leaderboard carries the tier as a projection and never orders or
filters on it. FS.6 is a real constraint here, not an aspiration.

What is left is perceptual and structural. The badge is drawn as a chess piece next to a rating, in
the exact vocabulary chess uses for strength, with a publicly documented price attached to each
piece. And the rule deciding who shows a badge has been copied into eight places, of which a test
pins two. Neither buys an advantage. Both are ways the line could look crossed, or quietly become
crossed, without anyone touching pairing code.

### Findings

#### $2-1 — The tier icons are chess pieces shown beside ratings, which reads as strength and carries a public price
- Severity: **Medium**
- Where: `ui/components/PatronIcon.tsx:34-65` (the five tiers are Pawn, Knight, Bishop, Rook, Queen);
  rendered next to the display name in the leaderboard table at `ui/app/leaderboards/page.tsx:284-288`,
  whose neighbouring columns are rating, rating deviation and games played; also at
  `ui/app/u/[id]/page.tsx:112`, beside the public profile's rating cards. The amount-to-tier mapping is
  public in `src/KiriChess.Server/Account/PatronTiers.cs:35-42` and visible on `/donate` itself.
- Impact: a newcomer scanning `/leaderboards` sees a queen beside one name and a pawn beside another,
  in a table that is otherwise entirely about strength. The natural reading is a ranking, not a
  donation. Because the thresholds are published, the glyph is also a legible price tag on a public
  page: a queen states that this account paid at least fifty euro. Nothing is bought here, but FS.6's
  wording is "never touch rating, pairing, clocks, or pools" plus implied strength, and this is the
  implied-strength half. The accessible label is already correct and unambiguous
  (`PatronIcon.tsx:100`, "Patron: Queen"), so a screen reader user gets the truth and a sighted user
  gets the ambiguity, which is the wrong way round.
- Fix: cheapest correct answer is one glyph for every tier, in one colour, with the tier itself shown
  only on `/donate` and the donor's own profile. If the five pieces stay, they should not sit inside a
  strength table: move the badge off the leaderboard row, or give it a visible non-piece treatment
  that a sighted user reads the same way the aria-label already reads.

#### $2-2 — The badge rule is inlined in eight places and the test pins two of them
- Severity: **Medium**. **Confirmed live 2026-08-24**: fixing **$5-2** required a change to the badge
  rule, and all six inlined copies had to be found and edited by hand, each also needing `PatronKind`
  added to its projection. Nothing would have failed had one been missed. This finding is no longer
  hypothetical and its fix is now the highest-value item in this section.
- Where: canonical pair at `src/KiriChess.Server/Account/PatronBadge.cs:18-29`, pinned by
  `tests/KiriChess.Server.Tests/PatronBadgeTests.cs:39`. The same three-part rule is inlined at
  `src/KiriChess.Server/Games/GameNameResolver.cs:37`, `Games/ReplayEndpoints.cs:53`,
  `Games/PublicApiEndpoints.cs:223`, `:239`, `:533`, `Ratings/RatingEndpoints.cs:69` and `:87`.
- Impact: `PatronBadge.cs`'s own doc comment says "If you change one, change the other, and the test
  that pins them together will say so if you did not." That guarantee covers two of eight copies. Each
  inlining carries a comment explaining itself (anonymous projections, EF translation risk in a Join),
  and those reasons are sound, so the duplication is defensible; what did not grow with it is the
  guard. A future change to the rule, a grace period after expiry or a lapsed-monthly state, silently
  leaves six surfaces on the old rule. The symptom is a badge that shows on the leaderboard and not on
  the profile, which reads as a rendering glitch and gets reported by nobody.
- Fix: extend the existing agreement test rather than refactoring the inlining away. Put one active,
  one lapsed one-off, one monthly and one never-patron account through each of the endpoints above and
  assert the tier string equals `PatronBadge.For`. The inlining should stay; only the test needs to
  reach it. Feed this to **C-10** as well.

#### $2-3 — Monthly tiers are switched off in client code while the payment links stay live and shipped
- Severity: **Medium** (High if the five monthly payment links are still active in the Stripe dashboard)
- Where: `ui/components/DonationTiers.tsx:44` (`const MONTHLY_ENABLED = false`), whose doc comment at
  `:29-43` states the reason is Belgium's Book XII WER transparency duties and the address they would
  require. The five `stripeMonthly` URLs remain in `TIERS` at `:46-87` and ship in the JS bundle
  regardless of the flag. Server side, `src/KiriChess.Server/Account/PatronService.cs:67-72` still
  grants `PatronKind.Monthly` on any subscription-mode checkout, with no equivalent switch.
- Impact: a legal decision is currently enforced by a constant in client-side code, over URLs that
  anyone can read out of the shipped bundle today and out of the repository once it is public. The
  comment says the links "must also be reactivated in the Stripe dashboard", which implies they were
  deactivated; nothing in the repository can show whether that half was actually done, and it is the
  half that is the control. If those links are still live, the UI change stops nobody who kept a link,
  read the bundle, or follows an old one, and the server will grant a monthly badge. The same comment
  asserts that TERMS section 12 says one-off only while this flag is false, a coupling between a TSX
  constant and a legal document that nothing enforces; `LegalDocumentDriftTests` cannot see a constant.
- Fix: deactivate the five monthly payment links in the Stripe dashboard, which is the actual control,
  and add it to `docs/ops/PATRON-BADGE-GOLIVE.md` as a step with a verifiable outcome. Remove the
  `stripeMonthly` URLs from `TIERS` while the flag is false so the bundle stops advertising what the
  policy forbids. If the coupling to TERMS section 12 is meant to hold, it needs a test that fails
  when the flag and the document disagree.

#### $2-4 — The filled-versus-outline distinction now encodes a state nothing explains
- Severity: **Low**
- Where: `ui/components/PatronIcon.tsx:99` (filled for `Monthly`, outline otherwise) and `:71-74`,
  which notes the public DTOs carry a tier but no kind.
- Impact: two things stack. The public surfaces (leaderboard, live game list, spectate, replay) pass no
  `kind` at all, so every badge there is already outline whatever the donor did. And with $2-3's flag
  off, no new donor can reach `Monthly`, so a filled icon on the owner's own profile now means "was
  subscribed before the tiers came off", a distinction the site explains nowhere. Harmless, but worth
  recording so it is not later mistaken for a bug and "fixed" in the wrong direction.
- Fix: nothing urgent. If monthly stays off past launch, drop the filled variant rather than leaving a
  visual distinction that carries no meaning a reader can recover.

### Already covered

- **Donations path, webhook, references, rate limiting**: **$1**, same run. $2-3 overlaps its $1-1
  and $1-2 only in that all three are Stripe-dashboard state the repository cannot verify.
- **Whether recurring tiers are remuneration at all**: **L-7**, **L-12**. $2-3 reports the enforcement
  layer, not the legal question, which is already decided against monthly.
- **Patron fields in export and erasure**: **L-2-4**, closed in code.
- **Stripe absent from ROPA and RETENTION**: **L-3-2**, and $1-7 for the trigger state.

Checked and deliberately not filed, because they are correct:

- `Games/MatchQueue.cs` and `Games/PairingNotificationService.cs` read no patron field.
- `Ratings/RatingEndpoints.cs:80-81` orders by `r.Value` alone; the tier is projected, never sorted or
  filtered on. The cache-hit branch at `:66-73` ranks by the ZSET order for a reason unrelated to
  patronage.
- `/game/[id]` shows no badge, matching `docs/ops/PATRON-BADGE-GOLIVE.md:225-228`, because
  `GameStateResponse` carries player Guids with no names to sit beside. That is the right call and it
  is the surface where an implied-strength cue would matter most.

### Action list

1. **[Owner]** Confirm the five monthly payment links are deactivated in the Stripe dashboard ($2-3).
   **Done 2026-09-04.** This was the only item here that is load-bearing before launch.
2. Remove the `stripeMonthly` URLs from `TIERS` while `MONTHLY_ENABLED` is false, and add
   the dashboard step to PATRON-BADGE-GOLIVE.md ($2-3).
3. Extend the badge-agreement test to the six inlined copies ($2-2).
4. **[Owner]** Decide whether the tier glyph stays a chess piece on the leaderboard ($2-1). It is a
   product call, not a defect; the audit's position is that it should not sit inside a strength table.
5. If monthly stays off past launch, drop the filled icon variant ($2-4).

---

## $3 — Money admin
Date: 2026-08-24
Packet: docs/audits/INDEX.md

### Verdict

KYC state, the legal entity on the Stripe
account, the payout bank account, and how a Belgian student declares donation income are all facts
that live in a dashboard and a tax return, not in this repository, and inventing findings about them would not help. What can be prepared here is mostly a
checklist plus two things the code does say.

The one structural fact worth stating up front: **there is no Stripe API key anywhere in this
system.** The application holds `Stripe:WebhookSecret` and nothing else, deliberately
(`docs/ops/PATRON-BADGE-GOLIVE.md:237-238`). It cannot list a charge, issue a refund, open a billing
portal, or reconcile anything. For money admin that means the Stripe dashboard is not the primary
ledger, it is the *only* ledger, and every item below is a dashboard or accountant action with no
software half.

### Findings

#### $3-1 — The application stores no donation amount, so there is no second record of what was received
- Severity: **Low** (informational; it is the correct privacy posture and a real bookkeeping constraint at the same time)
- Where: `src/KiriChess.Server/Data/Entities/DonationReference.cs:21-38` stores a reference, three
  timestamps and a user id, and no amount. The amount reaches the server once, as
  `session.AmountTotal` at `src/KiriChess.Server/Account/StripeWebhookEndpoints.cs:50`, is collapsed to
  a tier by `PatronTiers.FromAmountCents` (`Account/PatronTiers.cs:44-52`) and then discarded.
  `StripeCustomerId` is stored but read by nothing (`Data/Entities/ApplicationUser.cs:96-101` says so).
- Impact: this is right for privacy, and PRIVACY.md:76's claim depends on it. It also means the answer
  to "how much came in, from whom, when" exists in exactly one place, under one provider account, and
  the tier column is a lossy derivation that cannot reconstruct it (every amount at or above fifty euro
  is the same `Queen`). If that account is ever lost or locked, the books are gone with it. Do not fix
  this by storing amounts; fix it by exporting.
- Fix: schedule a periodic Stripe payments export (CSV) kept outside the Stripe account, and add the
  date to **B-4**'s quarterly calendar. That is the whole mitigation.

#### $3-2 — The page advertises a one euro minimum, which is close to the floor where fees dominate
- Severity: **Low**
- Where: `ui/messages/en.json`, `DonationTiers.everyBitHelps` ("Every bit helps and is greatly
  appreciated - €1 minimum"), against `src/KiriChess.Server/Account/PatronTiers.cs:41`, which grants a
  `Pawn` badge for any amount from one cent upward.
- Impact: not a defect and not a reason to raise the minimum. Worth knowing before the page goes public
  at ROADMAP #12: Stripe's per-transaction pricing has a fixed component, so at one euro a large
  fraction of the donation is fee, and the twelve-month badge ($5-1) is granted all the same. Check the
  current Belgian/EEA rate card rather than trusting any figure quoted here.
- Fix: none required. If the fee share is uncomfortable, the honest change is copy ("small donations
  are welcome, though card fees take a bigger share of them"), not a hidden minimum.

### Checklist for the owner

Nothing below can be verified from the repository. Ordered by what would hurt most if wrong.

1. **Entity and KYC.** Confirm the Stripe account is verified and that the legal entity on it is the
   individual named in `docs/legal/PRIVACY.md` §1. A mismatch between the payee on record and the
   controller named in PRIVACY is the kind of discrepancy that is cheap to fix now and awkward later.
   Feeds **L-3** and **L-7**.
2. **Payout destination and schedule.** A bank account attached, a payout schedule set, and at least
   one payout actually landed before launch. An unverified account can accept payments and hold them.
3. **Tax treatment.** Whether donations to an individual are taxable income in this situation is an
   accountant question, full stop. Do not answer it from the repository. The adjacent legal questions
   (remuneration, VAT, the address duty) are already held by **L-7** and **L-12**; this is the separate
   personal-tax half.
4. **Refunds and disputes are dashboard-only.** There is no code path
   (`docs/ops/PATRON-BADGE-GOLIVE.md:236-237`), and a refunded donation keeps its badge until it lapses
   ($1 already records this). Know where the button is before the first one arrives.
5. **Deactivate the five monthly payment links.** **Done 2026-09-04.** This lands here as well as in
   **$2-3**, because it is a dashboard action. The client-side `MONTHLY_ENABLED` flag is not the
   control.
6. **Records retention.** Decide how long the Stripe export from $3-1 is kept and where. It contains
   donor names and emails that this application deliberately never receives, so it is a more sensitive
   file than anything in the database. It does not belong in the repository.
7. **GitHub Sponsors** is still open and additive (`$1` action list item 9). No fee, one file, but only
   once the repository is public.

### Already covered

- **What Stripe stores versus what the policy says**: `docs/legal/PRIVACY.md:76`, verified accurate in
  **$1**. The processor and ROPA gap is **L-3-2**, with the trigger state in **$1-7**.
- **Donations as remuneration, VAT, the geographic address duty**: **L-7**, **L-12**. Not reopened here.
- **Whether money buys an advantage**: **$2**. It does not.
- **Recurring tiers**: **$4**, sealed. Not assigned, see below.

### Action list

Every item is **[Owner]**, which is the finding. The two items that are only preparation are:

1. Add a periodic Stripe export to **B-4**'s quarterly calendar ($3-1).
2. Optional copy adjustment on the one euro minimum ($3-2), only if the owner wants it.
3. **[Owner]** Items 1 to 7 above, in that order. Items 1 and 2 are still launch-gating; item 5
   (monthly links) is done 2026-09-04. The rest are not launch-gating.

---

## $5 — Patron supporter icon on kirichess.com
Date: 2026-08-24
Packet: docs/audits/INDEX.md

### Verdict

The icon itself works, and the part most likely to be wrong is the part that is right: a one-off badge
lapses correctly on every surface with no sweep, because `PatronBadge.For` is evaluated per request
against the current instant rather than read off a stale column, and a test pins exactly that. Three
things were off. The packet's own premise did not match the code, and the gap was twelve months wide;
that one is settled in favour of the code, and the INDEX row is corrected. Two remain: a one-off
donation made on top of a live subscription is silently worth nothing the moment that subscription
ends, and the runbook's list of where the icon appears is already stale.

### Findings

#### $5-1 — A one-off badge lasts twelve months, not one; the packet and the code disagree
- Severity: **Medium**. **Resolved 2026-08-24**, in favour of the code: the `$5` row in
  `docs/audits/INDEX.md` now states twelve months and names the setting. No code or configuration
  changed, because none needed to. See the Fix line below for what to do if the intent was the other
  way round.
- Where: `src/KiriChess.Server/Account/PatronOptions.cs:19` defaults `Patron:OneTimeGrantMonths` to 12,
  clamped to 1..120. The key is set nowhere in the repository: no `appsettings*.json`, no `fly.*.toml`,
  no workflow. `docs/audits/INDEX.md` (section 8, `$5`) states "One time donations dissapear after 1
  month."
- Impact: unless a Fly secret was set by hand, production grants twelve months. The two readings lead
  to opposite actions, which is why this is worth a finding rather than a note: either the code is
  wrong and needs `Patron__OneTimeGrantMonths=1`, or the packet is wrong and the expectation needs
  correcting before someone "fixes" the code to match it. Every design document argues for twelve on
  purpose (`PatronOptions.cs:8-12`): a flat, generous life reads as a
  thank-you rather than a purchase, which is the position **L-7-1** wants held, and it stops a fifty
  euro one-off donor being worse off than a five euro monthly one. Shortening it to one month moves in
  the wrong direction legally, because a small benefit tightly coupled to a payment looks more like a
  purchase, not less.
- Fix: **done**, by correcting the packet row rather than the code, on the reasoning above: twelve is
  the deliberate value, every design document argues for it, and shortening it weakens L-7-1. If the
  intent really was one month, this is not a repository change: set `Patron__OneTimeGrantMonths="1"` as a
  Fly secret on `kirichess-api` (`docs/ops/PATRON-BADGE-GOLIVE.md:100`), update
  the `PatronOptions.cs:8-12` doc comment to match, and re-read
  L-7-1 first, because this is the variable that argument turns on.

#### $5-2 — A one-off donation made while a subscription is active is silently forfeited when that subscription ends
- Severity: **Medium** (dormant for new donors while monthly is off, live for anyone already subscribed).
  **Fixed 2026-08-24**, see the Fix line.
- Where: `src/KiriChess.Server/Account/PatronService.cs:73-82` (the one-off branch is skipped entirely
  when `PatronKind` is already `Monthly`, leaving `PatronExpiresAt` null), then
  `PatronService.cs:104` calling `Clear` at `:115-123` on subscription end.
- Impact: a monthly patron who also drops a one-off gets their reference consumed and their tier
  possibly upgraded, but no expiry is ever recorded for that payment. The reasoning in the comment at
  `:80-82` is correct while the subscription lives: an active subscription outranks a one-off and must
  not be given an end date. It stops being correct at cancellation, where `Clear` wipes every patron
  column, and the one-off's remaining life goes with it. The donor paid twice and keeps nothing, with
  no record that the second payment happened, because `DonationReference` stores no amount. `Clear`
  also nulls `PatronSince`, so a returning donor loses the date they first supported the project, which
  is the one fact a supporter icon would most want to keep.
- Fix: **done**, no schema change. Three parts:
  1. `PatronService.GrantAsync` now banks a one-off's expiry in `PatronExpiresAt` unconditionally,
     including during a live subscription, and a subscription grant no longer nulls the column.
  2. `PatronBadge.For` and `TierExpression` now treat a `Monthly` badge as unexpiring regardless of
     that column, since the subscription and not the empty column is what keeps the badge alive.
     Without this the banked date would hide the badge of somebody still paying. **This is a change to
     the badge rule, so all six inlined copies named in $2-2 moved with it**: `GameNameResolver.cs`,
     `ReplayEndpoints.cs`, `PublicApiEndpoints.cs` (twice for leaderboards, once for replay names) and
     `RatingEndpoints.cs` (twice), each of which also needed `PatronKind` added to its projection.
  3. `RevokeBySubscriptionAsync` falls back to the remaining one-off life as a `OneTime` badge, and
     only calls `Clear` when there is none left.
- Deliberately **not** done: keeping `PatronSince` across `Clear`, which the original Fix line
  suggested. `Clear` is shared with `AccountDeletionService` (`:98-103`), so making it preserve
  `PatronSince` would leave "this person donated, on this date" on an erasure tombstone and reopen
  **L-2-4**. The privacy cost outweighs the badge-history value, and the downgrade path in part 3
  preserves `PatronSince` naturally anyway, because it no longer calls `Clear`.
- Tests: `PatronBadgeTests.cs` gained a monthly-with-lapsed-banked-expiry case in the
  `For`/`TierExpression` agreement test (the regression part 2 exists to prevent), plus
  `Cancelling_a_subscription_falls_back_to_the_one_off_life_already_paid_for` and
  `Cancelling_a_subscription_whose_banked_one_off_has_already_lapsed_clears_outright`. The old
  `One_off_on_top_of_an_active_monthly_badge_adds_no_expiry_and_does_not_downgrade` asserted the bug
  and is rewritten as `..._banks_expiry_without_downgrading`.

#### $5-3 — The runbook's list of surfaces is already stale
- Severity: **Low**
- Where: `docs/ops/PATRON-BADGE-GOLIVE.md:225-228` lists own profile, leaderboards, `/watch`,
  `/watch/[id]` and `/replay/[id]`. `/u/[id]`, the public profile added in `b0f0938`, also renders it
  at `ui/app/u/[id]/page.tsx:112`.
- Impact: the list is what step 5's manual verification pass is walked against, so a surface missing
  from it is a surface nobody checks. `/u/[id]` is arguably the most natural home for a supporter icon
  and it is the one not on the list.
- Fix: add `/u/[id]` to the list. The `/game/[id]` exclusion below it is still correct and should stay
  as written.

### Already covered

- **The badge lapsing correctly with no sweep.** `PatronBadge.For`
  (`src/KiriChess.Server/Account/PatronBadge.cs:18-21`) is evaluated per request, `DonationEndpoints.cs:69`
  derives `IsPatron` from it rather than the raw column, and `PatronBadgeTests.cs:207` pins the
  behaviour across the expiry instant. This is the thing `$5` asks about and it works.
- **The donor is told when it expires.** `ui/components/PatronBadge.tsx:49-53` shows "expires on
  {date}" for a one-off and a monthly note otherwise, so the disappearance is disclosed in advance on
  the donor's own profile. There is no mail or notice at the moment it lapses, which is acceptable at
  this size and not filed as a finding.
- **Rendering contract.** `PatronIcon` returns null for "None", for an unknown string and for
  null/undefined (`ui/components/PatronIcon.tsx:96-97`), so every call site renders it unguarded and a
  lapsed badge simply vanishes rather than erroring. Correct.
- **Whether the icon implies strength, and the eight-way duplication of the badge rule**: packet **$2**.
- **Whether the grant fires at all** (webhook API version, unpaid checkouts): packet **$1**, findings
  $1-1 and $1-3. If the icon never appears in production, look there first, not here.

### Action list

1. ~~**[Owner]** Decide twelve months or one, then make the losing side match ($5-1).~~ **Done
   2026-08-24**: settled at twelve, INDEX.md row corrected. Reopen only by setting the Fly secret, see
   $5-1's Fix line.
2. ~~Preserve one-off expiry under an active subscription ($5-2).~~ **Done 2026-08-24**:
   grant banks it, the badge rule treats Monthly as unexpiring across all eight copies, revoke falls
   back to it. Full suite green, 876 passed / 0 skipped with Docker up. The `PatronSince` half was
   dropped on purpose, see $5-2.
3. Add `/u/[id]` to the surfaces list in PATRON-BADGE-GOLIVE.md ($5-3).
4. $5-2's fix had to edit six unguarded copies of the badge rule by hand. That is
   **$2-2** happening, and it stands: the copies are still pinned by nothing. Its test extension is
   now the highest-value item in the funding section.

