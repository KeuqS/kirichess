# P — Product, UX, accessibility, content

Merged packet reports for category **P**. Individual packets were `docs/audits/P-N.md`; they now live as sections below. Index: [INDEX.md](INDEX.md).

---

## P-1 — Product surface

Date: 2026-08-30
Packet: docs/audits/INDEX.md, table 6

### Verdict

Every route exists, renders on its documented branches, gates auth correctly (admin pages refuse
with a clean `EmptyState`, the OAuth callback handles all four server slugs, missing-token branches
on the mail-link pages are all handled), and the nav / footer / catch-all wiring points only at
real routes. The route *machinery* is in good shape and most of what is wrong with individual
screens is already filed elsewhere (styling drift in STYLING-CONFORMANCE.md, reconnect/board
issues in C-11 and deep/FE-4, dialog focus in deep/FE-2).

What this packet adds is one theme the earlier passes did not treat as a product-surface problem:
**four demo/mock fixtures are shipped enabled**, so the public product a stranger evaluates is
substantially fabricated. `/leaderboards` and `/watch` — two of the five main nav destinations —
render invented data, `/leaderboards` links 38 of 40 rows to profiles that 404, and a signed-in
user's own `/profile` shows fake ratings and a fake peak. The second theme is the empty-lobby path:
it does resolve (instant unrated bot play works), but only after registration, it is below the fold
and undiscoverable from a stalled queue, and the one CTA offered at the "nobody is here" moment
(`PlayButton` on the `/watch` and `/leaderboards` empty states) silently no-ops for a logged-out
visitor and drops a logged-in one into a solo hot-seat board rather than a game.

### Findings

#### P-1-1 — Four demo/mock fixtures ship enabled; the public surface a stranger sees is fabricated

- Severity: High
- Where:
  - `ui/app/[locale]/leaderboards/page.tsx:26` — `const USE_DUMMY_ROWS: boolean = true;` (40 fake
    rows, `:32-51`, including the names "Carlsen", "Hikaru", "Nakamura", "Niemann", "Fabi",
    "Alexandra Kosteniuk-Longname"; real API call at `:115` is bypassed)
  - `ui/app/[locale]/watch/page.tsx:30` — `const SAMPLE: boolean = true;` (real `getLiveGames`
    polling is `return`ed past at `:53`; grid renders `WATCH_DEMO_GAMES` from
    `ui/lib/watchDemos.ts`)
  - `ui/app/[locale]/u/[id]/page.tsx:32` — `const USE_DUMMY_NAKAMURA: boolean = true;` (a fake
    "Nakamura" profile served for one hard-coded UUID, `:94-102`)
  - `ui/app/[locale]/profile/page.tsx:24,155` — `MOCK_RATINGS` rendered unconditionally into the
    six `PoolRatingCard`s; `:125` hard-codes peak "1680", `:169` current "1642", `:174-181` a
    placeholder rating chart
- Impact: a stranger judging whether the site is alive sees a full leaderboard of grandmaster
  names, a `/watch` dashboard of "live" games that are not live (and, `watch/page.tsx:126`, not
  clickable — `href={null}`), and on signup their own profile shows ratings they never earned. The
  real code paths behind each flag are dead until flipped. Attaching living public figures' names
  to fabricated accounts and a fabricated "Nakamura" profile page is also a small but real
  reputational/publicity exposure for a named individual controller. `USE_DUMMY_ROWS`,
  `USE_DUMMY_NAKAMURA` and `SAMPLE` have no env gating — they are literal `= true` in source.
- Fix: flip all three flags to `false` and replace `/profile`'s `MOCK_RATINGS` with the real
  `getMyRatings` fetch (the NavBar already makes that call) or an honest "no rated games yet" state,
  keeping only peak/chart as declared-unavailable. Add a `ui/lib/styling.test.ts`-style unit guard
  that fails the build if any of the fixture flags is truthy, so this cannot silently ship again.
  Cross-reference: `docs/ops/UI-CHECKLIST.md:43-48` names these flags but frames them as a
  populate-the-QA-walk convenience, not a ship-blocker.

#### P-1-2 — `/leaderboards` dummy rows are 38 dead links to `/u/[id]` profiles that 404

- Severity: High (same root cause as P-1-1; separated because it is also the launch state before
  real games populate the board, independent of the flag)
- Where: `ui/app/[locale]/leaderboards/page.tsx:78-82` builds a synthetic userId per row
  (`00000000-0000-4000-8000-<rank hex>`); only rank 1 matches the `USE_DUMMY_NAKAMURA` id at
  `ui/app/[locale]/u/[id]/page.tsx:35`. Every row is wrapped in `<Link href={`/u/${row.userId}`}>`
  at `:255-258`.
- Impact: 38 of the 40 visible leaderboard rows link to `/u/<fabricated-uuid>`, which calls the
  real `getPublicProfile` (`u/[id]/page.tsx:137`), gets a 404, and renders "player not found". The
  leaderboard is the site's main "is anyone good here" page and almost every name on it is a dead
  end. Even with the fixture off, an empty real leaderboard is the launch state — the built empty
  state (`leaderboards/page.tsx:210-228`, with a "play this pool" CTA) is the correct thing to ship
  and is currently unreachable.
- Fix: covered by P-1-1's flag flip; verify the empty-state branch is what renders at launch.

#### P-1-3 — `PlayButton` empty-state CTA silently no-ops for anonymous users and starts a solo hot-seat board for signed-in ones

- Severity: Medium
- Where: `ui/components/PlayButton.tsx:24-39` — `handlePlay` calls `createGame({ opponentId: null,
  rated: false })` directly; the `catch` only `console.error`s and clears `loading`. Used at
  `ui/app/[locale]/watch/page.tsx:184-189` (empty "no games" state) and
  `ui/app/[locale]/leaderboards/page.tsx:221-226` (empty "no ranked players" state).
- Impact: for a logged-out visitor, `createGame` 401s, is swallowed, and the button just resets —
  the primary CTA on both empty states does nothing, with no redirect and no message. For a
  signed-in visitor it creates a game with `opponentId: null` and routes to `/game/[id]` with **no**
  `mode=bot` param, which the game page treats as a solo board where one person moves both colours
  (`ui/app/[locale]/game/[id]/page.tsx:322-324,759`) — not a game against anyone. This is the CTA
  shown exactly when the lobby is empty, i.e. the moment the packet's "can a stranger play when
  nobody is online" question is live.
- Fix: send anonymous users to `/login?next=<current path>`; for the empty-lobby case open
  `BotGameDialog` or append `?mode=bot&level=<default>` so "Play" yields an opponent. Surface the
  failure (error text) instead of only `console.error`.

#### P-1-4 — The vs-bot fallback is below the fold and undiscoverable from a stalled queue

- Severity: Medium
- Where: `ui/app/[locale]/page.tsx:61-77` places `BotPlayPanel` under an "Others" divider beneath
  the rated pools and the challenge panel. `ui/components/RatedPoolsGrid.tsx:96-103` and
  `ui/components/PresetTile.tsx:81` show only a static "Searching…" with no timeout, no elapsed
  counter, and no alternative. `ui/components/PresenceIndicator.tsx:14` renders nothing until the
  first presence push, so a visitor on a dead lobby often sees no online count at all.
- Impact: a first-time visitor clicks a pool tile, waits in a queue that will never match because
  they are the only person online, and is given no signal that this is expected or that instant bot
  play exists one scroll down. On a young site this reads as "the site is broken", which is the
  single worst first impression for the retention story the packet cares about.
- Fix: after ~20–30s queued with no match, surface an inline "still nobody here — play the bot
  meanwhile" affordance in the tile/grid that opens `BotGameDialog` for the same pool. Cross-
  reference: **P-2** owns the onboarding funnel in depth; this is the mechanical gap.

#### P-1-5 — No play path at all for an anonymous visitor; CTAs bounce to `/login` with no `?next` and no explanation

- Severity: Medium (trigger: any not-signed-in visitor trying to start a game)
- Where: `ui/components/RatedPoolsGrid.tsx:86-88`, `ui/components/BotPlayPanel.tsx:24-27`,
  `ui/components/OpenSeekList.tsx:75-77` all `router.push("/login")` with no return path.
  `ui/components/ChallengeFriendPanel.tsx:375` and `ui/components/CustomGameCta.tsx:28` do pass
  `?next=`. `BotPlayPanel`'s six pool buttons give no hint before the click that they require an
  account.
- Impact: bot play — the one path that needs no second human and is the packet's designated
  empty-lobby answer — is gated behind registration with a silent redirect that loses the user's
  intent (after login they land on `/`, not in the bot game they picked). Whether anonymous play is
  allowed at all is a product decision not covered by the standing positions; if it stays gated,
  the surface should say so and preserve intent.
- Fix: owner decides anonymous play. If it stays gated: add `?next=` to the three redirects above
  and label the bot buttons (e.g. "Sign in to play the bot") so the redirect is not a surprise.
  Cross-reference **P-2**.

#### P-1-6 — `/u/[id]` has no route metadata and no h1 on its loading branch

- Severity: Low
- Where: `ui/app/[locale]/u/[id]/page.tsx` is `"use client"` with no sibling `layout.tsx`, so it
  inherits the root layout's generic `"KiriChess"` title; the loading branch (`:169-172`) renders
  a bare `<SkeletonRows>` with no heading.
- Impact: `/u/[id]` is publicly linkable (every leaderboard row) and shareable, so a generic tab
  title and share preview matter more here than on the auth screens. The missing loading-branch h1
  is a brief document-outline gap.
- Fix: add a thin `u/[id]/layout.tsx` with `generateMetadata` (title from the display name), and an
  `sr-only` / full h1 above the branch switch. Cross-reference: STYLING-CONFORMANCE.md already
  files the loading-branch h1 and bare-skeleton nits for this route; deep/FE-3 nit 1 files the
  missing-`layout.tsx` metadata gap for the sibling client routes.

#### P-1-7 — `/unsubscribe` success gives no route to manage preferences

- Severity: Low (trigger: a recipient who unsubscribes from a mail link and then wants to
  re-subscribe or change categories)
- Where: `ui/app/[locale]/unsubscribe/page.tsx:42-53` — success shows `resubscribeHint` text but
  the only action is "back to KiriChess" (`/`).
- Impact: the hint tells the user they can resubscribe, but the notification-preferences card lives
  on `/profile` and an unauthenticated recipient has to find it themselves. Minor dead-end.
- Fix: link to `/profile` (or a `/profile#notifications` anchor) from the success state.

#### P-1-8 — `/game/[id]` and `/watch/[id]` loading/not-found branches drop the page shell

- Severity: Low here (fully covered — one-line cross-reference)
- Where: STYLING-CONFORMANCE.md "HIGH severity, grouped" — `game/[id]/page.tsx:600-618`,
  `watch/[id]/page.tsx:240-275`, `replay/[id]/page.tsx:207-237`.
- Impact / fix: see that report. Noted here only because "does the route render on all branches"
  is P-1's remit and the answer is "yes, but the early-return branches render without the
  `<main id="main-content">` landmark and without an h1".

### Route inventory

Auth column: `anon` = fully usable signed out, `anon-view` = viewable signed out but actions
require login, `auth` = requires a session, `admin` = requires `ModerationPolicies.Admin`.

