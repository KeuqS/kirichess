# L — Legal, IP, and regulatory

Merged packet reports for category **L**. Individual packets were `docs/audits/L-N.md`; they now live as sections below. Index: [INDEX.md](INDEX.md).

---

## L-1 — Legal document fidelity
Date: 2026-08-21
Packet: docs/audits/INDEX.md

### Verdict

The three documents are unusually good: TERMS, PRIVACY and ROPA were written against the code, not
against a template, and almost every specific mechanism they describe (the PBKDF2 hasher, the SHA-256
token storage, the Restrict foreign keys, the two-party-record argument, the dormant-account report,
the export's five exclusions, Sentry's `sendDefaultPii: false`) checks out line for line. The failures
are not sloppiness in the prose; they are **the documents lagging behind code that shipped after they
were written**. Three phases landed without the legal set moving with them: 9d.2 (two-factor), AD.6 /
11a.3 (anonymous counters) and 11b.1 (notification mail, web push), plus an unrostered Stripe donation
path. That lag has two shapes. The first is *internal*: `PRIVACY.md` still says in §5 and §7 that no
email has ever been delivered, while §4 of the same document says Resend is live. The second is
*published*: `ui/app/privacy/page.tsx` is a hand-maintained twin of `PRIVACY.md` and is now missing
three of twelve data groups, three of thirteen retention rows, the 2FA half of the erasure list and one
export exclusion. `LegalDocumentDriftTests` passes throughout, because it compares version dates and
section *numbers*, and nothing here changed a section number. The floor held; the wall did not.

None of this is currently reaching a stranger - Cloudflare Access still gates the site until 8.7 - which
is what keeps every finding below at High rather than Critical. All of them should close before that gate
comes down.

### Findings

#### L-1-1 — The published privacy page is four sections behind `PRIVACY.md`, and the drift test cannot see it
- Severity: **High** (Critical the day 8.7 removes the Access gate)
- Where: `ui/app/privacy/page.tsx:124-190` (§2), `:309-455` (§5), `:481-524` (§6), `:529-566` (§7)
  vs `docs/legal/PRIVACY.md` §2/§5/§6/§7; guard at `tests/KiriChess.Server.Tests/LegalDocumentDriftTests.cs:76-93`
- Impact: The document a player actually reads omits, in each case, material the Markdown discloses:
  - **§2** lists 9 data groups; the Markdown lists 12. Missing: sign-in exchange codes
    (`ExternalLoginCodes`), **two-factor authentication** (TOTP shared secret, recovery codes, challenge
    tickets), and **anonymous counters** (`DailyCounters`) together with the whole "these counters are
    events, not people" paragraph. A visitor reading `/privacy` is told nothing about the analytics table
    or about the credential material 2FA stores.
  - **§5** lists 10 retention rows; the Markdown lists 13. Missing: 2FA login challenge tickets, the 2FA
    shared secret and recovery codes, and `DailyCounters`.
  - **§6** omits 2FA challenge tickets from the cascade-delete list and omits "including your TOTP shared
    secret and recovery codes" from the Identity line - so the page never tells a 2FA user that their
    authenticator secret is destroyed on erasure.
  - **§7** omits the sixth exclusion bullet (the TOTP secret and recovery codes are never exported).

  `LegalDocumentDriftTests` compares the `Last updated` / `Version` stamp (identical, `2026-08-19`,
  matching `TermsVersion.Current`) and the *sequence of numbered `<h2>` sections* (identical, 1–11).
  Both halves of the 8a.2 incident it was built for are green; this is a third shape it was never asked
  to catch - content added *inside* an existing section.
- Fix: Bring the four sections on the page up to the Markdown, then close the class of bug rather than
  the instance: either render `PRIVACY.md`/`TERMS.md` at build time (the test's own doc comment already
  names this as the right long-term fix), or extend `LegalDocumentDriftTests` with a per-section
  comparison of `<li>` / table-row counts, which would have caught all four of the above.

#### L-1-2 — Three documents still assert that no email has ever been sent; Resend has been live since 8a.2
- Severity: **High**
- Where: `docs/legal/PRIVACY.md` §5 ("Inactive accounts are not deleted automatically" paragraph) and §7
  ("not currently capable of delivering one reliably"); `ui/app/privacy/page.tsx:474`;
  `docs/decisions/RETENTION.md:105`; contradicted by `docs/legal/PRIVACY.md` §4 (Resend row: "**Live.**"),
  `docs/legal/ROPA.md` §3.5 ("**Active in production.**"), `docs/ops/PROVIDER-ACCOUNTS.md:19`
  (`Email__ApiKey` deployed), `docs/ops/DEPLOYMENT.md` §11.1
- Impact: A published privacy policy contradicts itself two sections apart. §5 uses "no email has ever been
  delivered by this system to anyone" as the *stated reason* dormant accounts are not erased, and §7 uses
  it as the reason the export is a synchronous download. Both reasons are now false, which means the two
  decisions they justify are currently unjustified in writing even though both remain defensible on other
  grounds. A supervisory authority reading §4 and §5 together sees a document that does not know its own
  state.
- Fix: Replace the "no email has ever been delivered" clause in all three places with the true reason each
  decision still stands on - for §5, that there are no real accounts to observe a reaper against and the
  erasure predicate has never run in production (`RETENTION.md` §4 already makes that argument); for §7,
  that a synchronous download is simply the right shape for a single-file export. `RETENTION.md`'s revisit
  trigger ("a verified mail path plus real signups") has had its first half fire and should say so.

#### L-1-3 — Two live mail categories are undisclosed; both policy and ROPA describe transactional mail as confirmation and reset only
- Severity: **High**
- Where: `src/KiriChess.Server/Infrastructure/Email/NotificationCategory.cs:11-17`,
  `src/KiriChess.Server/Games/PairingNotificationService.cs:76`,
  `src/KiriChess.Server/Games/RematchNotificationService.cs:83`; preference columns at
  `src/KiriChess.Server/Data/Entities/ApplicationUser.cs:69,72`; contradicted by
  `docs/legal/PRIVACY.md` §4 (Resend row: "account confirmation, password reset") and
  `docs/legal/ROPA.md` §3.5 ("**Purpose** | Account confirmation and password reset")
- Impact: The system sends two further categories of mail to a player's address - "your challenge was
  accepted" (`PairingFound`) and "your opponent wants a rematch" (`RematchOffered`). Neither is named in
  the policy, in ROPA §3.5's purpose, or in ROPA §3.1's data list. The two opt-out timestamps
  (`PairingFoundEmailUnsubscribedAt`, `RematchOfferedEmailUnsubscribedAt`) are personal data held on the
  account row, are returned in the export (`AccountExportBuilder.cs:160`), and appear in no §2 group.
  These are reach-back mails with unsubscribe links, a materially different thing from a confirmation
  mail, and the ePrivacy line between service mail and solicited notification is exactly where a reader
  needs the disclosure to be accurate. The code itself draws the distinction correctly -
  `NotificationCategory`'s doc comment excludes confirmation and reset from opt-out on purpose. Only the
  documents missed it.
- Fix: Add a "Notifications" group to `PRIVACY.md` §2 and the page (the two opt-out timestamps), extend the
  §4 Resend row and `ROPA.md` §3.5 to name all four mail categories, and state that notification mail
  carries a one-click unsubscribe while confirmation and reset deliberately do not. Hand the ePrivacy
  characterisation itself to **L-6**.

#### L-1-4 — Web push is an entire processing activity that appears in no legal document
- Severity: **High**
- Where: `src/KiriChess.Server/Data/Entities/WebPushSubscription.cs:19-27`,
  `src/KiriChess.Server/Data/KiriChessDbContext.cs:257-266`,
  `src/KiriChess.Server/Infrastructure/Push/PushEndpoints.cs:22-33`,
  `src/KiriChess.Server/Infrastructure/Push/PushNotificationService.cs:36-40`; absent from
  `docs/legal/PRIVACY.md` §2/§4/§5/§6/§7, `docs/legal/ROPA.md` §3, `docs/decisions/RETENTION.md` §1
- Impact: `WebPushSubscriptions` stores, per browser per user, a push-service endpoint URL, a P-256 public
  key and an auth secret. An endpoint URL is a stable, unique identifier for one browser install - it is
  personal data, and it is the one field in this database that points *outward* at a third party. The
  table is named in no data-collection list, has no retention row (`RETENTION.md` §1 presents itself as
  "the whole retention schedule" and does not contain it), is not in the export, and is not deleted on
  erasure - see **L-2-1**, which owns that defect. Sending also involves a push service (Google, Mozilla,
  Apple) that appears in no recipient table - see **L-3-3**. One mitigating fact: `VapidOptions` is not
  configured in any deployed environment (`PROVIDER-ACCOUNTS.md` lists no `Vapid__*` secret), so nothing
  is being *sent* today. `/api/push/subscribe` is not gated on `VapidOptions.Enabled`, so rows can still
  accumulate.
- Fix: Add a "Web push" row to `PRIVACY.md` §2 (endpoint, keys, one row per browser), a retention row to
  §5 and `RETENTION.md` §1, a cascade line to §6, an export entry to §7, and a §3.x activity to `ROPA.md`
  naming the browser push service as recipient.

#### L-1-5 — Donations are live and Stripe is in no document; `PRIVACY.md` §1's own address trigger has fired
- Severity: **Medium** (High once a donation is actually linked to an account)
- Where: `ui/components/DonationTiers.tsx:36,46-47,56-57,66-67,76-77,86-87` (eleven live
  `buy.stripe.com` links, one-off **and monthly**), `ui/app/donate/page.tsx`, linked from
  `ui/components/NavBar.tsx:111` and `ui/components/Footer.tsx:128`;
  `src/KiriChess.Server/Account/StripeWebhookEndpoints.cs:22,45-62` wired at
  `src/KiriChess.Server/Program.cs:731`; patron columns at
  `src/KiriChess.Server/Data/Entities/ApplicationUser.cs:56,61`
- Impact: `PRIVACY.md` §1 says, in terms: "If KiriChess ever takes money for anything, the separate
  transparency rules for commercial online services apply and this section gains a geographic address at
  that point." Live Stripe payment links, including recurring monthly ones, are that point arriving. Stripe
  appears in no §4 recipient table, no §2 data group, no §5 retention row, no §6 erasure list, and neither
  ROPA §3 nor §4 - see **L-3-2** for the processor delta. `IsPatron`/`PatronSince` are personal data on the
  account row that survive erasure untouched (**L-2-4**) and are not exported (**L-2-3**).
  One thing that is *not* wrong today: the links carry no `client_reference_id`, so the webhook's
  user-matching branch (`StripeWebhookEndpoints.cs:50`) never fires and Stripe currently receives no
  KiriChess account id. That also means `IsPatron` is never set by any live path.
- Fix: The smallest version: add Stripe to §4 and ROPA, add a "Donations" group to §2 covering
  `IsPatron`/`PatronSince`, and decide the §1 address question deliberately rather than by omission. Whether
  a recurring monthly tier crosses from patronage into remuneration - with the address and VAT duties that
  follow - is **L-7**'s call and a human one; this finding only records that the documents have not noticed
  the money path exists. The INDEX standing position reads recurring tiers as the grey-to-red edge and parks
  them at FS.4; the shipped `/donate` page already offers them.

#### L-1-6 — Turnstile is described as "the bot check at signup" but loads on every page
- Severity: **Medium**
- Where: `ui/app/layout.tsx:65-70` (root layout, `afterInteractive`) vs `docs/legal/PRIVACY.md` §4
  Cloudflare row and `ui/app/privacy/page.tsx:267`
- Impact: The Turnstile script tag sits in the root layout, so every page load - `/`, `/rules`, `/about`,
  a replay a stranger was linked to - makes a request to `challenges.cloudflare.com`, not only the signup
  form. The policy's phrasing tells a reader the bot check is scoped to registration. Whether that request
  is itself a consent-requiring terminal-equipment access is **L-6**'s question, not this packet's; the
  fidelity defect is that §4 describes narrower behaviour than the code has.
- Fix: Either scope the script to `/login` (it is only used there - `ui/app/login/page.tsx:86-91` renders
  the widget explicitly), which makes the existing sentence true, or amend §4 to say the bot-check script
  loads site-wide. Prefer the former: it is a smaller change and it reduces the third-party surface.

#### L-1-7 — TERMS §10 tells the reader the source is published, and links to a private repository
- Severity: **Medium** (trigger: 8.7 removing the Cloudflare Access gate, or any external reader today)
- Where: `docs/legal/TERMS.md` §10, `ui/app/terms/page.tsx:213-216`;
  repository visibility confirmed `PRIVATE` via `gh repo view`
- Impact: The Terms state "The KiriChess source is published at https://github.com/MichielGekiere/kirichess"
  and the page renders it as a link. It 404s for everyone but the owner. The same claim is load-bearing
  elsewhere - the terms banner points readers at `docs/game/CLAIMS.md` "in the project repository", and
  `docs/audits/INDEX.md` describes the repo as already public. The Apache-2.0 / CC BY split in §10 is a
  promise about a thing nobody can currently obtain.
- Fix: Publish the repository, or soften §10 to the future tense until it is. Do not leave a live link to a
  private URL in a document a player accepts at signup. **O-4** should run before the flip either way, and
  **L-9** owns the licence and attribution consequences.

#### L-1-8 — `RematchOffers` holds two account ids indefinitely, in no document and no retention rule
- Severity: **Low** (Medium once real accounts exist)
- Where: `src/KiriChess.Server/Data/Entities/RematchOffer.cs:14-21`,
  `src/KiriChess.Server/Data/KiriChessDbContext.cs:129-133`; absent from
  `src/KiriChess.Server/Retention/RetentionSchedule.cs:39-85`, `docs/decisions/RETENTION.md` §1,
  `docs/legal/PRIVACY.md` §2/§5/§6
