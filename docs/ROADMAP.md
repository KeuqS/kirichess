# KiriChess - Development Roadmap

> **KiriChess** is an original chess variant where the clock is the battlefield: captures, moves, and checks all add or remove time. This roadmap takes the project from rule engine to a rated, chess.com-style online platform.
>
> **Stack:** C# (.NET) backend - Next.js frontend - PostgreSQL + EF Core - SignalR - Glicko-2 ratings - Redis (later)

---

# Todo

> **Start here.** One queue, in execution order. Launch only when every **launch-gated**
> numbered row is done. **8.7 is #16.** Read the full item in the phase sections below for history,
> traps and gates. Phase grouping underneath is left intact; it encodes ordering constraints the
> table summary does not repeat. Items marked `[Owner]` take an account, a signature, a
> screen reader, or a click in someone else's dashboard.
>
> This table is open work only. Finished phases are in git history, not a second file. 9f.1,
> **9f.2**, **10c.1**, the GitHub-hosted half of **10c.5**, and **AD.1 (#17)** are not
> launch-gated. They stay in the table so the work is visible; do not treat them as launch blockers.



## Open work (execute in `#` order)

**Re-ordered 2026-08-20** around two constraints: the working repo stays **private** through
launch and after it (a clean public snapshot **is** now in this queue, **AD.13** at **#11a**; see
[CLAIMS.md](game/CLAIMS.md)), and
`kirichess.com` stays behind **Cloudflare Access** until **#12**.

**Launch-gated** unless noted. **#9–#11a are the gated window** (Access on, working repo private).
**#4 and #4a are done** (2026-09-24: [UI-CHECKLIST.md](ops/UI-CHECKLIST.md) walked, desktop and
mobile, and the adjustments shipped). **9f.2 (NVDA) is not launch-gated** - code is done; the human
pass sits after 9f.1. **AD.7 is done** (25 signups / 8 human games / 2 rolling-D7, read at +14d; not a table row). The play hour is
**Fridays 20:00 UTC** (11a.4's banner is unblocked; the #4 layout gate is closed). **AD.4 is not in this queue** (declined 2026-09-04).
**AD.1 is not in the gated window either** (moved after 8.7 on 2026-09-04: first players are the
Reddit thread). **#10–#11 are the same window** (load test; Access still on by design). The UI walk
they were waiting on is done.

**#12 is the flip:** Access off. **AD.13 / #11a** is the public development repo
(`MichielGekiere/kirichess`, clean import, source of truth for community + Fly) — not a read-only
mirror while a private working tree stays forever.

**#13–#16 are after the flip** (Google can fetch `/privacy`, archive.org can save `/rules` and
`/about`, the catalogue can link the site, then the post). **#17 is after 8.7** (press kit; not
launch-gated). The first room is r/chessvariants, not a coverage drop.

**1 Sep has passed.** 10c.5 and 10c.1 are still not launch-gated, and August's Actions minutes
are no longer why. Probes are disabled in the GitHub UI. The weekly dump is getting a runner and
failing on the R2 upload. Do not block 8.7 on either. Development is on the public repo after AD.13.