| Route | File | Auth | Branches render? | h1 + shell | Notes |
| --- | --- | --- | --- | --- | --- |
| `/` | `[locale]/page.tsx` | anon-view | yes | shell yes; **no h1** (known ROADMAP gap) | every play CTA → `/login` when signed out (P-1-5) |
| `/custom` | `[locale]/custom/page.tsx` | auth | yes | yes | clamps rule inputs client-side (deep/FE-4) |
| `/learn` | `[locale]/learn/page.tsx` | anon | yes | h1 oversized in 380px card (STYLING) | works offline once cached |
| `/rules` | `[locale]/rules/page.tsx` | anon | yes | yes (reference page) | — |
| `/about` | `[locale]/about/page.tsx` | anon | yes | yes | P-9 owns copy |
| `/leaderboards` | `[locale]/leaderboards/page.tsx` | anon | loading/error/empty/rows all built | `sr-only` h1, shell yes | **ships `USE_DUMMY_ROWS = true` (P-1-1); 38 dead `/u/[id]` links (P-1-2)** |
| `/watch` | `[locale]/watch/page.tsx` | anon | loading/error/empty/grid all built | `sr-only` h1, shell yes | **ships `SAMPLE = true` (P-1-1); demo cards not clickable** |
| `/watch/[id]` | `[locale]/watch/[id]/page.tsx` | anon | loading/ended/not-found/live | early-return branches drop shell (STYLING) | spectator snapshot restores correctly (deep/FE-4) |
| `/game/[id]` | `[locale]/game/[id]/page.tsx` | auth (participant) / anon spectate path | loading/not-found/live | early-return branches drop shell (STYLING) | reconnect wipes move list (FE-4-1 / C-11-2); board never flips for Black (C-11-1) |
| `/replay/[id]` | `[locale]/replay/[id]/page.tsx` (+ `layout.tsx`, `opengraph-image.tsx`) | anon | loading/error/loaded | error branch h1 ok; loading drops shell (STYLING) | no hub connection |
| `/challenge/[token]` | `[locale]/challenge/[token]/page.tsx` (+ `layout.tsx`) | anon-view | loading/not-found/open/accepted/cancelled/expired | yes on all branches | preview card can't reach internal API base (FE-4-2); `?accept=1` auto-accept after login |
| `/profile` | `[locale]/profile/page.tsx` | auth | single branch | h1 + shell yes; `id="main-content"` present (FE-3#3 fixed) | **`MOCK_RATINGS` + hard-coded peak/chart (P-1-1)**; history/2FA/tokens/export are real |
| `/u/[id]` | `[locale]/u/[id]/page.tsx` | anon | loading (no h1)/not-found/error/loaded | shell yes; loading branch no h1; no metadata | **`USE_DUMMY_NAKAMURA = true` (P-1-1)**; route exists ahead of ROADMAP 11b.2 checkbox |
| `/donate` | `[locale]/donate/page.tsx` | anon | yes | h1→h3 heading order, padding (STYLING) | patron wall is dummy names by design ($1) |
| `/developers` | `[locale]/developers/page.tsx` | anon | yes | yes | redefines `API_BASE` locally (FE-4-3) |
| `/login` | `[locale]/login/page.tsx` (+ `layout.tsx`) | anon | yes | h1 names the site not the page (STYLING); no `<main>` landmark (AuthShell) | Turnstile script actually loads globally (FE-3#1) |
| `/forgot-password` | `[locale]/forgot-password/page.tsx` (+ `layout.tsx`) | anon | form/sent | AuthShell (no `<main>`) | never reveals whether email exists |
| `/reset-password` | `[locale]/reset-password/page.tsx` | anon | form/done; missing email+token handled | AuthShell (no `<main>`); no metadata | — |
| `/verify-email` | `[locale]/verify-email/page.tsx` | anon | pending/success/error; missing params handled | AuthShell (no `<main>`); no metadata | effect lacks StrictMode latch (FE-3 nit 3, idempotent) |
| `/unsubscribe` | `[locale]/unsubscribe/page.tsx` | anon | pending/success/error; missing params handled | AuthShell (no `<main>`); no metadata | success has no link to preferences (P-1-7) |
| `/auth/callback` | `[locale]/auth/callback/page.tsx` | anon | 4 error slugs + success | AuthShell (no `<main>`); no metadata | local `safeReturnPath` weaker than shared guard (FE-3#2) |
| `/offline` | `[locale]/offline/page.tsx` | anon | single branch | h1 + shell (shell shortened, STYLING) | SW nav fallback; links only to `/` and `/learn` |
| `/analytics` | `[locale]/analytics/page.tsx` | admin | non-admin → `EmptyState` refusal; admin dashboard | eight 14px h2s (STYLING) | server enforces regardless of `isAdmin` |
| `/moderation` | `[locale]/moderation/page.tsx` | admin | non-admin → refusal; dashboard | one heading in the file (STYLING) | nav link hidden unless `isAdmin` |
| `/moderation/[userId]` | `[locale]/moderation/[userId]/page.tsx` | admin | loading/error/dossier | error branch h2-only, no h1 (STYLING) | `DetectionReportCard` stale-response gap (deep/FE-2#5) |
| `/privacy` | `[locale]/privacy/page.tsx` | anon | yes | raw `<table>` x2, `space-y-4` (STYLING) | L-1 owns content |
| `/terms` | `[locale]/terms/page.tsx` | anon | yes | clean | L-1 owns content |
| `/[...rest]` | `[locale]/[...rest]/page.tsx` | anon | `notFound()` → locale `not-found.tsx` | n/a | catch-all, correct |
| `not-found` | `[locale]/not-found.tsx` | anon | single | h1 + shell (shortened) | links to `/` and `/learn` |
| `error` | `[locale]/error.tsx` | anon | single | h1 + shell (shortened); hard-coded English (FE-3 nit 2) | route-level error boundary |
| `global-error` | `app/global-error.tsx` | anon | single | correctly minimal (no layout) | replaces root layout |
| `sitemap` / `robots` / `manifest` | `app/{sitemap,robots,manifest}.ts` | n/a | n/a | n/a | M-1 owns SEO |
| `opengraph-image` | `[locale]/opengraph-image.tsx`, `[locale]/replay/[id]/opengraph-image.tsx` | n/a | n/a | n/a | Satori build-time |

### Already covered

- **Reconnect/reload wipes move list, captured material, clock graph** — deep/FE-4-1, C-11-2.
- **Board and both clocks never flip for the Black player** — C-11-1.
- **Turnstile `<Script>` loads on every route, not just `/login`** — deep/FE-3 #1.
- **OAuth callback `safeReturnPath` is a weaker untested duplicate of `challengeLink.ts`** —
  deep/FE-3 #2, C-11 "already covered".
- **Challenge preview card can't use the compose-internal API base** — deep/FE-4-2.
- **`GameOverOverlay` / report / bot dialogs lack focus trap and dialog semantics** — deep/FE-2 #2.
- **Presence hub reconnects on every token refresh → `PresenceIndicator` flicker** — deep/FE-2 #1.
- **`/profile` skip-link target** — deep/FE-3 #3; now fixed (`profile/page.tsx:91` has
  `id="main-content"`).
- **All h1-size / button-padding / shell-string / gold-misuse drift across every route** —
  STYLING-CONFORMANCE.md.
- **`error.tsx` hard-coded English while `not-found.tsx` is translated** — deep/FE-3 nit 2.
- **Five client routes with no `layout.tsx` metadata** — deep/FE-3 nit 1 (P-1-6 adds `/u/[id]`).
- **Stockfish bot worker never terminated; single global resolver** — deep/FE-1-1.
- **`a11y.ts` announcer strings never went through next-intl** — deep/FE-1-2; P-8 owns i18n.
- **`ApiTokensCard` "Copied" label never resets** — deep/FE-2 #3.
- **`developers/page.tsx` redefines `API_BASE`** — deep/FE-4-3.
- **Public player profile framed as future work** — ROADMAP 11b.2 (unchecked); the `/u/[id]` route
  and `getPublicProfile` endpoint already exist, so that checkbox is stale (C-9 territory).

### Action list

Ordered.

1. **P-1-1 / P-1-2.** Set `USE_DUMMY_ROWS`, `SAMPLE`, `USE_DUMMY_NAKAMURA` to `false`;
   replace `/profile`'s `MOCK_RATINGS` with the real `getMyRatings` fetch (or an honest empty
   state) and mark peak/chart as not-yet-available; add a unit-test guard that fails the build if
   any fixture flag is truthy. Verify the leaderboard and watch empty states are what ship.
2. **P-1-3.** Fix `PlayButton`: anonymous → `/login?next=`; signed-in empty-lobby →
   `BotGameDialog` / `?mode=bot`; surface errors instead of only `console.error`.
3. **P-1-4.** Add an in-queue "play the bot meanwhile" nudge on the home pools after a
   short wait. Coordinate scope with **P-2** (onboarding).
4. **P-1-5 (owner decision).** Decide whether anonymous play is allowed. If it stays
   gated: add `?next=` to the `RatedPoolsGrid` / `BotPlayPanel` / `OpenSeekList` login redirects
   and label the bot buttons.
5. **P-1-6.** Add `u/[id]/layout.tsx` metadata and a loading-branch h1.
6. **P-1-7.** Link `/unsubscribe` success to `/profile`.

No standing-position reversals. "Free forever", "no correspondence", "individual controller" and
the rest are untouched by any of the above.

---

## P-2 — Onboarding

Date: 2026-08-31
Packet: docs/audits/INDEX.md, table 6

### Verdict

The teaching material is better than the funnel that delivers it. `/rules` is genuinely excellent
and structurally cannot drift: every number on it is read off `rules.generated.ts` at render time
and every ending row is gated on the config flag that governs it, so the manual and the engine
agree by construction. `/learn` is real, interactive, and honest about the numbers it shows, since
it drives them through the same `computeCostPreview` mirror the live board uses. The failure is not
correctness, it is **coverage and handoff**. The tutorial teaches two of the variant's four rules
and stops: the check chip and the repetition guard, the two that decide how endgames actually go,
never appear in any step, while three of the six steps are spent on capture-economy variants. Its
clocks never tick, so the single most important fact about a clock-economy game, that thinking is
itself an expense denominated in the same currency as a capture, is never shown to a learner. And
the landing page a learner is handed back to never states what KiriChess is: the site's own
one-sentence tagline exists in the codebase and is emitted into `<meta>` and the OG image, but is
not rendered anywhere a visitor can read it. A stranger arriving from a tagged link therefore meets
six tiles labelled "Anchor / 6+0 / Steal / C1" with no gloss, and the copy that would explain them
sits one click away behind a 1600-line manual. Separately, the `?src=` mechanism is complete and
working end to end, client to allowlist to dashboard, and not one published link carries it.

Nothing here stops a registered player finishing a first game: the vs-bot path works instantly and
is the correct empty-lobby answer. What is broken is everything upstream of the click that starts
it.

### Findings

#### P-2-1 — The landing page never says what KiriChess is; the tagline that would is used only in `<meta>`

- Severity: High
- Where: `ui/app/[locale]/page.tsx:13-52`. The first prose on the page is the banner at `:24-42`
  ("New to KiriChess? Learn it in a minute.", `ui/messages/en.json:65`), then `:49-51`
  ("Categories are named after how the gamemodes feel in ordinary chess, timewise (estimated).",
  `en.json:66`), then six tiles. `ui/lib/site.ts:8` defines
  `SITE_DESCRIPTION = "Chess where every move costs, or earns, time."` and
  `ui/app/[locale]/layout.tsx:22-39` emits it as the meta description, the OG description and the
  Twitter description. It is never rendered into the document body on any route.
- Impact: this is the page every `?src=` link, every press-kit URL and every search result lands
  on. A stranger who has never heard of the variant is told, in order, that a tutorial exists, that
  category names are estimates of how paces feel, and then shown six invented pool names. The one
  sentence that would orient them in four seconds is written, reviewed, and visible only to
  crawlers and link unfurlers. The missing `<h1>` is filed as a known gap in `.claude/rules/styling.md`
  §4 and in P-1's route inventory, but the product problem is larger than an outline defect: there
  is no premise statement at any size.
- Fix: render `SITE_DESCRIPTION` (or a close variant) as the landing page's `<h1>` plus a one-line
  lede above the pools, which closes the known no-h1 gap and the comprehension gap in one edit.
  Keep the existing learn/rules banner underneath it as the secondary affordance it already is.

#### P-2-2 — `/learn` teaches two of the four rules; the check chip and the repetition guard never appear

- Severity: High
- Where: `ui/lib/tutorial.ts:74-180` defines exactly six steps: `win-without-mate` (copy only),
  `moving-costs-time` (Rule 2), `captures-steal`, `captures-burn`, `captures-bounty` (Rule 1, three
  times), `done` (copy only). Rule 3 is `src/KiriChess.Engine/KiriChessEngine.cs:240-250`; Rule 4
  is the repetition guard (`config/kirichess.rules.json:60`,
  `repeatedPositionNullifiesTime: true`). Both have their own numbered section on `/rules`
  (`ui/app/[locale]/rules/page.tsx:934-981` and `:983-1004`). Neither has a tutorial step.
  `ui/app/[locale]/learn/page.tsx:157-163` computes a check chip inline and adds it to the popups,
  but no step's FEN admits a checking move that a learner would find, so the code is effectively
  dead and the concept is never introduced.
- Impact: the tutorial's own closing copy is "Those are the three economies"
  (`en.json` `learn.stepDoneTitle`), which is an accurate description of what it taught and an
  inaccurate description of the game. A player leaves it not knowing that a check moves the clock
  in both directions, or that shuffling to repeat a position nullifies the transfer. Those are the
  two rules that govern the phase of the game where clock pressure decides the result, which is the
  phase this variant exists to make interesting. Three of six steps are spent on the steal/burn/bounty
  distinction, which a player meets anyway as a pool label, while the two rules they cannot infer
  from a pool label are absent.
- Fix: add a check step (a FEN with an obvious checking move, showing both popups and the chip
  amount from `POOLS[preset].config.checkCheckerGain`) and a repetition step (two-move shuffle,
  showing the second occurrence nullifying the transfer). If six steps is the budget, fold burn and
  bounty into one comparison step rather than dropping a rule.

#### P-2-3 — The tutorial's terminal CTA hands an anonymous learner to a page where nothing they click starts a game

- Severity: High
- Where: `ui/app/[locale]/learn/page.tsx:211-229`. The final step replaces Next with "Play a game"
  (`en.json:288`) linking to `/`, and "Read the rules" linking to `/rules`. Nothing in the six steps
  or in either CTA mentions that an account is required. On `/`, every play affordance for a
  signed-out visitor is a redirect: `ui/components/RatedPoolsGrid.tsx:86-88`,
  `ui/components/BotPlayPanel.tsx:24-27`, `ui/components/OpenSeekList.tsx:75-77`.
- Impact: `/learn` is the only surface on the site built to convert a stranger, and its success
  state is a promise the next page cannot keep. The learner clicks "Play a game", lands on `/`,
  clicks the pool the tutorial just taught them, and is bounced to a login form with no explanation
  and no return path, having been given no prior signal that this was coming. The mechanics of the
  bounce (missing `?next=`, unlabelled buttons) are P-1-5; what this finding adds is that the
  tutorial sets the expectation and is the right place to correct it.
- Fix: make the final-step CTA auth-aware: signed in, open `BotGameDialog` for the pool the last
  step used so "Play a game" is literally one click to a game; signed out, send them to
  `/login?next=/` with a line of copy saying an account is needed to play and that a bot game is
  free and instant. Coordinate the `?next=` half with P-1-5 so it is fixed once.

#### P-2-4 — The tutorial's clocks never tick, so a learner never sees that thinking is also an expense

- Severity: Medium
- Where: `ui/components/learn/TutorialClocks.tsx:78-104` renders `whiteSeconds`/`blackSeconds`
  straight from props with no interval. `ui/app/[locale]/learn/page.tsx:44-45` seeds them from the
  step constants and `:168-174` mutates them only by move deltas. There is no timer anywhere in the
  tutorial.
- Impact: the clocks move only when an event prices them, so the mental model a learner builds is
  "the clock is a scoreboard that changes on captures, moves and checks". The real model is "the
  clock is a budget that drains continuously and is also charged for events", which is what
  `/rules#budget` (`rules/page.tsx:1347-1411`) exists to explain and what makes the Winder pool's
  60 seconds feel the way it does. Step 1's copy asserts "you win if your opponent's clock reaches
  zero" (`en.json:291`) while showing two frozen clocks, which is the exact opposite of the
  demonstration. This is the highest-value single fix in the tutorial: it is the difference between
  understanding the variant and understanding a scoring table.
- Fix: run the active side's clock down in real time during the board steps, and add one line of
  payoff copy on step 2 noting that the toll is charged on top of the time already spent thinking.
  A flag-fall in the sandbox needs no special handling beyond the existing `flagged` state.

#### P-2-5 — The six primary CTAs are labelled entirely in invented vocabulary with no gloss on the page

- Severity: Medium
- Where: `ui/components/PresetTile.tsx:52-66` renders label, time, `PoolBadge` economy and
  `preset.multiplier`, producing "Anchor / 6+0 / Steal / C1". The pool taglines that would explain
  each one exist in `config/kirichess.rules.json:71,90,109,128,147,166` ("The on-ramp.", "No
  take-backs.", "Double stakes.", "The rated default.", "Every trade bleeds.", "Room to think.")
  and are carried through to `POOLS[key].tagline` in `rules.generated.ts`, but no tile renders one.
  The only explanatory copy on the page is `page.tsx:49-51`, which explains the pace naming and not
  the economy or the dial.
- Impact: a first-time visitor choosing where to start is choosing between six proper nouns and a
  two-character code. "C1" is defined only in the `/rules` glossary
  (`rules/page.tsx:453-456`), and Steal/Burn/Bounty only in `/rules` §01 or in tutorial steps 3 to
  5. The per-tile "Game info" link (`PresetTile.tsx:84-89`) is the right affordance but points into
  a long manual, which is a poor answer to a two-second question. `/rules` correctly refuses to
  hardcode a pool name, and the tagline field is exactly the escape hatch that was built for this.
- Fix: render `POOLS[key].tagline` under the pool name on each tile (generated, so it stays in step
  with the config), and add one sentence beside `feelLine` glossing Steal, Burn and Bounty in a
  clause each. Do not write the words into `en.json`; take them from the generated data the way the
  rest of the tile already does.

#### P-2-6 — The vs-bot path, the designated empty-lobby answer, is named in no onboarding copy

- Severity: Medium
- Where: `ui/components/BotPlayPanel.tsx:31-58` sits under the "OTHERS" divider at
  `ui/app/[locale]/page.tsx:61-76`, below the six pool tiles, the seek list and the challenge panel.
  It is absent from the nav (`ui/components/NavBar.tsx:29-35`), from `/about`'s "Try it" row
  (`ui/app/[locale]/about/page.tsx:167-185`, which offers Play, Learn, Rules and Watch), from
  `/learn`'s closing CTAs (`learn/page.tsx:211-229`), and from `/rules` entirely.
- Impact: the packet's premise is that a stranger must be able to play when the lobby is empty, and
  the product does have that answer. It is simply never advertised. Every route into the site
  points a newcomer at rated matchmaking, which on a young site means an unbounded wait. P-1-4
  covers the mechanical fix (a nudge inside a stalled queue); this finding is that the copy on the
  three surfaces a newcomer actually reads never mentions that an instant opponent exists at all.
- Fix: name the bot path in the two places a newcomer finishes reading: `/learn`'s final step and
  `/about`'s "Try it" row. One button each, wording it as practice rather than as a lesser option.

#### P-2-7 — `?src=` works end to end and not one published link carries it

- Severity: Medium (trigger: launch day, when the attribution is meant to start counting)
- Where: the mechanism is complete. `ui/lib/api.ts:688-698` resolves `?src=` once per tab from
  `window.location.search` against a 12-value allowlist (`:666-669`), `:709-716` sends it with every
  beacon, `ui/components/CounterBeacon.tsx:16-28` fires one per navigation,
  `src/KiriChess.Server/Analytics/CounterEndpoints.cs:58-75` records `(date, key, source)` and
  `CounterKeys.cs:89-112` holds the server allowlist including `press` and `directory`. The
  published URLs do not use it: `press-kit/pitch.md:15` and `:20`, and
  `docs/plans/launch/POST-DRAFT.md:60`, all publish a bare `https://kirichess.com`. A repo-wide
  search finds no `?src=` outside `docs/audits/`, `docs/ROADMAP.md` and `docs/legal/PRIVACY.md`.
- Impact: the `press` and `directory` buckets were added for exactly the two artefacts that do not
  use them, so on launch day every arrival lands in `direct` and the source dimension of the
  dashboard reads as though the launch produced no traffic from anywhere. This is not the accuracy
  ceiling M-4-3 describes (bookmarks, refreshes, unlisted channels), which is inherent; it is the
  links the owner controls, which is not. It is also the cheapest thing on this list to fix.
- Fix: append the right `?src=` to the URL in `press-kit/pitch.md`, in `POST-DRAFT.md`'s posting
  checklist, and to the chessvariants entry when AD.3 goes out. Add a one-line note in
  `press-kit/README.md` that any link the owner publishes should carry one, with the allowlist as
  the menu.

#### P-2-8 — Tutorial completion is written to `localStorage` and never read

- Severity: Low (trigger: any returning visitor, and any attempt to route first-timers to `/learn`)
- Where: `ui/lib/tutorial.ts:52` defines `TUTORIAL_STORAGE_KEY`;
  `ui/app/[locale]/learn/page.tsx:77` writes `"1"` on reaching the last step. A repo-wide search
  finds no reader: nothing calls `getItem` on it.
- Impact: the site cannot tell a first-timer from a veteran, so the "New to KiriChess?" banner
  (`page.tsx:24-42`) shows identically forever to someone who has played a hundred games, and there
  is no mechanism to route a genuine first-timer into the tutorial rather than hoping they notice a
  banner. The flag looks like the beginning of that routing and is currently inert storage. Note
  that the `learn-complete` counter beside it (`learn/page.tsx:80`) is server-side and unaffected.
- Fix: either read it (suppress or reword the banner once set; consider a first-visit nudge toward
  `/learn` for a signed-out visitor with no flag) or delete the write, so the next reader is not
  misled into thinking first-run state already exists.

#### P-2-9 — The tutorial's opening claim about flag-fall is unqualified where `/rules` qualifies it

- Severity: Low
- Where: `ui/messages/en.json:291` (`learn.stepWinWithoutMateBody`): "just like in regular chess,
  you win if your opponent's clock reaches zero". `config/kirichess.rules.json:63` sets
  `flagVsInsufficientMaterialIsDraw: true`, and `ui/app/[locale]/rules/page.tsx:301-325` renders
  that ending with `verdictWinStar` and the with-draw description precisely because the plain claim
  is not true.
- Impact: the first sentence a learner reads about the win condition is the one sentence in the
  onboarding material that overstates it, and it is hand-written English rather than generated, so
  flipping the config flag would leave it behind. Small in isolation; it is on the tutorial's
  opening slide, which is the highest-read string on the site after the nav.
- Fix: add the qualifier ("unless they cannot mate with what is left, which is a draw") to the step
  1 copy across the catalogue, or link the phrase to `/rules#ending`. Cross-reference C-3-1, which
  files the engine-side ordering defect around flag-fall separately.

### Already covered

- **`?src=` lost across the Google OAuth round trip; OAuth signups fire no `signup` counter** —
  M-4-1. **`?src=` as a lower bound (bookmarks, refreshes, unlisted channels)** — M-4-3. This report
  does not re-derive either.
- **`/learn`'s module header described a superseded legality model** — C-3-6. The header now
  describes the chess.js sandbox, and the check chip goes through `checkChipDeltas`, which applies
  `doubleCheckChipMultiplier`. Noted only because P-2-2 touches the same code.
- **Flag-fall outranking stalemate/threefold/fifty-move in the engine** — C-3-1 (referenced by
  P-2-9, not re-filed).
- **`PlayButton` no-ops for anonymous users and starts a solo hot-seat board for signed-in ones** —
  P-1-3. **In-queue bot nudge after a stalled wait** — P-1-4. **`?next=` missing on the three login
  redirects; anonymous play undecided** — P-1-5. P-2-3 and P-2-6 are the copy-side counterparts and
  should be fixed in the same pass.
- **Four demo/mock fixtures shipping enabled (`USE_DUMMY_ROWS`, `SAMPLE`, `USE_DUMMY_NAKAMURA`,
  `MOCK_RATINGS`)** — P-1-1, P-1-2. This is the largest first-impression defect on the site and it
  is already filed.
- **`/learn` and `/rules` layout metadata not locale-aware; no bespoke OG per page** — M-1-1,
  M-1-6.
- **`/learn`'s h1 oversized inside the 380px step card; missing CC BY block on `/learn`** —
  STYLING-CONFORMANCE.md, L-9-4.
- **`/about` origin, inventor and dedication copy** — P-9 owns it; not re-read here.
- **Board keyboard/screen-reader affordances on the tutorial board** — P-5 and the NVDA pass.

### Action list

Ordered by first-session impact per unit of work. No standing-position reversals: nothing below
touches free-forever, cosmetics-only, no-correspondence, or the individual-controller position.

1. Add `?src=` to `press-kit/pitch.md`, `docs/plans/launch/POST-DRAFT.md` and the
   press-kit README guidance. One-line edits, and the only item here that expires if it is done
   after launch instead of before.
2. Render `SITE_DESCRIPTION` as the landing `<h1>` plus a lede. Closes the known
   no-h1 gap in the same edit.
3. Make the tutorial clocks tick during board steps, and say once that the toll
   is charged on top of think time.
4. Add a check-chip step and a repetition step to `TUTORIAL_STEPS`; compress
   burn and bounty into one comparison step if six is the budget. Rewrite `stepDoneTitle`
   accordingly. Refresh the stale `tutorial.ts` header while in the file (C-3-6).
5. Make `/learn`'s final CTA auth-aware and open
   `BotGameDialog` for signed-in learners; name the bot path on `/learn` and in `/about`'s "Try it"
   row; add `?next=` to the three login redirects once, for all callers.
6. Render the generated `tagline` on each `PresetTile` and gloss the three
   economies beside `feelLine`.
7. **Owner decision — P-2-8.** Decide whether first-run state is wanted. Read the flag
   or delete the write; do not leave it inert.
8. Qualify the step 1 flag-fall sentence across `ui/messages/*.json`.

---

## P-3 — Day-2 retention
Date: 2026-08-31
Packet: docs/audits/INDEX.md

### Verdict

KiriChess has built exactly one day-2 retention lever end to end, the rematch offer plus its
reach-back mail and push, and that lever does not work. The offer state is only ever delivered as a
live SignalR event, so the returning player, who is by construction the one who was away, arrives at
a page that offers them "Offer rematch" rather than "Accept rematch"; pressing it overwrites the
offer to point back at the person who already left. The 90-second delay that makes the mail a genuine
away-signal is also what guarantees the recipient can no longer act on it. Everything around that
lever is in good shape: preferences are honoured before the mail is built, not after, the unsubscribe
token is stateless and anonymous, and both mails are properly transactional, tied to a named game and
a counterparty's action, with no digest, newsletter or re-engagement mail anywhere in the tree. There
is no ePrivacy drift today. The deeper problem is structural rather than buggy: both notification
categories fire within ninety seconds of a game the recipient was in, so the site's entire ability to
contact a player is conditional on that player having just played. Nothing has absence as its trigger,
which is the literal definition of the day-2 problem this phase is named after. Compounding it, none
of these levers is instrumented at all, so the broken rematch loop would look identical in production
to a rematch loop nobody wanted, and 11a.3's ticked funnel does not contain the first-game and
empty-lobby events its own done-note claims. The measurement blind spot the packet asks about is real
and is worth stating plainly rather than closing: the anonymous counters can never answer a returning-
visitor question, and that is the price 8a.5 deliberately paid.

### Findings

#### P-3-1 — The rematch email and push land on a page that cannot accept the offer
- Severity: **Critical** (a core retention lever is broken end to end)
- Where: `ui/app/[locale]/game/[id]/page.tsx:144-146,339-342,369`;
  `src/KiriChess.Server/Infrastructure/Email/EmailTemplates.cs:74-83`;
  `src/KiriChess.Server/Infrastructure/Push/PushNotificationService.cs:38-39`;
  `src/KiriChess.Server/Games/RematchService.cs:70-75,98`;
  `ui/components/GameOverOverlay.tsx:136-156`
- Impact: The mail's call to action is "Accept the rematch" and its link is `/game/{finishedGameId}`
  (`EmailTemplates.cs:81`). On a cold load of that page `rematchState` initialises to `"idle"`
  (`page.tsx:144-146`) and is then explicitly reset to `"idle"` on first join (`page.tsx:369`). The
  only thing that ever sets `"incoming"` is the live `RematchOffered` hub event (`page.tsx:339-342`),
  which fired roughly ninety seconds earlier while the recipient was away, which is the entire premise
  of sending the mail. `JoinGame`'s snapshot carries no rematch field, so the overlay renders the
  "Offer rematch" button (`GameOverOverlay.tsx:136-144`) instead of "Accept rematch". Pressing it calls
  `OfferAsync`, which finds the existing unaccepted row and overwrites `OfferedById` to the presser
  (`RematchService.cs:72`); `PeekOpenOfferAsync` then refuses to let anyone accept their own offer
  (`RematchService.cs:98`). The offer now points the wrong way and arms a fresh mail back to the
  original offerer. The loop can only ever complete if the original offerer never left, which is
  exactly the case the delay exists to exclude. The immediate push (`PushNotificationService.cs:38-39`)
  lands on the same dead page. Both reach-back channels 11b.1 built therefore resolve to nothing.
- Fix: Carry the open rematch offer on the `JoinGame` snapshot so a cold load can render the accept
  button; `RematchService.PeekOpenOfferAsync` already computes precisely this value and needs only to
  be read during join and surfaced on `GameStateResponse`. Until then, `OfferAsync` should not
  overwrite an existing unaccepted offer made by the *other* player, since that turns a recoverable
  state into a ping-pong.

#### P-3-2 — Every reach-back trigger requires the player to have just played; nothing is triggered by absence
- Severity: **High**
- Where: `src/KiriChess.Server/Infrastructure/Email/NotificationCategory.cs:11-18`;
  `src/KiriChess.Server/Games/PairingNotificationService.cs:63-67`;
  `src/KiriChess.Server/Games/RematchNotificationService.cs:51-56`
- Impact: The complete set of ways this site can contact a player is two categories, `PairingFound`
  and `RematchOffered` (`NotificationCategory.cs:14,17`). Both fire within about ninety seconds of a
  game the recipient was a party to. There is no trigger whose precondition is that the player has
  been gone. Day 2 means, by definition, that the player is not in a game and did not just finish one,
  so the reach-back channel 11b.1 built cannot by construction reach anyone on day 2. The exit
  criterion "a player who is not on the site can be told that something happened on it" is met only
  for a player who was on the site ninety seconds ago.
- Fix: Add one absence-triggered category, and send it by **push only**, not mail: a single opt-in
  "someone is waiting in your pool" fired when a seek sits unmatched and the recipient holds a push
  subscription but no live hub connection. Push is already permission-gated by the browser, so it stays
  clear of the ePrivacy line that a "come back, the lobby is busy" *email* would cross, since that mail
  would be marketing rather than transactional however it is worded.

#### P-3-3 — 11a.3 is ticked, but the funnel contains neither the first-game nor the empty-lobby events its done-note claims
- Severity: **High**
- Where: `docs/ROADMAP.md:1536`; `src/KiriChess.Server/Analytics/CounterKeys.cs:25-47,56-58`;
  `src/KiriChess.Server/Games/MatchmakingService.cs:199,201`; `ui/lib/analytics.ts:6-19`
- Impact: The done-note says first-session events are counter keys covering "learn start/finish, first
  game start/finish, empty-lobby leave" (`ROADMAP.md:1536`). The allowlist actually holds `learn`,
  `learn-complete`, `learn-skip`, `signup`, `queue-join` and `queue-match` (`CounterKeys.cs:56-58`).
  `queue-join` and `queue-match` measure matchmaking, not a game starting or finishing, and the two
  events the item names as the cliff, a first game *finished* and a visitor who met an empty lobby and
  left, have no key at all. The only server-side increments in the codebase are the two matchmaking
  ones (`MatchmakingService.cs:199,201`). Bot games, which are the designated answer to an empty lobby,
  fire no counter whatsoever, so the one path a lone stranger can actually take is invisible. 11a.3 was
  the single item in this phase judged worth delaying a launch for, on the grounds that a one-shot
  event measured by a blind funnel cannot be re-run; the funnel is still blind at the two steps that
  matter most.
- Fix: Add `first-game-finish` and `empty-lobby-leave` to `CounterKeys.All` and its `ui/lib/analytics.ts`
  mirror, recording the first server-side, or amend the 11a.3 note to state what actually shipped.
  Leaving a ticked item claiming coverage it does not have is the more expensive of the two.

#### P-3-4 — No retention lever is instrumented, so a broken one is indistinguishable from an unwanted one
- Severity: **High**
- Where: `src/KiriChess.Server/Analytics/CounterKeys.cs:56-58`;
  `src/KiriChess.Server/Games/RematchService.cs:52,113`;
  `src/KiriChess.Server/Infrastructure/Push/PushNotificationService.cs:47-81`
- Impact: There is no counter key and no metric for a rematch being offered, a rematch being accepted,
  a reach-back mail being sent, or a push being delivered. `RematchService.OfferAsync` and
  `TryClaimAsync` record nothing, and `PushNotificationService.SendToUserAsync` logs only failures
  (`PushNotificationService.cs:79`). This is what would let P-3-1 ship and sit unnoticed: a rematch
  loop that never completes produces exactly the same telemetry, none, as a rematch loop nobody wants.
  Since the anonymous counters are the only analytics surface and `RetentionAnalytics` reads finished
  game rows, the maintainer has no way to ask whether the one day-2 mechanism he built is working.
- Fix: Add two allowlisted keys, `rematch-offer` and `rematch-accept`, recorded server-side in
  `GameHub` in the same shape as `MatchmakingService.cs:199`. The ratio between them is the whole
  health check, and it would have shown P-3-1 as a flat zero on the second key.

#### P-3-5 — Push is discoverable only on `/profile`, long after the moment that would justify granting it
- Severity: **Medium** (trigger: the first real player who leaves a rematch offer unanswered)
- Where: `ui/app/[locale]/profile/page.tsx:197`; `ui/components/PushNotificationToggle.tsx:40,87`
- Impact: `PushNotificationToggle` is mounted on the profile page only, and returns `null` unless the
  viewer is signed in, a service worker is registered, and VAPID is configured
  (`PushNotificationToggle.tsx:87`). Push is the better transport for every trigger this site has, and
  it is the transport P-3-2's fix depends on, but the permission ask never appears anywhere near a
  moment that would make a player want it, such as a finished game or an open seek. A player who never
  opens `/profile`, which is most players, has no push and will never learn it exists. The most
  valuable notification channel is therefore gated behind the least visited page.
- Fix: Offer the permission prompt once, contextually, at the game-over overlay after a first finished
  game against a human, keeping `/profile` as the place to change the setting afterwards.

#### P-3-6 — The anonymous counters cannot answer any returning-visitor question, and the account curve does not substitute
- Severity: **Medium** (definitional, for AD.7 to absorb rather than a code fix)
- Where: `src/KiriChess.Server/Data/KiriChessDbContext.cs:269-274`;
  `src/KiriChess.Server/Analytics/CounterKeys.cs:5-10`;
  `src/KiriChess.Server/Analytics/RetentionAnalytics.cs:31-49`
- Impact: A `DailyCounter` row is keyed by `(UTC day, counter key, campaign source)` and nothing else,
  and that bound is exactly what makes the unauthenticated write endpoint safe to expose
  (`KiriChessDbContext.cs:269-274`). The consequence, which is permanent and deliberate rather than a
  gap: how many of yesterday's `/learn` finishers came back today, whether a tutorial finisher ever
  signed up, and whether an anonymous bot-game player returned are all unanswerable, now and forever,
  and no amount of additional counter keys changes that. The only genuine cohort instrument,
  `RetentionAnalytics.BuildFacts`, joins games onto signups (`RetentionAnalytics.cs:31-49`) and
  therefore begins at registration, which is downstream of the largest drop-off. Everything before
  signup is a stack of independent daily totals, not a funnel that follows anyone. This should be
  written into AD.7's own definitions, not closed, since closing it means a visitor id and with it the
  consent banner, the DPA question and a retention rule, which is precisely the trade 8a.5 refused.
- Fix: State the limit in AD.7 and in the analytics page's own copy, so a future reader does not
  mistake a pre-signup step ratio for a cohort. Do not add a visitor or session identifier.

#### P-3-7 — The rematch mail timer is in-process and is dropped by any restart inside its window
- Severity: **Low** (trigger: a deploy or machine restart during play hours)
- Where: `src/KiriChess.Server/Games/RematchNotificationService.cs:55,62`
- Impact: `Arm` starts a detached `RunAsync` with a bare `Task.Delay` (`RematchNotificationService.cs:55,62`)
  and nothing persists the pending send. A restart inside the ninety-second window silently loses the
  mail, and the offer row carries no marker saying a notification was owed, so nothing reconciles it
  afterwards. The window is short, which is why this is Low rather than higher, but it is a
  fire-and-forget task with no bound on how many can be in flight.
- Fix: Once P-3-1 is fixed and the offer is readable at join time, the timer stops being the only path
  and this degrades gracefully on its own; otherwise reconcile pending offers on startup from the
  `RematchOffers` rows rather than relying on an in-memory delay.

#### P-3-8 — `WEEKLY_MEETUP` is a dead constant that reads as a published play hour
- Severity: **Low**
- Where: `ui/lib/site.ts:14`
- Impact: `WEEKLY_MEETUP = "Fridays 20:00 UTC"` is still exported and is referenced by nothing in `ui/`;
  8.8a removed the footer and lobby copy that used to render it, and 11a.4 owns putting a real hour
  back. Left as it is, the next reader of `site.ts` reasonably concludes an hour is published and that
  the liquidity question has an answer, when in fact nothing on the site tells a stranger when anybody
  will be online next. `PresenceIndicator` answers "now" and nothing answers "next". 11a.4 being open
  is not a finding; the constant surviving its own removal is.
- Fix: Delete the constant, and let 11a.4 reintroduce it with the banner that consumes it.

### What is clean, and worth not disturbing

- **Preferences are honoured before the mail is built, not after.** Both senders check
  `IsSubscribedAsync` and return early (`PairingNotificationService.cs:74`,
  `RematchNotificationService.cs:80`), and both also gate on a confirmed address.
- **Unsubscribe genuinely works without a login.** `POST /api/notifications/unsubscribe` is anonymous,
  rate-limited under the `Auth` policy, and verifies a stateless HMAC token
  (`NotificationEndpoints.cs:16-34`), so a link in a months-old mail still works.
- **Transactional-only holds today.** Both reach-back mails name a specific game and are triggered by a
  counterparty's action; confirmation and password reset correctly carry no unsubscribe link
  (`EmailTemplates.cs:98-102`, `NotificationCategory.cs:3-10`). There is no digest, newsletter or
  re-engagement mail anywhere in the tree. The ePrivacy risk here is entirely prospective, and P-3-2's
  fix is written to stay on the right side of it.
- The `rated: false` passed to the pairing mail (`PairingNotificationService.cs:79`) is correct, not a
  repeat of 11a.2's defect: challenge games are created unrated at `ChallengeEndpoints.cs:186`.

### Already covered

- List-Unsubscribe / RFC 8058 one-click, push ignoring per-category mail preferences, bounce and
  complaint handling, Reply-To: **M-5-1 through M-5-5**. Not re-reported.
- `?src=` attribution limits, OAuth signups firing no counter, the client/server allowlist being
  hand-mirrored, and "played again" versus "came back": **M-4-1 through M-4-7**. P-3-6 is the
  pre-signup half M-4-5 explicitly hands to this packet.
- Push and notification mail appearing in no legal document: **L-6-2**.
- The `/learn` funnel's content problems, the terminal CTA, and tutorial completion written to
  `localStorage` and never read: **P-2-2, P-2-3, P-2-8**.
- Puzzles (11b.4), progression that is not a rating (11b.3), the public profile (11b.2), the bot
  ladder leaving a trace (11b.5) and the play hour itself (11a.4) are open ROADMAP items, not
  findings. P-3-8 concerns only the stale constant left behind.

### Action list

1. Surface the open rematch offer on the `JoinGame` snapshot so the emailed and
   pushed link resolves to an accept button. This is the whole packet; everything else is secondary.
   Add a test that joins a finished game cold, with no prior hub event, and asserts the offer is
   visible, since that is the exact path no current test covers.
2. Add `rematch-offer` and `rematch-accept` counters before shipping the fix, so the
   repair is verifiable in production rather than only in a test.
3. Either add the two missing funnel keys or correct the 11a.3 done-note. Do not
   leave a ticked item claiming coverage it lacks.
4. Delete the dead `WEEKLY_MEETUP` constant.
5. **Owner — P-3-2.** Decide whether an absence-triggered notification is wanted at all before anyone
   builds it. If yes, push only; an email of this shape is marketing under ePrivacy regardless of
   wording, and this is a decision, not an implementation detail.
6. Move the push permission ask to a contextual moment, after 5 lands.
7. Write the pre-signup measurement limit into AD.7's definitions. No visitor id.
8. Fold into 1; revisit only if the mail is still the sole path afterwards.

---

## P-4 — Failure UX

Date: 2026-08-30
Packet: docs/audits/INDEX.md, table 6

### Verdict

The failure this packet exists to catch is real and unmitigated on the one screen that matters
most. The live game page wires `withAutomaticReconnect()` and an `onreconnected` resync, but nothing
else: there is no `onreconnecting` handler, no `onclose` handler, and no "reconnecting" state
anywhere in the UI (grep confirms the string does not exist in `ui/messages/`). So a WebSocket drop,
a server 5xx on the hub, or the single Fly machine being killed for a deploy all look **identical to
an opponent thinking** — for up to ~42 seconds, while the cosmetic clock keeps ticking down and even
renders "Flagged". After the 4th failed retry SignalR gives up silently and the tab is frozen until
a manual refresh, with the game possibly already forfeited or aborted server-side and the outcome
broadcast never delivered. The player who dropped — the only one who can act — is also the only one
not told anything, while their opponent gets a clear "forfeits in 30s" banner.

The auth / OAuth / email paths are the opposite story: explicit `pending`/`success`/`error` states,
translated error slugs, a StrictMode latch on the single-use exchange code, `safeReturnPath`
laundering, and honest copy. The offline page and both error boundaries (`error.tsx`,
`global-error.tsx`) are careful and well-reasoned. The one dead-end there: a blocked Turnstile
script (common — blockers routinely kill `challenges.cloudflare.com`) leaves the register button
permanently disabled with no message and an infinite 100ms poll.

The theme: **the game client treats "connected" as permanent and never tells the player when it
isn't.** Findings P-4-1 and P-4-2 are the ones to fix before launch.

### Findings

#### P-4-1 — Live game page has no "reconnecting" indicator; a dropped socket looks like a thinking opponent

- Severity: High (trigger: any transport drop mid-game — mobile network switch, wifi handoff,
  laptop sleep, Cloudflare/Fly blip; i.e. routine on mobile)
- Where: `ui/app/[locale]/game/[id]/page.tsx:383-386` (only `conn.onreconnected` is registered; no
  `conn.onreconnecting`, no `conn.onclose`); `ui/lib/realtime.ts:52` (`withAutomaticReconnect()`
  with SignalR's default 0/2/10/30s schedule); `ui/app/[locale]/watch/[id]/page.tsx:184-188` has
  the same gap for spectators
- Impact: During the entire reconnect window nothing on screen changes — no banner, no spinner, no
  dimming. The 250ms cosmetic ticker (`page.tsx:428-431`) is independent of the connection, so
  `displayTimes()` keeps counting the active player's clock down from the last sync point and will
  render `0:00` and the red "Flagged" label while no move can possibly land. A player cannot
  distinguish "opponent is thinking" from "my connection is dead". If reconnection succeeds,
  `onreconnected` silently resyncs and all is well; if it does not, see P-4-2.
- Fix: register `conn.onreconnecting(() => setConnLost(true))` and
  `conn.onreconnected(() => setConnLost(false))`; render a persistent banner ("Connection lost —
  reconnecting…") with the existing `notice-banner` class and an assertive announcement, mirroring
  the existing `OpponentDisconnected` handling at `page.tsx:320-329`. Apply the same to
  `watch/[id]`.

#### P-4-2 — When automatic reconnect gives up, the game page freezes permanently with no message

- Severity: High (trigger: a network outage longer than ~42s, or the owning Fly machine taking
  longer than that to come back after a deploy/crash — per `docs/audits/I.md (I-1)` a
  single-machine `strategy = "immediate"` deploy drops every live socket)
- Where: `ui/app/[locale]/game/[id]/page.tsx:195-425` (no `conn.onclose` handler);
  `ui/node_modules/@microsoft/signalr/src/DefaultReconnectPolicy.ts:7`
  (`[0, 2000, 10000, 30000, null]` — stops after the 4th attempt)
- Impact: SignalR fires `onclose` and stops for good; nothing handles it. Board, move list and
  controls stay on screen, the cosmetic clock keeps ticking to zero, and `MovePlayed` / `GameEnded`
  can never arrive. If the server forfeited the player on the 30s abandon timer
  (`src/KiriChess.Server/Games/GameConnectionTracker.cs:23`) or aborted the game on restart
  (`src/KiriChess.Server/Games/GameRecoveryService.cs:150-162`, `Termination = ServerRestart`), that
  outcome broadcast never reaches this tab. The player is left on a live-looking board of a game
  that is already over. Only a manual refresh recovers — and `JoinGame` then resyncs correctly, so
  the fix is purely about telling the player to do it.
- Fix: handle `conn.onclose` — show a blocking overlay ("Disconnected from this game") with a
  Reload button, or run a slower manual `conn.start()` + `join()` retry loop after the automatic
  policy is exhausted. At absolute minimum, surface the state.

#### P-4-3 — Moving, resigning or offering a draw while disconnected shows a raw SignalR error string

- Severity: Medium (trigger: interacting with the board during the P-4-1 window)
- Where: `ui/app/[locale]/game/[id]/page.tsx:466-477` (`handleMove` catch renders `err.message`
  verbatim), `:531-538` / `:540-547` / `:549-557` / `:559-567` / `:569-578` (resign / offer draw /
  respond draw / offer rematch / accept rematch, same pattern); `GameControls` is `disabled` only on
  `gameOver` (`:754-761`)
- Impact: While the socket is down, `connRef.current.invoke(...)` rejects with SignalR's internal
  English text ("Cannot send data if the connection is not in the 'Connected' State.") which is
  shown in red at the top of the board — technical, untranslated, and not actionable. Because there
  is no reconnecting banner (P-4-1), this cryptic line is the *only* feedback the player gets that
  something is wrong, and only if they happen to try to act.
- Fix: check `conn.state !== signalR.HubConnectionState.Connected` in the action handlers (the
  guard pattern already exists at `ui/components/OpenSeekList.tsx:83` and
  `ui/components/RatedPoolsGrid.tsx:93`) and show the P-4-1 copy; disable the board and
  `GameControls` while not connected.

#### P-4-4 — The disconnected player is never warned their own game is on a 30-second forfeit timer

- Severity: Medium (trigger: your connection drops for 10–30s mid-game)
- Where: server arms the forfeit at
  `src/KiriChess.Server/Games/GameConnectionTracker.cs:225` and `:240-253` (`GraceSeconds = 30`);
  the opponent is told via `OpponentDisconnected` → `ui/app/[locale]/game/[id]/page.tsx:320-329`
  ("Opponent disconnected. They forfeit in {seconds}s…", `ui/messages/en.json:593`); there is no
  message aimed at the dropped player's own client
- Impact: The player who dropped is the one who can fix it, and the only one told nothing. Combined
  with P-4-1 they have no idea a clock is counting down against their game until it is already lost
  (`Termination = Abandoned`). The asymmetry is stark: connected player sees a countdown,
  disconnected player sees a normal board.
- Fix: word P-4-1's `onreconnecting` banner with the stakes ("Reconnecting… you forfeit if you're
  away too long"). No server change — the client already knows it is reconnecting, and the 30s
  grace is a known constant.

#### P-4-5 — A blocked or failed Turnstile script leaves the register button permanently disabled with no message

- Severity: Medium (trigger: Turnstile enabled in production per DH.3, and the visitor's browser
  blocks `challenges.cloudflare.com` — ad/privacy blockers commonly do)
- Where: `ui/app/[locale]/login/page.tsx:79-110` (the `tryRender` poll re-schedules itself every
  100ms with no timeout and no error branch), `:401-407` (submit `disabled` while
  `!!TURNSTILE_SITE_KEY && !turnstileToken`), `ui/app/[locale]/layout.tsx:90-95` (the `<Script>` tag
  has no `onError`)
- Impact: If `api.js` never loads, `window.turnstile` stays undefined, the widget never renders,
  `turnstileToken` stays null, and "Create account" is disabled forever with nothing on screen
  explaining why. The register tab simply looks broken, and there is no diagnostic or fallback.
- Fix: bound the poll (e.g. give up after ~10s) and on give-up set an error ("Couldn't load the
  human-verification check — allow challenges.cloudflare.com or try another browser"). Add `onError`
  to the `<Script>` in `layout.tsx` to set the same state.

#### P-4-6 — Turnstile widget has no `expired-callback` / `error-callback`; a stale token is submitted and bounced

- Severity: Low (trigger: taking longer than ~5 minutes to complete the register form)
- Where: `ui/app/[locale]/login/page.tsx:91-98` — `window.turnstile.render` is passed only
  `sitekey`, `action` and `callback`
- Impact: Turnstile tokens expire ~300s after issue. A slow registrant submits a dead token; the
  server returns "Verification failed.", which *is* caught and the widget reset (`:176-179`), so it
  self-heals — at the cost of one confusing failed submit. An `expired-callback` that cleared
  `turnstileToken` would re-disable the button and silently re-challenge before the user ever hits
  submit.
- Fix: pass `"expired-callback": () => setTurnstileToken(null)` and an `"error-callback"` to
  `render`.

#### P-4-7 — `reconnectedFailedResync` is a terminal error with no recovery action

- Severity: Low (trigger: `join()` throws inside `onreconnected` — rare)
- Where: `ui/app/[locale]/game/[id]/page.tsx:385`, `ui/app/[locale]/watch/[id]/page.tsx:187`;
  string at `ui/messages/en.json:597` ("Reconnected, but failed to resync.")
- Impact: The socket is back but the client state may be stale, and the page just prints one
  sentence and stops. There is no retry and no Reload button; the user has to work out for
  themselves that a refresh would fix it.
- Fix: retry `join()` once or twice on a short delay before surfacing anything; if it still fails,
  render a Reload button rather than a bare line of text.

#### P-4-8 — The cosmetic clock keeps running during a disconnect, actively selling the "still live" illusion

- Severity: Low (facet of P-4-1; listed separately because the fix is independent and small)
- Where: `ui/app/[locale]/game/[id]/page.tsx:428-431` (250ms `setNow` interval, no connection
  gate), `:586-598` (`displayTimes()` interpolates from `syncRef` regardless of connection state);
  same shape in `ui/app/[locale]/watch/[id]/page.tsx:220-238`
- Impact: A moving clock is the single strongest cue that a game is live. Keeping it moving after
  the socket drops turns "no signal" into a *false positive* signal — a player watching the
  opponent's clock tick down has positive evidence the game is fine when it is not.
- Fix: when disconnected (the P-4-1 state), freeze the displayed clocks at the last known reading
  and grey them, so the stopped clock is itself the tell.

### Already covered

- **Reconnect wipes the move list / captured material / clock graph** — `docs/audits/C.md (section FE-4)`
  (FE-4-1), `docs/audits/C.md (C-1 improvement plan)` batch 2.1, `docs/audits/C.md` (C-11-2). Not
  re-filed. Note only: even once the server-side `GameSnapshotDto` move-log fix lands, the *silent*
  character of a reconnect (this packet's concern) is untouched without P-4-1 / P-4-2.
- **Flag-fall sweep serialises `GameEnded` broadcasts during a burst** — `docs/audits/I.md (I-1)`
  AS-5. That delays the "game over" signal by seconds; P-4 covers the *no*-signal case, not the
  slow-signal case.
- **A single-machine `immediate` deploy interrupts in-progress games** — `docs/audits/I.md (I-1)`
  §4.3, `docs/audits/C.md (section INF-1)`, `DEPLOYMENT.md` §12. P-4-2 is the client-side UX of
  that event, not the deploy strategy.
- **`api.generated.ts` is types-only; `api.ts` paths hand-maintained** — `docs/audits/C.md` (C-9-1),
  `docs/audits/C.md` (C-11-4).
- **Board never flips for Black; clock has no sub-second display** — `docs/audits/C.md` (C-11-1,
  C-11-8). Not failure UX.
- **`safeReturnPath` in `auth/callback` duplicates `challengeLink.ts`** — `docs/audits/C.md`
  "already covered" / IMPROVEMENT_PLAN batch 8. The callback's error handling itself is sound.

### Action list

Ordered. None reverses a standing position.

1. **P-4-1 + P-4-8 + P-4-4** — one change: add `onreconnecting` / `onreconnected` connection-lost
   state to `game/[id]` (and `watch/[id]`), render a `notice-banner` + assertive announcement,
   freeze and grey the clocks while lost, and word the player-facing copy with the forfeit stakes.
   Do before launch / Todo #16.
2. **P-4-2** — handle `conn.onclose` on both game and watch pages with a Reload overlay (or a manual
   retry loop after the automatic policy is spent).
3. **P-4-3** — gate the move / resign / draw / rematch handlers and the board on
   `conn.state === Connected`; reuse the existing guard pattern from `OpenSeekList` /
   `RatedPoolsGrid`. Rides on the state added in step 1.
4. **P-4-5 + P-4-6** — bound the Turnstile render poll, add failure copy, add `<Script onError>`,
   and pass `expired-callback` / `error-callback` to `turnstile.render`. Needed before Turnstile is
   switched on in production (DH.3), not after.
5. **P-4-7** — retry `join()` on resync failure; Reload button as the fallback. Low priority.

---

## P-5 — Accessibility (engineering)
Date: 2026-08-30
Packet: docs/audits/INDEX.md

### Verdict

The accessible board is a serious piece of engineering, not a checkbox: a parallel `role="grid"`
of 64 cells with roving tabindex next to the drag-and-drop board (`AccessibleBoard.tsx`), a spoken
cost preview on legal targets (`describeTarget`), one-utterance move announcements in a fixed
canonical order (`describeMove`), two always-mounted live regions split polite/assertive
(`LiveAnnouncer.tsx`), and urgency announcements derived from a pair of clock readings so a
throttled background tab cannot swallow a threshold (`crossedThreshold`). Reduced motion is handled
the right way — one global `@media (prefers-reduced-motion: reduce)` override that flattens every
CSS transition and animation app-wide, and there is no JS animation library in the board/clock
stack for it to miss.

The gaps are at the seams. The game-over overlay has no dialog semantics and no focus management at
all, so on checkmate a keyboard or screen-reader user is stranded on a now-inert board cell and has
to Tab the whole page to reach Rematch / New game / Replay. The shared `Dialog` has correct ARIA
but no focus trap, initial focus, or focus restore. The axe scan's page list has drifted behind the
route list and no longer matches its own "every anonymous page" claim. Selecting a piece produces no
spoken confirmation (deselecting does). The visual clocks have no accessible text alternative.

The single highest-value checks — does a real screen reader in *browse mode* ever reach the grid's
arrow keys (NVDA-PASS step 4), and do the urgency announcements actually arrive in time (step 32) —
still need a human, and NVDA-PASS.md is itself ~2 weeks stale: it predates the draw-offer, rematch,
disconnect/reconnect and rating-delta announcements and the game-over overlay, none of which have a
scripted step. Bring the script current before anyone runs it.

### Findings

#### P-5-1 — `GameOverOverlay` has no dialog semantics and no focus management
- Severity: High (trigger: any keyboard-only or screen-reader user finishing a game)
- Where: `ui/components/GameOverOverlay.tsx:103-104` (the shell is a bare
  `<div className="fixed inset-0 z-50 flex ...">` with a `<Card>` inside — no `role="dialog"`, no
  `aria-modal`, no `aria-labelledby`, no Escape handler); rendered at
  `ui/app/[locale]/game/[id]/page.tsx:775-798` with no focus move.
- Impact: The assertive result announcement does fire (`page.tsx:295`,
  `resultAnnouncement`), so a screen-reader user hears *that they lost*. But focus is never moved
  into the overlay, the background page is not inert, and there is no focus trap, so the actionable
  controls (Offer/Accept rematch, New game, View replay, Report opponent) are reachable only by
  tabbing forward through the entire NavBar, both boards, GameControls and the move list first. A
  sighted keyboard user also gets no Escape and no obvious focus landing. This is the one modal in
  the app that appears unprompted, at the moment the user most needs its buttons.
- Fix: render it through the shared `Dialog` (it already carries `role="dialog"`, `aria-modal`,
  `aria-labelledby`, Escape), or add the same four attributes here; on mount move focus to the
  heading or the primary button; on unmount restore focus to where it was. Pairs with P-5-2.

#### P-5-2 — Shared `Dialog` has ARIA but no focus trap, initial focus, or focus restore
- Severity: Medium (trigger: a keyboard-only user opening the report modal from the game-over screen)
- Where: `ui/components/Dialog.tsx:32-72` — `role="dialog" aria-modal="true" aria-labelledby` and
  Escape-to-close are present (`:41-47, :63`), but nothing calls `.focus()` on open, Tab is not
  constrained to the dialog subtree, and focus is not returned to the invoking control on close.
  Only consumer today is `ReportDialog.tsx`.
- Impact: `aria-modal="true"` tells a screen reader's virtual cursor to stay inside, but it does
  not constrain keyboard Tab focus. A keyboard user opening the report dialog can Tab straight out
  onto the visually-obscured page behind it, and on close lands back at the top of the document
  rather than on the "Report opponent" button they came from. Standard modal-accessibility miss.
- Fix: on mount, focus the first focusable element in the dialog (or the dialog container with
  `tabIndex={-1}`); wrap Tab/Shift+Tab to cycle within the dialog; store `document.activeElement`
  on mount and restore it on unmount.

#### P-5-3 — The axe scan's page list has drifted behind the route list
- Severity: Medium (trigger: a WCAG defect ships on an unscanned anonymous page)
- Where: `e2e/tests/accessibility.spec.ts:46-58` (`ANONYMOUS_PAGES`) and the file header comment
  `:6-12` ("every page a stranger can reach without a live game"); `.claude/rules/testing.md`
  describes the suite as covering "every anonymous page".
- Impact: These anonymous-reachable routes are not scanned: `/developers` (the bot API's front
  door, in `sitemap.ts`), `/reset-password` and `/verify-email` (both carry forms, both reachable
  only from an email link, so nothing has ever exercised them), `/offline` (PWA fallback),
  `/unsubscribe` (token link from every email), and the `[...rest]` 404 page. `/reset-password` and
  `/verify-email` are the concerning ones — an unlabelled field or contrast defect on a password
  reset form would never be caught. `/developers` is also named by L-8-4 from the legal-exposure
  angle; this is the engineering side of the same gap.
- Fix: add `/developers`, `/offline`, `/unsubscribe` and the 404 route to the loop; add
  `/reset-password` and `/verify-email` as separate tests with a syntactically-valid dummy token in
  the query so the form renders. Decide explicitly whether the staff-403 render is in scope.

#### P-5-4 — Selecting a piece in the accessible board is silent
- Severity: Medium (trigger: a screen-reader user selecting a piece to see move prices)
- Where: `ui/components/AccessibleBoard.tsx:141-172` (`handleActivate` sets `selectedFrom` and
  nothing else) vs `:221-227` (Escape *does* call `onAnnounce("Selection cleared.")`).
- Impact: On `Enter`/`Space` over one of your pieces the only feedback is `aria-selected="true"`
  flipping on the already-focused cell (`:297`), and a focused element's `aria-selected` change is
  announced inconsistently across NVDA / JAWS / VoiceOver. NVDA-PASS.md step 11 assumes "the
  selection is taken" is perceivable. The asymmetry with the spoken deselect makes the silence
  read as "nothing happened."
- Fix: call `onAnnounce` on successful selection, e.g. "e2 selected" plus the legal-target count;
  route it through the same translator work as P-8-1.

#### P-5-5 — Visual `Clock` has no accessible text alternative and is not hidden from assistive tech
- Severity: Low (trigger: a screen-reader user reading the board area in browse mode)
- Where: `ui/components/Clock.tsx:40-44` renders the `M:SS` digits with no `aria-label` / no
  `aria-hidden`; `ui/lib/a11y.ts:152` documents that "1:32" "reads as a date to a screen reader".
  The intended screen-reader path is the `c` key (`AccessibleBoard.tsx:228-232` →
  `describeClocks`, which is correct), but `c` and `m` are absent from the visible panel text
  (`AccessibleBoard.tsx:257-261` mentions only arrows / Enter / `?`); they appear only in
  `KEY_SUMMARY` spoken on `?`.
- Impact: A screen-reader user who navigates onto the clocks hears a bare `M:SS` with no "White" /
  "you" framing and no live updates, and may never discover the `c` key that does it properly.
- Fix: `aria-hidden` the digit node (keeping the `c` key as the readout) or add an `sr-only`
  polite mirror updated on a coarse cadence; and add `c` / `m` to the visible panel `<p>`.

#### P-5-6 — Arrow-key move entry cannot under-promote (accessible-input parity)
- Severity: Low (trigger: a keyboard-only or screen-reader user promoting to anything but a queen)
- Where: `ui/components/AccessibleBoard.tsx:134-139` (`buildUci` hard-codes the promotion suffix
  `q`); the `MoveEntryInput` regex `:60` accepts `[qrbn]?` and the typed path *can* send `e7e8n`
  (`:65` matches `u === normalized` before the queen fallback `:66`), but the arrow-navigate-and-
  Enter path is queen-only.
- Impact: Cross-references C-11-6, which flags the same for the click path; the packet notes there
  say "P-5 owns the accessible-input parity question". Net: a keyboard user who navigates rather
  than types can only ever promote to a queen. Rare, and the typed workaround exists, so Low.
- Fix: if `Board`'s drag path grows a promotion picker (C-11-6's fix), give `AccessibleBoard` a
  spoken promotion prompt when a pawn reaches the last rank instead of defaulting to `q`.

#### P-5-7 — Reduced-motion coverage is sound but `Board` still passes an unconditional animation duration
- Severity: Low (trigger: a `prefers-reduced-motion` user watching pieces move)
- Where: `ui/app/globals.css:221-230` (the global override — flattens `animation-duration`,
  `animation-iteration-count`, `transition-duration`, `scroll-behavior`); `ui/components/Board.tsx:220`
  passes `animationDuration={300}` to `<Chessboard>` regardless of the media query;
  `ui/components/TimeDeltaLayer.tsx` + `globals.css:87-160` are pure CSS keyframes and are covered.
- Impact: The global rule covers CSS transitions and animations, which is what react-chessboard v4
  uses for piece movement (inline `transition` style — the `!important` `transition-duration`
  override wins over it) and what every other animated component in the tree uses. There is no
  framer-motion / react-spring / GSAP anywhere in the board or clock stack, so the "JS animations
  don't respect the CSS media query" risk does not currently bite. Residual: this depends on
  react-chessboard's internal implementation staying CSS-transition-based, and cannot be confirmed
  without a browser.
- Fix: low priority — pass `animationDuration={0}` when
  `window.matchMedia("(prefers-reduced-motion: reduce)").matches`, as belt-and-braces against a
  future react-chessboard that animates in JS. Verify in the manual pass (below).

#### P-5-8 — NVDA-PASS.md is stale: no scripted step for four announcement types or the game-over overlay
- Severity: Low (trigger: someone runs the pass and reports it "complete")
- Where: `docs/plans/a11y/NVDA-PASS.md` (prepared 2026-08-17). Section D ends at promotion (step
  22); there is no step for the `GameOverOverlay` that appears immediately after checkmate. No step
  anywhere covers: the draw-offer announcement (`page.tsx:317`, `opponentOffersDraw`), rematch
  offered / accepted (`page.tsx:339-348`), opponent disconnect / reconnect
  (`page.tsx:320-338`), or the Glicko-2 rating delta folded into the result announcement
  (`resultAnnouncementWithRatingTemplate`, `page.tsx:98-107`).
- Impact: A completed run of the current script would leave the four most recently added assertive
  announcements and the overlay's keyboard reachability (P-5-1) untested, while reading as a full
  pass.
- Fix: add a Section H (post-game: overlay reachability and focus, rematch, report opponent) and
  extend Sections E/F to cover draw-offer, rematch and presence announcements, before the pass is
  run. Fold in the rating-delta clause at step 17/D.

#### P-5-9 — Zoom / reflow is untested and not verifiable without a screen reader
- Severity: Low (trigger: WCAG 1.4.4 resize-text / 1.4.10 reflow becomes a stated claim)
- Where: no automated coverage; `e2e/tests/accessibility.spec.ts` scans at the default viewport
  only. Layout is responsive (`page.tsx:685` `lg:grid-cols-[240px_1fr_320px]` collapses to a single
  column below `lg`), which should satisfy reflow at 320px and resize at 200%, but
  `ui/components/Clock.tsx:31` sets `overflow-hidden` on a container whose `text-6xl` (`:41`) digits
  could clip at 400% zoom in the narrow single-column stack.
- Impact: Nothing catches a clipped clock or a horizontal scrollbar at 320px CSS width. Overlaps
  P-6 (mobile layout) but from the zoom lens rather than the touch/PWA lens.
- Fix: add a Playwright assertion at 320px CSS width (`document.scrollingElement.scrollWidth <=
  clientWidth`) to the accessibility spec; check clock legibility at 400% in the manual pass.

### Needs a human screen-reader pass

None of the below can be done from the repository. All are blocked for outside volunteers behind Cloudflare
Access until DH.3 (already ROADMAP item #5, owner named — see L-8 "Already covered"); the owner can
run it now.

1. **NVDA-PASS.md step 4 — the make-or-break one.** Whether `role="grid"` + roving tabindex
   (`AccessibleBoard.tsx:263-298`) actually delivers arrow-key navigation in NVDA/JAWS *browse
   mode*, or only after the user manually presses `NVDA+Space` to enter focus mode. `role="grid"`
   usually auto-switches NVDA to focus mode when a `gridcell` takes focus, but this is exactly the
   "record it as a finding, not a pass" case in the script and nobody has heard it. If it fails,
   the fix is a real decision (`role="application"` on the grid, or an explicit instruction in the
   panel text) and belongs to whoever runs the pass.
2. **NVDA-PASS.md step 17 — the one-utterance rule.** Move + price + consequence must arrive as a
   single uninterruptible utterance from `describeMove`, not two announcements with a `Ctrl`-able
   gap between them.
3. **NVDA-PASS.md steps 32-34 — urgency timing.** Do the 60/30/15/10/5..1s announcements
   (`page.tsx:440-464`) arrive in time and interrupt cleanly, including a second crossing after
   winning time back? "An announcement that arrives late is worse than none, because it costs you
   the seconds it was warning about."
4. **NVDA-PASS.md Section C — cost preview timing.** Does `describeTarget`'s price clause arrive
   early enough to be a decision rather than a narration?
5. **Post-game overlay (new step, see P-5-8/P-5-1).** After the assertive result announcement, can
   a screen-reader / keyboard user actually reach and operate the overlay's buttons?
6. **Zoom walk (P-5-9).** 200% and 400% browser zoom across the lobby, a live game and the content
   pages; a real 320px reflow check; clock legibility at 400%.
7. Ideal: a daily screen-reader user runs the whole refreshed script. Their verdict outranks the
   checklist; steps 4, 17 and 32 first.

### Already covered

- Entire accessible board + `lib/a11y.ts` hardcoded English (not through next-intl) — **P-8-1**,
  **P-8-4**, deep/**FE-1-2**. P-5-4's fix must route through whatever is decided there.
- Malformed stray `focus-within:` token in `AccessibleBoard.tsx:255` (rounded-strip leftover) —
  **P-10-3**.
- Visual board never flips for Black; `you` feeds only announcements, never cell order — **C-11-1**,
  restated in **P-8**.
- Under-promotion impossible via click path — **C-11-6** (P-5-6 is the accessible-input framing the
  C-11 note defers to this packet).
- Clock shows no sub-second time in a scramble — **C-11-8** (notes the accessible layer's
  `crossedThreshold` already does better).
- Click move has no optimistic feedback, only the server round-trip — **C-11-7**.
- Skip-link target missing / no `scroll-mt-24` on early-return branches elsewhere in the app —
  **STYLING-CONFORMANCE.md:41-51, 474**, **P-10**. Note: the game page's own three return branches
  (`page.tsx:600-628, 651-658`) all *do* carry `id="main-content"` + `scroll-mt-24` + an `<h1>`.
- Heading-order / single-h1 defects on content pages — **STYLING-CONFORMANCE.md**.
- `AccessibleBoard` untested where `Board` is — **C-10** (`AccessibleBoard.test.tsx` exists; `Board`
  has no unit test).
- Background-tab clock throttling and the 250ms ticker — **P-6:14**.
- Legal a11y duty, an accessibility statement, and not inheriting the axe scope as a conformance
  claim — **L-8** (out of scope for this engineering packet; do not re-argue).

### Action list

1. **P-5-1** — give `GameOverOverlay` dialog semantics + focus move-in / trap / restore (highest
   impact, self-contained). Fold in P-5-2 by routing it through a fixed `Dialog`.
2. **P-5-2** — add focus trap, initial focus and focus restore to `Dialog`.
3. **P-5-3** — extend `ANONYMOUS_PAGES`; add `/reset-password` + `/verify-email` scans with a dummy
   token; reconcile the header comment and `testing.md`.
4. **P-5-8** — bring `NVDA-PASS.md` current (Section H for the overlay; draw/rematch/presence/
   rating-delta steps) *before* the pass is run.
5. **P-5-4** — announce piece selection in `AccessibleBoard` (coordinate wording with P-8-1).
6. **P-5-5** — `aria-hidden` the visual clock digits (or add an `sr-only` mirror) and surface
   `c` / `m` in the visible panel text.
7. **P-5-9** — add a 320px reflow assertion to `accessibility.spec.ts`.
8. **P-5-6** — decide accessible under-promotion together with C-11-6.
9. **P-5-7** — low priority: gate `animationDuration` on the reduced-motion media query.

Owner:
10. Run the refreshed `NVDA-PASS.md` — steps 4, 17, 32 first. Needs Cloudflare Access lifted for a
    volunteer, or the owner runs it (ROADMAP #5).
11. 200% / 400% browser-zoom walk and a real 320px reflow check (P-5-9), including clock legibility
    at 400%.

No production code was changed in this packet. No standing positions reversed.

---

## P-6 — Mobile and browsers

Date: 2026-08-30
Packet: docs/audits/INDEX.md

### Verdict

The PWA plumbing is honest and careful, and the clock model holds up on a phone. The service
worker is network-only for `/game`, `/watch` and `/replay`, caches only `/learn` and the per-locale
`/offline` shell, and never touches the SignalR upgrade (`ui/public/sw.js:1-10,140-143`); the
cosmetic clock interpolates off `Date.now()` deltas rather than counting ticks
(`game/[id]/page.tsx:586-598`) and `crossedThreshold` reads a pair of readings
(`lib/a11y.ts:186-213`), so a throttled or suspended background tab that jumps a whole second
cannot drift or swallow an urgency announcement — this is the thing most sites get wrong and it is
right here. Pinch-zoom is not disabled, tap-to-move works alongside drag, and `InstallAppButton`
correctly stays hidden on iOS.

The gap is layout, not logic. All three board pages have no designed shell below `lg:`: the two
clocks stack at full `text-6xl` size above the board, so on a phone a player cannot see their own
clock and the board at the same time — in a variant whose win condition *is* the clock. This is
already documented as tripping the press kit. On top of that sit the usual iOS Safari rough edges:
`viewportFit: "cover"` with no safe-area padding, `100vh` everywhere instead of `100dvh`, and a
default 4-try reconnect with no `onclose` recovery on the one platform (iOS) that reliably kills the
socket on a screen lock.

### Findings

#### P-6-1 — Below `lg:`, both clocks stack at full size above the board on every board page

- Severity: High (trigger: playing, watching or reviewing a game on any viewport under 1024px — a
  large share of traffic for a chess site)
- Where: `ui/app/[locale]/game/[id]/page.tsx:685-702`;
  `ui/app/[locale]/watch/[id]/page.tsx:324-357`; `ui/app/[locale]/replay/[id]/page.tsx:311-328`
  (each `grid grid-cols-1 items-start gap-6 lg:grid-cols-[240px_1fr_320px]` with an `<aside>` that
  renders `<Clock side="Black">`, `<GapMeter>`, `<Clock side="White">` in fixed source order);
  `ui/components/Clock.tsx:31` (`px-6 py-8`), `:41` (`text-6xl font-black ... sm:text-7xl`)
- Impact: single column, in source order: Black clock (~150px tall), gap meter, White clock
  (~150px), *then* the board (`ui/components/Board.tsx:203`, `w-full max-w-[800px]` aspect-square,
  ~360px on a 390px-wide phone), which begins roughly 380px into `<main>` and ~470px below the top
  of the viewport once the 64px sticky nav and `py-6` are counted. On a 667–730px usable phone
  viewport only part of the board is visible with either clock, so the player scrolls between their
  clock and the position throughout a blitz game. `press-kit/README.md:11-13` records this exact
  layout ("both clocks above the board, and that layout is not designed") as the reason the only
  phone-framed press clip was pulled.
- Fix: give the board pages a real narrow layout — board first; opponent clock in a compact bar
  directly above the board and the local player's clock directly below it, height coming from
  padding and roughly `text-3xl` rather than `text-6xl`; move list, controls and captured-material
  below. C-11-1's "local player's clock on the bottom" ordering falls out of the same rework. One
  shared layout for game / watch / replay (watch and replay have no local side, so opponent-top /
  White-bottom stays).

#### P-6-2 — The board has no viewport-height cap, so landscape and short viewports overflow both axes

- Severity: Medium (trigger: rotating a phone to landscape during a game, or any short window)
- Where: `ui/components/Board.tsx:202-203` (`<div className="mx-auto w-full max-w-[800px]">`, no
  `max-height`); `react-chessboard` sizes itself to the container's width
- Impact: in landscape on a phone (e.g. 844×390) or any window shorter than it is wide the board
  renders ~800px wide and ~800px tall, far past the visible viewport, and has to be scrolled
  vertically and horizontally to see all 64 squares. Both reference sites size the board to
  `min(available width, available height)`.
- Fix: cap the wrapper against the viewport height, e.g.
  `max-w-[min(800px,calc(100dvh-14rem))]`, or measure the available square in JS and pass
  `boardWidth`. Pairs naturally with P-6-1.

#### P-6-3 — `viewportFit: "cover"` is set but nothing pads for `env(safe-area-inset-*)`

- Severity: Medium (trigger: the installed iOS PWA, or Safari in landscape, on a notched iPhone)
- Where: `ui/app/[locale]/layout.tsx:43-46` (`viewport: { themeColor, viewportFit: "cover" }`),
  `:36-40` (`appleWebApp.statusBarStyle: "black-translucent"`), `ui/app/manifest.ts:11`
  (`display: "standalone"`); a repo-wide grep for `safe-area` / `env(safe-area` in `ui/` returns
  nothing
- Impact: `viewportFit: "cover"` plus `black-translucent` tells iOS to draw web content edge to
  edge, under the status bar and the home indicator. With no `env(safe-area-inset-*)` padding
  anywhere, the sticky nav's top row sits under the clock/notch and the bottom of the last element
  on a board page sits under the home indicator in the installed PWA. This is precisely the
  combination that produces the "content tucked under the notch" look.
- Fix: either remove `viewportFit: "cover"` (and `black-translucent`), which is the smallest change
  and costs nothing here, or add `padding-top: env(safe-area-inset-top)` to the sticky `<nav>` and
  `padding-bottom: env(safe-area-inset-bottom)` to the board-page `<main>` and the `Footer`.

#### P-6-4 — `min-h-screen` / `100vh` throughout; iOS Safari clips the bottom

- Severity: Low (trigger: iOS Safari with the toolbar shown — the default state)
- Where: 42 occurrences of `min-h-screen` / `100vh` across `ui/` page shells; `ui/components/Dialog.tsx:61`
  (`style={{ maxHeight: "calc(100vh - 2rem)", overflowY: "auto" }}`)
- Impact: on iOS Safari `100vh` includes the area behind the retractable toolbar, so a
  `min-h-screen flex items-center justify-center` `page-card` screen (login, `/offline`,
  `not-found`) can center its content partly behind the toolbar, and the resign-confirm `Dialog`
  can compute a height taller than the visible viewport and push its Confirm/Cancel row off-screen.
  Nothing is broken, but it reads as a layout that does not quite fit the phone.
- Fix: switch the page-shell min-heights and the `Dialog` `maxHeight` to `100dvh` (dynamic
  viewport), keeping a `100vh` line immediately before as the fallback for older engines.

#### P-6-5 — iOS kills the socket on backgrounding; an exhausted reconnect has no recovery UI

- Severity: Medium (trigger: locking the phone or switching apps mid-game on iOS, then returning
  when the network is still settling)
- Where: `ui/lib/realtime.ts:46-53` (`.withAutomaticReconnect()` with the default 4-attempt
  schedule `[0, 2000, 10000, 30000]` ms); `ui/app/[locale]/game/[id]/page.tsx:383-386` wires
  `conn.onreconnected` but there is no `conn.onclose` handler; `watch/[id]/page.tsx:184-188` the same
- Impact: iOS Safari closes WebSocket connections on tab-switch / screen-lock. On return SignalR
  works through four retries over ~42s; if the radio has not re-associated yet (common in the first
  seconds after an unlock) all four fail, `onreconnected` never fires, and the connection is
  permanently dead — board frozen, clocks frozen, no banner, no reconnect button. A player who
  locks their phone for a minute mid-game is the ordinary case that lands here.
- Fix: add a `conn.onclose` that surfaces a "connection lost — reload to resume" state (P-4 owns
  the visual treatment), and give `withAutomaticReconnect` a longer / open-ended retry policy
  (e.g. `[0, 2000, 5000, 10000, 30000, 30000, 60000]`). A `visibilitychange` listener that pokes
  the connection when the tab returns to the foreground would close most of the window before the
  retry schedule is even needed.

#### P-6-6 — In a bot game, backgrounding the tab on the bot's turn can flag the bot

- Severity: Low (trigger: backgrounding the tab while it is the bot's move; bot games only)
- Where: `ui/app/[locale]/game/[id]/page.tsx:479-529` (the bot's move is computed client-side —
  `getEngineMove` + `sleep(thinkMs)` on a `setTimeout`), `ui/lib/bot.ts` (Stockfish Web Worker)
- Impact: bot games are still server games with a server-run clock (`game/[id]/page.tsx:351`
  `JoinGame`). If the player backgrounds the tab while it is the bot's turn, iOS suspends the
  `setTimeout` and the Stockfish worker, so the bot never submits, while the server keeps charging
  the bot's clock. On return the bot "thought" for the whole gap and may immediately flag. Bot
  games are solo and unrated so the blast radius is small, but a bot self-flagging on move 6 is a
  confusing result.
- Fix: on `visibilitychange` back to visible with a bot move still pending, re-derive `thinkMs`
  from the bot's current remaining clock before submitting; or accept it as a known limitation of
  client-driven bot play and note it where bot mode is documented.

#### P-6-7 — In-game draw-prompt touch targets are below the 44px floor

- Severity: Low (trigger: responding to a draw offer, or resigning, on a touchscreen)
- Where: `ui/app/[locale]/game/[id]/page.tsx:670-682` (Accept `btn-primary px-3 py-1.5 text-sm`,
  Decline `btn-secondary px-3 py-1.5 text-sm` — about 30px tall); `ui/components/GameControls.tsx:37,46`
  (`py-2.5 text-sm`, about 40px)
- Impact: the draw Accept / Decline buttons, and marginally Offer draw / Resign, are under the
  44×44 CSS-px touch target that WCAG 2.5.5 and the iOS HIG ask for, on the exact controls where a
  mis-tap (an accidental resign, or declining a draw you meant to accept) costs the game. Cross-ref
  P-5 for the WCAG framing.
- Fix: on the game page render the draw prompt with the Default (`px-5 py-2`) or Split-row
  (`flex-1 py-2.5`) button size from `.claude/rules/styling.md` §6, and ensure a 44px minimum
  height on every in-game control.

### Already covered

- **Board orientation and clock order never flip for the Black player** — C-11-1
  (`docs/audits/C.md`). The ordering overlaps P-6-1, but C-11-1 treats it as desktop-sidebar
  presentation; P-6-1 is the narrow-viewport layout being undesigned, and the two fixes are the
  same rework.
- **Reconnect diverges the move list / clock graph** — C-11-2 and deep-audit FE-4-1. P-6-5 is the
  iOS-specific trigger and the missing `onclose` recovery path, not the divergence itself.
- **`page-board` has no h1 form so all four board screens guessed; Clock `font-mono` is blessed;
  board glow shadows** — the styling spec (`.claude/rules/styling.md`) §3/§4 and its cross-cutting section. Not
  mobile behaviour.
- **Own moves have no optimistic feedback on a laggy link; clock never shows tenths** — C-11-7,
  C-11-8. Felt more on a phone's connection but already filed.

### Action list

Ordered.

1. **P-6-1** — design the below-`lg:` board-page layout (board first, compact clock bars, local
   clock on the bottom), shared across `game` / `watch` / `replay`. Folds in C-11-1's clock
   ordering. Do before Todo #16.
2. **P-6-2** — cap the board wrapper against `100dvh` (or measure the square). Pairs with P-6-1.
3. **P-6-3** — drop `viewportFit: "cover"` / `black-translucent`, or add `env(safe-area-inset-*)`
   padding to the nav, board `<main>` and footer.
4. **P-6-5** — add a `conn.onclose` "connection lost" state (visual owned by P-4), lengthen the
   reconnect schedule, and force a reconnect check on `visibilitychange`.
5. **P-6-4** — sweep `min-h-screen` / `100vh` page shells and the `Dialog` `maxHeight` to `100dvh`
   with a `100vh` fallback.
6. **P-6-7** — move the draw-prompt and in-game controls onto a 44px+ touch target size
   (coordinate with P-5).
7. **P-6-6** — owner's call: re-derive bot think time on tab-return, or document the quirk.
8. **Owner / M-2** — re-shoot the phone-framed press-kit clip once P-6-1 lands
   (`press-kit/README.md:11-13`).

No standing-position reversals.

---

## P-7 — Frontend performance

Date: 2026-08-30
Packet: docs/audits/INDEX.md, table 6

### Verdict

The browser side of KiriChess is in good shape for a volunteer's phone, and the parts that
usually go wrong here were done deliberately. The heavy board (`react-chessboard` + `react-dnd`)
is `dynamic()`-imported with `ssr: false` on the only three routes that carry it, behind an
`aspect-square` placeholder that reserves its box so nothing shifts when it mounts. `chess.js`
is confined to `/learn`; the client-side clock-economy mirror (`ui/lib/costPreview.ts`) is pure
arithmetic over `ui/lib/fen.ts` with no engine dependency. Stockfish is an asm.js single-thread
build that runs in a Web Worker, so it never blocks the main thread and — because there is no
`.wasm`, no threads and no `SharedArrayBuffer` — the absence of COOP/COEP headers is correct,
not a gap. Montserrat is self-hosted through `next/font` with generated fallback metrics, so
there is no font-driven CLS or FOIT. The lobby is a server component. The service worker is
honest about what it can cache.

What is left is a handful of avoidable weight, none of it Critical or High: the decorative 404
and 403 boards drag the full drag-and-drop board library into those route bundles when a
zero-dependency renderer already exists; the 1.6 MB Stockfish worker script sits outside every
caching layer so each bot session refetches it; and next-intl serialises the whole active-locale
catalogue into every page. The two biggest real-world levers — the lobby's duplicate hub sockets
and the un-terminated Stockfish worker — are already filed (arch-speed AS-4, deep-audit FE-1-1).

### Findings

#### P-7-1 — The 404 and 403 decorative boards pull `react-chessboard` + `react-dnd` into those route bundles
- Severity: Low (trigger: any visitor hitting `/not-found` or a 403 — common pre-launch behind Cloudflare Access, and for crawlers / stale links after)
- Where: `ui/components/NotFoundScene.tsx:4` (`import { Chessboard } from "react-chessboard"`),
  `ui/components/ForbiddenScene.tsx:4` (same), rendered by `ui/app/[locale]/not-found.tsx:26` and
  `ui/components/ForbiddenScreen.tsx:31`
- Impact: `NotFoundScene` / `ForbiddenScene` render a *static, non-interactive* board
  (`arePiecesDraggable={false}`, `animationDuration={0}`) but import `react-chessboard` with a
  plain static `import`, which forces `react-chessboard` and its `react-dnd` /
  `react-dnd-touch-backend` transitive tree (~55–65 KB gzip) into the synchronous bundle for the
  `not-found` and forbidden routes. Every other board on the site that does not need dragging
  (`ui/components/MiniBoard.tsx`) is 64 `<div>`s and zero dependencies, and its header comment
  says exactly why. The error pages are `page-card`, the lightweight tier, and a 404 is one of
  the cheapest pages the site should serve.
- Fix: render these two decorative boards with `MiniBoard` (or a local 8×8 grid the way
  `NotFoundScene` already builds its 10×10 wrapper), keeping the overlaid animated piece SVG.
  `react-chessboard` then loads only via the `dynamic()` import on the three real board routes.

#### P-7-2 — The 1.6 MB Stockfish worker script is outside every caching layer
- Severity: Low (trigger: a second bot game, a reload, or a flaky mobile connection)
- Where: `ui/lib/bot.ts:12` (`new Worker("/stockfish.js")`), `ui/public/sw.js:70-79`
  (`isImmutableAsset` allow-list), `ui/next.config.ts:133-152` (`headers()` sets no
  `Cache-Control`)
- Impact: `public/stockfish.js` is 1.58 MB uncompressed (~380–420 KB gzip). It is served from
  the site root, not `/_next/static/`, so `sw.js` does not match it in `isImmutableAsset()` and
  its `fetch` handler falls through to network-only; `next.config.ts` adds only security headers,
  so the file gets Next's default short-lived cache policy for `public/`. The script is
  content-stable (one pinned asm.js build, GPL, never rebuilt by our pipeline), so today every
  bot game after the first pays a full revalidation or refetch — on a low-end phone on mobile
  data that is the single largest transfer the app ever makes.
- Fix: add `/stockfish.js` to `isImmutableAsset()` in `sw.js` so it is served cache-first with
  background revalidation (same treatment as `/logo.png`), and/or emit a long-lived
  `Cache-Control: public, max-age=31536000, immutable` for `/stockfish.js` from `headers()`. If
  the file is ever swapped, bump its name (`stockfish.<version>.js`) in the same commit.

#### P-7-3 — next-intl serialises the entire active-locale catalogue into every route
- Severity: Low (trigger: every page load and client navigation, on every route)
- Where: `ui/app/[locale]/layout.tsx:81` (`<NextIntlClientProvider>` with no `messages` prop),
  `ui/i18n/request.ts:44-47` (returns the whole `catalogues[locale]`), `ui/messages/en.json` is
  72 KB (fr 84 KB, de 82 KB) uncompressed
- Impact: with no `messages` filter on the provider, next-intl passes the full locale catalogue
  to the client, baked into the prerendered RSC payload of every statically rendered route. A
  content page such as `/about` or `/rules` that uses one namespace still ships ~15–20 KB gzip of
  strings it never reads, plus the JSON parse, on every navigation. The catalogue grows every
  week (`.claude/rules/frontend.md` notes `en.json` "moves most weeks"), so this cost only rises.
- Fix: pass a namespace-filtered `messages` to `NextIntlClientProvider` (next-intl's documented
  `pick`-style pattern), or move the provider down into the client subtrees that actually call
  `useTranslations` and give each the namespaces it needs. Server components already use
  `getTranslations` and are unaffected.

#### P-7-4 — `@sentry/nextjs` client SDK is bundled on every page while no Sentry project exists
- Severity: Low (informational — this is a deliberate 6d decision, fix is optional)
- Where: `ui/instrumentation-client.ts:1` (`import * as Sentry from "@sentry/nextjs"`,
  unconditional), `ui/next.config.ts:158` (`withSentryConfig` wraps the build)
- Impact: `Sentry.init` is correctly gated on `NEXT_PUBLIC_SENTRY_DSN`, but the static
  `import * as Sentry` and the webpack instrumentation `withSentryConfig` injects still add
  roughly 30–40 KB gzip to the shared client runtime for a feature that does nothing today (every
  Sentry config comment in the repo says "no Sentry project exists yet"). Every visitor carries
  it.
- Fix: acceptable to leave as-is if a DSN is genuinely imminent. If not, defer it — load the SDK
  lazily on the first unhandled error, or drop `withSentryConfig` until a real org/DSN is wired —
  and note the toggle in `docs/ops/DEPLOYMENT.md` so turning it back on is one step.

#### P-7-5 — No bundle-size budget or Core Web Vitals check anywhere in CI
- Severity: Low (trigger: a future PR that adds a heavy import or regresses a board-route chunk)
- Where: `ui/next.config.ts` (no `@next/bundle-analyzer`), `ui/package.json:6-17` (no size
  script), `e2e/tests/` (has `accessibility.spec.ts` and `seo.spec.ts`, no Lighthouse / web-vitals)
- Impact: for a one-maintainer project, a stray `import` that pulls a large dependency into the
  shared chunk, or a regression in the `/game` route's JS, lands with no signal. `.claude/rules/testing.md`
  deliberately keeps the live board out of the axe scan — that is an accessibility decision and
  does not cover JS weight.
- Fix: the smallest useful thing is a `next build` output assertion (fail if First Load JS for a
  key route crosses a threshold) or a single Lighthouse-CI run with a budget on the lobby and one
  board route. Not launch-blocking; a guardrail so the good current state does not quietly erode.

### Already covered

- **Lobby opens two `/hubs/game` WebSockets per visitor (three with a challenge link) plus presence; 5 s seek poll with no `document.hidden` gate** — arch-speed `AS-4` / `AS-11`, `I-1-1` / `I-1-7`. This is the largest real client-cost item on the lobby; not re-reported.
- **Stockfish worker is a leaked module-level singleton, never `terminate()`d after a bot game, one global resolver a second search overwrites** — deep-audit `FE-1-1`, `IMPROVEMENT_PLAN` batch 6 item 1, arch-speed §4.3. The phone angle to add when that is fixed: the asm.js heap plus Stockfish's hash table stays resident for the tab's whole life after the game ends. P-6 (`docs/audits/P.md:117-120`) owns the mobile-suspend interaction with the bot's `setTimeout`.
- **Turnstile script (`challenges.cloudflare.com/.../api.js`) loads on every route, not just `/login`** — deep-audit `FE-3` finding 1, `IMPROVEMENT_PLAN` batch “Turnstile loads on every route”. Filed there from the `AUTH.md` §1.7 supply-chain angle; the performance cost (a third-party script + its own network work on the lobby and the live game page) is the same fix — gate the `<Script>` to the auth routes.
- **No route-level `loading.tsx` anywhere under `ui/app/[locale]`** — arch-speed `AS-10` / `I-1-7`.
- **Reconnect wipes the move list / clock-economy graph; own moves have no optimistic feedback; clock never shows sub-second** — `C-11-2`, `C-11-7`, `C-11-8` (INP / perceived-latency of move handling is owned there).
- **`react-chessboard` raw `rgba()` / `borderRadius` / off-palette ring in `Board.tsx`** — `STYLING-CONFORMANCE.md`, packet **P-10** (visual, not performance).
- **Mobile layout, iOS Safari WebSockets, background-tab clock, PWA/offline shell** — packet **P-6** (`docs/audits/P.md`). This packet does not re-cover the service worker's route strategy, which P-6 confirms is correct.
- **Server-side move path, flag-fall sweep, Neon write RTT, SignalR JSON vs MessagePack** — packet **I-1** / arch-speed (the server, not the browser).

### Action list

Ordered. None launch-blocking, none reversing a standing position.

1. **P-7-1** — swap `NotFoundScene` / `ForbiddenScene` to `MiniBoard` (or a local grid) so
   `react-chessboard` + `react-dnd` leave the 404/403 bundles. Self-contained; add a render test.
2. **P-7-2** — add `/stockfish.js` to `sw.js`'s `isImmutableAsset()` and/or emit an immutable
   `Cache-Control` for it from `next.config.ts` `headers()`. Bump the filename on any future swap.
3. **P-7-3** — filter `messages` passed to `NextIntlClientProvider` (or scope the provider to
   client subtrees). Measure the First Load JS delta on `/about` before and after.
4. **P-7-5** — add a `next build` size assertion or a one-route Lighthouse-CI budget so 1–3 do
   not silently regress later.
5. **P-7-4** — owner decision: is a Sentry DSN imminent? If not, defer the client SDK and record
   the toggle in `DEPLOYMENT.md`.
6. **Rides other packets** — Turnstile route-gating (deep-audit FE-3), lobby hub-socket collapse
   (arch-speed AS-4), Stockfish worker teardown (deep-audit FE-1-1). Bundle P-7-2 with the
   FE-1-1 worker-lifecycle fix since both are in `ui/lib/bot.ts` / the worker path.

---

## P-8 — i18n
Date: 2026-08-30
Packet: docs/audits/INDEX.md

### Verdict

The next-intl retrofit (Phase 9f.4) is structurally sound and unusually well-guarded. `ui/i18n/routing.ts`,
`request.ts` and `proxy.ts` are correct; the six catalogues (`en`, `nl`, `es`, `fr`, `de`, `pt-BR`)
carry identical key sets (1041 leaves, 62 namespaces each) and identical ICU placeholders, and
`ui/lib/locales.test.ts` enforces that plus `sw.js` `LOCALE_PREFIXES`, the switcher labels, and ICU
well-formedness — all green locally. Locale-aware navigation is clean: every `Link`/`useRouter`/
`usePathname`/`redirect` inside `app/[locale]` comes from `@/i18n/navigation`, only the three
non-locale-aware primitives (`useSearchParams`, `useParams`, `notFound`) come from `next/*`, and the
one `window.location.pathname` reference (`RequireAuth.tsx`) is explicitly guarded against the
double-prefix bug. `sitemap.ts` derives its hreflang alternates from `routing.locales` dynamically.

The gaps are hardcoded English leftovers, not catalogue rot. Three player-facing components never
went through the migration at all: the entire screen-reader board (`AccessibleBoard.tsx`), the
two-factor login prompt (`TwoFactorPrompt.tsx`), and the route-level error boundary
(`[locale]/error.tsx`). `lib/a11y.ts` is a documented deliberate exception, but the condition the
deep audit attached to it ("add the namespace when the second locale is actually being built") has
now fired — nl/es/fr/de/pt-BR are all live. There is no lint rule stopping a fourth hardcoded string
from landing; `locales.test.ts` only compares catalogue to catalogue, never code to catalogue.
Meaning remains unverified for four locales, which is a standing position, not a finding.

### Findings

#### P-8-1 — The entire screen-reader chess board is hardcoded English
- Severity: Medium (trigger: a screen-reader or keyboard-only user playing in nl/es/fr/de/pt-BR — the
  live board is the one surface `docs/plans/a11y/NVDA-PASS.md` already treats as needing a real, non-axe
  accessibility pass)
- Where: `ui/components/AccessibleBoard.tsx` — `KEY_SUMMARY` (`:29-33`), `<label>Move</label>` (`:78`),
  `placeholder="e2e4"` (`:85`), `<button>Play</button>` (`:89`), `"\"…\" isn't a move in the form e2e4."`
  (`:61`), `"No legal move …"` (`:68`), section `aria-label` `"Accessible chess board. You are playing …"`
  (`:247-251`), the `<p>` help text (`:257-261`), grid `aria-label="Chess board, arrow keys to navigate,
  Enter to select"` (`:265`), `"No legal move from … to …."` (`:164`), `"Selection cleared."` (`:225`).
  Rendered on `/game/[id]` (`:720`), `/replay/[id]` (`:343`) and `/watch/[id]` (`:371`).
- Impact: A non-English screen-reader user gets a fully translated visual UI and a fully English board:
  every navigation instruction, every move-rejection reason, and both interactive control labels
  ("Move", "Play"). This is a component that can call `useTranslations` directly, unlike `lib/a11y.ts`.
- Fix: Add an `a11yBoard` namespace to `messages/*.json` and route `AccessibleBoard.tsx`'s literals
  through `useTranslations`. The pure describe-helpers it imports from `lib/a11y.ts` are a separate
  concern (P-8-4); the component's own chrome is not.

#### P-8-2 — The two-factor login prompt is hardcoded English
- Severity: Medium (trigger: a non-English user with 2FA enabled, or a linked-Google account that
  still hits a TOTP challenge via the OAuth callback)
- Where: `ui/components/TwoFactorPrompt.tsx` — `"Two-factor authentication"` (`:67`), the 6-digit-code
  instruction (`:70-72`), `"Code"` label (`:76`), `"Back"` (`:103`), `"Verifying…" / "Verify"` (`:110`),
  `"That code isn't right. Check your authenticator app and try again."` (`:47-49`), `"Something went
  wrong."` (`:55`). Used by `[locale]/login/page.tsx` and `[locale]/auth/callback/page.tsx`, both of
  which are otherwise fully translated (`login/page.tsx` uses `useTranslations`).
- Impact: A non-English user is walked through the whole login flow in their language and then hits an
  English wall at the most security-sensitive step, including the wrong-code error they are most
  likely to see. Inconsistent with every sibling auth screen.
- Fix: Add a `twoFactorPrompt` namespace and convert the component to `useTranslations`; the 423-lockout
  path already correctly defers to the server message via `onLockout`.

#### P-8-3 — `[locale]/error.tsx` is hardcoded English while `[locale]/not-found.tsx` is translated
- Severity: Low (trigger: a non-English visitor who hits a route-level render error)
- Where: `ui/app/[locale]/error.tsx:37-55` — `"Something went wrong"`, `"This page hit an unexpected
  error. Trying again usually fixes it."`, `"Error id:"`, `"Try again"`, `"Back to home"`. Contrast
  `ui/app/[locale]/not-found.tsx`, which pulls every string from `getTranslations("notFound")`.
- Impact: The two error-shaped pages diverge; a non-English 500 shows English, a non-English 404 does
  not. `error.tsx` is a `"use client"` component under `[locale]`, so `NextIntlClientProvider` from the
  layout is in scope and `useTranslations` works (unlike `app/global-error.tsx`, which is correctly
  left untranslated).
- Fix: Add an `error` namespace and use `useTranslations`. Cross-reference: flagged pre-migration as
  deep/FE-3 nit 2; it was not addressed when the file moved under `[locale]`.

#### P-8-4 — `lib/a11y.ts` announcer strings: the deep-audit trigger has now fired
- Severity: Low (trigger already met)
- Where: `ui/lib/a11y.ts:17-24,30-181` — `PIECE_NAMES`, `secondsPhrase`, `describeSquare`,
  `describeTarget`, `describeMove`, `describeClocks`, `crossedThreshold` phrasing, all hardcoded
  English. Consumed by `game/[id]/page.tsx:265,731` (`describeMove`, `describeClocks`) and by
  `AccessibleBoard.tsx` per-cell `aria-label`s.
- Impact: Every spoken move description and clock readout on a live game stays English in all five
  non-English locales. `frontend.md:63` currently lists `lib/a11y.ts` as a deliberate catalogue
  exception, and deep/FE-1-2 rated this Low with the explicit condition "add an a11y namespace when the
  second locale is actually being built, not before." That condition is now satisfied: nl shipped
  2026-08-24, es/fr/de/pt-BR the same day.
- Fix: Give the exported functions a `Translator` parameter the way `ui/lib/tutorial.ts:14-18,34-37`
  already does for the same non-component constraint, add the namespace, and update the `frontend.md`
  exception list (and its `.cursor/rules` mirror) in the same commit to say "until the a11y pass runs"
  rather than unconditionally. This is a real decision for the owner, not an automatic fix — English-only
  screen-reader output is a defensible position for a one-person hobby service; the finding is that the
  code and the documented rationale no longer match.

#### P-8-5 — Nothing stops a new hardcoded string from landing
- Severity: Low (trigger: any future UI change; P-8-1/2/3 are evidence it already happened or was
  never caught)
- Where: `ui/eslint.config.mjs` (no `no-literal-string` / formatjs rule), `ui/lib/locales.test.ts`
  (compares catalogue-to-catalogue only — key sets, ICU tokens, plural well-formedness — never
  code-to-catalogue).
- Impact: The i18n migration is enforced only for drift *between* catalogues. A component that skips
  `useTranslations` entirely, like the three above, passes every gate. `en.json` moving "most weeks"
  (`frontend.md:57`) means new UI lands often.
- Fix: Add `eslint-plugin-formatjs`'s `no-literal-string` (or the lighter `react/jsx-no-literals`)
  scoped to `app/[locale]/**` and `components/**`, with an inline-disable allowlist for the documented
  exceptions. This is the smallest thing that keeps the catalogue honest without adding a locale.

### Already covered

- **`lib/a11y.ts` never went through next-intl** — deep/FE-1-2 (Low). P-8-4 adds that its stated
  precondition has now fired and `AccessibleBoard.tsx` (P-8-1) is a separate, un-flagged file.
- **`error.tsx` hardcoded while `not-found.tsx` is translated** — deep/FE-3 nit 2. P-8-3 notes it
  survived the `[locale]` migration unchanged.
- **`/learn` copy threads priced quantities as prose instead of ICU placeholders** — C-9 (the
  `rulesCopy.test.ts` gap covers `rules.*` only, not `learn.*`). Not re-reported here; it is a
  copy/ICU-shape issue that C-9 owns.
- **Per-route rendering, dummy-data flags, missing route metadata under `[locale]`** — P-1 (route
  table). P-8 does not re-walk routes.
- **Board never flips for Black; clock stack unconditional** — C-11-1. `AccessibleBoard` takes `you`
  and uses it only for the `aria-label`, also noted there; P-8-1 is about that label being English,
  not about orientation.
- **Meaning of es/fr/de/pt-BR unverified by a native reader** — standing position, `frontend.md:40-47`
  and `routing.ts:22-29`. Out of scope to reverse. The `rules` namespace (223 keys) is where a
  mistranslation reads as a broken game; if any correction budget exists, it goes there first.

### Action list

1. P-8-5: add a scoped `no-literal-string` lint rule with an exception allowlist. Do this
   first; it makes the next three verifiable.
2. P-8-2: translate `TwoFactorPrompt.tsx` (`twoFactorPrompt` namespace). Smallest, purely
   player-facing, no a11y judgement call.
3. P-8-3: translate `[locale]/error.tsx` (`error` namespace).
4. **Owner decision** — P-8-1 and P-8-4: decide whether screen-reader output is localised
   or stays English-by-policy. If localised: `a11yBoard` namespace for `AccessibleBoard.tsx`, plus a
   `Translator` param for `lib/a11y.ts` mirroring `lib/tutorial.ts`. If English-by-policy: update the
   `frontend.md` exception wording (and `.cursor/rules` mirror) so code and rationale agree, and add
   `AccessibleBoard.tsx` to that exception list explicitly.
5. **No change** — routing, request config, proxy, catalogue parity, navigation imports, `sw.js`,
   `sitemap.ts`, switcher labels. All correct.

---

## P-9 — Copy and claims
Date: 2026-08-30
Packet: docs/audits/INDEX.md

### Verdict

The origin story is honest and modestly told. `/about` claims only that the variant is "original"
and "invented by Michiel Gekiere" — never a first, never a patent, never an exclusive right over
"time as a resource" — which is exactly the posture `docs/game/CLAIMS.md` argues for (mechanics are
not copyrightable; recognition is a documentation problem). The dedication ("named after my dad,
whose nickname is Kiri") is presented as a personal touch, not a factual claim. The donate page is
not a paywall: it carries an explicit "Are some features reserved for Patrons? No, KiriChess is
entirely free" and the tier buttons read "Donate €5", not "Unlock". Logo and wordmark licensing is
stated correctly on `/about` and `/terms` (name, logo, wordmark not licensed under Apache-2.0 or
CC BY 4.0).

Where the copy is not honest is a single word, repeated. "Non-profit" is asserted on the footer of
every page, on `/about`, and twice on `/donate`, in five locale files — while `/terms` §7 spends a
paragraph saying the project "is not a company and not a registered non-profit, and nothing here
should be read as claiming either". The site's marketing contradicts the site's own binding terms.
`L-7-5` already filed the claim against the privacy policy; this packet adds that it also
contradicts TERMS, that it reached `/about` and the translations after that finding was written, and
that it is still shipped. The rest is smaller: two literal `[TODO: …]` strings rendering on the
public `/about` page, an invention date with nothing to cite it to, and a typo `L-7-6` already
pointed here.

### Findings

#### P-9-1 — "Non-profit" is claimed site-wide and `/terms` §7 explicitly disclaims it
- Severity: Medium (trigger: DH.3, the site becoming publicly reachable; live now for anyone past Access)
- Where: `ui/messages/en.json:21` (footer tagline, "Non-Profit - Open-Source"),
  `ui/messages/en.json:1064` (`about.projectPara`, "It is non-profit, open-source, and free to
  play."), `ui/messages/en.json:1118` and `:1120` (`donate.metaDescription`, `donate.intro`), all
  repeated in `ui/messages/{nl,fr,de,es,pt-BR}.json` at the same keys (e.g. `nl.json:21,1064,1118,1120`).
  Against `ui/app/[locale]/terms/page.tsx:174-179` / `docs/legal/TERMS.md:68-70`: "It is not a
  company and not a registered non-profit, and nothing here should be read as claiming either:
  'non-profit' is a legal form, and this project does not have one."
- Impact: The operator is a natural person with no VZW/ASBL. A reader who sees "non-profit" beside a
  donate button, then reads the Terms they accepted at signup, finds the two documents flatly
  disagreeing on the project's legal form — on the page that collects money. This is the sharper
  version of `L-7-5` (which cited only the privacy policy): here the contradiction is with the
  contract the user is bound by, the kind of internal inconsistency `LegalDocumentDriftTests` exists
  to prevent but cannot see because one side is a marketing string. `L-7-5`'s fix has not been
  applied nine days on, and in the interim the claim was added to `about.projectPara` and copied into
  five translations, so the surface has grown, not shrunk.
- Fix: `L-7-5` already prescribes it — replace "non-profit" with something true and checkable
  ("independent, free, run at cost by one person") in the tagline, `about.projectPara`, and the two
  donate strings, and re-run `npm run gen:locale -- <locale> --fill` (or hand-edit) so the five
  locale files follow. The "open-source" half of the same tagline shares `L-1-7`'s defect (the
  source is not in fact published while the repo is private); fix both together.

#### P-9-2 — Literal "[TODO: …]" placeholder copy renders on the public `/about` page
- Severity: Medium (it ships to every visitor of the flagship attribution page)
- Where: `ui/messages/en.json:1076` (`about.a11yReplyPlaceholder`, "[TODO: reply time, e.g. within a
  few days.]") rendered unconditionally at `ui/app/[locale]/about/page.tsx:213-215`; and
  `ui/messages/en.json:1081` (`about.cataloguePlaceholder`, "[TODO: paste the chessvariants.com URL
  here once the submission is accepted.]") rendered at `about/page.tsx:234-236` whenever
  `CHESSVARIANTS_URL` is `null`, which it currently is (`about/page.tsx:24`).
- Impact: The Accessibility section reads "…I am one person and I will fix what is reported. [TODO:
  reply time, e.g. within a few days.]" and the How-to-cite section reads "A catalogue entry on The
  Chess Variant Pages is planned. [TODO: paste the chessvariants.com URL here…]". `/about` is the
  one page whose entire job is to look like a credible, deliberate origin record for a cataloguer or
  Wikipedia editor; visible unfinished editor's-notes undercut exactly that. `L-8-1` anticipated the
  accessibility-statement response-time line as "the owner's to set" — it was stubbed and the stub
  shipped. The catalogue placeholder is half-deliberate (the page comment at `about/page.tsx:22-24`
  says it "renders a visible TODO so the URL is not invented") but a user-facing `[TODO: …]` is still
  the wrong way to express "not yet available".
- Fix: For the a11y line, the owner sets a real response-time sentence (or the sentence is dropped and
  the paragraph ends at "I will fix what is reported"). For the catalogue line, replace the bracketed
  note with plain prose that stands on its own until the URL exists ("A catalogue entry on The Chess
  Variant Pages is planned; this page is the dated record until then."), keeping the real
  `cataloguePara`/link branch for when `CHESSVARIANTS_URL` is set.

#### P-9-3 — `/about` states "Invented February 2026" with no artifact that could ever cite it
- Severity: Low (trigger: a competing time-economy variant surfacing with a 2026 date, or a
  cataloguer asking for the basis)
- Where: `ui/messages/en.json:1048-1049` (`about.dateLabel` / `dateValue`, "Invented" / "February
  2026"), `ui/messages/en.json:1078` (`about.citePara`, "…invented February 2026. This page is the
  origin record."), matching `NOTICE:61-63`. The earliest dated artifact in the project is the first
  commit, `2026-07-21` (`git log --reverse`), and `about.repoPara` says so on the same page.
- Impact: `CLAIMS.md`'s thesis is "making the origin unambiguous and citable". The February date is
  the one origin fact the project asserts that has nothing behind it, not even in principle — no
  dated note, no email, no draft — and it sits five months before the first artifact. If the variant
  spreads and the date is ever contested, an unbackable earlier claim is the weak point a disputant
  attacks, and it makes the citable parts look weaker by association. `L-9-1` covers the broader
  "every timestamp is private or unsent" problem; this is specifically that the headline date is not
  a timestamp at all.
- Fix: Either soften to what is provable ("First built July 2026" / "Conceived early 2026, first
  code July 2026"), or have the owner create one dated private artifact now (a signed note, a dated
  gist, an email to self) so the February claim has a floor. Low effort, and it is the owner's call
  which.

#### P-9-4 — Typo "forbetter" in the donate copy, already flagged and still shipped
- Severity: Low
- Where: `ui/messages/en.json:1122` (`donate.whereMoneyGoesA`, "…expand the infrastructure forbetter
  and faster performance").
- Impact: It is in the paragraph a first-time donor reads to decide whether to give. `L-7-6`
  recorded this exact string on 2026-08-21 and explicitly handed it to P-9; the "Patreons" half of
  that finding was fixed, this half was not.
- Fix: "infrastructure for better and faster performance". One-character-plus-space edit; check the
  five locale files did not inherit it (`nl.json:1122` etc.).

#### P-9-5 — `/developers` licence line omits the name/logo/wordmark reservation that `/about` and `/terms` carry
- Severity: Low
- Where: `ui/messages/en.json:1113` (`developers.sourcePara`, "The server, engine and this site are
  Apache-2.0. Issues and pull requests go to …"), against `about.sourceBody`
  (`en.json:1086`, "…the name and logo are not licensed") and `TERMS.md:97-98`.
- Impact: `/developers` is the page most likely to be read by someone who will fork or redistribute,
  which is the exact audience the mark reservation is for, and it is the one public licence statement
  that does not mention it. Not false, just the weakest of the three, and `NOTICE` still governs.
- Fix: Append one clause: "The name, wordmark and logo are not — see NOTICE." Keep it shorter than
  the `/about` version; the link to the repo already carries the detail.

### Already covered

- "Non-profit" as a claim with no legal entity behind it: **L-7-5** (`docs/audits/L.md:106-119`).
  P-9-1 extends it with the TERMS §7 contradiction, the `/about` instance, and the locale
  propagation; it does not re-argue the base point.
- "Patreons" typo and the parked-Patreon naming risk on `/donate`: **L-7-6** (fixed half).
- Fifty invented names shown as this site's patrons on `/donate` (`PatronWall` / `PATRON_DEMOS`):
  **$1-2** (`docs/audits/funding.md:36-52`), High, "fabricated social proof", fix is to remove the wall
  until a consented Stripe-backed list exists. Not re-reported.
- Tier badges drawn as escalating chess pieces (Pawn→Queen) with published prices reading as implied
  strength / a price tag: **$2-1**. The monthly toggle shipped-but-disabled and its coupling to
  TERMS §12: **$2-3**. One-off badge lasting 12 months not 1: **$5-1**.
- `/about`, `/terms`, `/developers`, footer, `NOTICE:6`, `CITATION.cff` all telling the reader the
  source "is published" / linking a URL that 404s while the repo is private: **L-1-7** and **L-9-1**.
  The "open-source" half of the footer tagline shares this defect.
- `/about` calling itself "the origin record" while sitting behind Cloudflare Access, and every
  attribution timestamp being private or unsent: **L-9-1**.
- `CITATION.cff` flattening the three-way licence split; press kit shipping the reserved mark with no
  grant; `/learn` missing the CC BY block: **L-9-5**, **L-9-4**, **L-9-7**.
- `/about` not linking `STOCKFISH.LICENSE` from the bot panel: **L-10-2/L-10-3** area (the `/about`
  licence section does link it, `about/page.tsx:274-279`).
- Route function, heading order, empty-lobby paths for these pages: **P-1**. Onboarding
  comprehension of the economy: **P-2**. Visual/spacing/typography: **P-10** / STYLING-CONFORMANCE.

Checked and clean, filed here so the next reader does not re-open them:

- No "first ever" / "world's first" / "only chess variant" claim anywhere in `ui/` (grep).
- No patent or trademark-registration claim; `NOTICE` and `/about` both say the mark is merely
  reserved and EUIPO is optional and later, matching `CLAIMS.md`.
- The dedication is framed as personal preference, not a factual assertion.
- Site-level metadata / OG / manifest (`lib/site.ts:8`, `layout.tsx:20-41`,
  `[locale]/opengraph-image.tsx`) carry only "Chess where every move costs, or earns, time." — no
  claims to check.
- `/donate` is not a paywall: `donate.entirelyFree` ("No, KiriChess is entirely free"),
  `donate.patronBadgeNote` ("no exclusive features for Patrons"), TERMS §12 ("A donation buys
  nothing"). The CTA copy is "Donate €{amount}", never "unlock".
- `/rules` and `/learn` describe the variant as "chess … except the clock is a resource" with "no
  new pieces" — consistent with `docs/RULES.md` and the engine.

### Action list

1. apply `L-7-5`'s fix — "non-profit" out of the tagline, `about.projectPara`,
   and the two `donate` strings, in `en.json` and the five locale files (P-9-1). Same commit: the
   "forbetter" typo (P-9-4) and the `/developers` mark clause (P-9-5).
2. replace the two `[TODO: …]` strings on `/about` with prose that stands alone; leave the
   real catalogue-link branch intact for when the URL exists (P-9-2).
3. **Owner, one decision each:** the accessibility response-time sentence — set it or cut it (P-9-2);
   and whether "February 2026" is softened or given a dated private artifact to rest on (P-9-3).
4. **Not this packet:** removing `PatronWall` from `/donate` is **$1-2**'s action; publishing the
   repo / rewording "is published" is **L-1-7** / **L-9-1**. No standing position is reversed here —
   "free forever" and "cosmetics only" are untouched; the finding is that "non-profit" is a
   different, unfounded claim the project's own Terms already reject.

---

## P-10 — Visual consistency

Date: 2026-08-30
Packet: docs/audits/INDEX.md

### Verdict

The visual system is in good shape and getting better fast. Between `STYLING-CONFORMANCE.md`
(2026-08-28) and this pass, the `dev` branch landed five remediation commits (`4bb052a`..`6985910`)
that fixed most of that audit's numbered findings: the shared components are back on the type,
colour and elevation scales; gold no longer means "good/available/caution"; the ~40 off-scale
buttons are back on the size table; every rendered branch now carries a `<main id="main-content">`,
an `<h1>` and `scroll-mt-24`; the prose tables have a real recipe; and `styling.test.ts` grew a
175-line drift guard. The board and clock components specifically were cleaned up in the same pass
(`SELECTED_INSET` and the active-clock glow both moved off raw `rgba()` onto
`color-mix(in srgb, var(--bounty) …)`).

Dark-mode-only correctness is clean: no `dark:` variant anywhere in `app/` or `components/`, no
`bg-white` / `text-black` / `prefers-color-scheme` light-mode assumption, and the one sub-AA
secondary-text tier the previous audit flagged (`Footer` column headings at `text-white/70`) is now
`text-muted`. Footer legal links are effectively complete — terms, privacy, contact, Discord,
GitHub, plus `/about` and `/rules` which carry the CC BY 4.0 / Apache / NOTICE / Stockfish
attribution in prose.

What remains is a short tail of visual-judgement items the mechanical guard cannot see: two
inline-style values on `Board`'s `customBoardStyle` that the class-string tests do not scan, a
malformed class left behind by the `rounded-*` strip, a raw `opacity-70` on readable text, the
home page still having no `h1`, and `page-board`'s early-return branches not matching the `py-6`
the happy path now uses. None is stranger-facing harm; all are cheap.

### Findings

#### P-10-1 — The home page `/` has no `<h1>` on any branch

- Severity: Medium (trigger: the most-visited page in the app; a screen-reader user landing on it
  has no page heading, and the document outline starts at `<h2>`)
- Where: `ui/app/[locale]/page.tsx:19` (`<main id="main-content">`), first heading is
  `ui/app/[locale]/page.tsx:85` (`<h2 className="text-lg font-bold text-white">`)
- Impact: every other route now has exactly one `h1` in one of the three sanctioned forms; `/` is
  the lone exception. `styling.md` §4 names this itself ("Known gap, not a model to copy: `/` has
  no h1 at all. Worth fixing when you are in the file."), and `STYLING-CONFORMANCE.md` mentions it
  in passing, but no audit files it as an action. The lobby heads its rows with `h2`s, so the fix
  is additive, not a re-layout.
- Fix: add a visually-hidden `<h1 className="sr-only">` naming the page (the lobby / "Play
  KiriChess") directly inside `<main>`, matching the `sr-only` pattern `/leaderboards` and
  `/watch/[id]` already use.

#### P-10-2 — `Board`'s `customBoardStyle` carries a dead `borderRadius` and a raw-`rgba()` shadow the guards cannot see

- Severity: Low (trigger: `styling.test.ts` scans class strings and allowlists `Board.tsx`
  wholesale for `rgba()`, so both values are invisible to CI; a future author reads them as intent)
- Where: `ui/components/Board.tsx:222` (`borderRadius: "8px"`) and
  `ui/components/Board.tsx:223` (`boxShadow: "0 10px 40px -10px rgba(0,0,0,0.6)"`)
- Impact: `globals.css`'s `* { border-radius: 0 !important }` beats the inline `borderRadius`, so
  line 222 is exactly the inert `rounded-*` intent §1 says to strip — just expressed as an inline
  style instead of a class, which is why the `2a33020` rounded-strip pass missed it. Line 223 is a
  hand-rolled fourth elevation on the single most prominent component in the app (§5 has three
  tiers plus glows, and this is neither), and its `rgba(0,0,0,0.6)` rides the `Board.tsx` entry in
  `styling.test.ts:213` — an allowlist whose stated reason is the green legal-move dots
  (board-affordance hues outside the token vocabulary), not a black drop shadow.
- Fix: delete `borderRadius`. For the shadow, either drop it (the board already reads as raised
  against `--background`) or move it to `shadow-2xl` on the wrapper `div` at
  `Board.tsx:202`; if a bespoke shadow is genuinely wanted, tighten the `styling.test.ts` Board
  allowlist to the affordance colours rather than the whole file.

#### P-10-3 — `AccessibleBoard` has a malformed `focus-within:` class left by the `rounded-*` strip

- Severity: Low (trigger: dead/broken code; no runtime effect but it will confuse the next editor
  of this className and no test catches a dangling modifier)
- Where: `ui/components/AccessibleBoard.tsx:255` — the `<section>` className contains
  `… focus-within:max-w-[420px] focus-within: focus-within:border …` (a bare `focus-within:` with
  no utility)
- Impact: commit `2a33020` removed `rounded-xl` from `focus-within:rounded-xl` but left the
  `focus-within:` prefix in place. Tailwind silently ignores the empty class, so the keyboard
  focus panel is unaffected, but the string now reads as a typo.
- Fix: delete the stray `focus-within:` token.

#### P-10-4 — `TimeDeltaLayer` renders the popup label at raw `opacity-70` over coloured text

- Severity: Low (trigger: the label is meant to be read; §2's "never `/70` or lower" contrast rule
  is expressed here as an `opacity-70` utility, which the mechanical check for `text-…/70` does not
  match)
- Where: `ui/components/TimeDeltaLayer.tsx:27` (`<span className="ml-1 text-sm font-normal opacity-70">`)
- Impact: the label sits on a `text-gain` / `text-loss` base and `opacity-70` pushes the effective
  contrast down the same way `text-[var(--foreground)]/70` did. The popup animates and fades, so
  the practical harm is small, but this is the one remaining spot where readable text is dimmed by
  a raw opacity wash rather than by `--muted`.
- Fix: drop `opacity-70` and let the smaller size + `font-normal` carry the de-emphasis, or render
  the label in a full-strength colour.

#### P-10-5 — `page-board` early-return branches use `py-10` / `py-16` where the happy path is `py-6`

- Severity: Low (trigger: the same `watch/[id]` inconsistency `STYLING-CONFORMANCE.md` flagged was
  fixed to `py-6` in this pass; `replay/[id]`'s early returns were not)
- Where: `ui/app/[locale]/replay/[id]/page.tsx:211` (`page-board py-10`, loading) and
  `ui/app/[locale]/replay/[id]/page.tsx:226` (`page-board flex flex-col items-center py-16`, error)
- Impact: `styling.md` §3 documents `page-board` as `py-6`. `game/[id]` and `watch/[id]` now use
  `py-6 scroll-mt-24` on all three of their branches; `replay/[id]`'s two early branches drift.
  Cosmetic vertical-rhythm nit only.
- Fix: `py-6` on both branches (keep the `flex flex-col items-center` on the error branch if the
  centred `EmptyState` wants it).

#### P-10-6 — No footer or nav route to the licence / NOTICE / name-reservation notice

- Severity: Low (trigger: a fork or reuser looking for the attribution terms; the grant is on-site
  but two clicks deep and not where a reader looks for it)
- Where: `ui/components/Footer.tsx:61-64` (legal links are `/terms`, `/privacy` only); the CC BY
  4.0 / Apache / NOTICE text lives at `ui/app/[locale]/rules/page.tsx:1588-1606` (`#licence`) and
  `ui/app/[locale]/about/page.tsx:241-282` (`#licence`)
- Impact: the footer is "the only site-wide route" to the legal pages (its own comment,
  `Footer.tsx:58-60`), and the licence/attribution grant — the thing an OSS project most needs a
  stranger to find — is not among them. The name-reservation position from `NOTICE` (Apache does
  not grant the trademark; forks must rename) is not stated in user-visible copy at all, only
  implied by "see NOTICE". This overlaps P-9 (copy and claims) and L-9 (IP / trademark) — filed
  here only as the navigation gap.
- Fix: add a `/about#licence` link (label "Licence" or "Licence & source") to the footer's legal
  row next to Terms and Privacy. Whether the trademark line needs its own visible sentence is a
  P-9 / L-9 call.

### Already covered

- Board and both clocks never flip for the Black player; local clock pinned to the top of the
  sidebar — `C.md` C-11-1, and `P.md` P-6-1.
- Below `lg:` both clocks stack at full `text-6xl` above the board; board has no viewport-height
  cap; safe-area insets — `P.md` P-6-1/P-6-2/P-6-3.
- Skip link pointing at a missing `#main-content`; early-return branches with no `<main>` / no
  `<h1>` — `STYLING-CONFORMANCE.md` (HIGH cluster) and deep `FE-3.md` FE-3-1; remediated on `dev`
  by commit `1a7e6b4`.
- `/u/[id]` loading branch has no `h1` and a bare skeleton — `P.md` P-1-6 and
  `STYLING-CONFORMANCE.md`; remediated in this pass.
- `TutorialClocks.tsx` duplicating `Clock.tsx`'s `formatTime` and JSX — deep `FE-2.md`.
- The broad styling drift (button padding scale, gold-as-state, `--accent`/`--cyan`/`--blue` as a
  chart palette, prose tables, elevation tiers, `bg-black/NN`) — `STYLING-CONFORMANCE.md`, now
  largely remediated and guarded by `ui/lib/styling.test.ts`.
- `Board.tsx` green legal-move dots as raw `rgba()` — noted-not-filed in
  `STYLING-CONFORMANCE.md` ("board affordances are arguably outside the token vocabulary") and
  explicitly allowlisted in `styling.test.ts:213`.

### Action list

1. P-10-1: add the `sr-only` `<h1>` to `/`. One line, matches an existing pattern.
2. P-10-3: delete the stray `focus-within:` token in `AccessibleBoard.tsx:255`.
3. P-10-2: remove `borderRadius` from `Board.tsx:222`; decide shadow (drop, or
   `shadow-2xl` on the wrapper) and, if kept bespoke, narrow the `styling.test.ts` Board allowlist.
4. P-10-5: `py-6` on `replay/[id]`'s loading and error branches.
5. P-10-4: drop `opacity-70` on the `TimeDeltaLayer` label.
6. P-10-6: add a `/about#licence` link to the footer legal row. Trademark-sentence
   question is deferred to P-9 / L-9, not reversed here.

No standing-position reversals. No production code changed in this packet.