- Impact: The table carries `OfferedById` and `AcceptedById` - two user ids and the timestamps linking
  them to a finished game - with **no foreign key at all** (deliberate, per the entity's own comment), no
  sweep rule, and no deletion on erasure (**L-2-2**). `RETENTION.md` §1 claims to be "the whole retention
  schedule"; this row is missing from it. The exposure is small because the same pairing is already public
  in the `Games` record, but a table nobody has decided a retention position for is precisely what §1
  exists to prevent.
- Fix: Add a `RematchOffers` line to `RETENTION.md` §1 with an explicit position - either a sweep rule
  (these are ephemeral live-offer flags; a short grace past `OfferedAt` fits the entity's own description)
  or a stated "kept, and why".

#### L-1-9 — `Challenges` is deleted and swept but disclosed nowhere in the privacy policy
- Severity: **Low**
- Where: `src/KiriChess.Server/Retention/RetentionSchedule.cs:62-71` and
  `src/KiriChess.Server/Account/AccountDeletionService.cs:70` (both correct);
  `docs/decisions/RETENTION.md` §1 (documented); absent from `docs/legal/PRIVACY.md` §2/§5/§6
- Impact: Direct challenge links (9d.8) store a challenger id, an accepted-by id and an expiry. The
  engineering document covers them; the player-facing one does not mention them in what is collected, how
  long it is kept, or what erasure removes. The code behaves *better* than the policy promises, which is
  the harmless direction, but §5's framing ("the parts that are kept indefinitely are listed here rather
  than left to be discovered") implies the table is complete.
- Fix: One row in §2, one in §5, one clause in §6's cascade list. Same pass as L-1-4 and L-1-8.

#### L-1-10 — `DPA-CHECKLIST.md`'s premise and closing section are stale
- Severity: **Low**
- Where: `docs/legal/DPA-CHECKLIST.md:15-18` ("Resend is inert with no API key") and the "When it is done"
  section (five items said to assert no DPA exists; items 1–4 have already been updated)
- Impact: The document that says *why* the DPA work has a deadline argues from facts that have since
  changed. Read literally it now understates the position - Resend is live, so the "you are not in breach
  today" reasoning no longer rests on inertness but on there being no public users. Not a compliance defect;
  a maintenance one, in a file the owner will re-read the next time a processor is added, which is
  **L-3**'s whole action list.
- Fix: Rewrite the deadline paragraph around the one condition that still holds (no public users until 8.7),
  and delete the spent "When it is done" list.

### Already covered

- **Verified accurate, no finding:** Sentry's configuration matches §4 exactly - `sendDefaultPii: false`,
  `tracesSampleRate: 0`, no replay or profiling, in both halves (`ui/instrumentation-client.ts:12-22`,
  `ui/sentry.server.config.ts:15-23`).
- **Verified accurate:** the five swept tables in `RetentionSchedule.cs:39-85` match
  `RETENTION.md` §1 and `PRIVACY.md` §5 one for one, including the refresh-token-vs-API-token asymmetry.
- **Verified accurate:** the dormant-account report exists as described (`Retention/RetentionEndpoints.cs:34`,
  `Retention/DormantAccountPolicy.cs`), is admin-only, read-only, and returns no email address.
- **Verified accurate:** the export's exclusions in §7 match `AccountExportBuilder.cs` field for field
  (no opponent email or id, no reporter identity, no moderator note, no token hash, no TOTP material),
  and the caps are the constants at `AccountExportBuilder.cs:14-15`.
- **Verified accurate:** the erasure live-game guard, the Restrict-vs-Cascade split, and the tombstone scrub
  are exactly what §6 describes (`AccountDeletionService.cs:37-41,62-97`).
- **Verified accurate:** version stamps agree across all four copies and `TermsVersion.Current`
  (`src/KiriChess.Server/Auth/TermsVersion.cs:26`) - `LegalDocumentDriftTests` is doing its job.
- **Cross-reference:** the erasure and export *defects* behind L-1-4, L-1-5 and L-1-8 are written up as
  findings in **L-2**; the processor and transfer deltas are in **L-3**. Cookies and terminal-equipment
  access (Turnstile, push, counters) belong to **L-6**. Whether recurring donation tiers change the
  controller's transparency duties is **L-7**. Repository visibility and licence consequences are **O-4**
  and **L-9**.
- No `docs/audits/deep/`, `docs/audits/readiness/` or `docs/audits/arch-speed.md` existed at the time of
  this packet, so nothing here could be a re-report.

### Action list

1. Bring `ui/app/privacy/page.tsx` §2/§5/§6/§7 level with `PRIVACY.md` (L-1-1). Smallest correct
   change; it is the one a stranger sees.
2. Remove the "no email has ever been delivered" clause from `PRIVACY.md` §5/§7,
   `ui/app/privacy/page.tsx:474` and `RETENTION.md:105`, replacing each with the reason that still holds
   (L-1-2).
3. Add the three missing activities to `PRIVACY.md`, its page, and `ROPA.md` §3: notification
   mail (L-1-3), web push (L-1-4), donations and patron status (L-1-5). One pass, one version bump.
4. Add `Challenges`, `RematchOffers` and `WebPushSubscriptions` rows to `RETENTION.md` §1 and
   `PRIVACY.md` §5 (L-1-4, L-1-8, L-1-9).
5. Extend `LegalDocumentDriftTests` with a within-section count comparison, or render the
   Markdown. Without this, action 1 will be undone by the next phase (L-1-1).
6. **[Owner]** Decide the Turnstile question: scope the script to `/login`, or amend §4 (L-1-6).
7. **[Owner]** Publish the repository or soften TERMS §10 before 8.7 (L-1-7).
8. **[Owner]** Bump `TermsVersion.Current` and both `Last updated` lines once actions 1–4 land - the
   changes above alter what is collected, which §11 says triggers a new version. Note the re-acceptance
   Known Limit: existing accounts are not re-prompted.

---

## L-2 — GDPR rights and data lifecycle
Date: 2026-08-21
Packet: docs/audits/INDEX.md

> Run here as part of the L-1/L-2/L-3 batch, so
> the three legal packets share one reading of the code. The readiness **PRIV** report this packet is
> meant to build on does not exist yet (`docs/audits/readiness/` is empty), so nothing below could be
> a re-report; when PRIV runs, it should treat this file as prior art rather than the reverse.

### Verdict

The erasure design is the strongest thing in this codebase's compliance surface. Anonymise-and-tombstone
is the right answer to a two-party game record, the reasoning is written down rather than assumed, the
live-game guard runs before any write, the whole cascade is one retried transaction, and the export
withholds exactly the five things it should. That design is sound and none of the findings below ask to
reverse it.

What is wrong is the **edges of the lifecycle, not its middle**. Three tables have been added since 8a.2
and none of them was wired into the erasure path or the export: web push subscriptions survive an
erasure with a live, uniquely-identifying endpoint; rematch offers survive holding two account ids;
patron status survives on the tombstone. And one structural gap is larger than any of them: an account
ban blocks sign-in, 2FA completion *and* refresh, while every rights route sits behind
`.RequireAuthorization()` - so the population whose data is retained the longest, on the strongest
legal basis, is the exact population with no working way to exercise Art. 15, 17 or 20. That is the
finding to fix first.

### Findings

#### L-2-1 — Web push subscriptions survive account erasure
- Severity: **High**
- Where: `src/KiriChess.Server/Account/AccountDeletionService.cs:62-78` (the cascade block; no
  `WebPushSubscriptions` delete), `src/KiriChess.Server/Data/KiriChessDbContext.cs:257-266`,
  `src/KiriChess.Server/Data/Entities/WebPushSubscription.cs:19-27`
- Impact: The FK is declared `OnDelete(DeleteBehavior.Cascade)` on `ApplicationUser`, and the entity's
  own comment says "a subscription is meaningless without the account it belongs to". Both are true and
  neither fires: **erasure never deletes the user row**, it scrubs it in place, so the database cascade
  is unreachable by design and the service has to do it explicitly - which it does for `RefreshTokens`,
  `ApiTokens`, `ExternalLoginCodes`, `TwoFactorChallenges`, `Challenges`, `Ratings` and the four Identity
  tables, and does not do here. After "delete my account", the surviving rows hold a push endpoint URL, a
  P-256 public key and an auth secret, pointing at a browser install that still has an active subscription.
  A push endpoint is a stable unique identifier for one device; retaining it against a row that is
  supposed to be anonymised is exactly what Art. 17 asks not to happen, and `PRIVACY.md` §6's promise
  that "your active sessions and bot keys" are removed entirely does not cover it.
  Live-today qualifier: `VapidOptions` is unconfigured in every deployed environment
  (`docs/ops/PROVIDER-ACCOUNTS.md` lists no `Vapid__*` secret), so no message is being sent; but
  `/api/push/subscribe` is **not** gated on `VapidOptions.Enabled`
  (`src/KiriChess.Server/Infrastructure/Push/PushEndpoints.cs:22-33`), so rows can accumulate regardless.
- Fix: One line in the transaction -
  `await _db.WebPushSubscriptions.Where(s => s.UserId == userId).ExecuteDeleteAsync(ct);` - beside the
  existing `TwoFactorChallenges` delete, plus an `AccountDeletionServiceTests` case asserting the table
  is empty afterwards. The disclosure half is **L-1-4**.

#### L-2-2 — Rematch offers survive account erasure and are never swept
- Severity: **Medium**
- Where: `src/KiriChess.Server/Data/Entities/RematchOffer.cs:14-21`,
  `src/KiriChess.Server/Data/KiriChessDbContext.cs:129-133` (no foreign keys, by design),
  `src/KiriChess.Server/Account/AccountDeletionService.cs:62-78` (no delete),
  `src/KiriChess.Server/Retention/RetentionSchedule.cs:39-85` (no rule)
- Impact: `OfferedById` and `AcceptedById` are account ids with no FK to constrain them and no rule to
  age them out. They survive erasure pointing at a tombstone, and they survive forever. The privacy harm
  is genuinely small - the same two players are already linked publicly by the `Games` row the offer
  refers to, so nothing is learnable here that the game record does not already say - which is why this
  is Medium and not High. The problem is that it is undecided rather than decided: `RETENTION.md` §1
  presents itself as the whole schedule, and a table that is in neither the schedule nor the erasure path
  is the shape of an oversight even when its contents are harmless.
- Fix: Delete both directions in the erasure transaction (`r => r.OfferedById == userId ||
  r.AcceptedById == userId`), matching what `Challenges` already does at line 70, and add a sweep rule
  with a short grace past `OfferedAt`. If the decision is instead to keep them, say so in
  `RETENTION.md` §1 with the two-party-record reasoning.

#### L-2-3 — A banned account has no working route to access, portability or erasure
- Severity: **High**
- Where: `src/KiriChess.Server/Auth/AuthEndpoints.cs:172-174` (login), `:231-233` (2FA verify),
  `:281-283` (refresh) - all three return 403 on a blocking `SanctionScope.Account` sanction;
  `src/KiriChess.Server/Account/AccountEndpoints.cs:21-22` (`/api/account` group is
  `.RequireAuthorization()`), export at `:24`, deletion at `:47`
- Impact: An `AccountBan` correctly closes every path to a token - sign-in, the second factor, and
  refresh, so an already-issued access token dies at its next rotation. The rights routes are all behind
  that same authentication. The result is that a fully banned player cannot export their data, cannot
  delete their account, and cannot see what is held about them, through any self-serve route. This is the
  population whose data is retained longest and most deliberately: `PRIVACY.md` §6 and `TERMS.md` §6 both
  say sanctions survive deletion precisely so a ban cannot be laundered, and `ROPA.md` §3.3 records the
  retention as indefinite under Art. 17(3)(e). Retaining their data on a strong basis is defensible;
  leaving them no route to Art. 15 or Art. 20 is not, and Art. 12(2) says the controller shall facilitate
  the exercise of these rights.
  Nothing in `PRIVACY.md` §8 notices the gap: it says "Access, the export in §7 is the access mechanism"
  and "Erasure, §6" without qualification, and points at `privacy@kirichess.com` only for a complaint.
- Fix: The smallest correct change is documentary plus a manual path, not a new self-serve surface:
  state in §8 that a banned account exercises these rights by mail to `privacy@kirichess.com` (the
  mailbox is confirmed delivering - ROADMAP DH.0, closed 2026-08-16), and give the moderator tooling a
  way to run `AccountExportBuilder` for a named user id so the request can actually be served. Erasure for
  a banned user needs a deliberate answer too, since anonymising them must not drop the sanction -
  the current design already survives that (sanctions are `Restrict` and point at the tombstone), so
  allowing an owner-run erasure is safe.

#### L-2-4 — Patron status survives erasure and is not exported
- Severity: **Medium**
- Where: `src/KiriChess.Server/Data/Entities/ApplicationUser.cs:56,61`;
  `src/KiriChess.Server/Account/AccountDeletionService.cs:81-96` (scrub list; `IsPatron`/`PatronSince`
  untouched); `src/KiriChess.Server/Account/AccountExportBuilder.cs:155-162` (profile projection; neither
  field present)
- Impact: Two failures from one omission. On erasure, `PRIVACY.md` §6 promises "your email, password hash,
  phone number and every other identifying credential field are cleared" - the tombstone in fact retains
  a flag saying this person donated and the date they first did. That is a fact about a specific human
  attached to a row the document calls anonymised, and it is neither disclosed nor justified anywhere.
  On export, the same two fields are data the controller holds about the subject and Art. 15(1)/Art. 20
  reach them; the export omits them silently.
  Currently theoretical rather than live: the `/donate` payment links carry no `client_reference_id`
  (`ui/components/DonationTiers.tsx:46-47`), so the webhook branch that sets these fields
  (`src/KiriChess.Server/Account/StripeWebhookEndpoints.cs:50-62`) never fires and both columns are
  presently `false`/`null` for every account. It stops being theoretical the moment a link is wired up
  to pass the id.
- Fix: Add `IsPatron`/`PatronSince` to `AccountExportProfileDto`, and decide the erasure position
  explicitly - either clear both in the scrub (simplest, and consistent with §6's wording), or retain them
  with a stated reason in §6. Do this before any donation is linked to an account, not after. The
  disclosure half is **L-1-5**; whether donations change the controller's transparency duties at all is
  **L-7**.

#### L-2-5 — The export omits three further categories the controller holds
- Severity: **Medium**
- Where: `src/KiriChess.Server/Account/AccountExportBuilder.cs:35-170`
- Impact: Beyond the patron fields above, the export contains no entry for:
  - **Web push subscriptions** (`WebPushSubscriptions`) - one row per browser, endpoint and keys. The
    endpoint should be truncated or fingerprinted in the export rather than echoed in full (it is a
    capability URL: anyone holding it can push to that browser), but its *existence*, device count and
    creation dates are exportable and currently invisible.
  - **Challenges** (`Challenges`) - direct challenge links the account created or accepted, with
    timestamps. Deleted correctly on erasure (`AccountDeletionService.cs:70`), just never exported.
  - **Notification preferences** are exported (`:160`) but appear in no §2 group, so a reader has no way
    to know what the two timestamps in their export file mean.

  None of these is a large omission on its own. Together they are the same drift as L-1: three tables
  added after 8a.2, and the export builder - which has no test asserting it covers every user-scoped
  table - was never revisited.
- Fix: Add the three collections to `AccountExportDto` (endpoint truncated for push), and add a test that
  fails when a new `DbSet` carrying a `UserId` is introduced without an export projection. That test is
  the durable half; the three additions are the instance.

#### L-2-6 — "Indefinite" moderation retention has no stated review point
- Severity: **Low** (trigger: the first real sanction, or the first Art. 17 objection to one)
- Where: `docs/legal/PRIVACY.md` §5 (Reports / Sanctions rows) and §6; `docs/legal/ROPA.md` §3.3
  ("Retention | Indefinite, and deliberately survives account deletion"); `docs/decisions/RETENTION.md` §1;
  `src/KiriChess.Server/Data/Entities/UserSanction.cs:34,37` (`Reason`, `ModeratorNote` free text)
- Impact: The *position* is right and this packet does not reopen it - a ban that could be erased by
  deleting the account it was issued against would not be a ban, and Art. 17(3)(e) is the correct hook.
  The weak part is that Art. 17(3)(e) justifies retention "for the establishment, exercise or defence of
  legal claims", which is a purpose that ends, and none of the three documents names a point at which it
  does. A sanction row carries free-text `Reason` (exported to the subject) and `ModeratorNote` (never
  exported) written by a human about a human, kept forever with no review. A supervisory authority is more
  likely to ask "until when, and who decides" than to challenge the ban-laundering argument itself.
- Fix: One sentence in `RETENTION.md` §1 and `PRIVACY.md` §5 giving the review trigger rather than a
  duration - for instance, that a lifted or expired sanction older than a stated period is reviewed for
  deletion, while an active ban is kept for as long as it is active. Do not invent a retention *timer*;
  a trigger is proportionate and matches how the rest of this document reasons.

#### L-2-7 — The dormant-account revisit trigger has half-fired and the document has not noticed
- Severity: **Low**
- Where: `docs/decisions/RETENTION.md:95-110` ("The revisit trigger, named explicitly: a verified mail
  path plus real signups"); contradicted by `docs/legal/PRIVACY.md` §4 (Resend live) and
  `docs/legal/ROPA.md` §3.5 ("Active in production")
- Impact: The decision not to auto-erase dormant accounts rests on three stated reasons, and one of them
  ("the warning email cannot be sent, no email has ever been delivered by this system") is now false. The
  other two - nothing deployed, and an untested erasure predicate that would first run against real
  accounts - still hold and are sufficient on their own. This is not a wrong decision, it is a decision
  whose written justification is one-third stale, in the file an owner will consult when deciding whether
  the trigger has fired. See **L-1-2**, which owns the same stale claim in the published policy.
- Fix: Update the paragraph to note that the mail path is now verified and that the remaining conditions
  (real signups, a predicate that has been observed against real data) are what still gate the reaper.

#### L-2-8 — The account id survives erasure in operational logs, and §5/§6 do not say so
- Severity: **Low**
- Where: `src/KiriChess.Server/Infrastructure/UserScopeMiddleware.cs` (log scope is the account id),
  `docs/legal/PRIVACY.md` §5 ("Server logs | Whatever the hosting platform keeps") and §6
- Impact: The tombstone keeps its primary key - it has to, because `Games`, `RatingHistories`, `Reports`
  and `UserSanctions` all point at it under `Restrict`. That same id is written into every log line
  produced while the account was signed in, and those lines outlive the erasure in whatever log store
  holds them. This is *consistent* with the design rather than contrary to it: after erasure the id no
  longer resolves to a name or an email, so it is pseudonymous in the same way the retained `Games` rows
  are. But §6's "what delete my account actually does" list does not mention logs at all, and §5's
  "whatever the hosting platform keeps" is now incomplete for a second reason - logs are also shipped to a
  third party (**L-3-1**), whose retention is a different number.
- Fix: One clause in §6 saying that the account identifier persists in operational logs and in the game
  record, and that this is what "anonymised" means here - the id stops resolving to a person, it does not
  vanish. Update §5's log row once **L-3-1** names the log destination.

#### L-2-9 — Donation references survive erasure, are never swept, and are absent from the schedule entirely

- Severity: **Medium** (trigger: any account that ever started the patron handshake, then erased)
- Found: 2026-09-04, while fixing L-2-1. Same bug class, found by auditing that class rather than by a
  new pass.
- Where: `src/KiriChess.Server/Data/Entities/DonationReference.cs` (carries `UserId`),
  `src/KiriChess.Server/Data/KiriChessDbContext.cs:198-206` (FK declared
  `OnDelete(DeleteBehavior.Cascade)`), `src/KiriChess.Server/Account/AccountDeletionService.cs`
  (no delete; verified, zero occurrences of `DonationReference` in the file),
  `src/KiriChess.Server/Retention/RetentionSchedule.cs` (no rule; verified, zero occurrences),
  `docs/decisions/RETENTION.md` (**not mentioned anywhere in the document**; verified, zero occurrences)
- Impact: this is L-2-1 exactly, one table over, and the DbContext comment says so out loud: "Cascade: a
  reference is meaningless without the account it belongs to, same reasoning as WebPushSubscription
  below." That reasoning was unreachable for `WebPushSubscription` and it is unreachable here for the
  same reason, because erasure scrubs the user row in place and never deletes it, so no cascade ever
  fires. The row survives an erasure pointing at a tombstone. It also never ages out at all: the entity
  comment says it "dies in 24 hours", but that describes intended single-use redemption, not an enforced
  sweep, and nothing deletes it after `ExpiresAt`, consumed or not.

  The contents are genuinely low-sensitivity, which is why this is Medium and not High: an opaque
  `Reference` string the entity itself says "means nothing outside this table", plus `UserId`,
  `CreatedAt`, `ExpiresAt`, `ConsumedAt`. No credential, no device identifier, nothing like the live push
  capability L-2-1 left behind. The severity is about the schedule, not the payload. `RETENTION.md` §1
  presents itself as the whole retention schedule, and this table is not in it, is not flagged as a gap,
  and is not named in the prose the way L-2-2's `RematchOffers` at least is. It is the one table that is
  missing rather than undecided.

- Fix: delete it in the erasure transaction next to the L-2-1 line (`d => d.UserId == userId`), add a
  sweep rule keyed on `ExpiresAt` so unconsumed references age out, and add the row to `RETENTION.md` §1.
  If donations stay out of the launch the sweep is still worth
  having, because the table exists and accumulates whether or not the feature is reachable.
- Note: deliberately NOT fixed alongside L-2-1. That pass was told not to widen its own
  scope, and the retention posture of a table is a decision to record, not a delete to slip into an
  unrelated diff.
### Already covered

- **Verified correct, no finding:** the erasure transaction itself. The live-game guard runs before any
  write (`AccountDeletionService.cs:37-41`), the whole block is inside one
  `CreateExecutionStrategy().ExecuteAsync` transaction and is replay-safe, the scrub clears every Identity
  credential field plus rotates the security stamp, and the log line carries the user id and nothing else
  (`:105`). `ApiTokens` are deleted (`:63`), so `PRIVACY.md` §5's "until you revoke them or delete the
  account" holds.
- **Verified correct:** the export's five exclusions match the code exactly - no opponent email or id
  (`AccountExportBuilder.cs:64-76`), no reporter identity (`:117-119`), no moderator note (`:122-125`),
  no `TokenHash` for sessions or API tokens (`:128-149`, projected out before materialisation), no TOTP
  material (`:155-162`). Caps and per-collection truncation flags are real (`:14-15,42-45,102-104`).
- **Verified correct:** the DSAR mailbox works. `privacy@kirichess.com` routes through Cloudflare Email
  Routing and delivery was confirmed by the owner (ROADMAP DH.0, closed 2026-08-16); all four routed
  addresses were checked.
- **Verified correct:** the dormant-account scan is read-only, admin-gated, and returns no email address
  (`Retention/RetentionEndpoints.cs:34`, `Retention/DormantDtos.cs:17-21`), exactly as `RETENTION.md` §4
  describes. Nothing erases these accounts.
- **Verified correct:** public games as personal data. After anonymisation a `Games` row still carries
  both player ids, and every public surface resolves them through the display name, which is now
  "Deleted account" - so the two-party record survives without re-identifying the erased player. This is
  the design `PRIVACY.md` §6 describes and it does what it says.
- **Verified correct:** the five swept tables in `RetentionSchedule.cs:39-85` match `RETENTION.md` §1,
  including the deliberate refresh-token-vs-API-token asymmetry (`RETENTION.md` §2).
- **Cross-reference:** every *disclosure* gap behind these findings is written up in **L-1** (push at
  L-1-4, Stripe at L-1-5, `RematchOffers` at L-1-8, `Challenges` at L-1-9). Processor and transfer
  questions are **L-3**. Sanctions-vs-Art. 17 as a policy position is a standing position in
  `docs/audits/INDEX.md` and is not reopened here - L-2-6 asks only for a review trigger.

### Action list

1. Delete `WebPushSubscriptions` and `RematchOffers` in the erasure transaction, with tests
   (L-2-1, L-2-2). Two lines; the highest-value change in this packet.
2. Add `IsPatron`/`PatronSince`, push subscriptions and challenges to the export, plus a test
   that fails when a user-scoped `DbSet` is added without an export projection (L-2-4, L-2-5).
3. **Owner** Decide and document the banned-account rights path in `PRIVACY.md` §8, and give
   moderation a way to run the export for a named user id so the mailbox route can actually be served
   (L-2-3). Owner decides the policy; the tooling is a separate edit.
4. Clear `IsPatron`/`PatronSince` in the erasure scrub, or state in §6 why they are kept
   (L-2-4). Before any donation link is wired to pass an account id.
5. Update `RETENTION.md` §4's revisit-trigger paragraph and §1's moderation rows: mail is now
   verified, and "indefinite" needs a review trigger (L-2-6, L-2-7).
6. Add the logs clause to `PRIVACY.md` §6 (L-2-8), together with the §5 log-destination
   update that **L-3-1** requires.

---

## L-3 — Processors, transfers, ROPA
Date: 2026-08-21
Packet: docs/audits/INDEX.md

The owner files the DPAs. Accepting one is entering a contract as controller and cannot be delegated.

### Verdict

DH.5 was done properly. Five processors, five filed contracts, version identifiers and dates recorded,
one genuinely executed signature, Google correctly excluded as an independent controller with the
reasoning written out, and a transfer basis stated per provider rather than waved at. `ROPA.md` §4 and
`DPA-CHECKLIST.md` are better records than most companies keep.

The problem is that they are a snapshot of **2026-08-17**, and the live stack has grown since. The
sentence that matters is `DPA-CHECKLIST.md`'s own: "Art. 28(3) requires a contract with every processor
**before** it processes personal data on your behalf." By that test the roster is short by at least one
processor that is *already receiving personal data today* - Better Stack, which since 2026-08-19 has been
receiving the unfiltered Fly log stream for every app in the org, including the account-id-scoped
application logs - and by one recipient that is live in the product but in no document at all, Stripe.
Two more (Cloudflare R2, GitHub) become processors of a full database copy the first time a scheduled
workflow succeeds. `ROPA.md` §4's closing line anticipates exactly one future addition, Upstash; the real
queue is five deep.

The framing to keep: none of this is a breach today, for the same reason DH.5 was not late - Cloudflare
Access still gates the site and there are no public users. All of it must be closed before 8.7 opens the
door, and the Better Stack row should be closed now rather than at 8.7, because that one is already
carrying data.

### The delta, as a table

The deliverable this packet owes. Rows above the line are live; rows below fire on a named trigger.

| # | Party | What it actually receives | In PRIVACY §4? | In ROPA §4? | In DPA-CHECKLIST? | Action |
|---|---|---|---|---|---|---|
| 6 | **Better Stack** (Logtail) | Unfiltered Fly log stream, all four apps: application JSON logs scoped by account id, plus whatever the platform emits on the same subject | ✗ | ✗ | ✗ | **File a DPA now.** Add rows to all three |
| 7 | **Stripe** | Donor payment data via live payment links; a webhook endpoint is wired to receive `checkout.session.completed` | ✗ | ✗ | ✗ | Add rows; confirm controller/processor split for payment links |
| - | Cloudflare | Already filed (v6.4) - but the filed scope is DNS/proxy/CDN/Turnstile/Email Routing | ✓ | ✓ | ✓ | Scope needs widening for R2 (row 8) |
| - | Fly, Neon, Sentry, Resend | Filed 2026-08-17, accurate | ✓ | ✓ | ✓ | No action |
| - | Google | Independent controller, correctly excluded | ✓ | ✓ | n/a | No action |
| 8 | **Cloudflare R2** | A complete production database dump, weekly | ✗ | ✗ | ✗ | Trigger: first successful `offsite-backup.yml` run |
| 9 | **GitHub (Microsoft)** | The same dump, created and restored on a GitHub-hosted runner | ✗ | ✗ | ✗ | Same trigger as row 8 |
| 10 | **Browser push services** (Google/Mozilla/Apple) | Push endpoint plus an encrypted payload | ✗ | ✗ | ✗ | Trigger: `Vapid__*` secrets set |
| 11 | Have I Been Pwned | 5 hex characters of a SHA-1 prefix, k-anonymous | ✗ | ✗ | n/a | Trigger: `Auth__BreachedPasswordCheckEnabled=true`. Not personal data; ROPA §3.1 accuracy only |
| 12 | Upstash | Session/rate-limit state | ✗ | anticipated | anticipated | Trigger: 8.4 Redis. Already correctly foreseen |

### Findings

#### L-3-1 — Better Stack is a live processor receiving the whole log stream, and is in none of the three records
- Severity: **High**
- Where: `fly.logshipper.toml:31` (`SUBJECT = "logs.>"`, "Every app in the org, unfiltered"),
  `infra/betterstack/main.tf:304` (`logtail_source "app_logs"`), `docs/ops/DEPLOYMENT.md:761-764`
  (§11.1 "State as of 2026-08-19: **live**" - Live tail has shown real `kirichess-api` JSON, an EF
  command on `/api/auth/register`), log content per
  `src/KiriChess.Server/Infrastructure/UserScopeMiddleware.cs`; absent from `docs/legal/PRIVACY.md` §4,
  `docs/legal/ROPA.md` §3.6 ("Recipients | Sentry, Fly.io") and §4, and `DPA-CHECKLIST.md`'s "the five"
- Impact: A Vector collector has been forwarding the org's entire Fly log stream to Better Stack since
  2026-08-19. Application logs carry the account **id** as a log scope - `PRIVACY.md` §2's own "Logs" row
  and `ROPA.md` §3.6 say so - which makes them pseudonymous personal data, and the subject filter is
  deliberately wide (`logs.>`, all four apps plus the shipper), so this is not only application stdout:
  whatever else the platform emits on that stream goes with it. `ROPA.md` §3.6 names Sentry and Fly.io as
  the recipients of logging data and stops there. `PRIVACY.md` §4 says "§4 below lists every third party
  that receives data"; it does not list this one. And `DPA-CHECKLIST.md`'s own deadline test - a contract
  before processing begins - is already past due for this processor, three days before this audit.
  Two details the owner needs before filing, neither of which should be assumed: **the contracting
  entity** (Better Stack is EU-incorporated, which would change the transfer analysis materially versus
  the five US processors already filed) and **the data region** - the ingest host documented at
  `DEPLOYMENT.md:752` is of the form `sNNNN.us-west-2a.betterstackdata.com`, i.e. US, while the monitor
  resources are configured `regions = ["us", "eu"]` (`main.tf:80,335,360`). An EU vendor storing in
  us-west-2 still needs a transfer basis.
- Fix: Obtain and file Better Stack's DPA, then add it as a sixth row to `ROPA.md` §4 and its Art. 28(3)
  table, as a recipient in `PRIVACY.md` §4, and to `ROPA.md` §3.6's recipient list. While in the account,
  set an explicit log retention - `main.tf:308-310` deliberately sets none, so the current answer is "the
  plan default", which `PRIVACY.md` §5's "Server logs | Whatever the hosting platform keeps" no longer
  describes. Consider narrowing `SUBJECT` at the same time: forwarding staging's logs to the same
  destination is a second, avoidable copy.

#### L-3-2 — Stripe is live in the product and appears in no record
- Severity: **High**
- Where: `ui/components/DonationTiers.tsx:36,46-47,56-57,66-67,76-77,86-87` (eleven live
  `buy.stripe.com` links, one-off and monthly), `ui/app/donate/page.tsx` linked from
  `ui/components/NavBar.tsx:111` and `ui/components/Footer.tsx:128`;
  `src/KiriChess.Server/Account/StripeWebhookEndpoints.cs:22` wired at
  `src/KiriChess.Server/Program.cs:731`; absent from `docs/legal/PRIVACY.md` §4, `docs/legal/ROPA.md`
  §3/§4, `DPA-CHECKLIST.md`
- Impact: The donation path is shipped and reachable from every page's navigation. A visitor who clicks a
  tier is handed to Stripe, which collects their name, email and payment instrument. The KiriChess server
  holds a webhook route that receives `checkout.session.completed` events and, on the `ClientReferenceId`
  branch, writes patron status onto an account row. None of Stripe, the donation activity, or the patron
  fields appears in any of the three legal records.
  What is *not* true today, and should not be overstated when filing: the payment links carry no
  `client_reference_id` (`DonationTiers.tsx:33-36` names the parameter in a comment and never uses it), so
  Stripe currently receives no KiriChess account id and the webhook's user-matching branch never fires.
  Today the relationship is closer to "a donor transacts with the controller through Stripe" than "Stripe
  processes player data on the controller's instructions". That distinction is exactly what needs deciding
  before it is documented, and it changes the moment a link is wired to pass the id.
- Fix: Determine the Art. 28 posture for Stripe payment links - Stripe is a processor for the merchant's
  transaction data and an independent controller for its own fraud and AML processing, so the honest entry
  is closer to the Google row's shape than to Fly's - accept Stripe's DPA (self-serve, incorporated into
  the account terms), and add the party to `PRIVACY.md` §4, a §3.x donations activity to `ROPA.md`, and a
  §4 transfer row. `PRIVACY.md` §1's own geographic-address trigger is engaged by the same facts; that is
  **L-7**'s decision, flagged here so it is not lost between packets. Disclosure detail is **L-1-5**;
  erasure and export of patron fields is **L-2-4**.

#### L-3-3 — Cloudflare R2 and GitHub become processors of a full production dump on the first successful backup run
- Severity: **Medium** (High the moment the workflow succeeds)
- Where: `.github/workflows/offsite-backup.yml:13-18` (weekly cron, `41 5 * * 0`), `:50-62`
  (R2 credentials, `R2_JURISDICTION: eu`, restore-verify on the runner), `scripts/offsite-backup.sh`;
  `docs/decisions/RETENTION.md` §6 and `docs/ops/DEPLOYMENT.md` §5.3 (plumbing in, first dump parked);
  scope of the filed Cloudflare DPA per `DPA-CHECKLIST.md:25` ("DNS, proxy, CDN, Turnstile, Email Routing")
- Impact: Two new custody relationships arrive together. **R2** would hold a complete copy of every account,
  game, report and sanction - a materially different thing from proxying traffic, and the filed Cloudflare
  v6.4 DPA covers Cloudflare as processor generally, so the contract is likely adequate while the *records*
  are not: neither `PRIVACY.md` §4 nor `ROPA.md` §4 tells a reader that Cloudflare holds a database copy.
  **GitHub** is the larger gap: the workflow branches Neon, dumps the branch, puts the object in R2, then
  pulls it back and restores it into a Postgres container **on a GitHub-hosted runner**. Production personal
  data transits and is materialised on Microsoft-operated infrastructure, and GitHub appears in no record at
  all. Neither is happening yet - `RETENTION.md` §6 and I-4 both say the first dump never ran, parked on
  private-repo Actions minutes - but the cron is armed, not disabled, so the trigger is "the quota question
  resolves", including as a side effect of making the repo public (**L-1-7**, **O-4**).
  One thing already right: `R2_JURISDICTION` defaults to `eu`, so the bucket is in Cloudflare's EU
  jurisdiction restriction. Say so in the record rather than leaving it in a workflow default.
- Fix: Before the first dump succeeds - add an R2 row to `PRIVACY.md` §4 and `ROPA.md` §4 (or widen the
  existing Cloudflare rows to name backup storage and the EU jurisdiction), add GitHub as a processor row
  with its own transfer basis (GitHub's DPA is standard and self-serve), and add a backup activity to
  `ROPA.md` §3 with its retention - the dumps need a lifecycle rule on the bucket, which
  `RETENTION.md` §1 does not currently carry either. If that work cannot land first, disable the cron and
  run the backup by `workflow_dispatch` until it does.

#### L-3-4 — Browser push services are an undisclosed recipient, armed but not firing
- Severity: **Medium** (trigger: `Vapid__*` secrets set on the Fly app)
- Where: `src/KiriChess.Server/Infrastructure/Push/PushNotificationService.cs:36-40,57-75`,
  `src/KiriChess.Server/Infrastructure/Push/VapidOptions.cs:18-21` (fails closed absent all three values),
  `src/KiriChess.Server/Infrastructure/Push/PushEndpoints.cs:22-33`; no `Vapid__*` in
  `docs/ops/PROVIDER-ACCOUNTS.md`'s deployed-secret table
- Impact: Sending a web push means POSTing to a URL operated by Google (FCM), Mozilla or Apple, chosen by
  the subscriber's browser. Those services see the endpoint, the timing, and the fact that this deployment
  is messaging that browser. The payload is encrypted to the subscription's own keys, so they do not see
  its content - that is worth stating in the record, because it is the mitigating half. No push has been
  sent: `VapidOptions.Enabled` is false everywhere deployed. But `/api/push/subscribe` is not gated on it,
  so `WebPushSubscriptions` can already fill with endpoints, and the day the secrets are set the recipient
  relationship starts with no document mentioning it.
  Whether a push service is a processor or an independent controller for this is genuinely unsettled and
  this is not a decision to make from the repository; the practical answer for a hobby host is disclosure plus a
  transfer basis, the same shape the Google OAuth row already uses.
- Fix: Add a push activity to `ROPA.md` §3 and a recipient line to `PRIVACY.md` §4, both written now while
  the feature is inert, so that setting the VAPID secrets is a one-step change rather than a compliance
  event. Storage and erasure of the subscriptions themselves are **L-1-4** and **L-2-1**.

#### L-3-5 — `ROPA.md` §3.6 understates its own recipients, and §4 anticipates only Upstash
- Severity: **Medium**
- Where: `docs/legal/ROPA.md` §3.6 ("**Recipients** | Sentry, Fly.io") and §4's closing line
  ("Add a sixth row if 8.4's Redis is ever provisioned")
- Impact: This is the structural version of L-3-1 through L-3-4. The record's own forward-looking sentence
  names exactly one future processor, which was accurate on 2026-08-20 and is the reason the additions
  since have gone unnoticed: a document that says "one more is coming" reads as complete. §3.6 in
  particular is the activity that actually gained a recipient (logging), and it is the one that still says
  two. §6's review triggers are well chosen ("a new sub-processor is added, a new category of data is
  collected") - they simply were not run when Better Stack, push and Stripe landed.
- Fix: When adding the rows above, replace the Upstash sentence with a pointer to §6's review triggers, so
  the record stops implying a closed set. Consider a `ROPA` drift test in the shape of
  `LegalDocumentDriftTests` - one that fails when a new outbound HTTP client or `DbSet` appears without a
  matching ROPA row would have caught three of the five gaps in this packet automatically.

#### L-3-6 — HIBP is an outbound third-party call absent from ROPA §3.1's recipients
- Severity: **Low** (trigger: `Auth__BreachedPasswordCheckEnabled=true`)
- Where: `src/KiriChess.Server/Auth/BreachedPasswordChecker.cs:19-23` (off by default), `:66-72`
  (5-character SHA-1 prefix, `Add-Padding: true`); `docs/ops/DEPLOYMENT.md` §9; absent from
  `docs/legal/ROPA.md` §3.1
- Impact: On registration and password reset, when enabled, the server queries Have I Been Pwned's
  k-anonymity range API. The implementation is careful - only five hex characters of the hash leave the
  process, response padding is requested, and it fails open - and a 5-character prefix shared by hundreds
  of thousands of passwords is not personal data by any reasonable reading, so **no disclosure is owed to
  players and none should be added to `PRIVACY.md`**. The gap is narrower: `ROPA.md` §3.1 lists the
  recipients of the authentication activity and this outbound dependency is not among them, which matters
  only for the record's completeness and for anyone later auditing egress. Also worth noting it is
  currently disabled in production, which is a separate question for **S-1**.
- Fix: One clause in `ROPA.md` §3.1's recipients naming HIBP and stating the k-anonymity design, so a
  future reader does not rediscover the egress and mistake it for an undisclosed transfer.

#### L-3-7 — `DPA-CHECKLIST.md`'s deadline premise is stale in the direction that matters
- Severity: **Low**
- Where: `docs/legal/DPA-CHECKLIST.md:15-18` ("You are not in breach today: there are no public users,
  Resend is inert with no API key, and Google OAuth has no credentials configured")
- Impact: Resend is live (`PRIVACY.md` §4, `ROPA.md` §3.5, `PROVIDER-ACCOUNTS.md:19`), so one of the three
  supporting facts is false. The conclusion still holds on the first fact alone - no public users - but
  this is the paragraph an owner re-reads when deciding whether a new processor can wait, and it currently
  reasons from a stack that no longer exists. Same finding as **L-1-10**, restated here because this
  packet's action list runs out of this file.
- Fix: Rewrite around the one condition that still holds, and re-date the document when the rows above are
  added.

### Already covered

- **Verified accurate, no finding:** the five filed DPAs. `ROPA.md` §4's Art. 28(3) table matches
  `DPA-CHECKLIST.md`'s own table on version identifier and date for all five, the distinction between
  "executed" (Sentry, Fly) and "incorporated" (Cloudflare, Neon, Resend) is drawn correctly, and the
  filed PDFs exist in `docs/legal/` under the exact names both tables give.
- **Verified accurate:** Google is correctly treated as an independent controller, not a processor, with
  the reasoning stated in both `PRIVACY.md` §4 and `ROPA.md` §4 and the "do not go looking for a Google
  DPA" note in `DPA-CHECKLIST.md`. Google OAuth remains unconfigured in every deployed environment.
- **Verified accurate:** the transfer paragraph. All five filed processors are US companies, the DPF/SCC
  split is stated per provider, and Resend's SCC-only position (no DPF claim) is correctly singled out.
- **Verified accurate:** `infra/betterstack/terraform.tfvars` and `terraform.tfstate` are gitignored
  (`.gitignore:71,73`) - the Better Stack API token is not in the repository. Checked because
  `.claude/rules/security.md` records a prior live-token incident.
- **Verified accurate:** Upstash is correctly *not* on the list yet and correctly anticipated;
  `Redis__Configuration` is unset and the standing position holds.
- **Cross-reference:** `NEON_PROJECT_ID` is hardcoded as a workflow default at
  `.github/workflows/offsite-backup.yml:56`. That is **O-4**'s packet (public-repo hygiene, which names
  Neon project ids explicitly) and becomes live the day the repo is published; not re-reported here.
  Disclosure wording is **L-1**; erasure and export are **L-2**; cookies and terminal-equipment access
  for Turnstile and push are **L-6**; the donations-as-remuneration question is **L-7**.

### Action list

Ordered by when the obligation actually bites, not by severity.

1. **[Owner]** File a Better Stack DPA and record the contracting entity and data region (L-3-1). This is
   the only row where processing has already started; everything else below is pre-emptive.
2. Add Better Stack to `PRIVACY.md` §4, `ROPA.md` §3.6 and §4, and `DPA-CHECKLIST.md`, and set
   an explicit log retention on the source (L-3-1).
3. **[Owner]** Decide Stripe's Art. 28 posture and accept its DPA; add the rows and a §3.x
   donations activity (L-3-2). Do this before any payment link is wired to pass `client_reference_id`.
4. **Owner** Before the first `offsite-backup.yml` run succeeds: R2 and GitHub rows, a backup
   activity in `ROPA.md` §3, and a bucket lifecycle rule in `RETENTION.md` §1. If that cannot land first,
   change the trigger to `workflow_dispatch` only (L-3-3).
5. Write the push rows now, while the feature is inert, so enabling VAPID is a config change
   rather than a compliance event (L-3-4).
6. Replace `ROPA.md` §4's Upstash-only forward-looking sentence with a pointer to §6's review
   triggers, and add the HIBP clause to §3.1 (L-3-5, L-3-6).
7. Rewrite `DPA-CHECKLIST.md`'s deadline paragraph and re-date the file once 1–6 land
   (L-3-7). Same edit as L-1-10 - do it once.
8. A ROPA drift test in the shape of `LegalDocumentDriftTests`:
   fail when a new outbound HTTP client or user-scoped `DbSet` has no matching ROPA row. Three of the five
   gaps in this packet would have been caught at commit time (L-3-5).

---

## L-4 — Age and children
Date: 2026-08-21
Packet: docs/audits/INDEX.md

### Verdict

The age position is coherent, cheap, and honestly documented for the path it was designed around. A
16 checkbox with no date of birth is the right call for this service, the reasoning is written down
in three places that agree, and the UGC surface the child-safety story rests on really is as small
as the documents assume: no chat, no uploads, no avatars, no free text another player can read. The
one defect is on the Google sign-in path, where the account row records an age declaration the
person was never shown. Everything else here is wording drift and one missing operational answer:
what happens when the operator learns an account holder is under 16.

### UGC surface — confirmed

The packet asks for this confirmation before the child-safety claims can be trusted, so it is
recorded rather than assumed:

- **Display name** — the only player-authored string another person can read. ASCII-only, length and
  substance rules, blocklist and reserved terms (`src/KiriChess.Server/Moderation/Content/ContentPolicy.cs:6-16`),
  with a moderator scan and forced rename behind it (`src/KiriChess.Server/Moderation/Content/ContentEndpoints.cs:113-160`).
- **Report details** — free text capped at 1000 characters (`src/KiriChess.Server/Moderation/ReportEndpoints.cs:34-37`),
  moderator-visible only, never public and never exported (PRIVACY §2, §7).
- **PGN / replays** — moves plus display names. No annotation or comment field.
- **No chat.** The one content-check hook an eventual chat would call is unused and says so:
  `src/KiriChess.Server/Moderation/Content/ContentPolicy.cs:182-183`.
- **No uploads.** No `type="file"`, no `FormData`, no `IFormFile` anywhere in `ui/` or `src/`.
  Avatars are rendered initials (`ui/components/Avatar.tsx:8-23`).
- **No message on a challenge.** `Challenge` carries a token, a pool and ids only
  (`src/KiriChess.Server/Data/Entities/Challenge.cs:22-44`).

So "there is no way for an adult to send a private message to a child on this site" is true today,
and TERMS §2 / PRIVACY §9 are entitled to rely on it.

### Findings

#### L-4-1 — Google sign-up records an age declaration that was never made
- Severity: Medium (dormant: no Google credentials exist in any deployed environment today,
  PRIVACY §4 — the trigger is configuring a Google client id and secret)
- Where: `src/KiriChess.Server/Auth/ExternalLoginService.cs:121` stamps
  `AgeConfirmedAt = DateTimeOffset.UtcNow` at account creation. The text that stamp is supposed to
  rest on is `ui/messages/en.json:77` — "By continuing you accept the Terms of Service and Privacy
  Policy." — rendered at `ui/app/login/page.tsx:224-240`. It says nothing about age.
- Impact: three documents describe that column as evidence a declaration happened. TERMS §2 ("This
  is declared by a checkbox at signup"), PRIVACY §9 ("The age check is a self-declared checkbox at
  signup, recorded as a timestamp (`AgeConfirmedAt`)"), and ROPA §3.4 ("Evidence that ... the
  minimum-age declaration was made", with Art. 7(1)/Art. 24 accountability named as the reason it is
  recorded at all). For every Google account that record is false. Underneath the paperwork the
  substantive gap is the same one: the Google path has no age gate. The code comment at
  `ExternalLoginService.cs:110-116` is candid that this consent is weaker than the register form's,
  but it points at a line of text covering terms and privacy — the age half is the one piece that
  line does not carry.
- Fix: add the age clause to that one string so the existing stamp becomes true — make
  `googleConsent` read "By continuing you confirm you are at least 16 years old and accept the
  <terms>…</terms> and <privacy>…</privacy>.", matching `ageConsent` at `ui/messages/en.json:89`.
  The alternative (leave `AgeConfirmedAt` null on that path) is honest but leaves a live signup route
  with no age gate at all, so prefer the string.

#### L-4-2 — No stated route for acting on a discovered under-16 account
- Severity: Medium
- Where: moderation exposes resolve, sanction, lift and name-scan only
  (`src/KiriChess.Server/Moderation/ModerationEndpoints.cs:25-208`); erasure is self-service and
  authenticated as the account holder (`src/KiriChess.Server/Account/AccountEndpoints.cs:47`). There
  is no operator-invoked deletion path.
- Impact: a declared-16 model is defensible only if there is an answer for the case it exists to
  catch — a parent, or the child, writes to `contact@kirichess.com`. Today the only in-product tool
  is an `AccountBan`, which by design keeps the account row, the email address and the sanction
  indefinitely (PRIVACY §5, §6). That is the opposite of what discovering an under-16 account calls
  for, and it is also the outcome the documents never describe: nothing in TERMS §2 or PRIVACY §9
  says what happens on notice.
- Fix: one sentence in TERMS §2 and PRIVACY §9 — on credible notice that an account holder is under
  16, the account is closed and anonymised. Until an admin-invoked erasure path exists, record in
  `docs/decisions/RETENTION.md` (not in the public policy) that the operator performs it directly
  against the database, so the promise has a named mechanism instead of an implied one.

#### L-4-3 — PRIVACY §9 gates "use", TERMS §2 gates "create an account"
- Severity: Low (trigger: anyone reading both documents side by side, which is the normal case since
  they are accepted together at signup)
- Where: `docs/legal/TERMS.md` §2 — "You must be at least 16 years old to **create an account**";
  `docs/legal/PRIVACY.md` §9 — "You must be at least **16 years old to use KiriChess**". Mirrored in
  the deployed copies at `ui/app/terms/page.tsx:62-71` and `ui/app/privacy/page.tsx:138`.
- Impact: the anonymous surface (landing, `/rules`, `/learn`, `/watch`, `/replay`, `/leaderboards`)
  collects nothing, and every path that creates data requires an account — including bot play, which
  redirects to `/login` (`ui/components/BotPlayPanel.tsx:24-28`). PRIVACY §9 as written bars a
  14-year-old from reading a public web page: unenforceable, not required by Art. 8, and it makes the
  stricter-sounding document the less accurate one.
- Fix: align §9 to TERMS §2's scope — "to create an account and play". One clause, edited in the
  Markdown and in `ui/app/privacy/page.tsx` by hand; `LegalDocumentDriftTests` guards the date and
  the section headings, not the sentence.

### Already covered

- The 16-checkbox / no-DOB decision is a standing position in `docs/audits/INDEX.md` and is not
  reopened. PRIVACY §9's reasoning for it — a self-declared DOB adds no assurance and creates a new
  field needing its own retention, export and erasure — is sound and stands.
- Version-string drift across the five copies is already guarded by `LegalDocumentDriftTests` and
  documented at `src/KiriChess.Server/Auth/TermsVersion.cs:5-18`.
- Re-consent on a Terms version bump is a known limit, booked in the roadmap and referenced from
  TERMS §1. Not re-reported.
- Whether a sanctioned account gets an appeal is **S-3**, not this packet.

### Action list

1. add the age clause to `googleConsent` (`ui/messages/en.json:77`) — L-4-1.
   Do this before Google sign-in is configured, not after.
2. TERMS §2 and PRIVACY §9 gain "what happens on credible notice";
   `docs/decisions/RETENTION.md` gains the mechanism — L-4-2.
3. PRIVACY §9 scope aligned to TERMS §2, in both the Markdown and the `.tsx`
   copy — L-4-3.
4. **Owner:** nothing here needs a lawyer on its own. If **L-8** buys counsel time anyway, the only
   question worth appending is whether a declared-16 gate with no verification suffices for a Belgian
   host of a free service. The answer is very likely yes, which is why it does not justify the hour
   by itself.

---

## L-5 — Illegal content and notices
Date: 2026-08-21
Packet: docs/audits/INDEX.md

### Verdict

For a free, non-commercial, one-person host whose only public user-authored field is a display name,
what is actually owed here is small, and most of it already exists: a contact address on every page,
a moderator scan and forced rename over that one field, a real sanctions record, and no automated
decision-making to explain. Two gaps are worth closing, both cheap. The in-product way to report a
person is reachable only from the end of a game you just played against them — so nobody can report
the display name they saw on the leaderboard. And TERMS, the document that governs conduct and
enforcement, names no address to complain to and never mentions unlawful content at all. Neither
needs a programme; between them they are one UI entry point and two paragraphs.

### On whether the DSA applies

Worth settling before the findings, because it decides how much of this matters.

The DSA binds "hosting services", a subset of information society services, defined as services
"normally provided for remuneration". KiriChess takes no money for play. It does take Stripe
donations (`ui/components/DonationTiers.tsx:36-87`), and CJEU case law reads remuneration broadly
enough — a service funded indirectly rather than by its users still qualified in *Papasavvas* — that
"we are free, so the DSA does not apply" is an argument, not a settled answer. The micro-enterprise
carve-out does not rescue it either: Art. 19 exempts small and micro enterprises from the *online
platform* obligations in Section 3, while notice-and-action (Art. 16) sits in Section 2 and applies
to hosting providers generally.

The proportionate conclusion is not to litigate applicability. It is to notice that everything Art.
16 actually asks of a host this size — a reachable electronic notice channel, notices handled in a
non-arbitrary way, the affected user told the reason — is either already built or is a paragraph
away. So satisfy it, cheaply, and stop thinking about it. Nothing in this packet recommends a
transparency report, a trusted-flagger programme, an out-of-court dispute body, or a statement-of-
reasons database submission; those are Section 3 and VLOP obligations and importing them would be
exactly the corporate theatre the packet warns against.

### What already works

- **A public contact address on every page.** `ui/components/Footer.tsx:26` renders a `mailto:` to
  `contact@kirichess.com` site-wide, including on anonymous pages. That is a functioning electronic
  notice channel today, reachable by someone with no account.
- **Proactive enforcement over the one public UGC field.** A moderator display-name scan with a
  problems-only filter, plus forced rename (`src/KiriChess.Server/Moderation/Content/ContentEndpoints.cs:113-160`).
  The blocklist is honest about being a speed bump rather than a filter
  (`src/KiriChess.Server/Moderation/Content/ContentPolicy.cs:12-15`), and the scan is what backs it up.
- **The sanctioned player is told the reason.** `ui/components/SanctionBanner.tsx:36-45` surfaces the
  moderator's stated reason on the profile — including for a warning — while withholding the
  moderator note and the issuer. That is the substance of a statement of reasons, already shipped.
- **No automated decision-making.** ROPA §3.3 records it, and the code matches: analysers are
  advisory, a human issues every sanction (`src/KiriChess.Server/Moderation/SanctionService.cs:29-33`).
- **Reporter identity is protected.** A report filed against you is exported without the reporter's
  id or name (PRIVACY §7), so a notice cannot be used to unmask who filed it.

### Findings

#### L-5-1 — The only way to report a player is the game-over overlay
- Severity: Medium
- Where: `ui/components/ReportDialog.tsx` is imported from exactly one place —
  `ui/components/GameOverOverlay.tsx:7,178`. Nothing on `/profile`, `/leaderboards`, `/watch` or
  `/replay` can open it.
- Impact: display names are public on the leaderboards, on every game page and in every replay
  (TERMS §4 says so). A name that is illegal, hateful, or an impersonation is therefore visible to
  people who have no way to report it — anyone who has not just finished a game against that account.
  The reporting path is narrower than the exposure it exists to police. The server side is already
  ready for the wider case: `GameId` is nullable and a report with no game is accepted
  (`src/KiriChess.Server/Moderation/ReportDtos.cs:14`), and the game-ownership check only
  fires when a game id is supplied (`src/KiriChess.Server/Moderation/ReportEndpoints.cs:39-50`). This is a missing UI entry point,
  not a missing feature.
- Fix: add a report action to the profile page and the leaderboard row, opening the existing dialog
  with `gameId: null`. No backend change.

#### L-5-2 — TERMS names no contact address and no notice channel
- Severity: Medium
- Where: `docs/legal/TERMS.md` — the whole document. Its only pointer is §6's forward reference to
  the Privacy Policy for the moderation system. `privacy@kirichess.com` appears in PRIVACY §1 for
  data-protection requests; `contact@kirichess.com` appears only in the footer
  (`ui/components/Footer.tsx:26`), not in either legal document.
- Impact: two separate costs. A person who is not a player — the subject of an impersonating display
  name, someone who believes content here is unlawful — has to guess, and the address they most
  plausibly find is the one scoped to GDPR requests. And reports through the product require an
  account (`src/KiriChess.Server/Moderation/ReportEndpoints.cs:75`, `.RequireAuthorization()`), which
  is a reasonable anti-spam trade *only* if the open channel is documented somewhere the affected
  person will look. Right now the trade is made but the other half is not written down.
- Fix: one paragraph in TERMS, and the same sentence on `/about`: to report content or conduct on
  this site, including anything you believe to be unlawful, write to `contact@kirichess.com`; notices
  are read by the operator, there is no automated decision-making, and you will get a reply. Naming
  the mailbox in the Terms is what turns the footer link into a stated channel.

#### L-5-3 — TERMS prohibits cheating and bad names, but never unlawful conduct
- Severity: Low (trigger: the first enforcement action against something real that is not
  engine assistance, sandbagging, disconnection, or a blocklisted name)
- Where: `docs/legal/TERMS.md` §4 (display names), §5 (three fair-play prohibitions), §8
  ("We may suspend or terminate an account for violating these Terms").
- Impact: §8's power to terminate is scoped to violations of the Terms, and the Terms enumerate a
  short, chess-specific list. An account used for something plainly illegal but unlisted has no
  written ground for action — the operator would either act without a stated basis or hesitate over
  one. Also relevant to L-4: the age rule in §2 is stated as a condition but is not listed among the
  things that get an account closed.
- Fix: one clause in §5, or a short §5b: you may not use the service for unlawful purposes, to
  harass another person, or to impersonate anyone; accounts doing so may be suspended or terminated
  under §8. Keep it to a sentence — a longer acceptable-use policy would claim enforcement capacity
  that one person does not have, which is the failure mode L-11 exists to catch.

#### L-5-4 — What happens after a report is not stated anywhere the reporter can read
- Severity: Low (trigger: the first report where the reporter follows up asking what happened)
- Where: the dialog tells the reporter the report was stored and nothing further
  (`ui/components/ReportDialog.tsx:26-28`); `/api/reports/mine` returns a status but no reasoning
  (`src/KiriChess.Server/Moderation/ReportEndpoints.cs:86-100`, and the DTO comment at
  `ReportDtos.cs:18` — "There is no DTO for reports filed against you"). TERMS §6 describes sanctions
  but not the handling of the notice that produced one.
- Impact: withholding the outcome from the reporter is a deliberate and correct choice — telling a
  reporter what happened tells a cheater they were caught. But the *absence of any statement* reads
  as an unanswered report rather than a considered policy, and it is the one thing a notice-and-
  action posture asks be written down.
- Fix: two sentences in TERMS §6: reports are read by the operator, typically within a few days;
  outcomes are not disclosed to the reporter, because doing so would tell a suspected cheat they were
  detected; a player who is sanctioned is shown the reason on their profile. That last clause is
  already true (`ui/components/SanctionBanner.tsx`) and costs nothing to claim.
- Note: whether a sanctioned player gets an **appeal** is **S-3**'s question, not this one. Do not
  answer it here.

### Already covered

- The one-person moderation team and the false-positive cost of acting on thin signals are **S-2**'s
  subject; the appeal path is **S-3**'s. Both are named here only where a document should mention
  them, never re-derived.
- Whether TERMS §5 overclaims what the cheat detectors can do is **L-11**. This packet touches §5
  only to note it has no general unlawful-conduct clause.
- Display-name policy mechanics (normalisation, blocklist, cooldown) are Phase 8a.4 and are working
  as documented; not re-reported.

### Action list

1. open the existing `ReportDialog` from the profile page and the leaderboard row
   with `gameId: null` — L-5-1. Highest value per line in this packet.
2. TERMS gains a notice channel naming `contact@kirichess.com`, mirrored on
   `/about` and in `ui/app/terms/page.tsx` — L-5-2.
3. TERMS gains an unlawful-conduct / harassment / impersonation prohibition —
   L-5-3.
4. TERMS §6 states how a report is handled and why the outcome is not
   disclosed — L-5-4.
5. **Owner:** no lawyer needed for any of the above. The one judgement call worth holding is the
   DSA-applicability question in the section above — do not spend money resolving it while the
   cheaper answer is to satisfy Art. 16 with four paragraphs and a button.

---

## L-6 — ePrivacy, cookies, tracking
Date: 2026-08-21
Packet: docs/audits/INDEX.md

### Verdict

The headline claim survives a close reading: `DailyCounters` really is cookieless, IP-less, UA-less
and account-less, and the code is shaped to keep it that way rather than merely happening to be that
way today. No third-party font, image, or analytics script loads anywhere on the site. A consent
banner is correctly absent. The defects are elsewhere and all point the same direction — the
documents describe an earlier, smaller system than the one that now ships. PRIVACY §4 and ROPA §3.6
promise Sentry gets no tracing while the backend registers an OTLP trace exporter to Sentry; web
push and game-notification email do not appear in either document at all; and while the *consent*
duty for device storage is genuinely satisfied by the strictly-necessary exemption, the *information*
duty that sits alongside it is unmet, because nothing anywhere tells a visitor what this site puts on
their device.

### What is verifiably clean

Recorded because these are the claims the packet exists to test, and they hold:

- **The counter endpoint cannot become a tracker by accident.** The write handler takes a body and a
  `DbContext` and nothing else — no `HttpContext`, no `ClaimsPrincipal`, no headers
  (`src/KiriChess.Server/Analytics/CounterEndpoints.cs:58-75`), and the closing comment
  (`CounterEndpoints.cs:141-144`) makes that a reviewable invariant rather than an accident. The row
  is (date, key, source, count) with no surrogate id
  (`src/KiriChess.Server/Data/Entities/DailyCounter.cs:5`).
- **The campaign label cannot leak a URL.** `resolveCampaignSource` (`ui/lib/api.ts:676-684`) reads
  only `?src=`, only from the page's own query string, and only against a closed allowlist —
  anything else becomes `"direct"`. `document.referrer` is never read anywhere in `ui/`.
- **No dynamic route is ever counted.** `PAGE_KEYS` (`ui/lib/analytics.ts:28-37`) is an exact-match
  map, so `/game/<id>`, `/replay/<id>`, `/profile`, `/challenge/<token>` and `/moderation/*` report
  nothing (`ui/components/CounterBeacon.tsx:17-25`).
- **Nothing third-party renders on a public page.** `font-src 'self'`, `img-src 'self' data:`, and a
  `connect-src` assembled only from configured origins (`ui/next.config.ts:64-110`). Every optional
  integration fails closed to "not present" rather than "present and disabled".
- **The frontend Sentry claim is true.** `tracesSampleRate: 0`, `sendDefaultPii: false`, and expected
  API errors dropped in `beforeSend` (`ui/instrumentation-client.ts:12-23`).
- **Rate-limit IP keys stay out of the database**, matching PRIVACY §2 and §5.

### Findings

#### L-6-1 — "No tracing" is contradicted by an OTLP trace exporter to Sentry
- Severity: Medium (dormant: gated on `Sentry:Dsn`, unset in every environment today —
  `src/KiriChess.Server/Program.cs:88`. The trigger is configuring a DSN, which is a launch item.)
- Where: `src/KiriChess.Server/Program.cs:100` (`o.UseOtlp()`) and
  `src/KiriChess.Server/Program.cs:605-610` (`otelBuilder.WithTracing(t => t.AddAspNetCoreInstrumentation().AddSentryOtlpExporter(sentryDsn))`),
  against PRIVACY §4's Sentry row ("report errors only, no performance tracing, profiling or session
  replay") and ROPA §3.6 Recipients ("Sentry is configured `sendDefaultPii: false`, errors only, no
  tracing, profiling or session replay").
- Impact: Phase 9e.7 deliberately routes distributed traces to Sentry over OTLP rather than through
  Sentry's own span creation — the comment at `Program.cs:97-102` explains exactly that. The result
  is that spans from `AddAspNetCoreInstrumentation` go to a US processor: request paths, which on
  this app carry game ids and user ids, plus timings and status codes. That is more than errors, and
  two published documents say it does not happen. The claim is currently true only because the DSN is
  unset, and it becomes false the moment the project is created — silently, with no test failing.
- Fix: change both sentences rather than the code — 9e.7 wanted the traces. State that Sentry
  receives error events and request traces, that traces carry request paths and timings but no
  request bodies and no email addresses, and keep the `sendDefaultPii: false` and no-replay claims,
  which remain accurate.

#### L-6-2 — Web push and game-notification email appear in no legal document
- Severity: Medium
- Where: `src/KiriChess.Server/Data/Entities/WebPushSubscription.cs:12-27` stores, per browser, a
  push-service endpoint URL plus `P256dh` and `Auth` keys, all keyed to `UserId`;
  `src/KiriChess.Server/Infrastructure/Push/` POSTs to whatever push service that endpoint names.
  `src/KiriChess.Server/Infrastructure/Email/EmailTemplates.cs:56,74` sends `PairingFound` and
  `RematchOffered`. Neither appears in PRIVACY §2 (what is collected), §4 (who it is shared with) or
  §5 (retention), nor in any of ROPA's seven §3 activities, nor in ROPA §4's transfer list.
- Impact: three separate gaps. A push endpoint URL is a per-browser identifier tied to an account —
  personal data, held with no stated retention. The push service (Google FCM, Mozilla, Apple,
  whichever the browser chose) is a recipient in a third country that appears in no transfer
  analysis, and unlike the six named providers it is not one the operator contracts with at all,
  which is itself the disclosure worth making. And PRIVACY §4's Resend row limits itself to "account
  confirmation, password reset", while two game-event mail categories now ship. Those two are
  service messages about a game the user is in, not direct marketing, so opt-out
  (`src/KiriChess.Server/Data/Entities/ApplicationUser.cs:63-67`: null means subscribed, opt-out is
  timestamped) is the right default under ePrivacy Art. 13 — the defect is the silence, not the
  default.
- Fix: one row each in PRIVACY §2 and §4, one retention line in §5, and one new activity in ROPA §3
  covering both push and game-notification mail. Say plainly that a push subscription is deleted when
  the user turns push off or deletes the account, and confirm that erasure removes those rows.

#### L-6-3 — Nothing tells a visitor what is stored on their device
- Severity: Medium
- Where: the site stores, in a visitor's browser: access and refresh tokens in `localStorage`
  (`ui/components/AuthProvider.tsx:113-175`); a tutorial-completed flag
  (`ui/app/learn/page.tsx:73`); a service-worker shell in Cache Storage
  (`ui/public/sw.js:12-22`); a `kirichess.external` cookie and an OAuth correlation cookie on the API
  host during Google sign-in (`src/KiriChess.Server/Program.cs:326-352`); plus whatever the
  Cloudflare proxy and the Turnstile widget set. PRIVACY mentions device storage exactly once — in
  the `DailyCounters` row, to say the counters use none of it.
- Impact: ePrivacy Art. 5(3), as transposed in Belgium, exempts strictly-necessary storage from the
  *consent* requirement; it does not exempt it from the *information* requirement. Everything listed
  above is plausibly strictly necessary — session tokens, an OAuth correlation cookie, an offline
  shell — with the tutorial flag as the one arguable item, since a remembered preference is
  functional rather than essential. So the conclusion is favourable: **no banner is needed, and its
  absence is correct**. But the disclosure that justifies that absence does not exist, which means a
  visitor asking "does this site use cookies" finds no answer, and the strongest privacy claim this
  project can honestly make goes unmade.
- Fix: a short §2b in PRIVACY (mirrored into `ui/app/privacy/page.tsx`) listing the five items above
  in one sentence each, ending with: all of these are required for the site to work or for a sign-in
  you started, which is why there is no cookie banner. This is a page of prose that converts a
  compliance gap into the site's best privacy paragraph.

#### L-6-4 — Stripe receives visitors but appears in no processor or recipient table
- Severity: Low for this packet (the ePrivacy half is genuinely clean); the disclosure half belongs
  to **L-3**
- Where: `ui/components/DonationTiers.tsx:36-87` ships ten live `buy.stripe.com` links;
  `src/KiriChess.Server/Account/StripeWebhookEndpoints.cs:23` accepts anonymous webhooks. Stripe
  appears nowhere in PRIVACY §4, ROPA §3, or ROPA §4.
- Impact: on the ePrivacy question specifically the answer is reassuring — the CSP admits no Stripe
  script origin and no Stripe `connect-src` (`ui/next.config.ts:64-110`), so Stripe executes nothing
  and stores nothing in the browser while the visitor is on kirichess.com. The visitor then navigates
  to Stripe, which is their own request to a different controller. What is owed is therefore
  disclosure, not consent: PRIVACY §4 should name Stripe and say what it receives.
- Fix: hand the §4 and ROPA rows to **L-3** with the file references above. **Separately, flag for
  L-7 and $1:** five of those ten links are `stripeMonthly` recurring subscriptions
  (`DonationTiers.tsx:47,57,67,77,87`), so the recurring-patronage path the INDEX standing position
  files under "Patreon later, waits on traction" has already shipped. That is a fact for those
  packets to weigh, not a position this packet reverses.

#### L-6-5 — Turnstile is disclosed only as part of a CDN row
- Severity: Low (trigger: a visitor or a supervisory authority asking what the login page loads)
- Where: PRIVACY §4's Cloudflare row covers "DNS, proxy and CDN in front of both hosts, plus the bot
  check at signup and mail routing". The bot check is not a proxy function: it is a third-party
  script and iframe from `https://challenges.cloudflare.com` that executes in the visitor's browser
  on the login and register page (`ui/next.config.ts:57-62`, `script-src` and `frame-src`), plus a
  call to a separate siteverify Worker.
- Impact: whether that script stores anything on the device is Cloudflare's to state and not
  something this repository can assert either way — which is exactly the reason to name it explicitly
  rather than fold it into a line about DNS and CDN. A reader of §4 today would not learn that a
  third-party script runs on the signup page.
- Fix: name Turnstile in the §2b storage paragraph from L-6-3 and give it its own clause in the §4
  Cloudflare row, linking to Cloudflare's own statement about what the widget stores rather than
  paraphrasing it.

### Already covered

- The `localStorage` token decision itself is `docs/decisions/AUTH.md` §1.6/§1.7 and a standing
  position; none of its four reopening triggers has fired, and L-6-3 asks only that the storage be
  *disclosed*, not moved. Not a re-proposal of the cookie move.
- CSP composition, `'unsafe-inline'`, and what the policy does and does not stop are AUTH.md §1 and
  **S-1**. Referenced here only as evidence about third-party origins.
- Anonymous-counter design rationale (why views can never mean people) is Phase 8a.5 / AD.6 and is
  restated on the admin page at `ui/app/analytics/page.tsx:646`. Confirmed, not re-derived.

### Out of packet, handed on

While reading PRIVACY for this packet, two sentences look stale against §4's own table and should go
to **L-1** rather than be fixed here: §5's dormant-accounts paragraph says "no email has ever been
delivered by this system to anyone (§4)", and §7 says transactional email "is not currently capable
of delivering one reliably (see §4)". §4 now says Resend is **Live** with a key configured in
production and a DKIM/SPF/DMARC-verified sending domain (ROPA §3.5 agrees). Both dependent
conclusions — no dormant-account warning mail, no emailed export link — may still be the right
policy, but they now rest on a premise the same document contradicts.

### Action list

1. correct the Sentry claim in PRIVACY §4 and ROPA §3.6 to include OTLP
   traces and what they carry — L-6-1. Do this before a Sentry DSN is configured.
2. disclose web push and game-notification mail —
   L-6-2.
3. PRIVACY §2b, "what is stored on your device", in the Markdown and in
   `ui/app/privacy/page.tsx` — L-6-3, folding in the Turnstile mention from L-6-5.
4. **Hand to L-3:** Stripe as a §4 recipient and a ROPA activity — L-6-4.
5. **Hand to L-7 / $1:** recurring monthly donation links are already live on `/donate` — L-6-4.
6. **Hand to L-1:** the two stale "no email has ever been delivered" sentences in PRIVACY §5 and §7.
7. **Owner:** no banner, no consent-management platform, no vendor. Every item above is prose in
   files this repository already owns.

---

## L-7 — Donations vs remuneration
Date: 2026-08-21
Packet: docs/audits/INDEX.md

### Verdict

The packet asks us to flag only if the site starts looking like a paid service. It does, and the
document that predicted it says so in its own words. [ROADMAP.md](../ROADMAP.md) FS.4 draws the line
at "recurring paid tiers are not a grey edge at all" and books the consequence: if FS.4 ever ships,
PRIVACY §1 gains a geographic address. FS.4 is unticked, but the code shipped it anyway. `/donate`
carries five named tiers with a one-off/monthly toggle and ten live Stripe payment links, five of
them recurring subscriptions, plus a patron badge promised in the copy and an `IsPatron` column in
the database. Meanwhile no legal document on the site mentions donations, Stripe, or patron status:
a grep of `docs/legal/` for "stripe", "donat", "patron" or "remunerat" returns nothing, and
[TERMS.md](../legal/TERMS.md) §7 still calls the service "non-commercial".

Nothing here says the characterisation is wrong. A voluntary payment that unlocks nothing is
patronage, and the site's substance still supports that: play is free, no tier gates anything, and
(see L-7-4) the badge is not actually deliverable, so no counter-performance is supplied at all. The
problem is that the paperwork was written for a site that takes no money, and the site takes money.
Three cheap edits close most of it. One of them, the address, costs real money and is the owner's
call.

The whole set is trigger-based on the same event: the Cloudflare Access gate coming off at DH.3.
Today the payment links are reachable by the owner only. On launch day they are reachable by
anyone, and every finding below turns live at the same instant.

### Findings

#### L-7-1 — Recurring tiers already ship, so FS.4's address trigger has already fired
- Severity: High (trigger: DH.3, the day the site is publicly reachable)
- Where: `ui/components/DonationTiers.tsx:47,57,67,77,87` (five `stripeMonthly` subscription links),
  `ui/components/DonationTiers.tsx:166-177` (the monthly toggle), against
  `docs/ROADMAP.md:2118-2125` and `docs/legal/PRIVACY.md:21-25`
- Impact: FS.4 reasoned this through already and reached a conclusion the code has now overtaken.
  A free, ad-free site sits outside the e-Commerce transparency duties (Belgium: Book XII WER),
  which are scoped to services "normally provided for remuneration"; one-off donations are the grey
  edge; recurring subscriptions to named tiers are what FS.4 itself calls not a grey edge at all.
  PRIVACY §1 states "If KiriChess ever takes money for anything ... this section gains a geographic
  address at that point." That point is past. The document is now making a conditional promise whose
  condition it has already met, which is worse than having said nothing, because it demonstrates the
  author knew the test and did not apply it.
- Overlap: **L-1-5** (written the same day) reports the same trigger from the document-fidelity side
  and owns the PRIVACY §1 edit. What is left to this packet, and is not in that report, is the
  decision below: whether the recurring tiers should exist at all, given that removing them is the
  only fix that costs nothing.
- Fix: pick one before DH.3. Either (a) remove the monthly toggle and the five subscription links,
  leaving one-off donations only, which puts the site back on the side of the line FS.3 occupied and
  costs nothing; or (b) keep recurring tiers and budget a business or domiciliation address for
  PRIVACY §1, remembering FS.4's own note that a Belgian ASBL or BV publishes its seat in the KBO and
  therefore supplies a name to stand behind, not privacy. Do not leave it as (c), shipped and
  undocumented.

#### L-7-2 — No legal document mentions donations, Stripe, or the patron badge, and TERMS §7 says the opposite
- Severity: High (trigger: DH.3)
- Where: `docs/legal/TERMS.md:66` ("KiriChess is a free, **non-commercial**, independently run
  project"); `docs/legal/TERMS.md` has no donations section at all; grepping `docs/legal/*.md` for
  "stripe", "donat", "patron" or "remunerat" returns nothing
- Impact: The one sentence that most needs to exist for the patronage characterisation to hold, that
  donations are voluntary, buy nothing, and are not payment for the service, is not written anywhere.
  TERMS instead asserts "non-commercial" while the navbar and footer both link a page with ten live
  payment links. If the characterisation is ever questioned, the site's own terms are the first
  exhibit against it. This is the same overclaim shape L-11 hunts in the fair-play section, pointed
  the other way: here the document undersells what the code does.
- Fix: add a short TERMS section (a §7a is enough) saying donations are voluntary, unlock nothing,
  are not a purchase of the service or of any feature, that a patron badge if any is a token of
  thanks rather than a bought benefit, and how a donor reaches a human about a payment. Adjust §7's
  "non-commercial" to "free to play, funded by voluntary donations", which is both true and still the
  claim the project wants to make.

#### L-7-3 — Stripe missing from PRIVACY §4, ROPA §4 and the DPA checklist (reported elsewhere, cross-referenced here)
- Severity: High, and **not this packet's finding to make**
- Where: reported as **L-1-5** (`docs/audits/L.md:117-139`) and **L-3-2**
  (`docs/audits/L.md:78-97`), both written 2026-08-21 alongside this one
- Impact: recorded here only because the L-7 argument depends on it. The characterisation in L-7-1
  and L-7-2 cannot be defended from documents that do not mention the payment processor, the
  donation activity, or the `IsPatron` column it writes. Those two reports own the fix and reach it
  from the fidelity and processor angles respectively; nothing is added by a third copy.
- Fix: see L-1-5 and L-3-2. This packet's only ask is that the Stripe rows land in the same version
  bump as L-7-2's TERMS paragraph, so a reader is not told donations are undocumented in one place
  and voluntary-and-buy-nothing in another.

#### L-7-4 — The badge the page promises cannot be granted, which is lucky, because it was also the only thing resembling consideration
- Severity: Medium
- Where: `ui/components/DonationTiers.tsx:36,46-88` (no payment link carries a
  `client_reference_id` parameter) against `src/KiriChess.Server/Account/StripeWebhookEndpoints.cs:51`
  (which requires exactly that parameter to identify the donor); `ui/messages/en.json:1050`
  ("Patreons do get a badge"); no component under `ui/` renders `IsPatron` anywhere
- Impact: Two separate breaks in the same chain. The missing `client_reference_id` is also reported
  by **L-1-5** and **L-3-2**; the second break is not, and it matters as much. Even if the webhook
  fired, nothing in `ui/` displays patron status, so the page promises a benefit that no donor can
  receive by either route. Legally this helps: with no counter-performance supplied, the
  patronage reading in L-7-1 is stronger and the Belgian VAT question (a supply for consideration
  needs a direct link between the payment and something received) barely gets off the ground.
  Commercially it is a broken promise on a page asking for money. Note also that no subscription
  lifecycle event is handled, so had the badge worked, cancelling a monthly tier would never have
  removed it.
- Fix: decide which way to resolve it before DH.3 rather than after. Dropping the badge sentence is
  one line and keeps the legal position at its simplest. Making the badge real means threading
  `client_reference_id` through the payment links for signed-in donors, handling
  `customer.subscription.deleted`, and re-reading FS.6 on cosmetics. `$1` owns the payment path and
  `$2` owns the pay-to-win line; this entry exists here only because the answer changes the
  remuneration analysis above.

#### L-7-5 — "Non-profit" is claimed on the home page and the donate page, and there is no non-profit
- Severity: Medium (trigger: DH.3)
- Where: `ui/messages/en.json:21` (site tagline: "Independent, non-profit, and free for everyone"),
  `ui/messages/en.json:1043,1045` (donate meta description and intro), against
  `docs/legal/PRIVACY.md:15-16` ("a private individual resident in Belgium ... There is no company
  behind it")
- Impact: The operator is a natural person. "Non-profit" next to a donate button reads as a statement
  about legal form, and in Belgium the recognised forms (VZW/ASBL) are exactly what a reader would
  take it to mean. The claim is not needed: everything true about the project is stronger stated
  plainly, and an unfounded status claim on a page collecting money is the kind of thing consumer
  protection rules are shaped around. The privacy policy already gets this right in its own voice.
- Fix: replace "non-profit" with what is actually true and checkable, for example "independent, free,
  and run at cost by one person". Same edit in the tagline and in the donate copy. If the owner wants
  the word kept, that is a question for L-12 counsel, not a repository edit.

#### L-7-6 — Donate copy names a platform the project deliberately has not adopted, and has a typo in the same paragraph
- Severity: Low
- Where: `ui/messages/en.json:1050` ("**Patreons** do get a badge"), `ui/messages/en.json:1047`
  ("infrastructure **forbetter** and faster performance")
- Impact: FS.4 is the parked Patreon item; the page that talks about money is the one place the word
  should not appear by accident, because a reader cannot tell a misspelling of "patrons" from a
  statement that Patreon is in use. The missing space is small but sits in the paragraph explaining
  where donations go, which is the paragraph a first-time donor actually reads.
- Fix: "Patrons", and "infrastructure for better". P-9 owns copy and claims generally; these two are
  in the exact sentences this packet had to read, so they are recorded rather than left for it.

### Already covered

- The address obligation, its legal basis, and the ASBL-does-not-buy-privacy point are already
  reasoned out at `docs/ROADMAP.md:2111-2125`. L-7-1 does not re-argue it; it reports that the
  trigger fired.
- That the shipped donation path is Stripe rather than Ko-fi, and why the custom-amount form has no
  local euro input, is recorded in FS.3's own 2026-08-16 annotations (`docs/ROADMAP.md:2085-2101`).
- Stripe's absence from PRIVACY, ROPA and the DPA checklist, the `IsPatron` fields as undocumented
  personal data, and the missing `client_reference_id` are all reported by **L-1-5**
  (`docs/audits/L.md:117-139`) and **L-3-2** (`docs/audits/L.md:78-97`), both written the same
  day. L-7-3 exists only as a pointer, and L-7-1 and L-7-4 keep only the parts those reports do not
  reach: whether the recurring tiers should exist, and that the badge has no renderer either.
- Payment-link mechanics, webhook failure modes and what Stripe stores are packet **$1**; the badge
  versus pairing and ratings line is **$2**; recurring patronage as a product decision is **$4** and
  is sealed. This packet touches all three only where the legal characterisation depends on them.
- Copy and claims across the site are packet **P-9**.

### Action list

1. **Owner, before DH.3, one decision that gates the rest:** keep recurring tiers and buy an address
   for PRIVACY §1, or drop the monthly toggle and stay one-off. (L-7-1)
2. the TERMS
   donations paragraph and the §7 "non-commercial" wording are this packet's contribution; the
   PRIVACY and ROPA Stripe and patron rows are L-1-5's and L-3-2's. One version bump, not three.
   (L-7-2, L-7-3)
3. "non-profit" out of the tagline and the donate copy;
   "Patreons" and "forbetter" fixed. (L-7-5, L-7-6)
4. **Owner decides, then implement:** drop the badge sentence, or make the badge real via
   `client_reference_id` and subscription-lifecycle handling. Dropping it is the smaller move and the
   safer legal position. (L-7-4)
5. **Owner, on the accountant's next hour, not from this repository:** how recurring donations to a natural
   person are treated for Belgian income tax, and whether the VAT small-business threshold is
   anywhere in view. Nothing in this repo can answer that, and nothing above assumes an answer.

---

## L-8 — Accessibility as a duty
Date: 2026-08-21
Packet: docs/audits/INDEX.md

### Verdict

**No. Do not spend the lawyer's hour here.** The packet asks one question, whether the public site
has a legal accessibility exposure worth counsel, and the answer is that two independent exemptions
each dispose of it on their own, before anyone has to reason about how good the site is.

The Web Accessibility Directive (2016/2102) binds public sector bodies. A private individual running
a free chess site is not one, and no reading stretches it. The European Accessibility Act (Directive
(EU) 2019/882, applicable since 28 June 2025 and transposed in Belgium) is the one that reaches
private operators, and it misses twice: its service scope is a closed list (electronic
communications, access to audiovisual media services, defined elements of passenger transport,
consumer banking, e-books, and e-commerce services), and a free-to-play chess site is none of them,
including e-commerce, which is defined around concluding a consumer contract for remuneration. Even
if a reading got past scope, Art. 4(5) exempts microenterprises from the service obligations
outright, and a one-person project with no turnover is a microenterprise by a wide margin. Two
independent misses is not a close call, and it is the reason this packet closes without a bill.

What is left is small, real, and does not need counsel: Belgium's anti-discrimination act of
10 May 2007 covers access to and supply of goods and services available to the public, free ones
included, and carries a reasonable-accommodation duty scaled to the operator's means. "Reasonable"
for a volunteer running a free service is met by effort of exactly the kind already spent, and this
project has spent unusually much of it: an `AccessibleBoard` with roving tabindex and spoken move
entry, a 213-line announcement layer, a reduced-motion block, and an axe WCAG A+AA gate that blocks
CI on ten public pages. The gaps that remain are not engineering (that is **P-5**, and 9f.2's NVDA
pass is queued as ROADMAP item #5); they are that none of this is *said* anywhere a user can find,
and that nobody who hits a barrier has a route that names accessibility.

One cross-link worth carrying forward: **L-7** found that live recurring Stripe tiers already ship.
That does not change the EAA answer, because the microenterprise exemption survives it and the
checkout itself runs on Stripe's own pages, but it does move the site nearer the e-commerce hook
than the standing "free forever" position implies. If the funding shape ever changes again, this
packet is the one to re-read, not the one to re-argue.

### Findings

#### L-8-1 — Nothing on the site says accessibility was considered, or where to report a barrier
- Severity: Low
- Where: `ui/app/` has no `accessibility` route (compare `ui/app/privacy/`, `ui/app/terms/`);
  `ui/components/Footer.tsx:26` is the only contact route and it is a generic `contact@kirichess.com`
- Impact: A screen-reader user who hits a wall has no way to tell whether the board is meant to work,
  whether the problem is known, or that the maintainer would want to hear about it. That is the whole
  substance of the reasonable-accommodation duty at this size: not a conformance level, a way in.
  It also means the considerable work already done is invisible to the one person it was done for,
  which is a product loss on top of the legal one.
- Fix: a short accessibility statement, either its own page or a section of `/about`. Four things and
  no more: what has been built (keyboard board, screen-reader announcements, reduced motion), what is
  known not to be verified yet (the NVDA pass, 9f.2), that the maintainer is one person and will fix
  what is reported, and an address to report to. Do not write a conformance claim into it (see
  L-8-3). The page can be written from the code; only the promise about response time is the owner's to set.

#### L-8-2 — The exemption reasoning exists nowhere, so it will be re-litigated every audit
- Severity: Low
- Where: no file under `docs/decisions/` covers accessibility; `docs/plans/a11y/NVDA-PASS.md:1-10`
  is a test script, not a position
- Impact: The analysis above took an hour and reaches "exempt twice over". Unrecorded, the next audit,
  a later pass, or a nervous reading of an EAA headline pays for it again, and the likeliest failure
  mode is not paying twice but over-correcting: someone treats a hobby site as if it owed a public
  sector conformance programme, which is exactly the shape `docs/audits/INDEX.md` warns against for
  the DSA in L-5.
- Fix: a short `docs/decisions/ACCESSIBILITY.md` recording the position (WAD is public sector; EAA
  scope excludes a free game and Art. 4(5) exempts microenterprises anyway; the residual Belgian
  anti-discrimination duty is met by effort proportionate to one person) and the triggers that reopen
  it: the project acquiring employees or turnover near the microenterprise ceiling, or KiriChess
  starting to sell anything. Written like `docs/decisions/AUTH.md` §1.7, whose named-trigger format is
  the pattern to copy.

#### L-8-3 — Any future statement must not inherit the axe suite's scope as a conformance claim
- Severity: Medium (trigger: the moment an accessibility statement or a WCAG claim is written)
- Where: `e2e/tests/accessibility.spec.ts:18` (`wcag2a`, `wcag2aa`, `wcag21a`, `wcag21aa`),
  `e2e/tests/accessibility.spec.ts:46-56` (the ten scanned pages), against
  `.claude/rules/testing.md`, which records that `game/[id]`, `watch/[id]` and `replay/[id]` are
  deliberately excluded because axe cannot evaluate whether a screen-reader user can actually play
- Impact: Today the exposure is zero, because no page anywhere claims conformance and I could find no
  "WCAG" string in any user-facing copy. That is the correct state and it should survive L-8-1. The
  risk is that whoever writes the statement reads the green CI badge as "the site is WCAG 2.1 AA" and
  says so. It would not be true: the live board, the single most important surface for this
  particular user, is outside the scan by design, and an unfounded conformance claim is a worse
  legal position than saying nothing, because it is a claim rather than a shortfall.
- Fix: when L-8-1's page is written, describe what is tested and what is not, in those words. No
  conformance level until the NVDA pass has actually been run and its result recorded.

#### L-8-4 — Two public pages sit outside the automated floor, including the one strangers are pointed at
- Severity: Low
- Where: `e2e/tests/accessibility.spec.ts:46-56` lists ten anonymous pages; `/about` and
  `/developers` are both anonymous, both in `ui/app/sitemap.ts`, and neither is in the list
- Impact: `/about` is the page CLAIMS.md nominates as the attribution destination and the one a
  catalogue entry or a journalist lands on; `/developers` is the bot API's front door. They are the
  two public pages most likely to be linked from outside and the two not held to the standard the
  other ten are. Also a small drift against `.claude/rules/testing.md`, which describes the suite as
  covering "every anonymous page".
- Fix: add both paths to `ANONYMOUS_PAGES`. If either fails, that is a **P-5** engineering item, not
  a legal one; this packet notes only that the floor does not currently reach them.

### Already covered

- The engineering work, the keyboard and screen-reader implementation, reduced motion and zoom, is
  packet **P-5** and readiness **A11Y**. Nothing above re-reports it.
- That the NVDA pass is written and unrun, and that a volunteer cannot run it because Cloudflare
  Access gates the site, is already ROADMAP item #5 (`docs/ROADMAP.md:48`) with the owner named. This
  packet adds only that the unrun state is a reason not to claim conformance, not a legal exposure of
  its own.
- Why the live board is outside axe, and what covers it instead, is `.claude/rules/testing.md`.

### Action list

1. write the accessibility statement (L-8-1), scoped honestly per L-8-3. Owner supplies
   only the sentence about how quickly a report gets answered.
2. record the exemption position and its triggers in `docs/decisions/ACCESSIBILITY.md`
   (L-8-2), so this packet does not get re-run from scratch.
3. add `/about` and `/developers` to the axe page list (L-8-4); route any resulting
   failure to P-5.
4. **Owner, unchanged and unrelated to this packet's verdict:** run the NVDA pass (ROADMAP #5). It is
   the thing that makes a conformance sentence possible later; it is not owed to anyone legally.
5. **No counsel.** L-12's hour is better spent on TERMS, PRIVACY, the donations question raised by
   **L-7**, and the name. Accessibility does not compete for it.

---

## L-9 — IP, trademark, attribution
Date: 2026-08-21
Packet: docs/audits/INDEX.md

### Verdict

The hard part is done and done well. [CLAIMS.md](../game/CLAIMS.md) starts from the correct premise,
that mechanics are not copyrightable and recognition is a documentation problem rather than a legal
one, and the licence architecture built on it is coherent: Apache-2.0 on the software, CC BY 4.0 on
the manual and `config/kirichess.rules.json`, the name and marks reserved out of both, GPL-3.0
carved out for the vendored engine, and the attribution string written once and repeated identically
in NOTICE, README, `docs/RULES.md`, `/rules` and `/about`. Four files agreeing on a three-way split
is not the normal state of a hobby repo.

The theme of what is wrong is narrower than the theme of what is right: **the project's attribution
story assumes a public repository that does not exist yet.** `gh repo view` reports
`MichielGekiere/kirichess` as `PRIVATE`, while TERMS §10 tells every user the source "is published"
at that URL and four user-facing surfaces link to it. CLAIMS.md is the document that has this right,
and the publication path it prescribes points at a script that is not in the repo. **L-1-7** owns
the half of that which is a false statement in a live document; what belongs here is the other half,
that every dated record CLAIMS.md nominates as the attribution anchor is still private, gated or
unsent. Everything else
below is smaller: assets nobody has recorded the provenance of (the sounds, and the font the site
serves), a press kit that ships the reserved mark with no permission to use it, and a citation file
that flattens the three-way split back to one licence.

Nothing here threatens the variant's origin claim. The dated public record the strategy rests on is
still ahead of the project, not behind it.

### Findings

#### L-9-1 — Every attribution timestamp the strategy names is private, unsent, or gated
- Severity: High (live today for anyone who can reach the site; unambiguous at DH.3)
- Where: `docs/legal/TERMS.md:92` ("The KiriChess source **is published** at
  https://github.com/MichielGekiere/kirichess"), rendered at `ui/app/terms/page.tsx:213-216`, and
  linked from `ui/components/Footer.tsx:28`, `ui/app/about/page.tsx:114` and
  `ui/app/developers/page.tsx:126`. `NOTICE:6` and `CITATION.cff:8` make the same claim. The
  repository's actual visibility is `PRIVATE` (`gh repo view MichielGekiere/kirichess`), which is
  also what `docs/game/CLAIMS.md:30` says it is.
- Overlap: **L-1-7** (`docs/audits/L.md:154-164`, written the same day) reports the TERMS §10 half
  from the document-fidelity side and owns that fix. What this packet adds is the IP consequence
  below, which that report does not cover.
- Impact: Apache-2.0 does not require publication, so nothing is breached by the repository being
  private; the fidelity problem is L-1-7's. The IP problem is that the project's recognition strategy
  is built on a dated public record and there is not one. `docs/game/CLAIMS.md:28` makes a public
  repository one of the candidate timestamps and its checkbox is unticked; `/about` (the surfacing
  that is ticked) sits behind the same Cloudflare Access gate as the rest of the site; the
  chessvariants submission is drafted and unsent. So every timestamp CLAIMS.md nominates is currently
  either private or unsent, which is the one thing that strategy cannot survive if the variant
  spreads before the record exists.
- Fix: get one real timestamp on the record, and it does not have to be the repository. Flipping the
  repo public (after **O-4**'s history-hygiene pass, which owns that question and has already
  resolved the commit-address half at `.mailmap:1-4`) closes this and L-1-7 at once. Failing that,
  send the chessvariants submission that is already drafted at
  `docs/plans/chessvariants/SUBMISSION-DRAFT.md`, or put a dated copy of `docs/RULES.md` somewhere
  citable. Any one of the three is enough; having none of them while the site prepares to launch is
  the state to avoid, and the reword-TERMS option (see L-1-7) fixes the honesty problem without
  fixing this one.

#### L-9-2 — The documented route to publishing the repo points at a script that does not exist
- Severity: Medium
- Where: `docs/game/CLAIMS.md:30` ("When source goes public, run
  `scripts/export-public-snapshot.ps1`"), against `git ls-files scripts/`, which lists only
  `discord-setup/`, `neon-watch.sh`, `offsite-backup.sh`, `restore-drill.sh` and
  `synthetic-play.sh`. No file matching that name is tracked anywhere in the repository.
- Impact: The one action item standing between the project and the timestamp it wants is documented
  as "run this script", and the script is gone. Whoever picks the task up next either writes it from
  scratch without knowing what the original did, or assumes publication is a one-click affair and
  pushes the working history directly, which is precisely the outcome the CLAIMS paragraph was
  written to prevent.
- Fix: decide which it is and say so in the same line. Either restore or rewrite the export script,
  or record that the decision changed and that the working history goes public as-is, in which case
  the `.mailmap:1-4` note already carries the reasoning and CLAIMS.md should simply point at it.
  **O-4** owns the hygiene sweep itself; this finding is only that the instruction is unfollowable.

#### L-9-3 — Ten shipped audio files have no recorded provenance, and NOTICE's default clause claims them
- Severity: Medium
- Where: `ui/public/sounds/` (`move.mp3`, `capture.mp3`, `castle.mp3`, `promote.mp3`,
  `game-start.mp3`, `game-end.mp3`, `GenericNotify.mp3`, plus `Victory.mp3`, `Draw.mp3` and
  `Defeat.mp3`, which are byte-identical 17-byte placeholders), against `NOTICE:12-16` ("Unless a
  path below says otherwise, this repository is licensed under the Apache License, Version 2.0").
  They entered in one bulk commit, `7273652`, alongside the vendored Stockfish build.
- Impact: NOTICE's default clause is an affirmative statement that these files are the project's to
  license under Apache-2.0. Nothing in the repository records where they came from, and the naming
  is not neutral: `GenericNotify.mp3`, `Victory.mp3`, `Draw.mp3` and `Defeat.mp3` follow one
  well-known chess site's sound-set convention exactly, and the lowercase hyphenated names follow
  another's. Arriving in the same commit as a deliberately vendored third-party binary makes
  first-party origin less likely, not more. This is a fork's problem as much as the owner's, since a
  fork inherits the claim along with the files, and it is the one finding in this packet that a
  rights-holder could act on.
- Fix: establish origin for the seven files with real audio. If they are third-party, record the source
  and licence in NOTICE the way `ui/public/stockfish.js` is recorded, or replace them with a set
  whose licence is documented (freesound CC0, or lichess's own sound directory, which ships its
  licences alongside). If they are first-party, say so in NOTICE explicitly rather than relying on the
  default clause. Delete the three 17-byte placeholders or fill them.
- Status (2026-09-04): **Closed.** The unlabeled set was replaced with Kenney Casino Audio 1.1 and
  Interface Sounds 1.0 (CC0 1.0), transcoded Ogg Vorbis to MP3. NOTICE carves the path out of
  Apache-2.0; mapping and Kenney's licence text live at `ui/public/sounds/KENNEY.LICENSE`. The three
  17-byte placeholders are gone.

#### L-9-4 — The press kit ships the reserved mark and grants no permission to use it
- Severity: Medium
- Where: `press-kit/logo/kirichess-mark.svg` and `kirichess-mark-512.png`, catalogued at
  `press-kit/README.md:15-24`, against `NOTICE:18-22` ("The name "KiriChess", the wordmark, and the
  marks in press-kit/logo/ ... are not licensed under Apache-2.0 or CC BY 4.0"). `press-kit/README.md`
  contains no licence, permission or usage section of any kind.
- Impact: A press kit exists for one reason, so a stranger writing about the project can reproduce the
  mark without asking. This one hands a journalist a folder whose only licence statement, one
  directory up, says the contents are not licensed. The careful reader stops; the careless one uses it
  anyway and the reservation is untested either way. The reservation itself is right and should stay;
  what is missing is the narrow grant that makes it usable, which is the normal shape (reserve the
  mark, permit editorial use).
- Fix: three sentences at the top of `press-kit/README.md`: the mark may be reproduced unmodified to
  illustrate coverage of KiriChess; it may not be altered, used as anyone else's mark, or used to
  imply endorsement or affiliation; the screenshots and pitch text are CC BY 4.0 or Apache-2.0 as the
  file table says. **M-2** owns the kit's contents and the clip that still needs recapturing; this is
  the licence line it lacks.

#### L-9-5 — CITATION.cff flattens the three-way licence split to Apache-2.0
- Severity: Low
- Where: `CITATION.cff:10` (`license: Apache-2.0`), against `NOTICE:24-44` (CC BY 4.0 on the manual
  and `config/kirichess.rules.json`) and `NOTICE:46-55` (GPL-3.0 on the vendored engine)
- Impact: CITATION.cff is the machine-readable file that citation tooling, Zenodo and GitHub's own
  "Cite this repository" widget read, and it is aimed at exactly the academic or catalogue audience
  CLAIMS.md is trying to reach. It currently tells that audience the whole repository is Apache-2.0,
  which is the one claim NOTICE spends forty lines contradicting. `date-released: "2026-07-21"` is
  also the first-commit date rather than a release, and there is no `version:`.
- Fix: CFF accepts a list, so `license: [Apache-2.0, CC-BY-4.0]` with the split explained in
  `abstract`. Set `date-released` when there is a release to name, or leave the invention date where
  it already reads correctly, in the abstract text.

#### L-9-6 — The site serves an OFL-licensed font with no notice
- Severity: Low
- Where: `ui/app/layout.tsx:2,12-15` (`Montserrat` via `next/font/google`, which downloads the font
  files at build time and self-hosts them from the app's own origin), against `NOTICE`, which lists
  no font
- Impact: Montserrat is licensed under the SIL Open Font License 1.1, which asks that the copyright
  notice and licence travel with the font files when they are distributed, and `next/font/google`
  turns a CDN reference into distribution by design. This is the mildest kind of licence gap, no
  copyleft reaches the application and OFL-1.1 imposes nothing on the code, but NOTICE's own default
  clause claims everything not carved out, and the font files served from `/_next/static/media/` are
  not carved out.
- Fix: one paragraph in NOTICE naming Montserrat, its designers, and OFL-1.1 with a link. No code
  change.

#### L-9-7 — NOTICE licenses the `/learn` page under CC BY; `/learn` does not say so
- Severity: Low
- Where: `NOTICE:30-32` (the CC BY list includes "the /rules and /learn pages on the KiriChess
  website"), against `ui/app/learn/page.tsx`, which contains no licence text (grep for "licen" and
  "creativecommons" returns zero), while `ui/app/rules/page.tsx:1590-1607` and
  `ui/app/about/page.tsx:92-107` both carry the block
- Impact: CC BY only works if the licence reaches the person doing the copying, and `/learn` is the
  page a newcomer reads and is most likely to lift from. Two of the three named surfaces carry the
  notice, which makes the third look like an oversight rather than a decision, and CLAIMS.md:16
  claims the surfacing is done on `/rules` and `/about` without mentioning `/learn` at all.
- Fix: reuse the existing `licenceH2` / `licenceBody` / `licenceAttribution` message keys at the foot
  of `/learn`, or drop `/learn` from NOTICE's list if the intent was only the manual proper. Either
  is fine; the two documents disagreeing is not.

### Already covered

- That mechanics are not copyrightable, that CC BY protects the manual's wording rather than the
  mechanic, and that EUIPO registration is optional and later: `docs/game/CLAIMS.md:7-17`. Not
  re-argued.
- Personal email addresses in commit history are a known, deliberately declined decision recorded in
  `.mailmap:1-4`, and belong to **O-4** (public-repo hygiene) and **B-2** (maintainer exposure).
- TERMS §10 stating the source is published while the repository is private is **L-1-7**
  (`docs/audits/L.md:154-164`). L-9-1 cites the same fact for a different consequence and does not
  restate the fix.
- The four-file licence stack as a consistency check is packet **O-1**; transitive copyleft in
  dependencies is **O-6** and **L-10**, which found nothing that taints a fork.
- The press kit's missing clip and screenshot recapture are **M-2**.

### Action list

1. **Owner, one decision:** land at least one dated public record, publishing the repository being
   the option that also closes **L-1-7**. Rewording TERMS is the honesty fix, not the attribution
   fix. (L-9-1)
2. **Depending on the answer to 1:** restore, rewrite, or retire the export-snapshot
   instruction in CLAIMS.md. (L-9-2)
3. **~~Owner recall / L-9-3~~** — Closed 2026-09-04: set replaced with documented Kenney CC0.
4. press-kit mark grant (L-9-4); CITATION.cff licence list (L-9-5);
   Montserrat paragraph in NOTICE (L-9-6); `/learn` licence block or NOTICE correction (L-9-7).
5. **Not now:** EUIPO registration stays where CLAIMS.md put it, optional and later. Nothing found
   here moves it forward.

---

## L-10 — Stockfish / GPL isolation
Date: 2026-08-21
Packet: docs/audits/INDEX.md

### Verdict

**The isolation holds, and it holds for the right reason rather than by luck.** `ui/lib/bot.ts:13`
reaches the engine as `new Worker("/stockfish.js")`, a runtime URL fetched from the origin, not an
import. No bundler ever ingests the file: `ui/eslint.config.mjs:22` excludes `public/**`, Next copies
`public/` verbatim (`Dockerfile.frontend:44`), and the CSP names `worker-src 'self'`
(`ui/next.config.ts:92-94`) precisely so a future stricter `script-src` cannot silently take the
worker down. The two programs communicate over `postMessage` with UCI text. That is a separate
program by every test the FSF applies, and the carve-out is already written down in three places
that agree: `NOTICE:46-55`, `ui/public/STOCKFISH.LICENSE`, and `README.md:46`. Nothing is
relicensed, and no argument that KiriChess is a derivative work of Stockfish survives contact with
the code.

What is thinner is the other half of GPLv3, the part that applies not because the code is mixed but
because the object code is **conveyed**. Every visitor who opens a bot game downloads a 1.5 MB
compiled GPL program from kirichess.com. That is distribution, and §6 attaches: a copy of the
licence has to accompany it, and there have to be clear directions to the Corresponding Source for
*this* build. The repo has neither exactly. It links to gnu.org instead of shipping the licence
text, and it names the upstream project without recording which build, so "the Corresponding
Source" is not actually identifiable from what is written. Both are twenty-minute fixes and neither
threatens anything; they are the difference between a compliance story that is true and one that is
also demonstrable.

The copyleft hunt found nothing that would taint a fork. 626 npm packages across `ui/` and `e2e/`
yield only MPL-2.0 build and test tooling and an LGPL-3.0 image library inside prebuilt binaries
that never reaches a published artifact; the .NET side is MIT, Apache-2.0, PostgreSQL and BSD, with
one package whose licence the repository simply does not record.

### Findings

#### L-10-1 — No copy of the GPL-3.0 text accompanies the conveyed binary
- Severity: Medium
- Where: `ui/public/STOCKFISH.LICENSE:16` links `https://www.gnu.org/licenses/gpl-3.0.html`; no
  GPL-3.0 text exists anywhere in the repository (`git ls-files` has `LICENSE`, Apache-2.0, only)
- Impact: GPLv3 §6 conveys object code under §§4 and 5, and §4 requires giving every recipient a copy
  of the licence along with the program. A hyperlink to a third-party server is not a copy, and it is
  the one part of the arrangement that stops working exactly when it matters, when the site is gone
  or the URL has moved. The cost of the current state is not litigation risk, it is that a fork
  cloning this repo inherits a GPL binary with no licence text next to it and no way to know that was
  an omission rather than a decision.
- Fix: vendor the licence as `ui/public/GPL-3.0.txt` (it ships to the same origin as the binary, so
  the pairing is automatic) and reference it by relative path from `STOCKFISH.LICENSE` and from
  `NOTICE:46-55`, keeping the gnu.org URL as a secondary link.

#### L-10-2 — The Corresponding Source directions do not identify this build
- Severity: Medium
- Where: `ui/public/STOCKFISH.LICENSE:9-12` names `github.com/ddugovic/Stockfish` and
  `github.com/niklasf/stockfish.js` with no version, tag, commit or hash; `NOTICE:50-55` repeats the
  same two links. The shipped file is 1,579,996 bytes, sha256
  `335d46ddb3926e1288dcea0b63cfb6348f45c8106188522aab5394fd03e51787`, and carries no greppable
  version string, its UCI `id name` living inside the compiled asm.js memory blob.
- Impact: §6(d) is the clause this arrangement relies on, offering the object code from a place and
  offering equivalent access to the Corresponding Source, with clear directions next to the object
  code when the source lives elsewhere. "Somewhere in this repository's history" is not clear
  directions. Upstream has moved on several times since this file was committed, so a recipient
  following the link today lands on a different program than the one they were served, which is the
  practical failure the clause exists to prevent. It is also an ordinary supply-chain gap: nobody can
  currently answer "which Stockfish is this, and does it have a known CVE" without reverse-
  engineering the blob.
- Fix: identify the upstream release (the compiled bundle's own header comment at
  `ui/public/stockfish.js:1-14` names the author and project; the release can be matched by size and
  hash against the `niklasf/stockfish.js` releases) and record the release tag or commit plus the
  sha256 in `STOCKFISH.LICENSE` and `NOTICE`. If it cannot be identified, replacing it with a
  currently-released build and recording that one is cheaper than continuing to guess, and it drags
  the answer to **I-11**'s dependency question along with it.

#### L-10-3 — Nothing user-facing points at the licence notice
- Severity: Low
- Where: `ui/public/STOCKFISH.LICENSE` is served at `https://kirichess.com/STOCKFISH.LICENSE` (it is
  in `public/`, copied at `Dockerfile.frontend:44`), but no page links it: `ui/app/about/page.tsx`
  and `ui/components/BotPlayPanel.tsx` mention neither Stockfish nor the licence, and
  `docs/legal/TERMS.md:87-97` does not either
- Impact: The file being reachable by URL satisfies the letter of "next to the object code" about as
  well as anything can, since it sits in the same directory the binary is served from. But a
  recipient has to already know the file exists to find it, and the person actually receiving the GPL
  program, someone clicking "play a bot", is given no signal at all that they just downloaded one.
  This is the cheapest of the three compliance items and the most visible.
- Fix: one line in the bot panel or the `/about` licence section: the computer opponent is Stockfish,
  a separate GPL-3.0 program, with a link to `/STOCKFISH.LICENSE`. It also happens to be a good
  answer to the fair-play question of what the bot actually is.

#### L-10-4 — TERMS §10 states the software is Apache-2.0 with no GPL carve-out
- Severity: Low
- Where: `docs/legal/TERMS.md:91-94` ("The software is licensed under the Apache License 2.0; the
  rules manual and `config/kirichess.rules.json` are licensed under CC BY 4.0. The name ... not
  licensed"), against `NOTICE:46-55`, which carves out `ui/public/stockfish.js` as GPL-3.0
- Impact: TERMS §10 exists to tell a reader what they may do with the source, and it lists two of the
  three licences. It does defer to LICENSE and NOTICE in its last sentence, so a careful reader gets
  there, but the summary as written would let a fork conclude the whole tree is Apache-2.0, which is
  the exact misconception this packet is about. `ui/package.json:5` (`"license": "Apache-2.0"`) has
  the same shape, though it is `private: true` and never published to npm, so nothing acts on it.
- Fix: add a clause to the TERMS sentence naming the vendored engine as GPL-3.0 and pointing at
  NOTICE. **L-1** owns legal-document fidelity generally; this is the Stockfish-specific instance and
  it is one sentence.

#### L-10-5 — Copyleft hunt: nothing taints a fork, but one .NET package's licence is unrecorded
- Severity: Low
- Where: 626 packages scanned across `ui/node_modules` and `e2e/node_modules`. Non-permissive hits:
  `axe-core` and `@axe-core/playwright` (MPL-2.0, `e2e/` test-only), `lightningcss` (MPL-2.0, a
  Tailwind build-time dependency, never shipped), and `@img/sharp-*` prebuilds (Apache-2.0 AND
  LGPL-3.0-or-later, the LGPL part being the bundled libvips), reachable as an optional dependency of
  `next`. Runtime npm dependencies are MIT except `chess.js` (BSD-2-Clause). On .NET: `Gera.Chess`
  MIT, Npgsql and its EF provider under the PostgreSQL licence, `Stripe.net` Apache-2.0,
  `StackExchange.Redis` and `Sentry.AspNetCore` MIT, OpenTelemetry Apache-2.0, and **`WebPush`
  1.0.13**, whose nuspec carries only a deprecated `licenseUrl` pointing at
  `web-push-libs/web-push-csharp` and ships no licence file in the package.
- Impact: The MPL-2.0 packages are file-level copyleft, which reaches modified copies of their own
  files and nothing else, and neither is modified or shipped. The LGPL-3.0 inside `sharp` matters
  only on conveyance, and the frontend image is built and deployed to Fly rather than pushed to any
  registry (no `docker/build-push-action` or `ghcr` reference exists in `.github/workflows/`), so
  nothing is conveyed today. `WebPush` is the only real unknown: the repository has no local record
  of its licence at all, and the upstream project is commonly reported as MPL-2.0, which would be
  fine unmodified but is not something this audit can confirm from what is checked in.
- Fix: resolve `WebPush`'s licence with one lookup and record it. If a fork ever publishes the
  frontend image to a public registry, the LGPL-3.0 libvips obligations (notice plus the ability to
  relink) attach at that point and not before, which is worth one line wherever **O-5**'s forkability
  instructions land.

#### L-10-6 — No SBOM, so this hunt is a snapshot rather than a standing guard
- Severity: Low
- Where: no SBOM artifact in `.github/workflows/` or the repository; `.trivyignore` exists for
  vulnerability waivers but licence data is not part of that gate
- Impact: The finding above is true on 2026-08-21 and has no mechanism keeping it true. Copyleft
  arrives through transitive dependency bumps, which is exactly what Dependabot does automatically
  and unattended, and the contract-and-drift-test culture in `.claude/rules/testing.md` is otherwise
  the repo's answer to precisely this class of silent divergence.
- Fix: **O-6** owns the SBOM and licence-compatibility question and **I-11** owns the vulnerability
  half; this entry only records that the licence side is currently manual. A CycloneDX generation
  step on both ecosystems is the small version, and it does not need to gate CI to be useful.

### Already covered

- Why the CSP names `worker-src 'self'` explicitly rather than inheriting from `script-src` is
  reasoned at `ui/next.config.ts:91-94`. Confirmed here as
  the mechanism that keeps the worker separate, not re-reported as a finding.
- That client-side Stockfish is clock-blind and permanently unrateable is 7.1 and 7.3
  (`ui/components/BotPlayPanel.tsx:16`). Not this packet's
  business.
- Dependency freshness and vulnerability waivers are readiness **DEPS** and **I-11**; SBOM and
  licence compatibility as an ongoing programme are **O-6**.
- The Montserrat font's missing OFL notice is reported under **L-9** (L-9-6), since it is attribution
  rather than copyleft.

### Action list

1. vendor `ui/public/GPL-3.0.txt` and link it
   (L-10-1); add the TERMS §10 carve-out sentence (L-10-4); add the user-facing pointer to
   `/STOCKFISH.LICENSE` (L-10-3).
2. identify the `stockfish.js` build by release and hash and
   record both, or replace it with a current release and record that. (L-10-2)
3. record `WebPush` 1.0.13's licence. (L-10-5)
4. **Nothing to undo.** The isolation itself needs no change, and no dependency requires a fork to
   open its source. If a future change ever `import`s the engine instead of fetching it as a Worker
   URL, that is the moment this verdict stops being true, and it is worth a sentence in
   `.claude/rules/frontend.md` saying so.

---

## L-11 — Fair-play ToS vs enforcement
Date: 2026-08-21
Packet: docs/audits/INDEX.md

### Verdict

The fair-play machinery is better than the document that governs it. Both detectors are honest to a
degree that is rare: `EngineAssistanceAnalyzer` refuses to say anything below 200 scorable plies,
carries a non-removable false-positive caveat on every Watch/Elevated verdict, and states in its own
source that every threshold is a guess; `SandbaggingAnalyzer` returns `Insufficient` rather than a
low score and says out loud that "we cannot say" is not "they are clean". Report intake is
rate-limited, IDOR-safe and duplicate-suppressed. Sanctions are attributable, liftable, never
deleted, and never automatic.

The gap runs the other way from the one this packet was written to look for. TERMS §5 does overclaim
in one place (it says the detectors surface deliberate disconnection, and nothing detects it), but
the larger fidelity problem is **underclaim**: the site solicits reports for, and can sanction,
conduct the Terms never prohibit, and the public API openly invites the one thing §5 prohibits most
flatly. A sanction is a contractual act. Where the contract does not name the conduct, §8's "for
violating these Terms" has nothing to rest on.

### Findings

#### L-11-1 — §5 says the detectors surface deliberate disconnection; no detector does
- Severity: Medium
- Where: `docs/legal/TERMS.md:44-52`, mirrored at `ui/app/terms/page.tsx:103-128`. Detectors:
  `src/KiriChess.Server/Moderation/Detection/EngineAssistanceAnalyzer.cs:34`,
  `src/KiriChess.Server/Moderation/Detection/SandbaggingAnalyzer.cs:43`.
- Impact: §5's lead-in says the three bullets "are the specific things this codebase's
  cheat-detection signals are built to surface". Two detectors exist and neither looks at
  disconnection. What exists for bullet 3 is `AbandonmentPolicy`
  (`src/KiriChess.Server/Games/AbandonmentPolicy.cs:16`), a matchmaking cooldown derived from the
  Games table, plus one weight-0.10 signal inside the *sandbagging* score
  (`SandbaggingAnalyzer.cs:231`) whose own doc comment says 6e.3 "built a whole cooldown around that
  fact without ever calling it cheating". The claim is also stronger than the run model: nothing
  sweeps on a schedule. Both detectors are pull-only admin routes
  (`EngineDetectionEndpoints.cs:29`, `SandbaggingEndpoints.cs:44`), so nothing is surfaced until an
  admin asks about a named account.
- Fix: rewrite the lead-in to the actual shape, for example "The following are prohibited. Some are
  supported by statistical signals a moderator can run against an account; all of them are decided
  by a human." Do not add a disconnection detector to make the sentence true. The cooldown is the
  right response and the sentence is the thing that is wrong.
- **Follow-on (2026-09-08, B20a).** The lead-in rewrite landed. The owner then declined a pool-wide
  engine sweep. §5 now also says those signals run on demand, engine-assistance analysis is run
  against a named account or a named game (typically after a report), sandbagging has a separate
  on-demand scan a moderator can start by hand, and there is no pool-wide engine scan. The remaining
  pull-only half is stated, not left looking like an oversight.

#### L-11-2 — the site solicits and can sanction abuse, which TERMS never prohibits
- Severity: Medium
- Where: `src/KiriChess.Server/Data/Entities/Report.cs:4` (`ReportReason.Abuse`), surfaced as
  "Abusive behaviour" at `ui/messages/en.json:1071` and in `ui/components/ReportDialog.tsx`.
  Sanction path: `src/KiriChess.Server/Moderation/SanctionService.cs:35`. TERMS
  (`docs/legal/TERMS.md:42-52`) prohibits exactly three things, none of them abuse; §4 covers display
  names only; §8 (`docs/legal/TERMS.md:71-75`) grounds termination on "violating these Terms".
- Impact: a player who harasses another through the surfaces that exist (a display name, a report's
  free-text `Details`, conduct in a game) can be banned, and the contract they accepted does not say
  that conduct was prohibited. The mirror cost falls on the reporter: the UI invites a category of
  report the Terms do not back. This is the cheapest fix in this report and the one most likely to
  matter, because it is the ground under every non-cheating ban the account will ever issue.
- Fix: one bullet in §5, or a short §5b, in the register the document already uses: harassment,
  threats, or abuse of another player or of the maintainer, through any surface the site provides.
  One sentence, no policy programme.

#### L-11-3 — §5 prohibits engine play with no bot carve-out, while `/developers` invites it
- Severity: Medium
- Where: `docs/legal/TERMS.md:47-48` ("using chess engine analysis, in real time or otherwise, to
  choose your moves") against `ui/messages/en.json:1020` ("Call KiriChess from your own engine") and
  `src/KiriChess.Server/Games/PublicApiEndpoints.cs:357` / `:418`.
- Impact: rated play is correctly walled off. `GameHub.cs:199-201` remaps an API token's `JoinQueue`
  onto the `bot:` namespace, `/api/v1/games` refuses a second player
  (`PublicApiEndpoints.cs:271`), and challenge games are unrated (`ChallengeEndpoints.cs:193`). The
  challenge path is not walled off: a token with the `play` scope can mint an official-pool challenge
  or accept a human's, and there is no bot flag on `ApplicationUser`, so the human on the other side
  of that link cannot tell they are playing software. Read literally, §5 makes every one of those
  games a Terms violation by the bot's owner, which is the opposite of what `/developers` promises.
  The codebase already states the position that makes this bite: `EngineAssistanceAnalyzer.cs:162`
  writes into its own caveat text that "engine assistance in an unrated game is still cheating
  against a real opponent".
- Fix: pick one and make both documents say it. Either (a) §5 gains a carve-out for accounts playing
  openly through the public API, and a bot-authored challenge is labelled as such on the accept
  screen and the game page, or (b) `/api/v1/challenges` is restricted so a token-authenticated
  account can only challenge and be challenged inside the bot namespace. (a) is the smaller change
  and matches the "bots are part of the OSS surface" position in the index.

#### L-11-4 — the abandonment cooldown is an automatic restriction no document names
- Severity: Low
- Where: `src/KiriChess.Server/Games/AbandonmentPolicy.cs:29-88`, applied at
  `src/KiriChess.Server/Games/PlayEligibilityPolicy.cs:78-83`. TERMS §6
  (`docs/legal/TERMS.md:54-62`) gives the sanction vocabulary as warning, rated-play ban, account
  ban.
- Impact: three abandons in 24 hours buys a 15-minute queue lockout, decided by a query, applied with
  no moderator, no sanction row, and no lift path. It is explained at the moment it bites (the
  message at `PlayEligibilityPolicy.cs:83` names the reason and the wait) and it is proportionate,
  but it is a fourth adverse action and §6's list reads as exhaustive. Related, recorded rather than
  re-argued: the Art. 22 reasoning at `docs/ROADMAP.md:383` rests on "no sanction is ever issued
  automatically" and cites only the two analyzers. That conclusion still holds (a fifteen-minute wait
  on a free game is not a legal or similarly significant effect), but the sentence is narrower than
  the system it describes.
- Fix: one clause in §6, that repeated abandonment can also trigger a short automatic cooldown on
  matchmaking, separate from the sanctions above. Optionally widen the ROADMAP sentence to name
  `AbandonmentPolicy` and say why it does not change the Art. 22 answer.

#### L-11-5 — the rendered §5 drops the provenance the Markdown carries
- Severity: Low
- Where: `docs/legal/TERMS.md:44-45` carries "(Phase 6e.2 / 6f.2, reviewed by moderators under
  Phase 8.5)"; `ui/app/terms/page.tsx:104-107` does not.
  `tests/KiriChess.Server.Tests/LegalDocumentDriftTests.cs:42-45` compares section numbers only, by
  design ("the prose after them legitimately differs").
- Impact: the page is what a player accepts; the Markdown is what this audit and the drift test read.
  For most sections a prose difference is harmless. For §5 it is the operative text a sanction rests
  on, and the two copies disagree about who reviews and on what basis. Separately, the page renders
  "Engine assistance : using" and "Deliberate disconnection : intentionally", with a stray space
  before the colon, from the `{" "}` at `ui/app/terms/page.tsx:112` and `:124`; Sandbagging does not
  have it.
- Fix: make §5's bullet text identical between the two copies and drop the stray `{" "}`. If §5 is
  worth pinning, add it as a named body-text exception to `LegalDocumentDriftTests`.

#### L-11-6 — internal phase numbers are load-bearing in a consumer contract
- Severity: Low (trigger: any renumbering or archiving of the roadmap, or a reader trying to evaluate
  the claim)
- Where: `docs/legal/TERMS.md:44-45` ("Phase 6e.2 / 6f.2", "Phase 8.5") and `:38` ("Phase 8a.4").
- Impact: these are the load-bearing qualifiers on §4 and §5. A player cannot resolve them without
  reading a 1,700-line internal roadmap, and they go stale the first time work is renumbered. §4's
  "Phase 8a.4" is accurate today: `Moderation/Content/ContentPolicy.cs:120-180` implements exactly the
  ASCII, separator-placement, reserved and blocked rules §4 describes, and the moderator rename route
  §4 promises exists at `Moderation/Content/ContentEndpoints.cs:164`.
- Fix: replace with plain description ("the moderation rules this codebase enforces", "reviewed by a
  human"). §10 already gives a reader who wants the detail the repository link.

#### L-11-7 — "Stalling / time-wasting" is a report reason and is not in §5
- Severity: Low
- Where: `src/KiriChess.Server/Data/Entities/Report.cs:4` (`ReportReason.Stalling`),
  `ui/messages/en.json:1070`. §5 bullet 3 covers "intentionally dropping your connection or otherwise
  abandoning a game".
- Impact: the same class as L-11-2, smaller. Sitting on a running clock while connected, in a variant
  whose entire economy is time, is a real complaint the UI names and the Terms do not. It may not be
  misconduct at all, which is the point: the vocabulary should decide rather than leave it open.
- Fix: either widen §5 bullet 3 to include deliberately running a game out without playing it, or
  drop the `Stalling` intake reason and let it fall under `Other`.

### Already covered

- **Ban evasion by a fresh email address**: booked in TERMS §6 itself (`docs/legal/TERMS.md:60-62`)
  and at `docs/ROADMAP.md:449`. Not re-reported.
- **Art. 22 and solely-automated decisions**: settled at `docs/ROADMAP.md:383`. L-11-4 widens the
  sentence, it does not reopen the conclusion.
- **Detector thresholds are unvalidated guesses**: stated by the code itself
  (`EngineAssistanceAnalyzer.cs:30-32`, `SandbaggingAnalyzer.cs:33-35`) and the correct position for
  a site with no confirmed cheater. Not a finding.
- **The five-place `TermsVersion` drift trap**: `docs/ROADMAP.md:459`. Any edit made for L-11-1/2/3
  hits it.
- **API token scoping, hashing, revocation** is **S-4**; **detector tuning and false-positive cost**
  is **S-2**; **appeal path and moderator dossier** is **S-3**. L-11-3 hands S-4 a specific question
  (challenge-path bot isolation) rather than answering it.
- **General TERMS-vs-code fidelity outside fair play** is **L-1**. Findings here stay inside §5, and
  §6/§8 only where they carry the enforcement.

### Action list

1. **Owner, one sitting, all three text edits together** (they share the version bump): the conduct
   bullet (L-11-2), §5's lead-in (L-11-1), and the bot question (L-11-3). Bump `TermsVersion.Current`
   and all five copies per `docs/ROADMAP.md:459`.
2. **Owner, decision needed before item 1:** L-11-3 (a) or (b). (a) needs a bot label in the accept
   flow, so it is not purely a document change.
3. L-11-5 (mirror §5's body into `ui/app/terms/page.tsx`, drop the two stray `{" "}`) and
   L-11-6 (strip phase numbers from §4 and §5).
4. L-11-4's clause in §6, folded into the same version bump as item 1.
5. widen the ROADMAP Art. 22 sentence to name `AbandonmentPolicy`.

---

## L-12 — Counsel
Date: 2026-08-21
Packet: docs/audits/INDEX.md

### Verdict

No counsel is needed to launch. The packet is correct that this does not close from the repository, so what
follows is not a recommendation to buy the hour; it is the brief to hand over on the day the owner
decides to, plus the triggers that should make that day arrive. The honest current position is that
the documents are unusually well-grounded for self-written ones (every claim traceable to code, an
Art. 30 record filed, six DPAs collected, a governing-law clause that states its own consumer
carve-out instead of hiding it) and that the residual exposure sits in exactly two clauses of TERMS,
not in PRIVACY. `docs/ROADMAP.md:375-378` already reached that conclusion. This packet's contribution
is to name the specific clauses, the specific questions, and the point at which the answer changes.

One thing worth saying plainly, because it is the temptation this packet invites: the "not reviewed
by a lawyer" line is an asset, not a defect. It is consistent across all four places it appears
(`docs/legal/TERMS.md:3`, `docs/legal/PRIVACY.md:3`, `ui/app/terms/page.tsx:34`,
`ui/app/privacy/page.tsx:75`), and a document that says what it is beats a document that borrows a
confident voice it has not earned. Buying an hour of counsel would let that line be removed. That is
the change being bought, and it is worth less than it sounds.

### Findings

#### L-12-1 — the two clauses that carry the residual exposure
- Severity: Low (trigger: a user suffers actual loss and pursues it, or an account is terminated and
  the player disputes it)
- Where: `docs/legal/TERMS.md:64-69` (§7, as-is, no warranty, no guaranteed uptime, and "accounts,
  games and rating history may be lost or reset without notice") and `:71-75` (§8, termination "at
  our discretion").
- Impact: these are the clauses `docs/ROADMAP.md:376-378` flags as where self-written terms are
  typically unenforceable, because Directive 93/13 strikes unfair terms out of a consumer contract
  whatever was drafted. Two specifics an hour would resolve rather than a guess: whether a
  blanket exclusion of liability survives at all against a Belgian consumer, and whether §8's
  unreviewed discretion needs a stated procedure (notice, reason, a route to contest) to be relied
  on. The second interacts with L-11: `SanctionService.LiftAsync`
  (`src/KiriChess.Server/Moderation/SanctionService.cs:91`) already keeps the row and records who
  lifted it and why, calling the row history "the appeal path", but no document tells a player that
  path exists.
- Fix: Put these two clauses, in this order, at the top of the brief.

#### L-12-2 — the three questions worth an hour, in order
- Severity: Low (trigger: the owner decides to buy the hour)
- Where: this file; sources named per question.
- Impact: an unstructured hour on "review my terms" buys a boilerplate rewrite that undoes the
  document's main virtue. A structured one buys three answers.
  1. **The two clauses in L-12-1.** Highest value, lowest ambiguity. Bring TERMS §7 and §8 and the
     fact that the service is free, ad-free and run by an individual.
  2. **Donations.** `docs/ROADMAP.md:2117-2122` already reasons that a free, ad-free site sits
     outside the e-Commerce transparency duty scoped to services "normally provided for
     remuneration" (Belgium: Book XII WER), that FS.3's one-off donations are the grey edge because a
     donation is not payment for the service, and that recurring paid tiers are not a grey edge at
     all. Counsel confirms or corrects that reading; they do not need to derive it. Bring the
     `/donate` page, `src/KiriChess.Server/Account/StripeWebhookEndpoints.cs`, and PRIVACY §1's
     deliberate absence of a postal address. The VAT question rides along with the same facts.
  3. **The name.** `docs/game/CLAIMS.md:17` records that Apache-2.0 grants no trademark rights, that
     NOTICE reserves "KiriChess", the logo and the wordmark, and that EUIPO registration is optional
     and later. The question for counsel is narrow: what the unregistered reservation actually gets
     against a fork that ships under the name, and whether a filing is worth it and in which classes.
- Fix: hand over these three, in this order, and say explicitly that the document is deliberately
  plain-voiced and should stay that way.

#### L-12-3 — what counsel should be told not to do
- Severity: Low (trigger: the hour is booked)
- Where: `docs/audits/INDEX.md` standing positions, and its closing note that the "not reviewed by a
  lawyer" line is "honest, not a defect to close by rewriting them in a more confident
  voice".
- Impact: the predictable output of an unbriefed review is a corporate template: an arbitration
  clause, a US-style liability cap, a cookie banner the site does not need, an ODR link pointing at a
  platform shut down in July 2025 (`docs/ROADMAP.md:371-373` already declined that one), and a
  recommendation to incorporate. The last is worth pre-empting with a fact the roadmap already
  establishes: a Belgian ASBL or BV publishes its registered seat in the KBO, which is the founder's
  home address unless a service address is paid for, so an entity supplies a name to stand behind,
  not privacy (`docs/ROADMAP.md:2123-2126`).
- Fix: put the standing-positions table in front of counsel as constraints, not as opening positions.

#### L-12-4 — the triggers that change the answer from "optional" to "do it"
- Severity: Low (trigger-based by construction)
- Where: this file, consolidating `docs/ROADMAP.md:2103-2126` (FS.4), `docs/legal/PRIVACY.md` §1, and
  the L-7 / L-3 / L-8 packets.
- Impact: without a written trigger list this stays "optional" forever and then becomes urgent on a
  bad day. The four that actually matter:
  1. **Recurring paid tiers ship (FS.4).** The grey edge stops being grey; §1 gains a geographic
     address and the address itself needs budgeting. This is the strongest trigger and the roadmap
     already books it.
  2. **A user asserts a claim**, or a terminated player disputes a ban in writing. §7 and §8 stop
     being theoretical.
  3. **A fork ships under the name**, or a third party files the mark. The EUIPO question becomes
     timed rather than optional.
  4. **A supervisory-authority contact**, of any kind. ROPA is filed and produce-on-request ready
     (`docs/ROADMAP.md:361-366`), so this is a "have someone read it with you" trigger, not a panic
     one.
- Fix: keep this list where the owner will see it. `docs/audits/B.md`'s quarterly checklist is the
  natural home; this packet should not create a second calendar.

### Already covered

- **Lawyer review is optional and its value sits in TERMS, not PRIVACY**: `docs/ROADMAP.md:375-378`.
  This report does not reopen it, it only itemises what to bring.
- **Governing law, the consumer carve-out, and the deliberately omitted ODR link**: closed
  2026-08-16, `docs/ROADMAP.md:367-373`.
- **Art. 30 record, DPAs, transfer disclosure, DPO and Art. 27 not required**: `docs/ROADMAP.md:353-366`
  and `docs/ROADMAP.md:385-387`. Processor delta work is **L-3**, not this packet.
- **Donations as patronage rather than remuneration** is **L-7**; **accessibility as a legal
  exposure** is **L-8**; **IP, trademark and attribution** is **L-9**. All three route to the same
  person. This packet is the one that says what to ask and in what order, so those three should be
  read as inputs to the same hour rather than three separate hours.
- **The fair-play and abuse clauses** are **L-11**, written the same day. Its L-11-2 (abuse is
  sanctioned but not prohibited) and L-11-3 (the bot carve-out) are owner text edits that should land
  *before* any counsel review, not be added to its scope.
- **Out of scope, noted once for O-4:** `docs/game/CLAIMS.md:28` says a public GitHub repo can serve
  as the invention timestamp but warns "not this working repo, whose commit authors include personal
  addresses", and asks for a clean snapshot instead. The repo is now public and TERMS §10
  (`docs/legal/TERMS.md:92`) publishes its URL. Whether that instruction was superseded on purpose is
  **O-4**'s question (public-repo hygiene, history as well as HEAD), not this one.

### Action list

1. **Owner, no deadline:** decide whether to buy the hour. Nothing here is launch-gating and this
   report is not an argument that it is.
2. **Owner, before that hour if it happens:** land L-11's owner edits first, so counsel reads the
   document the site will actually run.
3. **Owner, when booking:** hand over L-12-2's three questions in that order, plus the
   standing-positions table as constraints (L-12-3).
4. add L-12-4's four triggers to the **B-4** quarterly
   checklist when that packet runs.
5. **Do not:** rewrite TERMS or PRIVACY in a more confident voice, remove the "not reviewed by a
   lawyer" line, or open a Belgian entity as a privacy measure. All three are settled positions.