| # | When | Item | Action | Unblocks |
| --- | ----------------------------- | ------------------------------ | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ | ----------------------------------------------------------------------------- |
| 9 | **Done** 2026-10-07 | **FS.4a** patron badge go-live | Deploy and the webhook secret were done 2026-09-26. Owner confirmed a signed-in Stripe checkout grants the badge on `/profile` (2026-10-07). | Donors get the badge the page promises. |
| 10 | Now, before #12 | **9e.8** load test | **Ran 2026-09-26/27** on production `shared-cpu-1x` / 512 MB. Saved files only. Before append-only plies: `prod-ladder-n50.json` pass; `prod-ladder-n100.json` save queue ~60→90, client p95 1511 ms, 85% of planned moves, 0 hub errors, 100 games still live at the end of the window (overall Void). After `AppendGameMoves` (image `13c9b858`, [PR #75](https://github.com/MichielGekiere/kirichess/pull/75)): `append-ladder-n50.json` pass (p95 155 ms, queue empty); `append-ladder-n100.json` held (queue mean ~3, p95 530 ms, 0 hub errors, overall Warn). No `n150` file, so do not quote a 150-game rung. **Steady cap from the files is 100 games.** Fly CPU was not sampled (the harness reads local Docker and printed 0%). **Next:** read `fly_instance_cpu` on a 100-game rung. Save a result file before any higher rung changes the cap. The old "a bigger machine will not help" note is from the pre-batch code and does not apply here. | The number DH.1 rests on: 100 games on this VM. |
| 11 | **Done** 2026-10-07 | **10a.3** shutdown measurement | Measured 2026-09-26: a restart flushed 100 live games in 550 ms (2 batches). Copied into [DEPLOYMENT.md](ops/DEPLOYMENT.md) §4.1. `kill_timeout` is 30 s, so the flush fits. | Stated shutdown budget with a measured fit. |
| 11a | **Done** 2026-10-11 (cutover) | **AD.13** public source of truth | Public repo [MichielGekiere/kirichess](https://github.com/MichielGekiere/kirichess): clean import (**no private history**), community issues/PRs, Fly deploys from this remote. Pre-public history archived as `kirichess-archive`. Plan: [PUBLIC-SOURCE-OF-TRUTH.md](plans/open-source/PUBLIC-SOURCE-OF-TRUTH.md). **Does not establish invention priority** (#14 / #15 still load-bearing). | Public code under a license; AD.3 gets a URL; `SECURITY.md` resolves; deploys move off the archive. |
| 12 | Launch day - the flip | **DH.3** remove Access | Remove the Cloudflare Access application. Load `kirichess.com` in a private window that has never authenticated. Access is the only constraint that changes on launch day. **I-5-5, same day:** drop `302` from `betteruptime_monitor.edge_site`'s `expected_status_codes` in `infra/betterstack/main.tf` (marked `# DH.3: drop 302` on the line), restore `follow_redirects`/`remember_cookies` to their defaults, and update `BetterStackAlertingDriftTests` to expect `[200]` - once Access is off, a 302 from the apex is a redirect loop or a misconfiguration, not a login wall, and leaving it accepted means that never pages. | Site reachable by strangers. |
| 13 | Launch day, after #12 | **DH.5** Google OAuth | Consent-screen URLs are fetched by Google and sit behind Access today. Do this after #12, not before. Redirect URI is `https://api.kirichess.com/signin-google`. | 8a.2's last obligation; live OAuth. |
| 14 | Launch day, after #12 | **AD.12** archive snapshot | Save `/rules` and `/about` to archive.org; record URLs in [CLAIMS.md](game/CLAIMS.md). Needs the public site - a Save Page Now before #12 captures the Access login. | Third-party citable timestamp. |
| 15 | After #12 | **AD.3** chessvariants.com | Send [SUBMISSION-DRAFT.md](plans/chessvariants/SUBMISSION-DRAFT.md). Find submission route, pick the inventor name. **Not gated on 8.7**, but wait for #12 - the draft links `kirichess.com`. **Link** [MichielGekiere/kirichess](https://github.com/MichielGekiere/kirichess) if a source URL helps; never `kirichess-archive`. | Earliest public dated record of the invention. |
| 16 | After #12–#15 | **8.7** launch post | [POST-DRAFT.md](plans/launch/POST-DRAFT.md) + pre-flight checklist. r/chessvariants first, no clip, no journalist pitch. The first players are people who read the rules and queued. | The launch. |
| 17 | After #16 | **AD.1** press kit recapture | **Not launch-gated. Do not block 8.7.** First players come from the Reddit post, not from coverage. Recapture after the thread has run so the first room is organic. Pick a clip from [press-kit/README.md](../press-kit/README.md) (clutch queen vs opening swing). Desktop `lg` recapture + `convert.mjs`. Local Playwright. Capture the post-8.8a layout (#4a closed 2026-09-24). | A stranger who wants to cover it later has footage. Not a launch asset. |
| - | Not a launch gate | **10c.5** GitHub dispatch | **Not launch-gated. Do not block 8.7.** Terraform, `SYNTHETIC_API_TOKEN`, and both heartbeat URL secrets landed 2026-08-20. The 15-minute cron is in `ops-probes.yml`, and GitHub has the workflow **disabled_manually**, so it is not spending minutes. Last scheduled attempts are 2026-08-22 ([32602717028](https://github.com/MichielGekiere/kirichess/actions/runs/32602717028)): both jobs died in ~2 s with no steps. Nothing has run in September. **Owner:** re-enable the workflow and dispatch once. The **working** repo stays private, and AD.13's snapshot carries no workflows, so #11a does **not** unblock this. | Synthetic + Neon heartbeats. |
| - | Not a launch gate | **10c.1** off-provider backup | **Not launch-gated. Do not block 8.7.** Bucket, token, secrets, and the `.eu.` endpoint are in. August dispatches (32395327365, 32395991126) never started a runner. The Sunday cron on `main` is active and ran 13, 20, and 27 Sep; each failed. Latest [36313475991](https://github.com/MichielGekiere/kirichess/actions/runs/36313475991) started a runner and died uploading to R2 (`SSLV3_ALERT_HANDSHAKE_FAILURE` on `*.eu.r2.cloudflarestorage.com`). Neon is still the only copy. GitHub's DPA is not accepted ([DPA-CHECKLIST.md](legal/DPA-CHECKLIST.md)). The working tree turns the cron off until that acceptance is on `main`; until then Sunday keeps firing. Do not dispatch another run before the DPA. After it, the leftover is the TLS upload. | Second copy of every game, rating and account. |
| - | Parked | **9f.1** tournaments | **Not launch-gated. Do not start.** Trigger judged 2026-08-16: not yet. AD.7's 25 / 8 / 2 at +14d does not unpark this. Revisit when a Friday 20:00 UTC hour produces two concurrent human games without the owner filling a seat. | Revisit when a Friday hour fills without the owner. |
| - | After 9f.1 | **9f.2** NVDA pass | **Not launch-gated.** Code is done (`AccessibleBoard`, announcements, P-5 gaps 2026-09-04). Remaining: the ~40 min scripted pass in [NVDA-PASS.md](plans/a11y/NVDA-PASS.md). After tournaments because launch will not have screen-reader traffic worth gating on, and a volunteer can reach the site after #12. Do not claim WCAG until this has been run. | Closes 9f.2 (accessibility verification). |




**Not tasks:** FS.1, FS.2, FS.5 and FS.6 are standing positions and should never be ticked. **AD.11 is
one too** (distribution is a recurring cost, not a launch). **AD.7 is written** (2026-09-04: 25 / 8 / 2
at +14d - do not put it back in the table). **AD.4 is declined** (2026-09-04: launch
without a pre-recruited founding cohort - do not put it back in the table). **AD.1 is still a
task** (#17, after 8.7; not a launch gate). **AD.5 is a pointer at 11a.4** rather than a second
task, so it should be ticked with it. FS.4's
gate on FS.3 is released now that FS.3 is done, but it stays out of order on its own terms, being
explicitly post-traction. **FS.4a (#9) is not FS.4 landing early:** it is the badge half only, built
2026-08-23 to resolve L-7-4, and it gates nothing else. Patreon, recurring paid tiers and the payout
handling remain parked under FS.4, and ticking #9 does not tick FS.4. 8a.2's own work is finished and is parked behind DH.5 and 8.7, not behind
code.

---

> **One item needs no account and no dashboard step:** 9f.1 (9e.4, 9e.9 and 9f.3, the three that did, all
> shipped 2026-08-15). Everything else names its owner step, and the steps cluster hard:
> **DH.5's four signups are the bottleneck**, with 9e.5 queued behind parts of it (9e.5's own
> step, "pick a transport," is also resolved - it shipped 2026-08-14 as email, the note's own
> recommended default; only real delivery still waits on DH.5's Resend account, same as every other
> transactional mail 8a.1 already built. 9e.7's own step, "pick a destination," is likewise resolved -
> it shipped 2026-08-15 as Sentry, the note's own recommended default; only real traces wait on DH.5's
> Sentry account, same as 6d.2's error tracking already does). **9c.5's own step, "pick a vendor," is
> also resolved** - it shipped 2026-08-15 as Better Stack, and the Terraform to act on that choice is
> written and committed (`infra/betterstack/`) - but unlike 9e.5/9e.7 this one is **not** queued
> behind DH.5: Better Stack was never one of its four, so 9c.5 now waits on its own, fifth signup
> rather than sharing DH.5's queue. **9c.6 rides that same fifth signup rather than adding a sixth**:
> its own Terraform (`logtail_source.app_logs` in the same `infra/betterstack/main.tf`) and its own
> Fly app config (`fly.logshipper.toml`) are now written and committed too, so the one Better Stack
> account 9c.5 needs is the whole prerequisite for both items.
>
> **Correction 2026-08-16, and it moves the bottleneck.** The paragraph above is written as though
> **signups** are what everything queues behind. That was true when it was written and is no longer
> the whole picture: Better Stack's fifth account exists, `terraform apply` has run, and the uptime
> monitor is live. **The new shared blocker is deployment itself, and it is one expired credential**:
> the `FLY_API_TOKEN` repository secret is invalid, the Deploy workflow has never once succeeded, and
> every release so far went out by hand from a laptop. That single token now gates 9c.2's staging, all
> of 9c.5's metrics half, and any release of the 56 commits sitting on `dev` that 8.7 depends on.
> **It is also the cheapest thing in this document to fix** (`fly tokens create deploy`, then reset
> the secret), which is an unusually good ratio for something this load-bearing.
>
> **Spent 2026-08-17, and the cheap fix above names the wrong command.** The token was rotated, the
> Deploy workflow has since authenticated, deployed production and deployed staging, so deployment is
> no longer anyone's blocker and the two paragraphs above are kept only as the history of how it was
> found. `fly tokens create deploy` **fails here** with `Could not find App`: it infers the app from a
> `fly.toml`, this repo has none (only `fly.backend.toml`, `fly.ui.toml` and the two staging pairs),
> and a deploy token is scoped to **one** app while the workflow drives four from a single secret.
> The shape that fits is `fly tokens create org -o personal`, capped with `-x 8760h`. The same wrong
> command appears twice more below, under 9c.2 and 9c.5, both times inside a dated note recording
> what was believed then; this is the correction for all three.



## Phase 8 - Polish & Launch

- [ ] **8.7** Soft launch to a chess/variant community (e.g., r/chessvariants, Lichess variant circles) for feedback
  ```
  about tutorial copy that had nothing to do with it. [CLAIMS.md](game/CLAIMS.md).

  _**8.8 was a fourth trap, added 2026-08-19, and it is closed.** Walk
  [UI-CHECKLIST.md](ops/UI-CHECKLIST.md) on desktop and mobile (**#4**) and ship the adjustments
  (**8.8a / #4a**). Both closed **2026-09-24**. The visual gate is no longer in front of DH.3._

  _**This item collected prerequisites from three separate reviews on 2026-08-17, and the marketing
  one added four.** From [Adoption & Distribution](#adoption--distribution-cross-cutting--not-a-phase):
  **AD.6** (there is no funnel, no cohort curve and no traffic counter in the code, so as things
  stand this one-shot event produces a feeling and nothing else, and week one cannot be counted
  afterwards) and **AD.7** (written 2026-09-04: 25 signups / 8 human games / 2 rolling-D7, read
  at +14d, so "did it work" has an answer). **AD.1 is not a prerequisite:** moved after this post on 2026-09-04 (#17). The first
  players should be this thread - r/chessvariants, people who read the rules - not a press drop.
  Do not attach a clip. **AD.4 is not a prerequisite:** declined 2026-09-04, launch without a
  pre-recruited ten. **AD.5/11a.4 is the free one:** post the day before a play hour and name it,
  which turns a read into an appointment. **AD.3 is explicitly not a prerequisite** and should
  happen regardless of what this thread does._

  _**The thing most likely to embarrass this item, found 2026-08-16 while deploying 9c.5's fix:
  production is running `main`, and `main` is 56 commits behind `dev`.** Verified against the live
  API rather than inferred, three `dev`-only routes (`/api/seeks`, `/api/account/sessions`,
  `/api/challenges`) all answer 404 there, and `main` contains none of them. So roughly 55 commits
  of work this roadmap has already ticked and archived are **merged but not live**: 9d.7, 9e.2,
  9e.3, 9e.4, 9e.6, 9e.9, 9f.3's PWA, and DH.3's server-side Turnstile among them. Fly is at
  release `v11`, hand-deployed 2026-08-15._

  _**Why this is 8.7's problem specifically:** this item's whole content is a post that links
  strangers to the site, and 9d's gate ("everything above is what the post links to") is written
  as though those features are reachable. They are not. **A release of `dev` to `main` is a
  prerequisite of the soft launch**, alongside removing DH.3's Cloudflare Access gate, and it wants
  to be a deliberate release rather than something discovered on launch day. Note the two are
  independent traps that fail in opposite directions: the Access gate means nobody sees the site,
  the stale `main` means everybody sees an old one._

  _**The release is open as [PR #31](https://github.com/MichielGekiere/kirichess/pull/31)
  (2026-08-16, on the owner's "release it"), and it is red.** `dev` was pushed (29 unpushed
  commits) and the PR opened; `e2e`, `frontend` and `gitleaks` pass, **`backend` fails**._

  _**The failures are not the release's and not new.** CI on `dev` has been red since `853af86`
  (2026-08-15, "add patron status ... game history and session management endpoints"), which
  predates the 2026-08-16 diagnosis of this failure. Six tests fail, all of them Docker-dependent, and
  that is why nobody noticed: **without Docker those 195 tests skip rather than fail**, so a local
  `dotnet test` reports a clean run and the [test baseline note](#) that called this suite "fully
  green" was reading a suite that had quietly stopped running its integration half._

  _**Diagnosis, unverified but well-supported.** Four `GameHistoryEndpointTests` fail with a bare
  `400` thrown by their **register helper**, not by the endpoint under test, which is this repo's
  known signature for a rejected `DisplayName`. Those fixtures register the display name `"Me"`
  three times (lines 88, 134, 161) against one shared Postgres container, and 8a.2 added a
  **unique index on display name**. The two `PairingNotificationTests` failures are a different
  shape (an email collection holding 3 items where 1 was expected, and non-empty where empty was
  expected), consistent with a shared email sink accumulating across tests rather than a
  product bug. **Both were read as test-isolation defects in other in-flight test changes**, and the
  likely fix is per-test unique display names and a per-test email sink, not a change to the
  application._

  _**Fixed 2026-08-16 (`f33351b`), and the diagnosis above was wrong on both halves.** It was
  right that both are test-side and that nothing under `src/` needed to change, which is the part
  that mattered; it was wrong about why, and both errors pointed at "isolation" when neither test
  class shares anything. The diagnosis below is the record._

  - _**Neither class shares a fixture.** Both implement `IAsyncLifetime` **directly** rather than
    through `IClassFixture`, so xUnit builds a fresh instance per test method and
    `InitializeAsync` starts **a new Postgres container per test**. A duplicate display name
    across tests cannot collide, and 8a.2's unique index is not involved at all._
  - _**The real cause was length, not uniqueness.** `ContentModerationOptions.MinLength` is `3`
    and `ContentPolicy.CheckAsync` rejects anything shorter as `TooShort`. `"Me"` is two
    characters. The proof was already in the file: `Never_shows_another_users_games` registers
    `"Isolated Me"` and is the one test in the class that passed. Fixed by naming the four
    accounts `"History Me"`, `"Tombstone Me"`, `"Rating Me"` and `"Paging Me"`._
  - _**The email sink is per-test too, and it was holding real mail.** `/api/auth/register` sends
    a confirmation email on every successful registration, so two registrations put two messages
    in the bag before the pairing email is ever sent: `Assert.Single` saw 3, `Assert.Empty` saw
    2. Fixed by asserting on `EmailTemplates.PairingFoundSubject` specifically rather than on the
    size of the whole sink, which is what those tests were always about._
  - _**Verified with Docker up**, since these skip silently without it: the eight tests in the two
    classes pass 8/8 with **0 skipped**, and the full server suite runs 581 with both classes
    green._

  _**A second, unrelated defect surfaced while verifying the first, and it is now fixed too.**
  Local Windows runs failed 2 to 7 tests per run at random, always with
  `AggregateException: ... Cannot access a disposed object. Object name: 'EventLogInternal'`,
  never an assertion, and mostly in classes nobody had edited (`ChallengeTests`,
  `AccountDataTests`, `LiveGameDurabilityTests`). Cause: `Program.cs` only calls
  `ClearProviders()` outside Development (6d.2), every fixture builds its host with
  `UseEnvironment("Development")`, and on Windows the surviving default provider set includes
  **EventLog**. One host's teardown disposes the shared handle while a concurrently stopping host
  is still writing a shutdown line through it. CI never saw it because it runs on
  `ubuntu-latest`, where the provider does not exist._

  _Fixed test-side by `WindowsEventLogSuppression`, a module initializer that sets
  `Logging__EventLog__LogLevel__Default=None` for the whole assembly before any host is built.
  Configuration rather than `ClearProviders()` per fixture, so it covers fixtures written later
  and leaves console output available for diagnosing a test. **Nothing under `src/` changed**;
  the product's own logging is untouched. Two consecutive full runs afterwards: 581 passed, 0
  failed, 0 skipped, against three consecutive pre-fix runs that failed 2, 5 and 7._

  _**Nothing was merged.** A red release must not land. The tests are now green and pushed to
  `dev`, but the merge of PR #31 itself stays with the owner, since landing a release is
  a decision rather than a repair._

  _**Shipped 2026-08-20** via [PR #33](https://github.com/MichielGekiere/kirichess/pull/33)
  (`dev` → `main`); `/privacy` §1 verified on the deployed site. That publish row is done; the
  stale-`main` paragraphs above are historical._

  _**A further prerequisite appeared on 2026-08-17, and it is code rather than a decision: all of
  10a.** This item already books two traps that fail in opposite directions (the Access gate means
  nobody sees the site, a stale `main` means everybody sees an old one). 10a is a third shape
  again, **the site works and individual strangers quietly fail**: 10a.1 means the confirmation
  link in a new player's inbox stops working the next time anything is deployed, 10a.2 means the
  lobby crowd this post is designed to produce can spend the connection budget the players need,
  and 10a.3 means the deploy that fixes either of them drops the games in flight. None of the
  three is visible from a smoke test of the deployed site, which is why they are named here rather
  than left to the pre-flight checklist in
  [POST-DRAFT.md](plans/launch/POST-DRAFT.md)._

  _**Two of the three are done as of 2026-08-18** (10a.1 and 10a.2, both archived below). 10a.3's
  config half is in as well, so what remains of this prerequisite is one measurement riding a
  load-test window the owner has already authorized, rather than any code._

  _**11a landed against this gate the same day, from a different review**, and the two were written
  independently of each other. Phase 11's own preamble settles the ordering and this note does not
  re-argue it: **10a goes first**, on the grounds that a site losing live games on deploy has a
  retention problem 11a cannot fix. The distinction between them is worth carrying: 10a is what
  breaks for a stranger who stays, 11a is why a stranger does not stay._
  ```



### 8a - Launch infra (unglamorous but non-optional for a public site)

- [ ] **8a.2** **Legal/compliance**, Terms of Service, Privacy Policy, **GDPR account deletion + data export**,
  ```
  and an **age gate / COPPA** consideration (chess sites attract minors; unavoidable once EU strangers sign up).

  _**Why this is not ticked, and what is actually left.** As of 2026-08-20 the list below is down
  to **one open item, and it is not this item's**: Google OAuth, which is DH.5's (#13). DPAs and
  mail delivery are closed. Every obligation the
  regulation imposes on the *content* of these documents is now written, and the contact mailbox
  they publish is confirmed delivering. What otherwise keeps the box unticked is publication
  rather than drafting: `/terms` and `/privacy` are deployed on `kirichess.com`, but the whole site
  sits behind DH.3's pre-launch Cloudflare Access gate, so no member of the public can read either
  document yet, and a privacy policy nobody can reach is not notice. **8.7 removes that gate**, and
  the tick belongs there or after it, together with DH.5. This paragraph used to lead
  with "no lawyer has reviewed either document," which collapsed two different things into one
  line: **no law requires that a lawyer draft or review either document.** GDPR regulates their
  *content* (Art. 12–14), not their authorship. The split below is read off the regulation text
  and is not a lawyer's opinion, which is the distinction [CLAIMS.md](game/CLAIMS.md) exists to keep._

  _**The paragraph above was true of the working tree and not of the repository, found
  2026-08-17.** Every closure recorded below was written on 2026-08-16 and **never committed**:
  the controller identity, the transfer disclosure, ROPA.md, the terms-version bump and both
  `.tsx` copies were sitting as uncommitted changes. Production therefore still served
  `[CONTROLLER NAME AND CONTACT ADDRESS: TO BE COMPLETED BEFORE LAUNCH]` on `/privacy`, which is
  the line 8.7 was about to put in front of strangers. Committed as `e69faf2`, with
  `LegalDocumentDriftTests` green 6/6 and the UI typecheck clean. **It still has to be released
  and deployed**, so the gap this closes is not closed on the live site until then._

  _**Required, still open, and every one of these is self-serviceable:**_

  - ~~**Controller identity and contact address** (Art. 13(1)(a))~~ **closed 2026-08-16.** §1 now
    names Michiel Gekiere, a natural person resident in Belgium, as controller, with
    `privacy@kirichess.com` as the contact, a mailbox DH.0 already routes through Cloudflare Email
    Routing. **No postal address is published, deliberately:** Art. 13(1)(a) asks for identity and
    contact details, not a street, and the rule that does demand a geographic address is a
    different one, the e-Commerce Directive's transparency duties (Belgium: Book XII WER), which
    bite on services normally provided for remuneration. A free, ad-free site sits outside them.
    **FS.4 is where that changes**, and FS.4 now says so._
  - ~~`[Owner]` **Confirm `privacy@kirichess.com` actually delivers**~~ **closed 2026-08-16**,
    Email Routing confirmed working by the repo owner. This mattered because
    [PRIVACY.md](legal/PRIVACY.md) §1 publishes that address as the single route for every
    Art. 15–22 request and §8 tells people to use it, so a silently failing forward would not have
    looked like an outage, it would have looked like a controller ignoring rights requests. All
    four routed addresses were checked, which matters because Cloudflare Email Routing rules are
    **per address** rather than per zone, so a working `privacy@` would not have proved the rest:
    `contact@` is published on every page by [Footer.tsx](../ui/components/Footer.tsx), and
    `dmarc@` is the `rua` mailbox in [DEPLOYMENT.md](ops/DEPLOYMENT.md) § 8.2's record._
  - ~~**Third-country transfer disclosure** (Art. 13(1)(f))~~ **closed 2026-08-16.** §4 now states
    that Resend, Sentry, Google, Fly.io, Neon and Cloudflare are all US companies, so using any of
    them transfers data outside the EEA, and names the basis per provider, checked against each
    provider's own DPA rather than assumed: Fly.io, Neon, Sentry, Google and Cloudflare currently
    self-certify under the EU–US Data Privacy Framework, Resend's DPA relies on Standard
    Contractual Clauses only, and SCCs are the stated fallback for all six regardless. This is the
    disclosure, not the DPAs themselves, those are still the next bullet, unaffected by this one.
  - ~~_**A DPA per processor** (Art. 28(3))_~~ **Filed 2026-08-17** into [ROPA.md](legal/ROPA.md)
    §4; PDFs gitignored. **Fly receipt closed 2026-08-20** (Dropbox Sign complete 2026-08-17).
    DH.5's leftover is Google OAuth (#13), not another DPA and not mail.
  - ~~**An Art. 30 record of processing**~~ **closed 2026-08-16.** [ROPA.md](legal/ROPA.md), seven
    processing activities derived from §2's table and the code behind it, with the Art. 30(1)(e)
    transfer table and the Art. 30(1)(g) security list. **Internal, not published and not linked
    from the site**, Art. 30(4) is a produce-on-request duty to a supervisory authority, not a
    transparency document. It opens by stating why the Art. 30(5) under-250 exemption does not
    apply here: a live account system is not "occasional" processing.
  - ~~**Governing law**~~ **closed 2026-08-16.** [TERMS.md](legal/TERMS.md) §9 is Belgian law and
    Belgian courts, written with the caveat stated in the clause rather than left to be discovered:
    under Rome I Art. 6(2) an EU consumer keeps the mandatory protections of where they live, and
    under Brussels I bis Art. 18 they may sue where they live and may only be sued there. The
    clause sets a default and moves nobody's case. **One template line was deliberately not added:**
    the EU ODR platform link, which most terms boilerplate still carries. That platform was shut
    down in July 2025 and the link would point at nothing.

  _**Optional, a risk decision, not a compliance one:** lawyer review. If it is ever bought, the
  value is in [TERMS.md](legal/TERMS.md) and not the privacy policy: limitation of liability and
  disclaimers are where self-written terms are typically unenforceable, and Directive 93/13 strikes
  unfair terms out of a consumer contract regardless of what was drafted._

  _**Already satisfied, recorded so nobody re-opens them:** purposes and lawful basis per data
  group (Art. 13(1)(c)–(d), §3); recipients (Art. 13(1)(e), §4); retention (Art. 13(2)(a), §5,
  stated honestly as indefinite until 8a.3); and the rights list including the right to complain to
  a supervisory authority (Art. 13(2)(b),(d), §8). **Art. 22 does not bite**: no sanction is ever
  issued automatically, `SandbaggingAnalyzer` and `EngineAssistanceAnalyzer` are advisory and
  write no `UserSanction`, a human issues every one, so there is no solely-automated decision and
  no Art. 13(2)(f) disclosure owed. Engine assistance is computed on a named account or named game,
  typically after a report; it is not a pool-wide scan (B20a / S-2-3, 2026-09-08). A DPO is not
  required (Art. 37, no large-scale systematic monitoring), and no Art. 27 EU representative is
  needed while the controller is EU-established._

  _**A live hole found and closed while grounding this work:** `deletedaccount` was not a reserved
  display name. `NormalizeForIdentity("Deleted account")` folds to `deletedaccount`, which matches
  none of `reservedExact`'s existing entries, so, before this phase, any player could have
  registered that exact name and been indistinguishable from a deleted account on every leaderboard,
  game page and replay. `deletedaccount` is now reserved._

  _**The display-name unique index is now partial (`WHERE "DeletedAt" IS NULL`), and must stay that
  way.** Every tombstone shares the key `deletedaccount`, so under the old total index the *second*
  account ever erased would fail on a duplicate key. The obvious alternative, a per-user suffix,
  is a trap worth recording so nobody re-derives it: `NormalizeForIdentity` folds `0`→`o`, `1`→`l`,
  `4`→`a` and `5`→`s`, so over a hex guid fragment it is **not injective** (`a4` and `aa` both key
  to `aa`), and the collision would have surfaced as a random 500 on somebody's deletion request
  months later. `AccountDataTests.Two_deletions_do_not_collide_on_the_display_name_index` is the
  guard: it fails on a duplicate key if the filter is ever removed._

  _**What shipped:** both legal documents derived from reading this codebase's actual entities,
  middleware and third-party wiring rather than from a template, down to naming the real database
  columns and the real (inert) status of each sub-processor; `/terms` and `/privacy`; GDPR export
  (Art. 20) as a synchronous authenticated JSON download that excludes other players' identifying
  data and every moderator note; and GDPR erasure (Art. 17) by anonymisation and tombstoning, with
  games, rating history, reports and sanctions retained as other-party and legal-claims data rather
  than deleted. A minimum-age declaration is recorded at signup as three timestamps
  (`TermsAcceptedAt`, `TermsVersion`, `AgeConfirmedAt`), no date of birth is stored anywhere._

  _**The wiring Tasks A–D were not allowed to do has since landed** (`f835a15`, plans in
  Phase 8a.2). For most of this phase the compliance
  surface existed in the codebase and not on the site: `AccountDataCard` was built, typechecked
  and rendered by nothing, so no button anywhere exported or erased an account, and `/terms` and
  `/privacy` were reachable only by typing the URL. That was deliberate rather than forgotten,
  `ui/app/profile/page.tsx` and `ui/components/Footer.tsx` had uncommitted work in flight from a
  concurrent session, and wiring them from a parallel branch would have clobbered it. Once that
  work landed (`51670d8`, `d5aee5c`), four edits closed it: `Footer.tsx`'s `LEGAL_LINKS` carries
  both documents, so the legal row renders on every page that has a footer; `/profile` mounts the
  export and erasure card under `DisplayNameCard`; and `/terms` and `/privacy`, the only two
  pages in the app with a nav bar and no footer, now render one, so the row pointing at them is
  reachable from them. **There is now a button on the site that exports an account and a button
  that erases one**, which is the distinction that matters for a policy page whose entire purpose
  is being found. `/profile` is otherwise still the static mock page it has been since 8a.4;
  those two cards are the only parts of it wired to a real account._

  _**Known limits, stated rather than half-solved:**_

  - _**Erasure is refused while a game is in progress** (409, not 400, the request is well-formed,
    it is the timing that is refused). Scrubbing a player mid-game would strand the opponent against
    a session whose owner stops resolving, and let the abandonment timer decide a game somebody is
    still playing. It is a bounded delay of one game, not a condition on the right, and it must not
    be allowed to become one._
  - _**Existing accounts are not re-prompted for consent.** `TermsAcceptedAt` is `NULL` for every
    account that predates this phase, which is the truth rather than a backfilled lie, a re-consent
    interstitial for accounts that predate the policy, or that accepted an older `TermsVersion`, is
    not built here._
  - _**The age gate is a self-declared checkbox and stops nobody who is willing to tick it.** This is
    deliberate over storing a real date of birth: a self-declared DOB gives no more assurance and
    would create a new field needing its own retention, export and erasure handling for no actual
    gain._
  - _**Google sign-in accepts the terms implicitly**, via the consent line under the button, rather
    than through an explicit tick, the same checkbox the direct-registration path requires._
  - _**There is no automated data-retention or purge job.** Account data is kept indefinitely until
    a user triggers their own erasure; the retention/purge job itself is 8a.3's item, not this one._
  - _**Deletion does not lift sanctions, deliberately**, so deleting an account and re-registering
    does not launder a ban, though a fresh email address still evades it entirely, exactly as 8.5
    and 8a.4 already book under IP bans and device fingerprinting._


  _**A version-drift trap this pass walked into, recorded because the next edit will hit it too.**
  `TermsVersion.Current`, both `Last updated` / `Version` lines in `docs/legal/`, and the two
  hand-mirrored copies in `ui/app/terms/page.tsx` and `ui/app/privacy/page.tsx` are **five** places
  holding one date, and `TermsVersion.cs`'s own comment says "change one, change all three" while
  undercounting its own list. Editing the transfer paragraph bumped `PRIVACY.md` alone, which left
  the deployed page showing the old date **and missing the new section entirely**, and would have
  stamped every new registration with a version string that matched no document. All five now read
  `2026-08-16`. **The `ui/app/*/page.tsx` copies are hand-maintained duplicates of the Markdown, not
  renders of it**, so every future policy edit is two edits, and the drift is silent._

  _**The drift is no longer silent (2026-08-16).** `LegalDocumentDriftTests` fails the build on
  both halves of what happened above: every one of the five copies must carry
  `TermsVersion.Current` as its `Last updated` / `Version` date, **and** the numbered sections of
  each Markdown document must all exist as numbered `<h2>`s on its page, which is the assertion
  that would have caught the missing section rather than only the stale date. Section prose is
  deliberately not compared (the page legitimately shortens a parenthetical and uses typographic
  quotes); the numbering is what must not drift. Verified by mutating one date and watching it
  fail, not merely by watching it pass. **The duplication itself is untouched**: rendering the
  Markdown instead of hand-mirroring it is still the right fix and is still not done, so a policy
  edit remains two edits, now with a guard that says so out loud._
  ```



---



## Phase 9 - Professional-Platform Gaps (what a mature site has and this does not)

> Written 2026-08-07 from a gap review of this roadmap **against the code**, asking one question: what
> does a professional chess platform have, technologically, that KiriChess has no line for? Every item
> below is a gap this roadmap never named, items it already tracks (DH.0–DH.5, 8.7, 8a.1's DNS half,
> 8a.2's Art. 13 paragraph) are deliberately excluded.
>
> **The reframe, and the reason this phase exists.** Phases 0–8a are unusually strong on the things
> solo projects skip, clock correctness, live-game durability, GDPR, retention, moderation posture,
> and unusually honest about what is unverified. But **every phase ends with the same paragraph**:
> *"Not proven: nothing is deployed."* The roadmap reasons carefully about correctness and contains no
> **mechanism** that converts unproven into proven. 9b is that mechanism, and it is the part of this
> phase that changes what the rest of the document is allowed to claim.
>
> The gaps cluster in three places the roadmap never looks: **(1)** nothing has ever been verified in
> a browser or under load, **(2)** the supply-chain and edge-security layer does not exist at all, and
> **(3)** there is no liquidity mechanism for the empty lobby Phase 7 was written to solve.
>
> **This phase is grouped by *when to build it*, not by topic**, because the topics interleave: two
> supply-chain switches are an afternoon and should precede everything, one database line must land
> before Neon exists, and two items usually filed under "polish" are hard prerequisites for the soft
> launch. The six groups below are **stages, in order**, each with the gate that closes it.
>
### 9c - Ship it (rides with DH.0–DH.5)

> Everything here is either a prerequisite of going public or is meaningless until something is
> running. Provisioning can start during 9b; the items below land around it.

- [x] **9c.2** `[Owner]` **A staging environment**, DH.0–DH.5 go from nothing to production in one
  ```
  step, with no environment to make the first mistake in. Neon branching makes a staging database
  nearly free and a second Fly app is a few dollars; this is where the CI/CD workflows (6a.8/6a.9, both
  **never executed**), the Fly `[metrics]` block, the Data Protection key ring and the SignalR
  backplane get their first real run. **Provision it as part of DH.1/DH.2, two of everything, once,
  rather than retrofitting it after production exists**, which is when it never happens. You
  create the apps; the second `fly.*.toml` pair and the environment split in CD follow from this spec.


  _**Two corrections found 2026-08-16 while trying to deploy 9c.5's fix, both of which change what
  "one secret away" means.**_

  - _**"Never executed" is no longer true, and the truth is worse.** The Deploy workflow has run
    three times and **failed all three**, every one at `Authenticate: token validation error`:
    the `FLY_API_TOKEN` repository secret is invalid or expired. It has therefore never deployed
    anything, to either environment. Every production release so far (Fly is at `v11`) was pushed
    **by hand from a laptop**, which is why nothing ever surfaced this. Issue a new token
    (`fly tokens create deploy`) and reset the secret; until then the `ConnectionStrings__Postgres`
    secret above is the second blocker, not the first._
  - _**Everything this item shipped lives on `dev` and `main` has none of it.** The staging jobs,
    the `environment` dispatch input and the second `fly.*.staging.toml` pair are all dev-only, so
    `main`'s `deploy.yml` still takes no inputs and knows nothing about staging. A dispatch against
    `main` with `-f environment=staging` fails with `Unexpected inputs provided`, and
    [DEPLOYMENT.md § 2.1](ops/DEPLOYMENT.md#21-staging-9c2) currently describes a pipeline the deployed
    branch does not have. This resolves itself whenever dev lands on main, but until then the
    runbook and the reality disagree._

    _**Both corrections are spent as of 2026-08-17.** The token was rotated (see the correction at
    the top of this document, including why the obvious command is the wrong one), and the release
    merged as `344015a` at 04:47 UTC, 87 commits and all eight checks green. `main`'s `deploy.yml`
    **now has** the `environment` input and the staging jobs, so the runbook and the reality agree
    again. That merge also put `restore-drill.yml` on the default branch, which is what 9c.4 was
    waiting for._

  _**Closed 2026-08-17, and the one remaining secret is set.** `ConnectionStrings__Postgres` was
  set on `kirichess-api-staging` by the owner, run 31997770961 went green on both jobs with
  migrations clean, `https://kirichess-api-staging.fly.dev/health/ready` returns
  `{"status":"ready","redis":"disabled"}` and the staging UI answers 200. **Staging now deploys
  itself on every push to `main`.**_

  _**The runbook needed two fixes before it worked, both now in
  [DEPLOYMENT.md § 2.1](ops/DEPLOYMENT.md#21-staging-9c2)**, and the second is the more reusable
  lesson. `neonctl connection-string` needed `--role-name` and `--database-name` to name a
  specific branch's credentials rather than guessing. And the whole block was **bash-only on a
  PowerShell machine**: `${var#pattern}` is not PowerShell syntax and there is no inline
  `VAR=x cmd` form, so pasting it does not half-work, it fails on every line. A PowerShell form now
  sits beside the bash one. **This is the trap 9c.6 is about to hit**, which is why its own § 11
  block was given the same treatment in advance._
  ```
- [x] **9c.4** `[Owner]` **Run the restore drill once**, 6a.7/8a.3 chose Neon PITR as a decision and both
  ```
  admit **nobody has run the drill**. Do it the day DH.2 creates the project, while the database is
  empty and a mistake costs nothing: restore to a branch, boot the app against it, assert a known game
  replays. Automating it monthly is 9e.8; doing it at all is here.


  _**2026-08-16: the drill is now a script rather than a procedure, written as 9e.8's automation
  half and shared with it deliberately.** [scripts/restore-drill.sh](../scripts/restore-drill.sh)
  is the whole of both items, so the once-by-hand run and the monthly run cannot drift into
  testing different things. It branches Neon at a past timestamp, boots this build against the
  branch, asserts `__EFMigrationsHistory` matches the newest migration in the checkout, replays
  the most recently finished game through `/api/games/{id}/replay`, and deletes the branch on the
  way out including on failure. Runbook and the failure-meaning table are in
  [DEPLOYMENT.md § 5.2](ops/DEPLOYMENT.md#52-the-restore-drill-scripted-and-scheduled-9c4--9e8).
  **Still the owner's step, and a smaller one:** `NEON_API_KEY` and a go-ahead, then
  `NEON_API_KEY=... ./scripts/restore-drill.sh`. It reads production only to branch it and never
  writes to the production branch. **Note the empty-database caveat cuts both ways:** with no
  games yet, the replay assertion warns rather than proves, so the drill's strongest current
  assertion is the schema one, and it gets stronger by itself once real games exist._

  _**Owner said go 2026-08-16. Half-unblocked, and the remaining half is a sequencing fact rather
  than a decision.** `NEON_API_KEY` is now set as a **repository secret**, taken from the
  `.env.neon` already on the owner's machine, so the CI path needs nothing further. **Run it by
  dispatching `restore-drill.yml`**, not locally: `psql` and `jq` are both missing from the
  owner's machine. The branch password stays in the secret store (9c.2 hit the same wall). **The one blocker left is that
  `restore-drill.yml` only exists on `dev`**, and `workflow_dispatch` requires a workflow to be on
  the default branch, so the drill becomes runnable the moment the dev release lands on `main`.
  That release is open as PR #31 and is currently red for reasons that have nothing to do with
  this item, see 8.7._

  _**`.env.neon` also holds an unused `RESTORE_DRILL_CONNSTRING` that nothing in the repo reads.**
  The drill deliberately creates a fresh branch and reads that branch's own connection string,
  because a fixed connection string would test a database that was never restored, which is the
  one thing the drill exists to prove. If it was put there to sidestep the credential-read block,
  it is the wrong trade and the CI path is the right one._

  _**Ran and passed 2026-08-17 (run 31996903275). All four assertions, no skips.** Restore point
  `2026-08-17T04:10:01Z`, schema `20260814043103_AddPatronStatus`, and the replay found a real
  finished game rather than warning past it: `f7f19ebb`, 30 plies, `BlackWins`. The empty-database
  caveat above is therefore spent, production has games now and the strongest assertion is no
  longer the schema one. **PITR is proven rather than assumed**, which is what DEPLOYMENT.md § 5's
  whole recovery plan rested on._

  _**It took four attempts, and the three failures are worth keeping, because each was invisible
  until the one above it was fixed.** (1) `neonctl connection-string` refuses to guess a role when
  a branch has several, and this project has four. (2) Then the same refusal for the database,
  between `kirichess` and Neon's empty default `neondb`. Both were hard errors, which was luck:
  had it picked `neondb` there would have been no `__EFMigrationsHistory` and the drill would have
  reported a restore failure that never happened. (3) **The real bug, and the one that mattered:
  the BOOT assertion had never worked.** The script gave `psql` the raw `postgresql://` URI, which
  is correct, and gave the app the same URI with `;SSL Mode=...` appended, which Npgsql rejects at
  parse time because it reads key/value pairs only. `CanConnectAsync` swallows the parse error and
  returns false, so `/health/ready` answered 503 in about a millisecond for the full 120s window,
  **indistinguishable from a dead database**. Left unfound, the monthly drill would have failed
  every month for a reason that had nothing to do with the backups. Fixed with the conversion
  DEPLOYMENT.md § 2.1 already documented for setting the secret by hand._
  ```
- [x] **9c.5** `[Owner]` **Alerting**, 6d built metrics and exception tracking and **nothing
  ```
  fires**. `/metrics` is, by its own note, scraped by nobody; there are no alert rules, no external
  uptime check, and no status page. Sentry will email on a new issue once a DSN exists, but nothing
  watches the numbers that say the server is _unwell rather than broken_: `/health/ready` failing,
  error rate, matchmaking queue depth stuck, flag-fall sweep duration climbing. Observability without
  alerting means players notice first, and until 8.7 there is nobody to notice, so this must exist
  **at** launch rather than after it. The owner picks and pays for the destination; the rules
  themselves are config that can be written against the instrument names 6d.3 already defines.


  _**Vendor named 2026-08-15: Better Stack. The config half is now written, not yet run.**
  [infra/betterstack/](../infra/betterstack/) provisions the `/health/ready` uptime check plus
  threshold alerts on the three signals this item names (hub error rate, matchmaking queue depth
  stuck, flag-fall sweep duration climbing) against 6d.3's instrument names, with the runbook in
  [DEPLOYMENT.md § 10](ops/DEPLOYMENT.md#10-alerting--uptime-9c5). **This does not close the item**:
  it is committed Terraform, not a monitor that has ever fired, the same "not proven" status
  6d.2/6d.3 shipped in before a real Sentry project existed (see
  [infra/betterstack/README.md](../infra/betterstack/README.md) for exactly what is confirmed vs.
  inferred in the metric names, and its "Confirm it works" section). **Still the owner's step:** the
  Better Stack signup itself - a fifth account alongside DH.5's four, not one of them, since
  Better Stack was never on that list - and the two API tokens `terraform apply` reads. After
  that, one command turns this from paper into a running check._

  _**2026-08-15: `terraform apply` has run. It is a running check, half of it. The account was
  created, the tokens issued, and all 12 resources now exist in Better Stack - the escalation
  policy, the `/health/ready` monitor, the Prometheus scrape source, the dashboard, its four
  charts and three alerts.** One real bug turned up on the way and got fixed: the account's
  default escalation severity is named `"High severity"` (lowercase), not `"High Severity"` as
  Better Stack's own Terraform example shows - `data.betteruptime_severity.high` failed against
  the real account until `main.tf` was corrected to match.

  **The uptime half is real right now**: the monitor is live and polling `/health/ready` every
  3 minutes. **The metrics half is not** - `curl https://kirichess-api.fly.dev/metrics` with the
  correct bearer token returns a bare **404**. Until it's fixed, Better Stack's scrape source has
  nothing to scrape, so `games_live`, `queue_depth` and `hub_error_rate` have no real data behind
  their charts/alerts no matter how correct the SQL is - a different, more basic failure than the
  "unverified metric name" caveat `infra/betterstack/README.md` already documents. **Not done
  yet** - ticking this item means the whole thing is live, and right now only the uptime half
  is._

  _**Cause found 2026-08-16, and the paragraph above guessed wrong about it, which is worth
  recording because the wrong guess was the plausible one.** It was **not**
  `MetricsOptions.Enabled` evaluating false: `Metrics__ScrapeToken` was set, `Metrics__Port` is
  set to `9091` in [fly.backend.toml](../fly.backend.toml), and `Enabled` is
  `Port > 0 || token`, so it was true the entire time. The 404 came from the guard in
  [MetricsEndpoint.cs](../src/KiriChess.Server/Infrastructure/MetricsEndpoint.cs), which treated
  its two gates as a **conjunction**: any request whose local port was not 9091 was 404'd
  **before the token was read**, and only 8080 is published by `[http_service]`. Every external
  scraper was therefore permanently locked out regardless of credentials, while `/health` on the
  same machine answered 200 - which is precisely why this looked like a misconfigured process
  rather than a gate doing what it was written to do. The Terraform comment in
  [infra/betterstack/main.tf](../infra/betterstack/main.tf) states the intended contract
  ("the token above is the entire access control") and had been true of the design and never of
  the build._

  _**Fixed in code: the gates are now alternatives.** The private metrics port is served
  tokenless, because Fly's built-in Prometheus scrapes it and cannot be configured to send a
  header; any other port requires the exact bearer token; and with no token configured the public
  port stays 404, so an unconfigured deploy still fails closed and 6d.3's original contract is
  unchanged. The decision matrix is now a pure function (`MetricsEndpoint.Evaluate`) with
  `MetricsScrapeGuardTests` over every combination, because the case that actually matters in
  production, the private port with no token, is unreachable from `WebApplicationFactory`
  (TestServer synthesizes a connection with `LocalPort` 0), so an integration test alone would
  have left the regression invisible until a deploy - the same way it was invisible this time._

  _**The fix is merged to `main`** ([PR #30](https://github.com/MichielGekiere/kirichess/pull/30),
  `606aa2e`, four green checks), cherry-picked rather than merged wholesale so that none of dev's
  56 unreleased commits rode along with an alerting fix. It is landed code, not deployed code:
  production is still answering 404, see the deploy blockers below._

  _**The metric names are no longer a caveat, they are a test (2026-08-16).** The paragraph above
  used to say the first successful scrape would be what finally tested `queue_depth` and
  `hub_error_rate`. That turned out to be avoidable: `MetricsContractTests` parses the `metric_*`
  lines out of [infra/betterstack/main.tf](../infra/betterstack/main.tf)'s `locals`, exercises each
  instrument and asserts the exporter really emits every name, with no account and no deploy
  involved. **All four are confirmed**, including the flag-fall histogram that was the least
  certain of them, so `flagfall_sweep_slow` is **unpaused** and `infra/betterstack/README.md`'s
  per-name confidence grading is gone. Renaming an instrument in C# now fails the build instead of
  quietly turning a chart into "no data", which was the whole failure mode the grading hedged
  against. What remains genuinely unproven is the **SQL**, not the names._

  _**One finding fell out of writing that test, and it changes how a chart reads.**
  `kirichess_matchmaking_queue_depth` is an observable gauge emitting one measurement **per pool
  that currently has somebody waiting**, so with an empty queue, the normal state, the series does
  not exist on the wire at all and the chart reads "no data" rather than "0". Same for
  `hub_error_rate` until a hub method actually throws. Neither is a fault and neither weakens its
  alert (an absent series cannot cross a threshold, and `on_missing_data = "dont_fire"` says so
  explicitly), but **an empty chart there is not evidence of a broken scrape**, which is exactly
  the wrong conclusion to reach at 3am while looking for one._

  _**Still the owner's step, and the deploy turned out to be blocked twice over rather than being the
  one easy action the previous paragraph promised.**_

  - _**CI cannot deploy: `FLY_API_TOKEN` is invalid.** The Deploy workflow has **never succeeded**,
    two runs in its life, both dying at `Authenticate: token validation error`, and a third
    triggered 2026-08-16 died identically. It fails at authentication before touching anything, so
    production is never at risk from trying. Issue a new token (`fly tokens create deploy`) and set
    it as the repository secret. **This is a bigger fact than this item**: it means every green
    pipeline in this repo has been building and testing a deployment path that has never once run
    to completion._
  - _**A laptop `flyctl deploy` is not the path.** The fix can be in the checkout and `flyctl` can
    be authenticated, and a deploy from the laptop is still how the dead token stayed hidden.
    Production deploys go through CI._

  _**What actually closes this item**, once one of those is unblocked:_

  ```bash
  fly deploy --config fly.backend.toml --remote-only   # backend only; the UI needs nothing here
  curl -o /dev/null -w '%{http_code}\n' https://kirichess-api.fly.dev/metrics   # expect 401, was 404
  cd infra/betterstack && terraform apply              # pushes paused = false
  ```
  *Then confirm a chart draws a real number. **Tick it only then**: everything above is landed and
  tested code plus a live uptime monitor, and the metrics half has still never scraped anything.*
  ***Closed 2026-08-17, all three lines above executed.** The token was rotated (*`fly tokens create org`*, the deploy-scoped form being wrong for a repo with four apps on one secret), production
  deployed through CI as run 31998072354, and* `/metrics` *now answers **401** where it answered 404,
  on both* `kirichess-api.fly.dev` *and* `api.kirichess.com`*. The second* `terraform apply` *landed the*
  `flagfall_sweep_slow` *unpause, and a follow-up* `terraform plan` *returns* `No changes`*, so Better
  Stack matches the committed config exactly.*
  ***One correction worth keeping, because it was made in this document earlier the same day.**
  The launch-queue table was edited to say the* `terraform apply` *step was spent, on the grounds that an
  apply had run on 2026-08-15. It had, but* `c2c0664` *unpaused the alert on 2026-08-16, **after**
  it, so* `paused = false` *had never reached Better Stack and the alert sat paused for a day while
  the roadmap said otherwise. The lesson is narrow and repeatable: **"terraform apply has run" is
  not a durable fact**, it is only ever true as of a commit, and the check that settles it is*
  `terraform plan` *returning* `No changes` *rather than any note in a document.*
  ***The deploy blockers above are both spent, and one of them inverted.** A laptop* `flyctl deploy`
  *and a local* `terraform apply` *are not required. A* `workflow_dispatch` *with a valid token
  deploys production through CI on the owner's go-ahead, with a run record, which is strictly
  better than the laptop deploys that hid the dead token for five days.*
  ***Owner said go on the*** `terraform apply` ***(2026-08-16); it stopped on a missing tool, not a
  permission.** Terraform was not installed locally. The three tokens it needs are
  already in* `infra/betterstack/terraform.tfvars` *and* `terraform.tfstate` *records the 12 resources
  from the 2026-08-15 apply, so this is one command the moment terraform exists. Installing it
  system-wide is the owner's decision.*
  ***And the apply is not what makes the charts work.** It only pushes* `paused = false` *on the
  flag-fall alert. The charts need the **deploy**, which is blocked on* `FLY_API_TOKEN`*. Running
  the apply first is harmless and proves nothing, so do not read a green apply as progress on
  this item.*
  ```
- [x] **9c.6** `[Owner]` **Log retention / aggregation**, 6d.2's JSON-to-stdout is the right format
  ```
  going to a destination measured in hours (Fly's buffer). The first investigation of a cheating report
  or a lost game will find the evidence already gone, and logs are currently the **only** operational
  record behind 6e.2's report table, which is otherwise read with `psql`. Same split as 9c.5: choosing
  and provisioning a sink is human, shipping to it is a config change.


  _**Vendor inherited from 9c.5, no new signup: Better Stack, same account. The config half
  is now written, not yet run.** [infra/betterstack/main.tf](../infra/betterstack/main.tf) adds
  a second `logtail_source` (`platform = "fly_io"`, next to 9c.5's `prometheus_scrape` one) and
  a new [outputs.tf](../infra/betterstack/outputs.tf) exports its token and ingesting host as
  `sensitive` Terraform outputs, never written to a file this repo tracks.
  [fly.logshipper.toml](../fly.logshipper.toml) deploys
  [superfly/fly-log-shipper](https://github.com/superfly/fly-log-shipper), a prebuilt Vector
  image, as its own Fly app subscribed to this org's internal log stream, so 6d.2's
  JSON-to-stdout survives past Fly's few-hour buffer instead of being the only copy of a
  cheating report's evidence. Runbook in
  [DEPLOYMENT.md § 11](ops/DEPLOYMENT.md#11-log-retention--aggregation-9c6). **This does not close
  the item**: it is committed Terraform and a committed fly.toml, not a log line that has ever
  reached Better Stack, the same "not proven" status 9c.5 shipped in.

  **Still the owner's step:** `terraform apply` (one run covers both this and 9c.5's resources, if done
  together), then for the shipper app itself, `fly launch --image ... --config
  fly.logshipper.toml`, `fly secrets set` with four values (`ORG`, a readonly `ACCESS_TOKEN`,
  and the two Terraform outputs above - the runbook has the exact commands), and `fly deploy
  --no-public-ips`. All four secrets go through `fly secrets set`, never into this
  repo._

  _**2026-08-19: the launch already happened, and it is why nothing arrived.**
  `kirichess-log-shipper` exists (`personal` / `ams`, v1 on 2026-08-17) with **no secrets** and
  **two stopped Machines** (the default `--ha` pair). Vector's NATS source requires
  `ACCESS_TOKEN`, so those Machines exited and Live tail is still empty. The Better Stack sink
  is in Terraform state; do not `terraform apply` or `fly launch` as part of finishing this.
  Remaining work is in [DEPLOYMENT.md § 11.2](ops/DEPLOYMENT.md#112-recreate-powershell-from-the-repo-root):
  destroy the two stopped Machines, `fly secrets set` the four values, `fly deploy --config
  fly.logshipper.toml --no-public-ips --ha=false`. The committed toml no longer has an
  `[http_service]` (Fly's own log-shipper docs: this app takes no inbound traffic) and adds a
  Vector `/health` check so a silent hang is visible._

  _**Done 2026-08-19.** Those two Machines were destroyed, the four secrets set, and the app
  redeployed `--ha=false`. One Machine in `ams`, no public IPs, Vector `/health` passing,
  sink `better-stack` configured. Better Stack Live tail on **KiriChess app logs** showed real
  `kirichess-api` JSON (EF command on `/api/auth/register`). Secrets stay on Fly, never this
  repo. Recreate from [DEPLOYMENT.md § 11](ops/DEPLOYMENT.md#11-log-retention--aggregation-9c6) if
  the app is torn down._
  ```



**Gate:** the app is on a real domain over TLS, staging exists, something wakes you when it breaks, and
last week's logs are still readable. This is also the point at which most of Phase 6's exit criterion
finally becomes true.

### 9e - After the first real players (built in the order they ask for it)

> Deliberately after 8.7, because this is the first stage whose priorities should be set by evidence
> rather than by reasoning. 8a.5's funnel and retention curve exist precisely to order this list; if
> the numbers disagree with the sequence below, follow the numbers.

- [ ] **9e.8** `[Owner]` **Load test at real scale**, the second half of 9b.3, now that real
  ```
  hardware and a real database exist. Re-run the capacity test against the deployed machine to get the
  number DH.1's single-instance decision actually rests on. The restore-drill automation half is done
  and archived below; this row is the load run only. The harness is already built, so this is
  scheduling and interpretation rather than design.


  _**2026-08-16: the automation half is written and committed.**
  [restore-drill.yml](../.github/workflows/restore-drill.yml) runs
  [scripts/restore-drill.sh](../scripts/restore-drill.sh) on the 1st of each month, plus a
  `workflow_dispatch` button. Same script as 9c.4's manual run, on purpose. The four assertions
  and what each failure means are tabulated in
  [DEPLOYMENT.md § 5.2](ops/DEPLOYMENT.md#52-the-restore-drill-scripted-and-scheduled-9c4--9e8); the
  one that carries this item's argument is the schema check, since "a backup nobody has restored
  from **since the schema last changed**" is precisely a restore that boots and then fails on the
  first query touching a new column. **This does not close the item**, on both halves: it has
  never run (it needs `NEON_API_KEY` in repository secrets), and the load-test half still needs
  the owner's window and go-ahead._

  _**One honest gap in the automation, stated rather than papered over.** Failure notification is
  GitHub's own e-mail on a failed scheduled run, the only path needing no extra account. That
  makes a **disabled** schedule indistinguishable from a passing one, and GitHub disables cron on
  repos with 60 days of no activity. A heartbeat into 9c.5's Better Stack account is the upgrade,
  and it is deliberately not built here: 9c.5's own metrics half is not live yet._

  _**Owner gave the load run a go-ahead 2026-08-16 ("sure"). It is authorized and deliberately
  not yet run, because running it now would measure the wrong thing.** The deployed backend is
  still the pre-fix build (`v11`, 2026-08-15), and the whole point of this item is to get "the
  number DH.1's single-instance decision actually rests on" from **real hardware running the real
  build**. **Sequence: deploy first, confirm `/metrics` returns 401, then run the ladder** so the
  run can be watched through the instruments it is meant to exercise, which is also the only way
  the queue-depth and hub-error series produce anything at all (9c.5 records why they are absent
  on an idle server). Running a capacity ladder blind against a stale binary produces a number
  nobody should quote._

  _**The restore-drill half is unblocked**: `NEON_API_KEY` is set as a repository secret, and the
  monthly schedule starts working once `restore-drill.yml` reaches `main` with the dev release._

  _**Both halves moved on 2026-08-17, and only one of them is finished.** The release landed
  `restore-drill.yml` on the default branch and the drill then **ran and passed** as run
  31996903275, all four assertions including a real game replay, so the restore half is done and
  is recorded under 9c.4. **The load half is now gated on nothing but a window.** Its sequencing
  condition was "deploy first, confirm `/metrics` returns 401, then run the ladder", and both
  happened the same day: production deployed through CI as run 31998072354 and `/metrics` answers
  401. The ladder would now measure the right binary through live instruments, which is exactly
  what the paragraph above asked for. It stays unrun because it is sustained load against a single
  paid production instance, and that remains the owner's call, per the
  standing note above._

  _**The ladder ran 2026-09-26/27, on the owner's go-ahead. Quote the saved files, not a rung
  that was not written down.** First against the build that rewrote the whole jsonb move list on
  every ply: [prod-ladder-n50.json](../loadtest/results/prod-ladder-n50.json) passed.
  [prod-ladder-n100.json](../loadtest/results/prod-ladder-n100.json) finished the window with 100
  games still live and 0 hub errors; the save queue climbed from about 60 to 90, client p95 was
  1511 ms, 85% of planned moves happened, and the overall verdict is Void. `AppendGameMoves`
  shipped the same night ([PR #75](https://github.com/MichielGekiere/kirichess/pull/75),
  production image `deploy-13c9b858`) and the ladder was repeated.
  [append-ladder-n50.json](../loadtest/results/append-ladder-n50.json) passed (p95 155 ms, queue
  empty). [append-ladder-n100.json](../loadtest/results/append-ladder-n100.json) held: queue mean
  about 3, no hub errors, no failed snapshots, client p95 530 ms, overall Warn.
  **There is no `n150` file. Do not quote a 150-game rung.** The steady number from the files is
  **100 concurrent games on `shared-cpu-1x` / 512 MB.** Fly's own CPU was not read; the harness's
  0% is a missing local Docker sample, not a measurement. The next step is that CPU reading on a
  100-game rung. A higher rung changes the cap only once its result file is in
  `loadtest/results/`. Do not quote the August "a bigger machine will not help" line for this
  build._
  ```



### 9f - Only if it takes (traction-gated, and none of it before then)

> Each of these is real work whose payoff scales with a playerbase that does not exist yet. Building
> them early is the classic way to spend a year on a site nobody visits, the Funding & Sustainability
> section's argument, applied to engineering effort instead of money.

- [ ] **9f.1** **Tournaments / arenas**, an arena is the standard answer to a sparse
  ```
  playerbase: it does not create players, it **concentrates** them into a window so the pool is briefly
  deep enough to pair well. Real arena software (pairing, standings, berserk-style incentives) wants
  ~30–100 interested people to be worth its complexity; below that the published-weekly-time version in
  9d.8 does the same job for a sentence. **Trigger: enough regulars that a scheduled hour reliably
  fills.** Comfortably the largest new subsystem in this phase, repeated pairing, live standings and
  scheduled state on top of the ownership model 8.4 built.


  _**Trigger judged 2026-08-16: not yet.** The owner said no. **Do not
  start this**, and do not treat "it is the largest thing in the document" as a
  reason to, which is the exact temptation this stage's preamble warns about: building it early is
  the classic way to spend a year on a site nobody visits. Revisit only when the playerbase
  has changed. The honest state of the evidence is that 8.7 has not happened, so there are no
  regulars and no funnel, and the answer could not have been anything else yet._

  _**AD.7 (2026-09-04) does not unpark this.** Hitting 25 / 8 / 2 at +14d is a launch that landed,
  not an hour that fills. Revisit when a Friday 20:00 UTC hour produces two concurrent human games
  without the owner filling a seat._
  ```
- [ ] **9f.2** **Full accessibility**, keyboard move entry and a screen-reader-navigable
  ```
  board, on top of 9d.4's baseline. Lichess is notably strong here and it is part of why it is trusted.
  **Planned in detail because there is no prior art for the hard half**: announcing a
  _clock economy_ to a screen reader is a variant-specific design problem, a blind player needs to
  know that a capture just cost four seconds as urgently as they need the move itself, and no existing
  chess site has had to solve it.


  _**Built 2026-08-16 and now waiting on exactly that pass.** `lib/a11y.ts` (pure, unit-tested),
  `components/LiveAnnouncer.tsx` (two always-mounted live regions) and
  `components/AccessibleBoard.tsx` (a `role="grid"` of 64 cells with roving tabindex, plus a
  move-entry input), wired live into the game page and read-only into replay and watch.
  **`react-chessboard` v4 was not touched**: it has no per-square semantics and cannot be given
  any, so the accessible board is a second, parallel view over the same state, `sr-only` until it
  takes focus and visible after, which serves a screen-reader user and a sighted keyboard user
  with one component._

  _The half the item calls unprecedented resolved into four rules. **A move and its price are one
  utterance** in a fixed order ("Knight takes e5 pawn. You minus 2, opponent plus 4. Check."),
  because two utterances can be interrupted between them and the second alone is meaningless.
  **Deltas are pushed, absolute times are on demand** (`c` key), since a ticking clock announces
  nothing useful and floods the queue. **Urgency thresholds interrupt** at 60/30/15/10 and every
  second under 5, on your own clock only. **A candidate move's cost is spoken as part of the
  target square's name before you commit it** ("e5, black pawn, capture, costs you 2 seconds"),
  which is what makes the economy decision information rather than narration; `computeCostPreview`
  already existed and was already threaded into `Board` as a prop `Board` ignores, so this feature
  is its first real consumer._

  _One defect was found and fixed during verification rather than shipped: urgency was first
  derived from the current second alone, which needs a reading to land inside every threshold
  second. The ticker is 250ms and a stalled tab can jump 30.4 to 29.2, silently swallowing the
  announcement. It now derives a crossing from a **pair** of readings, which also deletes the
  re-arm bookkeeping, since winning time back and spending it again is simply a second crossing._

  _**Proven:** `tsc --noEmit` clean, lint 0 errors (8 pre-existing warnings unchanged), `npm test`
  99 passing (63 before, 36 new in `lib/a11y.test.ts`), production `next build` green with all 27
  routes still prerendering as they did. **Not proven, and this is the whole remaining item:** what
  any of it sounds like. Nothing here has been heard with a screen reader. **The human pass is
  still required to tick this**, and the pass may well move the wording; the four rules above are
  the part worth arguing with while listening._

  _**Ungated 2026-09-04.** Owner: code is done; the verification pass is not a launch constraint
  (launch traffic will not include screen-reader users worth gating on; no WCAG duty, see
  [ACCESSIBILITY.md](decisions/ACCESSIBILITY.md)). P-5 gaps that were code-only landed the same
  day (selection is spoken, Dialog focus trap, clock `spokenTime` names, promotion prompt,
  NVDA-PASS.md refreshed with draw/rematch/overlay steps). **The remaining pass sits after 9f.1**,
  not in the #1–#11a window. Do not claim WCAG until it has been run._
  ```
- [ ] **9f.4** **Internationalisation**, every string is hardcoded English across ~21 routes, and
  ```
  the retrofit cost grows with each page added, so there is an argument for extracting strings early. It
  loses to the fact that translations need translators, and translators need a community. Interacts with
  8a.4's ASCII-only display-name rule, which is already a stated cost to non-Latin-script players.
  **Trigger: the first non-English community that adopts the variant.** Mechanical once the library and
  routing strategy are chosen. That choice is the only design decision left, and it is one paragraph.


  _**Decided 2026-08-16. The owner accepted the recommendation:**_

  - _**No machine-translated locales.** The recommendation above is now the decision. The rule
    this app is built around is unfamiliar to every reader by definition, so a reader cannot tell
    a mistranslated rule from a broken one, and the failure is silent: they conclude the game is
    buggy and leave without reporting anything. A locale ships when a human who plays the variant
    has read it, or it does not ship._
  - _**Library: `next-intl`**, routing by locale-prefixed path (`/en/...`, `/nl/...`). It is the
    App Router-native option and the routing strategy is the half that is expensive to change
    later; the string extraction is not._
  - _**The extraction may be done early, the locales may not.** The item's own argument, that the
    retrofit cost grows with every page added, is correct and does not depend on the trigger. So
    strings may be extracted into an English catalogue whenever it is convenient, shipping a
    site that behaves identically. **Adding a second locale stays trigger-gated** on the first
    non-English community, per the item's text._

  _**The extraction is done, 2026-08-16.**
  split into two gated tasks because the second one had to wait for 9f.2 to finish rewriting the
  game, replay and watch pages. `next-intl@4.13.6` (which declares `next: ^16.0.0`, so the
  recorded library choice holds on Next 16.3.0), one catalogue at `ui/messages/en.json`,
  **888 keys across 53 namespaces**, 57 files. The site renders identically: verified by diffing
  every removed string against the catalogue (315 checked across both tasks, zero lost, and the
  only flags were `'` entities that became the same U+0027 apostrophe they had always
  rendered as), plus a `next start` pass over the built site._

  _**One recorded decision is deliberately half-implemented, and this is the part to re-read
  before touching it.** The decision above says locale-prefixed paths (`/en/...`, `/nl/...`).
  **That target stands; the route restructure does not ship yet.** Moving all 23 routes under
  `app/[locale]/` days before 8.7 buys a one-language site nothing and moves four things pinned to
  today's unprefixed paths: the backend's own transactional mail links
  (`Infrastructure/Email/EmailTemplates.cs` builds `/verify-email`, `/reset-password` and
  `/game/{id}`, some already delivered), 9f.3's `sw.js`, which pins `/offline` and `/learn` by
  exact path, the e2e suite's `goto` paths, and the sitemap and canonicals. The expensive,
  ever-growing half of an i18n retrofit is the **copy**, and that half is now permanently done;
  the routing half touches no copy. The migration recipe lives in `ui/i18n/request.ts` as a
  comment, at the place someone would change it: add `routing.ts` with `localePrefix:
  "as-needed"` so `/learn` stays `/learn` and Dutch becomes `/nl/learn`, add the middleware, move
  the routes, and leave `sitemap.ts`, `robots.ts`, `manifest.ts` and the icon routes at the root._

  _**Scoped out on purpose**, each for a reason rather than for time: **privacy and terms** (8a.2
  pins them to `docs/legal/*.md` and `TermsVersion.cs` with `LegalDocumentDriftTests` failing the
  build on drift, so a catalogue must not sit between a consent document and the version a user
  agreed to); **moderation and analytics** (staff tooling, audience of one); **`lib/rules.generated.ts`**
  (generated from `config/kirichess.rules.json`, so its numbers are data); and **`lib/a11y.ts`**
  (9f.2's announcements, which hold 36 of the frontend's 99 tests precisely because vitest here
  only runs `lib/**/*.test.ts` under a node environment; threading a translator through them
  would trade that provability for nothing while there is one locale, on a feature still awaiting
  its NVDA pass). `AccountDataCard`'s `"DELETE MY ACCOUNT"` also stays a literal: the server
  ordinal-matches it._

  _**Still trigger-gated, unchanged:** no second locale, no machine translation, and no locale
  switcher, since a switcher with one option is worse than none._

  _**The routing half landed, and Dutch with it, 2026-08-24, on the owner's instruction.** The
  route restructure is done exactly as the recipe above described: `i18n/routing.ts` with
  `localePrefix: "as-needed"`, all 23 routes under `app/[locale]/`, and `sitemap.ts`, `robots.ts`,
  `manifest.ts` and the icon routes left at the app root. Two deviations from that recipe, both
  forced by Next 16.3.0 rather than chosen: the middleware is **`proxy.ts`**, because Next 16
  deprecated the `middleware` file convention and renamed it; and `i18n/request.ts` reads the
  locale from **`next/root-params`** (introduced in 16.3.0) rather than next-intl's `requestLocale`,
  which 4.13.6 deprecates in favour of it. The second is not just a deprecation chase: it is what
  lets static rendering work without a `setRequestLocale()` call at the top of all 23 routes.
  `app/opengraph-image.tsx` had to move **under** `[locale]` too, not stay at the root: with the
  root layout at `app/[locale]/layout.tsx` there is no segment above it for a root-level metadata
  image to attach to, and leaving it there emits no `og:image` at all._

  _**`messages/nl.json`, 989 keys, is machine-translated and therefore does not satisfy this
  item's own rule yet.** The rule stands: a locale ships when a human who plays the variant has
  read it. What is mechanically proven is only structure, not meaning: `lib/locales.test.ts`
  fails if the key sets diverge, and every ICU placeholder and rich-text tag was compared
  key-by-key against English (0 mismatches) and parsed (0 failures). **The `rules` namespace, 223
  keys, is the half that matters** and is exactly what this item argued nobody can machine-check:
  a mistranslated clock rule reads as a broken game. Deliberate choices worth arguing with while
  reading: pool names (`Steal`/`Burn`/`Bounty`) and the time-control labels stay English because a
  Dutch player and an English player need the same word for the same mode; `schaakchip`,
  `herhalingsbeveiliging` and `conversierekening` are coinages mirroring English coinages; API
  scope names in `ApiTokensCard` and `developers` stay English because a bot author types them._

  _**A pipeline exists for the next locale, 2026-08-24.** `ui/scripts/gen-locale.mjs` drafts a
  catalogue from `en.json` via DeepL, reading the vocabulary decisions of the Dutch pass from
  `ui/i18n/glossary.json` (mode names and API identifiers protected verbatim, chess terms glossed).
  It exists for the reason this item keeps circling: the one-off cost of a locale is now trivial,
  and the recurring cost is not. `en.json` moved on eight commits in the six days before this was
  written, so `--fill`, which translates only the keys a locale is missing and leaves reviewed
  strings alone, is the mode that decides whether five locales are affordable to own. **The four
  tier-2 languages are deliberately NOT in `routing.ts`**: the trigger-gate is now cheap to honour,
  and a draft nobody has read is worth less than the hreflang it would claim._

  _**The four were written and then shipped, 2026-08-24, both on the owner's instruction.**
  `messages/es.json`, `fr.json`, `de.json` and `pt-BR.json` are complete
  1027-key catalogues. **DeepL was not used and no key was spent**: there is no `DEEPL_API_KEY`
  configured, so they were translated the same way `nl.json` was, by hand, with
  `gen-locale.mjs`'s glossary and its `icu-protect` rules applied by hand. What is proven is
  exactly what was proven for Dutch and no more: identical key sets, identical ICU placeholders and
  rich-text tags key-by-key, balanced braces, and `tsc`/lint/381 vitest tests/production build all
  green. **What is not proven is the meaning**, and for the 223-key `rules` namespace that is still
  the half that matters._

  _**`i18n/glossary.json` was filled first, deliberately, and it is the file to argue with.** Its
  `preferred` map now carries ~46 terms per locale, and its `$comment` records the four collisions
  that a machine gets wrong by default: **replay vs. repetition** (both are `repetición` /
  `répétition` / `Wiederholung` / `repetição` by default, and Rule 4 has to stay unambiguous, so a
  replay is `reproducción` / `relecture` / `Wiedergabe` / `reprodução`); **German
  `Umwandlung`**, which already means promotion in German chess, so the conversion bill is
  `Verwertungsrechnung` rather than the literal `Umwandlungsrechnung`; **French `classement`**,
  which is both leaderboard and rating, kept because the two never share a sentence; and **`toll`**,
  kept as a road toll (`peaje`, `péage`, `Maut`, `pedágio`) rather than flattened to a tax._

  _**Gender forced a rewrite that is worth knowing about before editing `learn`.** English
  "You moved a {piece}" cannot be translated directly: `{piece}` resolves to a catalogue string
  whose gender varies (`la torre` but `el peón`, `la tour` but `le pion`, `der Turm` but
  `die Dame`), and no single article works. The four locales therefore name the piece after a
  colon ("Has movido tu {piece}", "Vous avez pris une pièce adverse : {piece}",
  "Du hast eine Figur gezogen: {piece}"). **If someone adds an article back in front of `{piece}`,
  it will be wrong for roughly half the pieces and nothing in CI will catch it.**_

  _**They were added to `routing.ts` the same day, which drops this item's own gate.** The rule
  was that a locale ships when a human who plays the variant has read it; the owner chose to ship
  all four without that, the same call that shipped Dutch. So the switcher offers six languages and
  `sitemap.ts` emits `hreflang` alternates for all six, on four catalogues nobody has read. **The
  gate is the thing that was traded away here, and it is worth re-reading the argument above before
  treating this as settled**: a reader cannot tell a mistranslated clock rule from a broken one, and
  the failure is silent._

  _**What shipping them bought, concretely:** `lib/locales.test.ts` walks `routing.locales`, so all
  four are now under CI and can no longer go stale silently, which is what would have happened to
  them as drafts. Two of that file's assertions had to be rewritten to survive the jump from two
  locales to six: the brace walk called `expect` once per character (~600k assertions, 5s timeout)
  and the key-set check re-parsed the catalogue inside a `filter`. Both now assert once per locale
  over collected offenders. **The invariants are unchanged**, and a failure still names the exact
  key; only the assertion count is._

  _**English moved 87 strings under the four while they were being written**, including a new
  `AvatarPicker` namespace and a rewritten `about`. All four were reconciled afterwards, and
  `es`/`fr` were corrected where a parallel pass had written "las pools oficiales" against the
  glossary's `grupo`: one concept, two words, is the failure `glossary.json` exists to prevent.
  One live Dutch string (`rules.poolTableFootnote`) had gone stale the same way and was trimmed._

  _The correctness core is `ui/scripts/icu-protect.mjs`, covered by `lib/icuProtect.test.ts` against
  the real catalogue: whatever a translator does to the words, every placeholder, rich-text tag and
  plural branch has to come back. Writing that test paid for itself three times, each a bug that
  would have shipped silently: the sentinel `<x>N</x>` was itself matched by the rich-text-tag pass
  and re-stashed, so messages came back with raw markers in them; a sentinel containing whitespace
  was torn in half by anything splitting on spaces; and the placeholder-parity check counted the
  branch bodies of `{count, plural, one {code} other {codes}}` as placeholders, which would have
  rejected every locale that translates them, Spanish `one {codigo}` included. That last one lived
  in `lib/locales.test.ts` too, where it would have failed CI rather than the script._

  _**Known open point, not fixed:** the 404 page renders in Next's bare error document
  (`<html id="__next_error__">`), so it loses the nav, the footer and the `lang` attribute. This
  is a regression from the pre-move 404, which rendered inside the root layout. The copy is
  correctly localised and the status code is correctly 404; a nested `not-found.tsx` boundary was
  tried and does not change it. The documented fix is Next 16's `app/global-not-found.tsx`, which
  is still flagged experimental, needs a `globalNotFound` config flag, and bypasses the layout by
  design, so it would trade the missing `lang` for a 404 that cannot be localised at all.
  `e2e/accessibility.spec.ts` does not scan the 404, so nothing gates on this today._
  ```
- [x] **9f.5** **Public / bot API**, Lichess's API is a large part of why its ecosystem
  ```
  exists; third parties write clients, bots and analysis tools nobody funded. It depends on 9d.7 and is
  the cheapest form of leverage available to a solo project, but leverage on zero users is zero.
  _**That dependency is now met, and 9d.7 handed this item one decision on purpose.**
  [docs/api/openapi.json](api/openapi.json) exists and is guarded, so the endpoint surface is
  already described. What 9d.7 deliberately did **not** do is publish it: `MapOpenApi()` stays
  inside its `IsDevelopment()` guard and `ShouldInclude` filters to `api/`, because exposing the
  complete admin, moderation and detector route surface is a decision that belongs with this item's
  auth, versioning and rate-limit choices rather than ahead of them. Note also that the document
  describes HTTP only; a bot that wants live play needs the hub contract, 9e.9._
  **Trigger: the first person who asks for it**, which, for a variant with an unusual rule engine, is a
  genuinely likely event and worth having designed the endpoint surface towards. **Planned because
  every decision in it is permanent**: versioning, whether a bot authenticates with a JWT or a scoped
  token, which rate limits apply, and whether bot games are rated, 7.3 already answered the last one
  for client-side bots and the reasoning does not automatically transfer to a server the caller
  controls.


  _**Signed off 2026-08-16. The owner accepted the recommendation on all four, so they
  are decided here and are not to be re-opened without a new decision from the owner.** Each is
  permanent in the sense the item means: once a stranger's client depends on it, changing it
  breaks their code._

  1. _**Versioning: path, `/api/v1/`, and only for the published surface.** The app's own routes
     stay unversioned. Versioning is a promise to third parties, and the first-party UI ships in
     the same repo as the server and needs no promise; publishing a versioned subset avoids
     freezing every internal route forever the day the first bot connects. 9d.7's `ShouldInclude`
     filter is already the mechanism for deciding what is in that subset._
  2. _**Auth: a scoped API token, not the session JWT.** A bot key must be independently
     revocable, long-lived, and must not carry the holder's full account authority, none of which
     a JWT issued for a browser session does. Scopes start at exactly two, `read` and `play`, and
     **no token can ever carry moderation or admin scope**, which is the one line here that must
     not be relaxed later for convenience. Storage follows the existing refresh-token table shape
     rather than inventing a second credential store._
  3. _**Rate limits: a dedicated bot partition, keyed by token rather than IP.** More generous
     than a human on reads, **no more generous on moves**. A bot that can move faster than a
     person is a competitive advantage bought with an API key, which FS.6's "cosmetics only,
     never competitive advantage" rules out on principle even though FS.6 was written about money._
  4. _**Bot games are unrated by default, with an opt-in bot pool kept separate from the human
     pools.** 7.3 answered this for client-side bots and the reasoning does not transfer to a
     server the caller controls, which is exactly why the item flagged it: an engine account in a
     human Glicko-2 pool makes Phase 5's ratings mean something different for everyone in it.
     A separate pool preserves both the rating's meaning and the bot author's wish to measure
     their engine._

  _**Unchanged: the trigger.** These decisions being made is not permission to build it. Build on
  the first person who asks, per the item's own text._

  _(Done 2026-08-19. The owner overrode the trigger. `/api/v1` is the published surface, first-party
  UI stays on unversioned `/api/...`, hashed `kc_` tokens with `read`/`play` only, token-id rate
  limits, always-unrated HTTP create/challenge, hub `JoinQueue` remapped onto `bot:{preset}`.
  Production serves only the public OpenAPI document at `/openapi/v1.json`. The public client is
  `/developers`. Local `AddApiTokens` applied 2026-08-19; staging/production pick it up on the
  next deploy migrate.)_
  ```



**Exit criteria:** the critical path is proven in a browser and under load rather than argued for; a
dependency CVE, a leaked secret and an unsafe header are caught by machines on every PR; something wakes a
human when the server is unwell; two players who want a game can find each other without a queue coinciding;
and a rated player can list, export and understand their own games.

**The three sequencing constraints that are not preferences:** 9a.4 must precede DH.2 (Neon drops
connections, and the failure loses finished rated games); 9b.3 must precede DH.1 (its answer sizes the
machine that decision pins you to); and the whole of 9b must precede 8.7 (launching an unverified stack
makes the first production incident the first end-to-end run). Everything else in this ordering is
judgement, and 9e onward should be re-ordered by whatever 8a.5's funnel says once real players exist.

---



## Phase 10 - Second Gap Review (2026-08-17, read against the running system)

> Written the way Phase 9 was, asking the same question: what is missing that this roadmap has never
> named? The difference is what it was read against. **Phase 9 reviewed a codebase; this reviewed a
> deployed one**, so several findings below are facts about the machine that is running rather than
> about the source, and one is a correction to a box this document already ticked.
>
> **The shape of what it found is the useful part, and it is not "missing features".** Phase 9 found
> absent subsystems; this pass found almost none, because the feature surface is genuinely complete for
> a soft launch. What it found instead is **four mechanisms that exist and do not reach the case that
> matters**, which is a strictly harder class to see, since from this document every one of them reads
> as done. A key ring configured only on the code path production does not take. A capacity limit that
> counts the right unit for the wrong population. A shutdown flush with no time budget to run in. A
> static-analysis workflow deleted for a correct reason, with the consequence booked nowhere.
> **The lesson generalises past those four:** this roadmap's habit of recording what shipped is
> excellent at catching work that was never done and blind to work that was **undone afterwards**.
>
> **Stages, in order, same convention as Phase 9:** 10a is a prerequisite of 8.7 and every item in it
> fails in front of a stranger; 10b is correctness the test suite structurally cannot see; 10c is
> durability, supply chain and ops, none of which hurts anybody by landing after the launch.



### 10a - Before 8.7 (each of these fails in public, and none is visible from a smoke test)

- [ ] **10a.3** `[Owner]` **Give the shutdown flush a budget, and find out whether it fits.**
  ```
  `kill_timeout` appears in **none** of the five `fly.*.toml` files, so Fly's 5 second default
  applies, and [fly.backend.toml](../fly.backend.toml) pairs that with `strategy = "immediate"` on
  a single machine. 6c.1's `LiveGamePersister` "flushes every live session on graceful shutdown",
  which is the guarantee the whole of 6c rests on, and 9c.7 established that persistence throughput
  is bounded by `1 / (write RTT)` to Neon regardless of core count. At the capacity this machine is
  sized for, that flush is hundreds of round-trips into a five second window. The 2026-09-26
  measurement below is 100 games in 550 ms; a larger live set is still unmeasured.

  _**So 6c's durability guarantee is conditional on a number this document does not contain**, and
  it fails in the direction nobody notices: a partial flush loses the games that did not make it
  and reports nothing, because the process is exiting anyway._

  _**A second, independent consequence of the same two settings.** With one machine and
  `immediate`, a release that fails to boot has nothing to roll back to, and 9c.3 deliberately made
  migrations roll-forward only. That combination is defensible and is argued for in the toml file's
  own comments; what follows from it is that the recovery path for a bad release is "fix forward,
  fast", and that is worth knowing **before** the day it is needed rather than during it._


  _**Measured 2026-09-26.** A production restart logged `Shutdown flush complete: 100 sessions, 2 batches, 550 ms`. That is inside the 30 s budget. The number is not yet written into DEPLOYMENT.md §4.1, which is the only remainder of this item._

  _**The config half landed 2026-08-18, and the number stopped being a judgement call on the way.**
  `kill_timeout = 30` in [fly.backend.toml](../fly.backend.toml) and its staging twin. 30 is not a
  chosen figure: it is .NET's own `HostOptions.ShutdownTimeout` default, which is the token
  `LiveGamePersister.StopAsync` already runs under, so the flush was being killed at 5s by **the
  smaller of two budgets nobody had lined up**. Aligning them is the whole change. The other three
  `fly.*.toml` files were read and deliberately left alone, with a comment saying so, since nothing
  in them holds state to flush._

  _**The arithmetic says 30 is generous, which is the point rather than a problem**: 9c.7 writes 64
  games per round trip, so 250 live games is four round trips, about 60 ms at a 15 ms Neon RTT. The
  headroom is for what the arithmetic does not cover, a cold or suspended Neon compute. **What is
  left is exactly the measurement.** 10c.7 recorded Neon as `aws-eu-central-1` (Frankfurt),
  adjacent to Fly `ams`, so this budget and 9c.7's capacity figure share the same 5–15 ms band
  rather than an unknown distance factor._
  ```

**Gate:** a confirmation link still works after a deploy, a crowd of spectators cannot lock players
out, and the shutdown flush has a stated budget that a measurement says it fits inside.

***Two of the three clauses are met, 2026-08-18.** The link survives a deploy (10a.1) and the
spectator crowd has a ceiling with a reserve behind it (10a.2), both pinned by tests. The third is
half met and the half that is missing is the load-bearing one: the flush now has a **stated**
budget, and nothing has yet measured whether it fits inside it.*

### 10b - Correctness the test suite structurally cannot see

> Not "more tests". Each item here is a **kind** of test this repo has none of, in a place where the
> kind it does have cannot reach the defect by construction.

**Gate:** met. The two implementations of the economy are compared by a machine (10b.1), the engine
is exercised by positions nobody chose (10b.2), and "what is untested" has a number rather than an
impression behind it (10b.3). All three archived below.

### 10c - Durability, supply chain and ops (can land after 8.7)

- [ ] **10c.1** `[Owner]` **A backup that leaves Neon.**
  ```
  8a.3 chose Neon PITR and 9c.4 ran the drill and passed it, and both prove the same thing:
  **restore within Neon**. Nothing covers account lockout, an accidental project deletion, a
  billing lapse or a provider-side incident, and the `pg_dump` path in
  [DEPLOYMENT.md § 5](ops/DEPLOYMENT.md) exists only for the compose deployment nobody runs. **One
  vendor holds the only copy of every game, rating and account this project has.**

  _**This is 9c.4's own argument moved up a level.** That item's case was that a backup nobody has
  restored from is a hypothesis; the case here is that a backup restorable only **into the account
  that might be the thing that failed** is a hypothesis about a different failure. Neither replaces
  the other, and the second is cheaper._

  _**Reuse [restore-drill.yml](../.github/workflows/restore-drill.yml)'s plumbing rather than
  writing new plumbing.** It already branches Neon, already reads a branch's own connection string
  from `NEON_API_KEY`, already runs in CI rather than on a laptop that has neither `psql` nor `jq`
  (9c.4 found both the hard way), and already cleans up on failure. A weekly dump is that workflow
  with a different last step._


  _**Not launch-gated.** Phase 10's own stage said 10c can land after 8.7. The failure it covers
  is real and total. Setup is finished (bucket, token, secrets, `.eu.` endpoint). August's
  private-repo minutes are no longer the blocker: the Sunday cron on `main` has been getting
  runners since 13 Sep and failing on the R2 upload, recorded below. **Do not block 8.7 on it.**
  Do not wait for a public flip. GitHub's DPA is still unaccepted, so do not dispatch another
  run until that acceptance is recorded. The working tree turns the cron off until the same
  change is on `main`._

  _**Plumbing landed 2026-08-19.** [scripts/offsite-backup.sh](../scripts/offsite-backup.sh) is
  the whole of both halves, weekly dump and disaster restore, so they cannot drift into testing
  different objects. [.github/workflows/offsite-backup.yml](../.github/workflows/offsite-backup.yml)
  was written to run every Sunday at 05:41 UTC plus `workflow_dispatch`. On `main` that cron is
  still active (workflow state `active`). The working tree comments it out until GitHub's DPA is
  accepted ([DPA-CHECKLIST.md](legal/DPA-CHECKLIST.md)); that edit is not on `main`, so Sunday
  keeps firing until it lands. Do not `workflow_dispatch` before the acceptance either. It branches current production HEAD
  (unpooled - pg_dump through pgbouncer is how dumps come back empty), puts the custom-format dump
  in R2, **re-downloads that object**, and restores it into a local Postgres to run 9c.4's schema /
  boot / replay assertions against a database that is not Neon. Runbook in
  [DEPLOYMENT.md § 5.3](ops/DEPLOYMENT.md#53-off-site-backup-out-of-neon-10c1). The two Neon-branching
  jobs share a concurrency group so they never create compute endpoints at the same time.
  `CiWorkflowDriftTests` fails the build if the workflow file or that shared
  group disappears, and if a schedule is put back while the `github-dpa-before-schedule` marker is still in the file. **Bucket created 2026-08-20** on the domain Cloudflare account:
  `kirichess-backups`, EU jurisdiction (S3 host `.eu.r2.cloudflarestorage.com`), public URL off.
  Account id `c3ba1594949d710f9fa8d68b9398dc36`. Secrets `R2_ACCESS_KEY_ID` /
  `R2_SECRET_ACCESS_KEY` and variables `R2_ACCOUNT_ID` / `R2_JURISDICTION=eu` are set.
  **August dispatches never started** (2026-08-20, runs 32395327365 and 32395991126: spending
  limit, no runner). **September did.** The Sunday cron ran 2026-09-13, 2026-09-20, and
  2026-09-27, and failed each time. Latest is
  [36313475991](https://github.com/MichielGekiere/kirichess/actions/runs/36313475991): the runner
  started, and the upload to `kirichess-backups` died with `SSLV3_ALERT_HANDSHAKE_FAILURE`
  against `*.eu.r2.cloudflarestorage.com`. Until a run is green, Neon is still the only copy.
  When the first dump lands, the Cloudflare row in PRIVACY.md §4 and ROPA.md §4 should mention
  R2 object storage; they do not yet, because the bucket does not yet hold anyone's data._
  ```

- [ ] **10c.5** `[Owner]` **The three signals 9c.5's alerting does not have.**
  ```
  9c.5's monitors cover hub error rate, matchmaking queue depth and flag-fall sweep duration. All
  three are **inside the app**, which is the half that was easy because 6d.3 had already named the
  instruments.

  _**What is unwatched:** the **database** (Neon compute suspended, connection failures, storage
  approaching the plan limit), the **edge** (5xx rate, which Cloudflare already counts and nobody
  reads), and **whether a game can actually be played**. The uptime check polls `/health/ready`,
  which proves the process answers and a connection opens. 9c.4's drill is the only thing in this
  repo that has ever asserted end-to-end behaviour against a real deployment, and it runs monthly
  against a **restored branch**, not against production._

  _**A synthetic is this item's real content:** register, pair against the bot, make one move,
  assert the clock moved. That is the check that would have caught 9c.5's own 404 without waiting
  for somebody to notice an empty chart, and it is the shape 9b.2 already wrote, pointed at
  production instead of compose. **Note the interaction with 10c.4 and DH.5:** a synthetic that
  registers needs either an exempt path past Turnstile or a pre-created account, and the second is
  the safer of the two._


  _**Plumbing landed 2026-08-19.** [infra/betterstack/main.tf](../infra/betterstack/main.tf) adds
  two Cloudflare-proxied monitors (`api.kirichess.com/health/ready` and `kirichess.com`, the
  second accepting 200 or 302 so Access does not page until DH.3) and two heartbeats. Playwright
  monitors at Better Stack are a paid add-on; the synthetic is therefore
  [scripts/synthetic-play.sh](../scripts/synthetic-play.sh) (pre-created `play` token, POST an
  unrated bounty1 practice game, e2e4, assert White's clock moved, resign) pinging the first
  heartbeat, and [scripts/neon-watch.sh](../scripts/neon-watch.sh) (Neon management API: storage
  ≥ 80% of the plan limit, or a disabled read_write endpoint) pinging the second.
  [.github/workflows/ops-probes.yml](../.github/workflows/ops-probes.yml) runs both every 15
  minutes. Runbook in [DEPLOYMENT.md § 10.2](ops/DEPLOYMENT.md#102-database-edge-and-can-a-game-be-played-10c5).
  `BetterStackAlertingDriftTests` and `CiWorkflowDriftTests` fail the build if the four
  resources, the two scripts, or the schedule disappear. **Secrets landed 2026-08-20:**
  `terraform apply`, throwaway `play` token (`SYNTHETIC_API_TOKEN`), `SYNTHETIC_HEARTBEAT_URL`,
  and `NEON_WATCH_HEARTBEAT_URL` are set on the repo. Edge monitors accept 200 or 302, so Access
  does not page. **The GitHub half is disabled, not waiting on 1 Sep.** The 15-minute cron is in
  the file, and the workflow state is `disabled_manually`, so it is not spending minutes. Last
  scheduled attempts are 2026-08-22
  ([32602717028](https://github.com/MichielGekiere/kirichess/actions/runs/32602717028)): both jobs
  died in about 2 seconds with no steps. Nothing has run in September. Re-enable the workflow and
  dispatch once. Until a green run, the synthetic and Neon heartbeats are silent; 9c.5 still
  watches the app, and the edge monitors exist after apply._
  ```

**Exit criteria:** the only copy of the data is no longer at one vendor; a machine looks at every diff
and at the image it ships; something can be turned off without a deploy; alerting watches the database
and the edge rather than only the app; and no ticked box in this document describes a mechanism that
has since been deleted.

---



## Phase 11 - Retention: the day-2 problem

> **Written 2026-08-17 from a retention review of this roadmap against the code**, asking the one
> question the document never asks: **why does someone open this site tomorrow?** Phases 0 to 10 are
> unusually strong on correctness, durability, compliance and observability, and they treat retention
> as a **consequence of quality** rather than as a system somebody builds. Phase 7 owns the cold start,
> 9d owns the first impression, 9e reacts to players who already came back. **Nothing owns day 2**, and
> this phase is that owner.
>
> **The structural finding, and it is why this is a phase rather than four loose items.** Every
> retention mechanism in this document is **synchronous-multiplayer-shaped**: rated pools, open seeks
> (9e.4), challenge links (9d.8), spectating (8.1) and arenas (9f.1) all need two humans online in the
> same minute. The roadmap knows the lobby is empty and answers it four separate times, and every
> answer is *pair better*. None of them is *remove the need for simultaneity*.
>
> **A normal chess site's escape hatch is correspondence play, and this variant cannot have one.** Time
> is the win condition, so an asynchronous KiriChess is not a slower version of this game, it is a
> different game. That is a **stated position and never a task**, recorded here rather than left
> implicit because it is what makes the rest of this phase load-bearing: **KiriChess has less liquidity
> slack than the sites it will be compared to**, and the slack has to be manufactured, by 11a.4's
> scheduled hour and 11b.4's single-player loop, instead of arriving for free.
>
> **Four items claim to precede 8.7, and this review ran after the launch was already queued**, which
> is an awkward thing to hand someone holding a drafted post and a green release PR. The claim is made
> anyway, on 9d's own argument: 8.7 is one-shot and the post gets one round of attention. Launching
> first is defensible and its cost is specific rather than vague, **every game end scatters both
> players back to an empty lobby (11a.1), the rated reward is invisible and currently misdescribed
> (11a.2), and 8a.5's funnel cannot see the cliff the launch will fall off (11a.3)**. Only 11a.3 is
> genuinely worth delaying a launch for, because it is the only item here whose value is **destroyed**
> by going first: a one-shot event measured by a blind funnel cannot be re-run.
>
> **Relationship to Phase 10.** That review found infrastructure and correctness gaps in what exists;
> this one found the loop that does not exist at all. They share the 8.7 gate and nothing else, and
> 10a's three rows should still go first, since a site that loses its live games on deploy has a
> retention problem this phase cannot fix.
>
> ***That ordering was honoured and is now spent, 2026-08-18**: 10a.1 and 10a.2 are done and
> 10a.3 is waiting on a measurement rather than on anybody's attention, so nothing in 10a is
> holding 11a back any more.*



### 11a - Before 8.7 (the launch is one-shot)

- [x] **11a.3** **First-session funnel events**, the only item in this phase worth delaying a
  ```
  launch for. 8a.5 shipped a real cohort curve and a four-step funnel, and its four steps are
  **signup → first game → second game → D7**. On a variant nobody has heard of, the largest
  drop-off happens **before step one and inside the first session**, and none of it is measured: how
  many arrivals start `/learn`, how many finish it, how many start a first game, how many finish it,
  and how many meet an empty lobby and leave. 9e's own preamble says its order should be set by what
  the funnel says. **The funnel is blind exactly where 8.7 will fail**, so the numbers that are
  supposed to order 9e cannot be produced by watching the launch happen.

  _**Done 2026-08-18 on `dev`, with AD.6.** First-session events are allowlisted counter keys on
  `DailyCounters`, not a second tracking system. The keys are `learn`, `learn-complete`,
  `learn-skip`, `signup`, `queue-join`, `queue-match`, `first-game-finish`, `empty-lobby-leave`.
  `first-game-finish` is recorded server-side on persist when `PlyCount > 0`. `empty-lobby-leave`
  is the client bounce from `/` with an empty seek list. Traffic dashboard first-session games
  still come from the Games table; these two keys are the named cliff events 11a.3 asked for,
  stored in `DailyCounters`. Rematch health uses the same table (`rematch-offer`,
  `rematch-accept`). No visitor id. The no-tracker constraint below is unchanged._

  _**The constraint is 8a.5's own and it is load-bearing.** These are counters keyed by
  `(UTC date, allowlisted key)` and **nothing else**, on the `PageViewDaily` shape that already
  exists. The moment anyone adds a visitor id, a session id or an IP hash "just to deduplicate", the
  consent banner, the DPA question and a `RetentionSchedule` rule all arrive together and 8a.5's
  entire no-tracker argument collapses. Individual sessions are not reconstructable from this and
  are not meant to be: the deliverable is the **shape of the cliff**, not the person falling off it._

  ```
- [ ] **11a.4** `[Owner]` **A published weekly play hour**, the cheapest liquidity mechanism in
  ```
  this document, and it is currently gated behind the wrong thing. **9f.1's own text concedes it**:
  "below that the published-weekly-time version in 9d.8 does the same job for a sentence." An arena
  does not create players, it **concentrates** them, and a named hour concentrates them for the cost
  of a line of copy plus somebody showing up. `PresenceIndicator` already tells an arriving stranger
  who is online **now**; nothing tells them when anybody will be online **next**, which is the
  question that decides whether they bookmark the site or close the tab.

  _**Hour named 2026-09-04:** Fridays 20:00 UTC. One slot, not per-region. Canonical time is UTC so
  DST does not move the appointment. Launch post names it. Banner + next-occurrence maths still
  open. The #4 layout gate closed 2026-09-24, so the banner is no longer waiting on the UI walk._

  ```



### 11b - After the first players (ordered by leverage, re-order by 11a.3's numbers)

- [ ] **11b.2** **A public player profile, and the head-to-head record behind it.** There is
  ```
  no route for looking at another player: `/profile` is your own, `/moderation/[userId]` is staff,
  and the leaderboard is a list of names that lead nowhere. **You can play a stranger the best game
  of the week and have no way to ever find them again**, which on a site whose whole hope is thirty
  regulars deletes the mechanism by which regulars become regulars. Ratings, pools and game rows all
  exist already, so the profile itself is mostly assembly; the genuinely new and genuinely cheap
  piece is **head-to-head**, derivable from `Games` with no new table, and it is what turns a
  stranger into a rival.

  _**Two traps that are not obvious from here:** an account erased under 8a.2 must not become
  reachable again through this surface, and a sanctioned account's profile must not leak the
  sanction to the public, which is a moderation decision rather than a display one._

  ```
- [ ] **11b.3** **Progression that is not a rating.** Glicko-2 is the only thing that
  ```
  accumulates here, and **a rating goes down half the time by design**, which makes it a poor
  instrument for keeping a beginner who has just lost four in a row. There is no first-win moment,
  no games-played marker, no title, nothing that only goes up. The one badge in this document is
  FS's patron badge, which is funding rather than play.

  _**The design decision is the whole item, and the wrong
  answer is expensive.** Anything that rewards raw volume creates pressure to play when you do not
  want to, which is the mechanic every retention system eventually regrets, and FS.6's "cosmetics
  only, never competitive advantage" is a line this must also stay behind even though FS.6 was
  written about money. Decide and write down what may be rewarded (mastery, variety, milestones)
  and what may never be (anything that punishes a missed day, anything visible to an opponent that
  implies strength)._

  ```
- [ ] **11b.4** **Clock-economy puzzles**, the missing single-player loop and the
  ```
  largest thing in this phase. Today a stranger arriving alone gets `/learn` (eight scripted steps,
  one-shot) and then unrated bot games. **Nothing in this repository gives that person a reason to
  come back tomorrow**, which is the literal statement of the problem this phase is named after. A
  puzzle here is a position plus a question only this variant can ask: _you are four seconds down,
  find the line that flips the clock_. It is inherently single-player, replayable daily, shareable
  as a link, and it **teaches the rule nobody arrives knowing**, which is the job 7.5 does once and
  this does repeatedly.

  _**Deliberately not filed under 9f, and the reason matters.** 9f gates work whose "payoff scales
  with a playerbase that does not exist yet". This is the exact inverse: **its payoff is highest
  when the playerbase is smallest**, because it is the only mode that works with nobody else online.
  Applying the traction gate here would be a category error._

  _**The generator is the hard half.** Positions want to
  come from real finished games once those exist, the correct answer is a **clock** outcome rather
  than a mate, and the engine plus [costPreview.ts](../ui/lib/costPreview.ts) already compute the
  economy on both sides of the wire. The open design question is what makes a position a puzzle
  rather than merely a position._

  ```
- [ ] **11b.5** **Let the bot ladder leave a trace.** 7.2 built a real difficulty ladder and
  ```
  7.3 made bot games permanently client-side and unrated, both correctly. The consequence neither
  item wrote down is that **the only always-available mode remembers nothing**:
  [BotGameDialog.tsx:17](../ui/components/BotGameDialog.tsx#L17) says it plainly, "the server has no
  concept of bot", so the game persists as an ordinary solo row and beating Master is
  indistinguishable from beating Novice forever after. A player who climbs the ladder has no
  evidence they climbed it.

  _**The honest version of this is uncomfortable.** The client would be
  self-reporting which tier it beat, and 7.3's trust boundary says the client is not trusted. The
  decision to make and record is whether an **untrusted, permanently unrated, purely cosmetic**
  progression marker is acceptable. The reading this item was written under is yes, and precisely
  because it can never touch a rating: what 7.3 protects is Glicko-2's meaning, and a tier marker
  that is explicitly self-reported costs that nothing. **Write the reasoning down before building
  it**, or a later change will read it as a trust-boundary violation and revert it._

  ```

**Exit criteria:** a finished game offers something to do that is not "return to an empty lobby"; a
rated player sees their rating move at the moment it moves; the funnel can say where a first session
ended; a stranger who arrives with nobody online learns when somebody will be there and has something
to do until then; and a player who is not on the site can be told that something happened on it.

**Not a task, and it must never be ticked:** correspondence or asynchronous play, for the reason in
this phase's preamble. It is recorded as a position because the absence of it is the argument for
11a.4 and 11b.4, and a later reader who does not know it was decided will propose it again.

---



## Deployment & Hosting (decided stack - cross-cutting)

> **Cloudflare hosts nothing here.** Its edge compute can't run a long-lived, stateful .NET process
> holding live games + WebSockets, nor a normal Postgres, it's the *front door*, not the home. Two
> layers: the front door (Cloudflare) points at the real host (a container platform + managed DB).

**The three things that need a home:** the `KiriChess.Server` .NET process (long-lived, stateful,
WebSockets), Postgres (durable data), and the `ui` Next.js frontend.

**Chosen stack, Fly.io + Neon + Cloudflare** (solo-dev, low-ops, ~$5–15/mo to start):

- [ ] **DH.3** **Cloudflare** front door, DNS pointed at Fly with proxying on (DDoS/CDN), Turnstile on signup;
  ```
  WebSockets proxy fine, just mind idle timeouts.

  _**DNS/proxy/TLS half done and verified end-to-end; Turnstile is not, so the box stays open.**
  `kirichess.com` and `api.kirichess.com` both resolve through Cloudflare's proxy, hold valid
  Fly-issued Let's Encrypt certs (`Full (strict)` mode), and were confirmed live with real HTTP
  requests. Turnstile itself has no code anywhere in the repo yet (checked: no reference outside
  planning docs) - "Turnstile on signup" is still a real, unstarted piece of this item, not a
  formality._

  _**Fly's cert validation cannot see through an already-proxied record.** `flyctl certs check`
  resolves the hostname's DNS itself; with the record set to Proxied it sees Cloudflare's edge IPs
  and reports a mismatch even though the record is correct. Fix: set the record to DNS-only, let
  `flyctl certs check` report Issued, **then** switch it back to Proxied._

  _**`www.kirichess.com` does not resolve, cause unknown.** `kirichess.com` (apex) and
  `api.kirichess.com` both verify and serve correctly. `www` was deleted and re-added once already
  with identical A/AAAA content, and still returns NXDOMAIN from both of Cloudflare's own
  authoritative nameservers directly queried, ruling out client-side caching or propagation lag. No
  Workers/Pages custom domain and no Redirect Rule claims the name. Nothing in the app depends on
  `www` today (`NEXT_PUBLIC_SITE_URL` and CORS both use the bare apex), so this did not block the
  rest of DH.3, but it is unresolved and worth either a Cloudflare support ticket or another look
  before `www.kirichess.com` is put in front of anyone._

  _**The site sits behind a Cloudflare Access login wall right now, on purpose, not as part of
  this item.** `kirichess.com` and (briefly) `api.kirichess.com` were put behind Access as a
  pre-launch gate so that having a real, guessable, Certificate-Transparency-logged domain didn't
  mean the unfinished site was actually open to the internet. **`api.kirichess.com` was then
  removed from the gate** - Access authenticates via a cookie scoped to the hostname you log into,
  and this app's frontend calls a separate API host with JWT bearer tokens rather than
  credentialed cross-origin cookies, so a gated API silently ate every request behind Access's own
  login HTML instead of JSON, breaking registration with a generic "Something went wrong." The UI
  alone being gated is enough: an ungated bare JSON API with its own auth requirement exposes
  nothing usable. **Remove the Access application before the real soft launch (8.7)**, or nobody
  but the allow-listed emails will ever reach the site._

  _**Correction 2026-08-14: the Turnstile paragraph above is stale, and the box now stays open for
  a different reason.** `ff3e742` shipped the client half: the widget renders on the register tab
  ([login/page.tsx](../ui/app/login/page.tsx)), `verifyTurnstileToken` calls a **deployed**
  siteverify Worker ([ui/lib/turnstile.ts](../ui/lib/turnstile.ts)), the CSP in
  [next.config.ts](../ui/next.config.ts) admits both, and [fly.ui.toml](../fly.ui.toml) carries a
  real site key and Worker URL. **What is missing is the half that enforces anything: verification
  is client-side only.** The browser asks the Worker, and on success calls `/api/auth/register`;
  the backend contains no reference to Turnstile at all, so a script posting straight to the API
  registers without ever meeting the widget, which is precisely what a signup bot does. It stops
  the browser-driven case and nothing else._

  _**2026-08-16: the enforcement gap is now closed in code, one secret away from live.**
  `TurnstileVerifier` ([TurnstileVerifier.cs](../src/KiriChess.Server/Auth/TurnstileVerifier.cs))
  calls Cloudflare's siteverify API directly from `/api/auth/register`
  ([AuthEndpoints.cs](../src/KiriChess.Server/Auth/AuthEndpoints.cs)) against the raw token the
  frontend now forwards ([api.ts](../ui/lib/api.ts), [page.tsx](../ui/app/login/page.tsx)) rather
  than discarding after its own client-side check. Same fail-open shape as 9d.1's
  `BreachedPasswordChecker`: `Turnstile:SecretKey` empty (the default, `appsettings.json`) means
  the check is skipped entirely, so a bare checkout and every existing register test are
  unaffected, and a genuine Cloudflare outage passes registration through rather than blocking
  it - only a missing or rejected token, once enabled, fails the request. Runbook in
  [DEPLOYMENT.md § 9.1](ops/DEPLOYMENT.md#91-turnstile-server-side-verification-dh3). **Owner:**
  (1) set the secret key with `fly secrets set --app kirichess-api Turnstile__SecretKey=...`
  (docs/ops/DEPLOYMENT.md § 9.1 has the exact command) - until then the deployed gate is still
  client-side only; (2) **remove the Cloudflare Access application before 8.7**, unrelated to
  Turnstile and still open, the single easiest thing in this document to forget on launch day,
  with the loudest consequence; (3) decide whether the `www` NXDOMAIN is worth a support ticket,
  or whether the apex is enough (nothing in the app uses `www`)._

  _**Turnstile secret confirmed 2026-09-04.** Item (1) is done: the owner confirmed
  `Turnstile__SecretKey` is Deployed on `kirichess-api`. `DEPLOYMENT.md` §9.1 / §12 were the stale
  side; [PROVIDER-ACCOUNTS.md](ops/PROVIDER-ACCOUNTS.md) was already right. Server-side
  siteverify is live. Items (2) and (3) are unchanged._

  _**`www` decided 2026-08-16.** The owner asked whether `www` is even still used. It is, but not
  enough to block anything: browsers hide it, and a soft-launch post is **clicked** rather than
  typed, so almost no 8.7 traffic will ever touch it. The cost of leaving it broken is not lost
  visitors, it is that anyone who does type it gets a hard DNS error that reads as "the site is
  down" rather than "wrong subdomain", and that impression is unrecoverable in a way a redirect
  never would have been._

  _**No support ticket** (slow, and this is self-service). **One more attempt, with a different
  record type:** a proxied `CNAME www -> kirichess.com`, since the A/AAAA pair has now failed
  twice with identical content and CNAME flattening takes a different path through Cloudflare's
  zone handling. **If that still returns NXDOMAIN, drop `www` permanently** and never print it
  anywhere: no marketing copy, no post, no README. Nothing in the app uses it
  (`NEXT_PUBLIC_SITE_URL` and CORS are both the bare apex), so the only way it can hurt is if
  somebody advertises a hostname that does not resolve._

  _**Overtaken by events, 2026-08-16, and the new fact is the opposite problem.** `www` now
  resolves: it returns Cloudflare's proxy IPs from Google's resolver, the same pair the apex
  returns, so the NXDOMAIN is gone and the CNAME attempt above is moot. **The harm is no longer a
  hostname that does not resolve, it is a hostname that resolves past the pre-launch gate.**
  `https://www.kirichess.com` returns 200 and the real app (`<title>KiriChess</title>`, 46 KB),
  while `https://kirichess.com` returns the `Sign in / Cloudflare Access` page. The Access
  application matches the apex only, `api` having been removed from it on purpose, and nobody
  added `www` because at the time `www` did not exist. **The unfinished site is therefore openly
  reachable today**, which is exactly the state Access was added to prevent, and it will stay that
  way silently because the apex still looks correctly gated.
  **One dashboard task, not three:** add a Cloudflare **Redirect Rule**
  `www.kirichess.com/* -> https://kirichess.com/$1` (301). That lands `www` traffic on the gated
  apex, which closes the hole now, and it is also the right permanent answer after 8.7, when the
  gate comes off and `www` should still be one canonical hostname rather than a second origin.
  Adding `www` to the Access application instead would close the hole but leave two live
  hostnames to un-gate on launch day, which is one more thing to forget._

  _**Closed 2026-08-17, and `www` is now settled permanently rather than deferred.** The owner
  deployed the Redirect Rule and it verifies end to end: `www.kirichess.com/` returns 301 to
  `https://kirichess.com/`, `www.kirichess.com/play/abc?x=1` returns 301 to
  `https://kirichess.com/play/abc?x=1` with path **and** query intact, and the apex still answers
  302 to the Access login, so the gate is whole again. The A/AAAA records for `www` stay in place
  deliberately: the rule runs at Cloudflare's edge, so the proxied record is what lets the request
  arrive at all. **The standing instruction to never print `www` anywhere is withdrawn**, it now
  redirects correctly and is safe in copy. What survives from all of this is the launch-day step
  that was always the real one: **remove the Access application before 8.7.**_
  ```
- [ ] **DH.5** `[Owner]` **The service accounts the code already reads, and a DPA for each.** Every
  ```
  integration in this codebase fails closed on an empty config key, which is the right default and also
  means **an account that was never created is indistinguishable from a feature that was never built**.
  Four of them:
  - **Sentry**, two projects (backend `Sentry__Dsn`, frontend `NEXT_PUBLIC_SENTRY_DSN`). 6d shipped the
    wiring; the only trace of the account is a "not proven: no Sentry project exists yet" caveat, and
    no item ever owned creating one.
  - **Resend** (8a.1). The code half is done and inert; the DNS half needs DH.0's domain.
  - **Google Cloud** (6b.3's OAuth client). Its consent screen wants the privacy-policy and terms URLs
    that DH.0 makes exist, so it cannot be finished earlier.
  - **Turnstile** (DH.3, same Cloudflare account, no separate signup).

  **Accept each provider's DPA at signup**, which is the half of 8a.2 that lands here rather than in
  code: Art. 28(3) makes it genuinely mandatory, Fly.io, Neon, Sentry, Resend and Cloudflare all
  publish a standard one accepted online, and it is an afternoon of clicking at signup and archaeology
  at any later moment. Redis is deliberately **not** in this list: per
  [DEPLOYMENT.md](ops/DEPLOYMENT.md) § 7 it gets provisioned when a second machine does, not before.

  _**Per-provider mechanism, looked up 2026-08-16 so it is not re-derived.** "An afternoon of
  clicking" overstates it: **four of the five are already in force** by virtue of the terms accepted
  at signup, and the task for those is to download and file the current version, not to sign
  anything. **Only Fly.io needs an actual exchange.**_

  | Provider | Mechanism | Action |
  | --- | --- | --- |
  | **Cloudflare** | DPA is incorporated into the Self-Serve Subscription Agreement; in force already. Current version **v6.4, 3 Apr 2026** | Download from [cloudflare.com/cloudflare-customer-dpa](https://www.cloudflare.com/cloudflare-customer-dpa/), file it |
  | **Resend** | Binding on acceptance of the Agreement. SCCs included, and Resend is the one provider making **no DPF claim**, so the SCCs carry the whole transfer | Download from [resend.com/legal/dpa](https://resend.com/legal/dpa), file it |
  | **Sentry** | Electronically accepted; current version **5.1.0, 29 May 2024** | Check for an accept action in org settings, else file [sentry.io/legal/dpa](https://sentry.io/legal/dpa/) |
  | **Neon** | Auto-embedded in the terms. A separately signable copy exists for anyone who wants a countersigned one | [neon.com/dpa](https://neon.com/dpa), or request via support for a signed copy |
  | **Fly.io** | **The only real task.** Fly pre-signs its DPA and emails it on request; it binds once countersigned | Request via [fly.io/documents](https://fly.io/documents/), sign, file |

  _**Google is deliberately absent from this table and it is not an oversight.** For OAuth sign-in
  Google is an **independent controller**, not a processor acting on this project's instructions: it
  runs its own relationship with its own user and returns an opaque account key. Art. 28 does not
  apply, and Google's DPA covers Cloud/Workspace services this project is not a customer of. What is
  owed for Google is disclosure and a transfer basis, both already in
  [PRIVACY.md](legal/PRIVACY.md) §4. **Do not go looking for a Google DPA to sign for this.**_

  _**Record the accepted version and date for each**, outside the repo, and put the dates in
  [ROPA.md](legal/ROPA.md) §4. Art. 28(3) requires the contract to be demonstrable; a DPA nobody can
  produce is functionally an unsigned one._


  _**Corrected 2026-08-17 against live state, and the item is now roughly a quarter of what it
  reads as.** Everything above was written from the roadmap rather than from the deployed apps.
  `fly secrets list --app kirichess-api` shows `Email__ApiKey`, `Sentry__Dsn` and
  `Turnstile__SecretKey` all **Deployed**, so **three of the four accounts already exist** and
  their keys are live. Resend's DNS half is complete too: DKIM, SPF, MX and DMARC all resolve
  under `mail.kirichess.com`, checked with `Resolve-DnsName` against 1.1.1.1. A payment card is
  not outstanding either, since Fly is running two paid apps and Neon is provisioned. The full
  state check, with the command that proves each line, is [PROVIDER-ACCOUNTS.md](ops/PROVIDER-ACCOUNTS.md)._

  _**What is genuinely left, as of 2026-08-20, is one thing, and it is a signup:**_

  - _**Google OAuth**, the one real account, and it carries an ordering trap nothing had noticed:
    the consent screen's privacy-policy and terms URLs are **fetched by Google**, and both sit
    behind DH.3's Access gate today, so this must happen **after** the gate comes off rather than
    before. The redirect URI is `https://api.kirichess.com/signin-google`, on the API host, not
    the site host._
  - _~~**A DMARC record at the apex.** `_dmarc.mail.kirichess.com` exists; `_dmarc.kirichess.com`
    does not.~~ **Closed 2026-08-19.** Cloudflare refused a second `_dmarc` TXT because the
    identical `p=none` record already exists at `_dmarc.kirichess.com`. The 17 Aug "does not
    exist" line was stale._
  - _~~**One delivery test.**~~ **Closed 2026-08-20.** Confirmation and password-reset mail both
    arrived at a real inbox from production Resend (`Confirm your KiriChess account` plus a reset).
    Turnstile double-redeem was the 19 Aug blocker; the UI deploy unblocked register. mail-tester
    10/10 was not run; DNS was already checked. Inbox arrival is the launch proof._
  - _~~**The five DPAs**, unchanged.~~ **Filed 2026-08-17**, versions and dates in
    [ROPA.md](legal/ROPA.md) §4, PDFs gitignored under `docs/legal/`. **Fly receipt closed
    2026-08-20:** Dropbox Sign envelope from `mclaren@fly.io`, completed 2026-08-17 21:59 UTC.
    The unsigned Neon counterpart is optional and can stay that way._

  _**Owner pass 2026-08-20, and the leftover is one thing:** Google OAuth (after Access). Mail
  delivery and Fly's DPA are closed. The box stays open for OAuth._
  ```



**Cheaper alternative (more control, more ops):** one small VPS (Hetzner/DigitalOcean) running
`docker compose` with **Caddy** (auto-TLS + WebSocket proxy, near-zero config, the easy answer to the
Phase 6a TLS-proxy item) + Postgres container (self-managed `pg_dump` backups) or still Neon. Take this
only if the managed convenience of Fly+Neon ever costs enough to matter.

**Open fork:** frontend on Fly (one provider to manage) vs. Vercel (smoothest Next.js DX, but splits
hosting; browser WebSockets would still go **direct to the Fly backend**, not through Vercel). Default to
**both on Fly** for now.

---



## Adoption & Distribution (cross-cutting - not a phase)

> **Written 2026-08-17 from a marketing read of this document**, asking the one question every other
> review here has skipped: what does an invented variant need in order to be **found, understood and
> adopted**, that this roadmap has no line for? It is the same exercise Phase 9 and Phase 10 ran
> against the code, run instead against the distribution surface, and it is placed beside Funding &
> Sustainability rather than inside a phase for the reason FS states in its own first line: funding
> follows players. So does everything else, and **nothing in this document was ordering the work that
> produces players.**
>
> **The finding, in one paragraph.** The engineering here is far ahead of the distribution. The plan's
> entire acquisition surface is **one Reddit post** (8.7), which is a one-shot event with no line after
> it, so the day after launch this document has nothing to say. The assets that make a rule idea
> travel do not exist: no clip, no press kit, no `/about`, no licence on the manual. The attribution
> moves are written in [CLAIMS.md](game/CLAIMS.md), including the one it calls "the single highest-leverage
> move available", and **none of them are tracked as work in any table here**. And the
> instrument that was supposed to say whether any of it worked is not in the code at all (AD.6).
>
> **What this section deliberately is not.** It is not a growth-hacking programme, and none of it is a
> feature. Every item is hours rather than days, and each one is either an asset that outlives the
> launch or a measurement that makes the next decision readable. The parts that cannot be bought back
> later are marked; the rest can wait for evidence, which is the same discipline 9e applies to
> engineering.
>
> **Relationship to Phases 10 and 11, all three written the same afternoon and none aware of the
> others while being written.** Phase 10 reviewed the infrastructure, Phase 11 reviewed the retention
> loop, this section reviewed the distribution surface, and the overlap is small and instructive:
> **11a.4 and AD.5 are the same item** found from opposite ends (AD.5 is now a pointer, not a second
> task), **11b.1 owns the product half of AD.8**, and **AD.6 is 11a.3's unstated prerequisite**, since
> 11a.3 builds on a counter table that does not exist. Everything else here is disjoint from both,
> which is the expected result: they were looking at the code, and this was looking at what a stranger
> outside the code can see.
>
> **Sequencing.** **AD.7 is done** (2026-09-04: 25 / 8 / 2 at +14d). AD.3, AD.5 and AD.6 still want
> to precede 8.7. **AD.1 does not** (moved after
> 8.7 on 2026-09-04: the first room is the Reddit thread, not coverage). **AD.4 does not** (declined
> 2026-09-04). Only AD.6 is irreversible in the sense that matters: **traffic that was never counted cannot be counted
> afterwards**, so launch day is the one day it must already work. AD.2 is cheap enough to precede
> anyway. AD.1, AD.9 and AD.10 are post-launch on purpose. AD.11 is a standing position and is not a task.
>
> **AD.12 was added on 2026-08-18 and does not fit that ordering, which is why it is worth a line
> here.** It is technically post-launch, since it needs public URLs, and it is the one item in this
> section that is not about acquisition at all: it is about the date. It exists because the
> repository-visibility decision made that day removed the citable timestamp
> [CLAIMS.md](game/CLAIMS.md) had been resting the whole recognition case on, and because nothing else in
> this document was watching that assumption. Cheapest item here, and the only one whose window
> closes quietly rather than loudly.

- [ ] **AD.1** `[Owner]` **A moving image of the clock swinging, and a press kit around it.**
  ```
  The mechanic is visual and instantaneous, a queen capture moves 25 seconds across the clocks, and
  **nothing in this document produces a picture of it happening**. The kit is the clip plus three
  screenshots, the logo, and a one-paragraph pitch, in one folder, so that a stranger who decides
  to cover this later has something to take.

  _**Moved after 8.7 on 2026-09-04 (#17).** The first players should be organic: r/chessvariants,
  people who read the rules and care about the design. A kit that exists so a stranger can cover
  this tomorrow would pull a different room into the first games. Recapture after the post has
  run. Do not attach a clip to 8.7, do not pitch journalists or creators on day one, and do not
  treat this item as a launch gate. The post stays 1,200 words of prose on purpose; the people
  who queue from it are the ones who wanted the variant, not the footage._


  _**The one design note, because it decides whether the clip works:** the subject is the **clock**,
  not the board. The shot has to hold both clocks in frame with the capture, and the number has to
  move large enough to read on a phone in a feed. A normal gameplay recording buries the only
  novel thing in the product in a corner of the screen._

  _**A first cut landed 2026-08-18: [press-kit/](../press-kit/README.md).** The script is
  [e2e/press-kit/capture.spec.ts](../e2e/press-kit/capture.spec.ts) (its own config, run against a
  real stack, never part of CI): it plays a scripted queen trade in the Flagfall pool (Steal, base
  C1, the ±25s this item quotes) on a phone-shaped viewport tall enough to keep both clocks and the
  board in frame at once - the design note above, built into the capture rather than trusted to luck
  - and copies the result plus two more screenshots, the site's existing logo, and a pitch paragraph
  built from POST-DRAFT.md's own wording into the committed folder. **What is still the owner's, exactly
  as this item always said:** press-kit/README.md's own first two lines put it plainest - whether
  this is the featured swing, and whether the clip reads in two seconds. Ffmpeg conversion commands
  (GIF/mp4, trimming) are written out there; none of that had run with the first cut._

  _**Conversion ran 2026-08-19, then the clips were pulled the same day.** The owner kept the
  Flagfall queen-trade as a sequence and rejected the framing: 430×1400 stacks both clocks above
  the board (`grid-cols-1` below `lg:`), and that layout is not designed. MP4, GIF and raw webm
  deleted; screenshots / logo / pitch and [press-kit/convert.mjs](../press-kit/convert.mjs) stay.
  Recapture on the desktop `lg` grid (clocks in the 240px column beside the board). Clip list,
  including a clutch 20s-takes-the-queen's-25s, is in [press-kit/README.md](../press-kit/README.md)._
  ```

- [ ] **AD.3** `[Owner]` **The chessvariants.com catalogue entry.** CLAIMS.md calls it "probably the
  ```
  **single highest-leverage move** available" and it appears in no table in this document;
  the only place it is written down as an action is a note inside the launch-post draft, which is
  not a queue. It is **not** a launch post and is not gated on 8.7: a catalogue entry is a
  permanent, dated citation whose value does not decay, and it is the one asset here that keeps
  working while nothing else is happening.

  _**Promoted 2026-08-18 from highest-leverage to load-bearing**, by a decision made elsewhere in
  this document. CLAIMS.md's recognition plan has four channels and rests its first one on a public
  commit history; 10c.2 settled that the repo stays private, permanently, so that channel does not
  exist. What is left is this catalogue entry, the `/about` page (AD.2) and AD.12's snapshot, and
  of the three **this is the only one hosted by somebody other than the owner**, which is the
  property that makes a citation worth anything in a dispute about who was first. It should now be
  read as the primary record rather than a good idea, and it is cheap enough that the gap between
  those two readings is only the sending._

  _**2026-08-19:** a public GitHub history was considered as the citable timestamp and reverted
  the same day. This working repo stays private (personal addresses in commit authors). Catalogue
  entry + `/about` + AD.12 remain the public citation path; a later public repo should be a clean
  snapshot, not a push of this history. Do not skip the catalogue send._

  _**2026-08-26:** that "later public repo" is now a queued item, **AD.13** at **#11a**, sequenced
  before #12 so this submission has a URL to cite. It changes what this item links, not what this
  item is for: a launch-day snapshot carries no priority, so this catalogue entry remains the
  primary dated record. Send it even if #11a slips._

  _**2026-08-20:** send after Access comes off (**#12**), not before - the draft links
  `kirichess.com`. **Do not include the GitHub URL** in the submission; the working repo is
  private and 404s for anyone else._


  _**Drafted 2026-08-18: [docs/plans/chessvariants/SUBMISSION-DRAFT.md](plans/chessvariants/SUBMISSION-DRAFT.md).** A
  page-style entry (name, inventor, date, board/pieces, the four rules, the endgame-bill
  consequence, the six pools, the licence) plus a shorter cover-note version for a length-limited
  form, both built from RULES.md so the numbers can't drift. **What it could not do:** confirm
  chessvariants.com's current submission mechanism - both the `.com` and its `.org` mirror returned
  HTTP 403, and search didn't surface a current address either, so that
  page still has to be found on the site itself. What it deliberately did not do either: pick the inventor name on the
  entry. Both are called out at the top of the draft rather than guessed._
  ```

- **AD.4** **Declined 2026-09-04.** A founding cohort of ten, online before the post. Not a task.
  Do not put it back in the open-work table. The original argument is kept so a later reader does
  not re-file it as missing work: the post asks strangers to try a two-player game, and none of
  9d.8 / 9e.4 / 7.x fix the visitor who queues and meets nobody. The owner launches without that
  gate. Liquidity is the named weekly hour (11a.4) plus whatever the post itself draws.

  _**Declined, not done.** 8.7 is not gated on a pre-recruited ten._

- [ ] **AD.5** **The weekly meetup: folded into 11a.4, kept here as a pointer.** This item was written
  ```
  independently on the same afternoon as Phase 11 and reached the same conclusion from the other
  direction, that `WEEKLY_MEETUP = "Fridays 20:00 UTC"` in [ui/lib/site.ts](../ui/lib/site.ts) is a
  liquidity mechanism sitting in a footer constant. **11a.4 is the fuller version and owns the
  work**; this row is not a second task and should be ticked with it.

  _**One thing the marketing read adds, and it costs nothing: post the day before a session.**
  11a.4 asks the launch post to name the hour, which is right and is where the two reviews agree.
  Naming an hour that is **tomorrow** does something further: it converts a read into an
  appointment. It is the only mechanism in this document that turns launch attention into two
  people at a board in the same minute, and it is a scheduling decision rather than a feature._

  _**Hour named 2026-09-04 in [POST-DRAFT.md](plans/launch/POST-DRAFT.md):** Fridays 20:00 UTC. The
  remaining half is 11a.4's banner, and posting on a Thursday so the named Friday is tomorrow._
  ```

- [x] **AD.6** **Measurement, and the discovery that there is none.** This item
  ```
  began as "8a.5 cannot attribute a visit to a source", which its own known-limits list states
  plainly. Grounding that against the code found something larger, recorded at 8a.5 itself:
  **none of 8a.5's product analytics exists in the repository.** `/api/analytics/balance` (8.3) is
  the only analytics route in `AnalyticsEndpoints.cs`; the funnel, the cohort retention curve, the
  page counter and the traffic view are absent from `src/` on **every branch**, local and remote.

  _**Done 2026-08-18 on `dev`, with 11a.3.** `DailyCounters`, anonymous beacon, campaign `?src=`
  allowlist, retention and traffic read models, and the admin dashboard tabs. The 2026-08-17
  finding below is what this item was written from; it is no longer true of the repository._

  _**Why this is a distribution item and not only a bookkeeping one.** Three things in this
  document rest on that funnel: 9e's entire ordering rule ("if the numbers disagree with the
  sequence, follow the numbers"), 9f.1's trigger, and any judgement about whether 8.7 worked.
  Without it, launch day produces a feeling and nothing else, and **the traffic it produces is
  unrecoverable**: a counter added in week two cannot count week one._

  _**Scope, in order.** First land 8a.5's counter and funnel as they are already specified in
  Phase 8a.5, which is an execution job against an existing spec.
  Then add the campaign bucket this item was originally about: an **allowlisted** `?src=` value
  recorded as one more dimension on the daily count. The allowlist is what preserves the property
  that let 8a.5 ship without a consent banner, no personal data, no visitor id, no referrer string
  taken raw, and that property is load-bearing rather than tidy, see the item's own note about the
  moment anyone adds "an IP hash just for deduplication"._

  _**This needs a plan rather than a straight edit,** because the tick on 8a.5 has to be reconciled at the same
  time (it is a Done box describing code that is not there, and 9e/9f read it as evidence), and
  because the retention rule for the counter table was handed to 8a.3 and never written, so
  landing the table without it re-opens a retention gap 8a.3 closed._

  _**This item is 11a.3's prerequisite, and it invalidates one line of its reasoning.** Phase 11
  was written the same afternoon from a retention review and reaches an adjacent conclusion: the
  funnel is blind exactly where 8.7 will fail. It builds on "the `PageViewDaily` shape that already
  exists" and opens by saying 8a.5 "shipped a real cohort curve and a four-step funnel". **Neither
  is true of the code**, per the check recorded at 8a.5, so 11a.3 is not the incremental afternoon
  it reads as until this lands. Nothing else in 11a.3 changes: its keys, its no-tracker constraint
  and its "before means before" gate are all right, and they are the same constraint this item is
  built to preserve. The two should be done together, in this order._
  ```

- [x] **AD.7** `[Owner]` **Write down what a good launch looks like, before it happens.**
  ```
  _**Done 2026-09-04.** Floor, not stretch. Read on `/analytics` **14 days after the 8.7 post**
  (UTC). Subtract known owner and test accounts. The window is those 14 days, not a calendar
  month.

  1. **25 signups.** Traffic `TotalSignups` (`AspNetUsers.CreatedAt` in the window). The client
     `signup` counter also fires when a Google handshake registers the account (M-4-1). A
     returning Google sign-in and a link onto an existing password account do not. `?src=` does
     not survive the round trip, so that count lands on `direct`. `TotalSignups` stays the
     number to read. A gap is a beacon that did not land, not a missing Google cohort.
  2. **8 games between two humans** with `PlyCount > 0`. Traffic `GamesStarted`. Bot-pool games
     (`RatingPool` starting with `bot:`) are excluded from traffic, retention, and the queue
     counters (M-4-4). A custom game, whose pool is null, still counts.
  3. **2 rolling-D7.** Retention funnel "Came back after a week" (`retained-d7`): `LastGameAt >=
     SignedUpAt + 7 days`, counted only among accounts old enough to have reached the horizon.
     Rolling, not calendar-day-2 (M-4-6). The dashboard already renders D1/D3/D7/D14/D30; D7 is
     the one that answers this item.

  **Miss:** fewer than 10 signups, or zero human games. The post did not produce a room.
  **Crush, and still not 9f.1:** 80+ signups, or a second channel (lichess forum, Discord)
  matching Reddit. Arenas want ~30–100 regulars; a good week-1 is not that. Revisit 9f.1 when a
  Friday 20:00 UTC hour produces two concurrent human games without the owner filling a seat.

  **Companion, not a target.** `QueueMatchPercent` (searches that found an opponent): if signups
  hit and this is near zero, the post worked and the lobby did not. Retention understates
  intent-to-return while the lobby is thin (M-4-5). First-game step: expect roughly a third of
  the 25 to play a game; the empty lobby is the cliff, not the post. Source mix is tagged
  landings and client signups by `?src=`, never per-channel retention (M-4-2). Tag `#16` with
  `?src=reddit` and the lichess follow-up with `?src=lichess`; an untagged link is `direct`.
  OAuth signups are counted, and they land on `direct`: still unattributable to a campaign
  channel. Pre-signup counters (`learn`, page views) are
  independent daily totals, not a cohort that follows anyone (P-3-6). Do not add a visitor id
  to close that.

  **I-9-1.** Expected launch-day volume is well under Resend's 100 emails/day. Stay on the free
  tier. If a confirmation 429s, recovery is `/api/auth/resend-confirmation` and
  `/api/auth/forgot-password`. Cap is recorded in `DEPLOYMENT.md` §8.4.

  _**Why these numbers.** r/chessvariants is ~14k. A well-received original-variant post is tens
  of upvotes and a few hundred clicks, not thousands. Email-gated signup on an unknown site
  converts a small fraction of those, AD.4's ten were declined, and the lobby is empty except
  Fridays 20:00 UTC. 25 in two weeks is a post that landed. 8 human games is two play hours that
  actually paired, plus a couple of challenge games. Rolling D7 of 2 is ~10–20% of the eligible
  week-1 slice; 0 with 25 signups is they looked and left._

  _The item used to say nothing in this document stated a number, and 9f.1's trigger was settled
  by asking rather than reading. That is no longer true._
  ```

- [ ] **AD.10** `[Owner]` **Content that meets the search demand that actually exists.**
  ```
  9d.6 shipped the plumbing (sitemap, robots, canonical metadata) and there are exactly two
  evergreen pages behind it. **Nobody searches "KiriChess"**, a brand with no audience has no search
  demand by definition; the demand that exists is "chess variants", "chess variant with time", and
  the wikis and listicles that already rank for it. This is deliberately post-launch: what to write
  is a question the launch thread will answer better than reasoning can, since the objections
  people actually raise are the articles worth writing.
  ```

- [ ] **AD.11** **Standing position: distribution is a recurring cost, not a launch.** This is the
  ```
  counterpart to FS.1's "self-fund" and should never be ticked. 8.7 is one event; a variant spreads
  because somebody keeps showing it to people. The failure this section exists to prevent is the
  one FS already names for money, applied to attention: a beautifully engineered site, launched
  once, quietly finished. **Practically:** after 8.7 there is a week two, and it should have one
  named action in it, whichever one the launch suggests.
  ```

- [ ] **AD.12** `[Owner]` **A citable timestamp, since the one this plan assumed it had was
  ```
  removed on 2026-08-18.** [CLAIMS.md](game/CLAIMS.md)'s first and most basic recognition action is to
  publish the manual somewhere with a permanent, citable timestamp, and it names "a public GitHub
  repo (commit history is a timestamp)" as the way, adding that the project is "most of the way
  there already". **It was never true and is now permanently untrue:** the repository has been
  private throughout and 10c.2 settled on 2026-08-18 that it stays that way. A private commit
  history proves the date to the one person who already knows it. So of CLAIMS.md's four actions,
  the one everything else was supposed to stand on is currently unfulfilled, and no other item in
  this document noticed, because every item that cites the date cites the repo.

  _**2026-08-19:** relicensed Apache-2.0 / CC BY 4.0. Making *this* git history public was
  considered and reverted the same day (personal author emails). **This item still owns the
  archive.org half:** a Save Page Now capture of `/rules` and `/about` after 8.7, recorded back
  in CLAIMS.md. A later clean public snapshot does not replace a third-party snapshot._

  _**2026-08-26:** that clean snapshot is now **AD.13** at **#11a**. It does not discharge this
  item and does not shorten it. A repo created on launch day timestamps launch day, not
  2026-07-21, so the archive.org capture stays the third-party dated record this item owns._

  _**What replaces it, in the order the pieces become possible.** First AD.2's `/about`, which puts
  the invention date and the inventor's name on a public page under the owner's control. Then
  AD.3, which is the same claim in a catalogue somebody else runs, and is the one that carries
  weight precisely because it is not self-hosted. Then **this item's own half: a Save Page Now
  capture of `/rules` and `/about` at archive.org**, whose capture URLs are third-party, dated,
  and outlive the site. Record the capture URLs back in CLAIMS.md so the chain is written down in
  one place rather than remembered._

  _**Why it is worth the ten minutes it costs.** The whole recognition strategy in CLAIMS.md is
  "make the origin unambiguous and citable, so if it does spread there is exactly one place
  attribution points back to". Every other AD item is about spreading. This is the only one about
  the "if it does" clause, it is the cheapest item in the section, and it is the only one whose
  absence cannot be repaired after the fact: a date can be established early or argued about
  later. **Gated on 8.7's release** for the URLs to exist, and on nothing else._
  ```

- [x] **AD.13** `[Owner]` **Public source of truth (clean import), cut over 2026-10-11.**
  Originally framed as a thin public snapshot while the working repo stayed private forever (10c.2).
  **Superseded product shape:** [MichielGekiere/kirichess](https://github.com/MichielGekiere/kirichess)
  is where development, review, and Fly deploys happen. Pre-public history is
  `MichielGekiere/kirichess-archive` (do not link). Clean import still required: private commit
  objects carry personal / student / machine identities; never push or fork that history.
  Plan: [PUBLIC-SOURCE-OF-TRUTH.md](plans/open-source/PUBLIC-SOURCE-OF-TRUTH.md). Export appendix:
  [PUBLIC-SNAPSHOT.md](ops/PUBLIC-SNAPSHOT.md).

  **What it does not buy.** Invention priority. A single public-root commit does not prove a
  2026-07-21 invention date. **AD.3 and AD.12 stay load-bearing.**