# M — Discovery and community

Merged packet reports for category **M**. Individual packets were `docs/audits/M-N.md`; they now live as sections below. Index: [INDEX.md](INDEX.md).

---

## M-1 — SEO and public pages
Date: 2026-08-30
Packet: docs/audits/INDEX.md

### Verdict

The SEO plumbing is competent and, for a one-person hobby host whose scarce resource is adoption,
close to sufficient. `robots.ts` and `sitemap.ts` are thoughtfully commented, exclude the right
thin/gated routes, and the sitemap uses a single-entry-per-page `alternates.languages` map rather
than locale-duplicated entries. `opengraph-image.tsx` sits under `[locale]` (frontend.md's hard
requirement) so an `og:image` is actually emitted, and `/replay/[id]` and `/challenge/[token]`
build genuinely per-object OG/Twitter cards for chat unfurl. Titles are templated (`%s - KiriChess`)
and the four content pages that matter for citation (`/rules`, `/about`, `/developers`, `/donate`)
carry real, translated `metaTitle`/`metaDescription`.

The gaps are all in the same shape: metadata that is set once at the root and never specialised.
Every ranking page except the two transient ones inherits the root layout's generic
`openGraph`/`twitter` object wholesale, so a shared `/rules` or `/about` link previews as the bare
homepage card. No route sets a canonical URL, which matters because `www` and the apex both resolve
today (ROADMAP DH.3 notes). There is no JSON-LD anywhere, which is the cheapest "citable" win a
project this size can buy. hreflang lives only in the sitemap, not the page head. And three client
routes carry hardcoded-English layout metadata while their peers are translated.

**DH.3 / Todo #12 is the standing blocker on actual indexing, not a code bug.** `kirichess.com`
sits behind a Cloudflare Access login wall on purpose until the soft launch (ROADMAP.md:1697-1707,
1733; `docs/plans/chessvariants/SUBMISSION-DRAFT.md:33`; `PROVIDER-ACCOUNTS.md:48-50`). Googlebot,
unfurlers and the chessvariants.com submission all meet that wall right now. Nothing in this packet
should be read as "the site is not indexed" — that is expected and dated. The findings below are so
that the metadata is correct *before* the gate comes off, since the first crawl sets the baseline.

### Findings

#### M-1-1 — Every ranking page except replay/challenge shares one generic OG/Twitter card
- Severity: Medium (trigger: anyone pasting a `/rules`, `/about`, `/developers`, `/learn`,
  `/leaderboards` or `/donate` link into Discord, Slack, Mastodon or a forum after launch — which is
  exactly how AD.3/AD.12 adoption is expected to happen)
- Where: `ui/app/[locale]/layout.tsx:24-35` defines the only `openGraph`/`twitter` objects.
  `ui/app/[locale]/rules/page.tsx:32-38`, `about/page.tsx:8-14`, `developers/page.tsx:8-14`,
  `donate/page.tsx:10-16`, `learn/layout.tsx:6-10`, `leaderboards/layout.tsx:4-7` each set only
  `title` + `description`. Next merges `openGraph` shallowly per key, so a child that omits it
  inherits the parent's whole object — confirmed in-repo by
  `ui/app/[locale]/replay/[id]/layout.tsx:31-33` and `challenge/[token]/layout.tsx:36-37`, which
  spell this out and work around it.
- Impact: A shared `/rules` link previews with `og:title` = "KiriChess", `og:description` =
  "Chess where every move costs, or earns, time." and `og:url` = "/", identical to the homepage.
  The one page most likely to be linked when someone explains the variant ("here's how the clock
  works") unfurls as if it were the front door. `og:url` pointing at `/` on every sub-page is also a
  weak canonical signal to crawlers.
- Fix: In each of those `generateMetadata`/`metadata` blocks add
  `openGraph: { title, description, url: "<path>" }` and `twitter: { title, description }`, mirroring
  the pattern already in `replay/[id]/layout.tsx`. `/rules` and `/learn` deserve a one-line bespoke
  description aimed at a stranger, not the same string as the `<title>`.

#### M-1-2 — No canonical URL on any route
- Severity: Medium (trigger: `www.kirichess.com` and `kirichess.com` both currently serve — ROADMAP
  .md:1753-1768 — plus `?src=` acquisition params (ROADMAP AD.7) and locale-prefixed paths all
  create URL variants of the same document)
- Where: no `alternates.canonical` anywhere under `ui/app/`; only `metadataBase` is set
  (`ui/app/[locale]/layout.tsx:21`).
- Impact: Until the `www` → apex 301 redirect rule is added (still open per ROADMAP.md:1763-1766),
  `www` and apex are two indexable origins with byte-identical content. Add `?src=hn` tracking
  params and every landing page has a small family of near-duplicate URLs competing with each other,
  with nothing telling Google which one is real.
- Fix: Set a self-referencing `alternates: { canonical: "<path>" }` on the root layout metadata
  (relative to `metadataBase`) and per content route. This is one line and it also fixes the `og:url`
  half of M-1-1. It does not replace the `www` redirect rule — that is still the right permanent
  answer and belongs on the DH.3 checklist.

#### M-1-3 — robots.ts disallow list omits two tokenised routes; `/unsubscribe` also has no noindex
- Severity: Medium
- Where: `ui/app/robots.ts:28-41`. `/reset-password`, `/verify-email` and `/auth/callback` are
  disallowed with the stated reason "a crawled/cached copy of that URL is a leaked credential"
  (`robots.ts:15-16`). `/unsubscribe` has the identical shape —
  `?userId=&category=&token=` (`ui/app/[locale]/unsubscribe/page.tsx:15-17`) — and fires the state
  change automatically on load (`:22-34`, `unsubscribeFromNotifications`), yet is not in the list and
  the page sets no `robots` metadata (P-1 flagged its total lack of metadata). `/challenge/[token]`
  is also not disallowed, unlike its siblings `/replay/`, `/watch/`, `/game/`.
- Impact: A crawler or link-preview bot that ever sees an unsubscribe link (email clients that
  prefetch, a user pasting one into a chat) hits the tokenised URL, unsubscribes the user, and the
  token-bearing URL can be cached. `/challenge/[token]` pages are indexable with a live invite token
  in the path; a challenge link posted anywhere public becomes a "X challenged you" search result.
- Fix: Add `"/unsubscribe"` and `"/challenge/"` to the `disallow` array. Additionally give
  `unsubscribe/page.tsx` a `generateMetadata` returning `robots: { index: false, follow: false }`
  (the `not-found.tsx:11` pattern), since robots.txt is advisory and unfurlers ignore it. For
  `/challenge/` the rich unfurl card is intentional (like `/replay/`), so robots-disallow + keeping
  the OG tags is the consistent move.

#### M-1-4 — No structured data (JSON-LD) anywhere
- Severity: Low (trigger: the project's own goal — "being findable and citable" (INDEX.md §7) —
  and the chessvariants.com / archive.org / launch-thread plan in AD.3/AD.12)
- Where: no `application/ld+json` in `ui/` (grep: zero matches across the tree).
- Impact: No `Organization` / `WebSite` node means search engines and AI crawlers have no
  machine-readable statement of what KiriChess is, who runs it, the logo, the GitHub/Discord links,
  or the site search endpoint. For a niche term with no existing authority, this is the single
  cheapest thing that improves how the project is represented in knowledge panels and LLM answers.
- Fix: Add one `<script type="application/ld+json">` to the root layout (or homepage) with a
  `WebSite` + `Organization` graph: name, url, `sameAs` (GitHub repo, Discord), logo. Optionally a
  `HowTo` or `FAQPage` block on `/rules` describing the four clock rules — that content is already
  generated from `config/kirichess.rules.json` via `lib/rules.generated`, so it can be built from the
  same source without drift. Keep it minimal; do not fabricate ratings or review markup.

#### M-1-5 — hreflang alternates exist only in the sitemap, not in the page head
- Severity: Low (trigger: the four unreviewed locales (es/fr/de/pt-BR) are live in `routing.ts:32`;
  Google prefers head/​sitemap agreement and some crawlers only read head links)
- Where: `ui/app/sitemap.ts:41-45` emits `alternates.languages`. No route sets
  `alternates.languages` in its `Metadata`, so no `<link rel="alternate" hreflang=...>` is rendered
  in `<head>`, and there is no `x-default`.
- Impact: Minor and partly cosmetic — sitemap hreflang is valid on its own — but head + sitemap
  disagreement (one present, one absent) is a weaker signal than either alone, and `x-default` is
  missing entirely, so no locale is nominated as the fallback for an unmatched `Accept-Language`.
- Fix: Add `alternates: { languages: {...}, canonical }` to the shared metadata, derived from
  `routing.locales` the way the sitemap already does, including an `"x-default"` pointing at the
  unprefixed English URL. Cross-reference P-8, which confirmed the sitemap side is correct but did
  not cover head tags.

#### M-1-6 — Three client-route layouts carry hardcoded-English metadata while peers are translated
- Severity: Low (trigger: a non-English user or crawler on `/nl/learn`, `/de/leaderboards`,
  `/fr/watch`)
- Where: `ui/app/[locale]/learn/layout.tsx:6-10`, `leaderboards/layout.tsx:4-7`,
  `watch/layout.tsx:7-10` set English string literals for `title`/`description`. Contrast
  `rules/page.tsx:32-37`, `about/page.tsx:8-13`, `developers/page.tsx:8-13`, `donate/page.tsx:10-15`,
  which all pull `metaTitle`/`metaDescription` from `getTranslations`.
- Impact: `/nl/learn` renders a Dutch page with an English `<title>` and meta description. `/learn`
  is priority 0.8 in the sitemap and named in the roadmap growth set, so its SERP snippet matters.
  Inconsistent with the translated content pages.
- Fix: Add `learn.metaTitle` / `learn.metaDescription` etc. to `ui/messages/*.json` and convert the
  three layouts to `async generateMetadata` with `getTranslations`. Cross-reference P-8 (which
  covered runtime UI strings, not route metadata).

#### M-1-7 — sitemap `lastModified` is the build timestamp for every route
- Severity: Low (trigger: crawlers that weight `lastmod` and learn to distrust it when it is always
  "now")
- Where: `ui/app/sitemap.ts:39` — `lastModified: new Date()` for all ten routes.
- Impact: Every deploy stamps `/terms`, `/privacy`, `/rules` etc. as modified at build time even
  when their content did not change. Google explicitly discounts `lastmod` when a site reports the
  whole tree as freshly modified on every crawl. Low harm, but the field is currently noise rather
  than signal.
- Fix: Either drop `lastModified` entirely (a small sitemap of stable pages does not need it), or
  derive a real per-route date — e.g. git commit date of the page file at build time, or a hardcoded
  constant per route bumped when the content changes. Dropping it is the smaller change.

### What seo.spec.ts asserts, and does not

`e2e/tests/seo.spec.ts` (5 tests, runs in CI's `e2e` job against the real prod images) checks:
robots.txt is 200 and contains the sitemap line plus two disallow entries (`/moderation`,
`/replay/`); sitemap.xml is 200 and contains 4 `<loc>` values (`/`, `/rules`, `/learn`,
`/leaderboards`); the **home** page has `og:title` = "KiriChess", non-empty `og:description`,
non-empty `og:image`, `twitter:card` = `summary_large_image`; `/rules` and `/learn` have titles
matching `/Rules/` and `/Learn/`; a nonexistent `/replay/{uuid}` returns 200 with the fallback OG
card and its `opengraph-image` is a 200 `image/png`.

It does **not** cover: OG/Twitter tags on any page other than home and the replay fallback (so
M-1-1 would not be caught); `/about`, `/developers`, `/donate`, `/leaderboards`, `/watch` titles or
descriptions at all; canonical tags (M-1-2); hreflang / `alternates` in either head or sitemap body
(the sitemap test only greps `<loc>`, never `<xhtml:link>`); `x-default`; JSON-LD (M-1-4);
sitemap `priority` / `lastmod` / per-page alternate maps; that disallowed routes actually emit
`noindex`; the default `opengraph-image` rendering (only the replay override is checked); locale
prefixes in any of the above.

### Already covered

- **Replay metadata cannot reach the compose-internal backend when self-hosting** — deep/INF-1-5 and
  deep/FE-4-2 (challenge's code-level version). Not re-reported.
- **Five client routes with no `layout.tsx` metadata; `/u/[id]` no metadata** — deep/FE-3 nit 1,
  P-1-6. M-1 only adds that `/unsubscribe`'s missing metadata is also a noindex gap (M-1-3).
- **sitemap hreflang alternates derived from `routing.locales`, catalogue parity, switcher labels** —
  P-8 (verdict + action list item 5, "all correct"). M-1-5 is the head-tag half P-8 did not cover.
- **Hardcoded English UI strings in player-facing components** — P-8-1/2/3/4. M-1-6 is route
  metadata, a different surface.
- **`/` has no h1** — known styling gap (`.claude/rules/styling.md` §4, "Known gap"); P-10 / styling
  own it, not M-1.
- **Cloudflare Access blocks all crawlers until launch** — ROADMAP DH.3 / Todo #12, standing
  blocker. Not a bug. See verdict.
- **Press kit, logo asset, convert script** — M-2 owns. `opengraph-image.tsx:10-11` notes "this app
  has no logo asset to embed"; the OG card is text-only as a result. Relevant to M-1-4's
  `Organization.logo` field but the asset itself is M-2's.
- **Copy honesty on `/about` (inventor claim, dedication, donate CTA not a paywall)** — P-9 owns.
- **Route function / empty-lobby paths** — P-1 owns.

### Action list

1. M-1-3: add `/unsubscribe` and `/challenge/` to `robots.ts` disallow; add
   `robots: { index: false }` metadata to `unsubscribe/page.tsx`. Smallest, clearest, mild security
   angle.
2. M-1-1 + M-1-2: add per-route `openGraph`/`twitter`/`alternates.canonical` to the six
   content routes, mirroring `replay/[id]/layout.tsx`. Do these together; canonical fixes `og:url`.
3. M-1-6: move `/learn`, `/leaderboards`, `/watch` layout metadata into the message
   catalogues and `generateMetadata`. Coordinate with P-8's catalogue work.
4. M-1-5: add `alternates.languages` + `x-default` to shared metadata (one helper, same
   shape as `sitemap.ts`).
5. **Owner decision, then** — M-1-4: decide scope of JSON-LD (WebSite + Organization only, or
   also a `/rules` HowTo). Needs the `sameAs` list and a logo URL (depends on M-2).
6. M-1-7: drop `lastModified` from `sitemap.ts` or replace with a real date.
7. **Owner, on the DH.3 / Todo #12 checklist (not this packet)** — `www` → apex 301 redirect rule;
   remove the Cloudflare Access application; then verify the metadata above with a live crawl and the
   chessvariants.com submission.

---

## M-2 — Press kit
Date: 2026-08-30
Packet: docs/audits/INDEX.md

### Verdict

The kit has the right shape and one honest gap that has quietly widened into three. AD.1 asks for
"the clip plus three screenshots, the logo, and a one-paragraph pitch, in one folder"; four of those
five exist and the pitch (`press-kit/pitch.md`) is genuinely good — accurate to `docs/RULES.md`,
correctly hedged, with a caption-length cut. What is missing is the only thing AD.1 actually cares
about: a moving picture of the clock swinging. `press-kit/` has no `clip/` directory and the script
that would regenerate one is now broken in two independent ways.

The subtler problem is that the three committed screenshots and the capture script were all frozen
on 2026-08-18 and the app moved underneath them. The pool rename on 2026-08-22 (commit `e6c891f`)
turned `home-pools.png` into a picture of pool names that no longer exist ("Zeitnot", "Pendulum",
"Flagfall" are now "Winder", "Fulcrum", "Anchor") and broke `capture.spec.ts`, which still clicks a
"Queue Flagfall" button. `e2e/` sits outside `PoolLabelDriftTests`, so nothing caught it. The README
prose was updated for the rename; the artefacts it points at were not. A journalist who took this
folder today would publish a screenshot of a UI that isn't live and, if they tried to regenerate the
clip themselves, would hit a timeout.

Logo licensing is a real gap but already filed as **L-9-4** — the kit ships the reserved mark with
no permission line. Cross-referenced, not re-argued.

### Findings

#### M-2-1 — The kit still has no clip; its entire reason to exist is unmet
- Severity: High (trigger: anyone covering the launch — the audience AD.1 is for)
- Where: `press-kit/` has no `clip/` directory (`git ls-files press-kit/` lists only README, pitch,
  convert.mjs, logo/, screenshots/); `press-kit/README.md:11-13` records the 430×1400 phone take was
  "pulled" on 2026-08-19; ROADMAP `#1` / AD.1 (`docs/ROADMAP.md:47`, `:1927-1968`) still open.
- Impact: AD.1's premise is "the mechanic is visual and instantaneous... and **nothing in this
  document produces a picture of it happening**". That is still true. The kit conveys the clock
  economy only through `screenshots/queen-capture.png` (a still, on a layout the owner rejected — see
  M-2-5) and `screenshots/rules-captures-table.png` (a static table of numbers). Neither is the
  ten-second swing every channel in AD.1's list rewards over prose. This is the packet's headline
  gap and it is a content gap, not a tooling one: the featured position still has to be chosen and
  the result still has to be eyeballed by a human ("does this read in two seconds"), exactly as AD.1
  says.
- Fix: Pick a clip from `press-kit/README.md:26-102` (README and ROADMAP
  both nominate the clutch-queen or the opening-swing), fix M-2-3 and M-2-4 so the pipeline runs,
  generate on a ≥1024px viewport, and have the owner confirm it reads. Until then the launch post
  should carry no "see the press kit for a clip" line.

#### M-2-2 — `home-pools.png` shows three pool names that no longer exist
- Severity: High (trigger: the screenshot being reproduced anywhere public)
- Where: `press-kit/screenshots/home-pools.png` (last written 2026-08-18, commit `0002fa5`) shows
  pools "Zeitnot", "Ratchet", "Pendulum", "Flagfall". `config/kirichess.rules.json:68-164` now names
  them "Winder" (bounty1), "Ratchet" (burn1), "Fulcrum" (steal2), "Anchor" (steal1), "Mainspring"
  (burn2), "Eclipse" (steal3) — renamed in commit `e6c891f`, 2026-08-22. `ui/lib/rules.generated.ts`
  (`:137,233,281`) confirms the live UI shows the new names.
- Impact: `press-kit/README.md:19` describes this file as "the six rated-pool tiles on the home
  page" with no staleness note (contrast `:18`, which flags `queen-capture.png`). A cataloguer or
  reporter would publish "Zeitnot", "Pendulum" and "Flagfall" as this game's pools; none of those
  strings appears on the site. Two secondary problems in the same file: it is a 430px phone-width
  capture (the spec's `PHONE_VIEWPORT`, M-2-3), so the six tiles are stacked and only ~3.5 are
  visible in the frame — "the six rated-pool tiles" is not what the picture shows; and "Flagfall"
  visible at the bottom is the same dead name the capture script keys off (M-2-3).
- Fix: regenerate after M-2-3 lands, on a desktop viewport so all six tiles fit, and give
  `README.md:19` the same "as of <date>, re-shoot on pool changes" caveat the other rows carry.
  `PoolLabelDriftTests` cannot see a PNG, so this needs a human note in the README, not a test.

#### M-2-3 — `capture.spec.ts` is broken by the pool rename and still forces the pulled phone framing
- Severity: High (trigger: running `press-kit/README.md:104-113`'s "Regenerating" steps)
- Where: `e2e/press-kit/capture.spec.ts:125,128,132` — `queueButton(pageA, "Flagfall")` resolves to
  `getByRole("button", { name: "Queue Flagfall" })` and `:128` waits on `/Searching Flagfall/`. The
  pool keyed `steal1` (still the key at `:134` `?preset=steal1`) is labelled "Anchor" now, not
  "Flagfall" (`config/kirichess.rules.json:125-126`). `e2e/` is not covered by `PoolLabelDriftTests`
  (which scans `ui/` only, per `ui/lib/presets.ts:95-106`). Separately, `:39` `PHONE_VIEWPORT =
  { width: 430, height: 1400 }` and `:86-91` record both contexts at that size, despite the spec's
  own docstring (`:22-25`) and ROADMAP `#1` saying recapture must be on the desktop `lg` grid.
- Impact: the documented regeneration path fails at the first queue click (15s action timeout, then
  spec timeout). Even with the label fixed, the spec reproduces the exact 430×1400 phone framing
  that was pulled on 2026-08-19 — the "stacked clocks above the board" layout `docs/audits/P.md`
  P-6-1 covers. So the recapture is blocked on a code change this packet cannot make, and the README
  line "`convert.mjs` stays; it just needs a new `clip/queen-capture-raw.webm`" (`:12-13`)
  understates what is required.
- Fix: retarget the spec to a desktop viewport (≥1024px, e.g. `{ width: 1280, height: 900 }` to
  match the rules-table capture) so the `lg:grid-cols-[240px_1fr_320px]` layout renders, and replace
  the hardcoded `"Flagfall"` with the label pulled from `POOL_NAMES.steal1` (or `PRESETS.steal1.label`)
  so a future rename cannot silently break it again. Note in the spec that AD.1's desktop recapture
  is **not** blocked by P-6-1 — the mobile layout bug is irrelevant at ≥1024px — which corrects
  P-6-1's action item 8.

#### M-2-4 — `convert.mjs` hardcodes the phone width, so a desktop clip becomes a 430px GIF
- Severity: Medium (trigger: running `node convert.mjs` on a desktop-viewport raw clip)
- Where: `press-kit/convert.mjs:69` (`scale=430:-1:flags=lanczos,palettegen=...`) and `:82`
  (`scale=430:-1:flags=lanczos[x];...`). The MP4 branch (`:44-58`) has no scale filter and is fine.
  `:22` `SSEOF = "-12"` with the comment "Flagfall queue glow → four moves → Qxf6 ±25s → settle"
  assumes the opening-swing sequence (README clip 2), not the clutch-queen (clip 1), which README
  and ROADMAP both name as the one to try first and which needs a clock-building wait
  (`README.md:40-42`).
- Impact: `README.md:12-13` says "`convert.mjs` stays; it just needs a new
  `clip/queen-capture-raw.webm`". It does not — the GIF output width is pinned to the dead phone
  format, so a desktop `lg` capture (board + two clock columns, ≥1024px) would be downscaled to
  430px wide and be unreadable, which is the opposite of AD.1's "large enough to read on a phone in
  a feed". The `SSEOF` window may also miss the swing if clip 1 is chosen.
- Fix: take the GIF width from a constant near the top of the file (or a CLI arg) sized to the
  desktop capture — roughly 720–900px wide for a feed GIF — and re-derive the trim window per clip,
  or document that `SSEOF` must be tuned to the chosen sequence. Same commit as M-2-3.

#### M-2-5 — All three screenshots predate three UI passes; `queen-capture.png` is the rejected layout
- Severity: Medium (trigger: any of the three being published)
- Where: `press-kit/screenshots/{queen-capture,rules-captures-table,home-pools}.png` all last
  written 2026-08-18 (commit `0002fa5`). Since then: the button-style/page-width pass (`e6c891f`,
  2026-08-22), the pool rename (same commit), and the board-layout rework tracked in
  `docs/audits/P.md` P-6-1 / `docs/audits/C.md` C-11-1. `press-kit/README.md:18` already says
  `queen-capture.png` is "Still the mobile stacked layout — replace on the next capture". In that
  PNG the move list shows only "1. e2e4 -1s" while the board is at move 5 (post `3.Qxf6`).
- Impact: `queen-capture.png` is the kit's one economy-in-context still and it is a layout the owner
  explicitly rejected on 2026-08-19 (`docs/ROADMAP.md:1963-1968`); shipping it in the press folder
  contradicts that decision. `rules-captures-table.png` is the least stale of the three (its content
  — the ±5s/±10s/±15s/±25s capture table — still matches `config/kirichess.rules.json`) but was shot
  before the button/spacing pass, so the surrounding page chrome may have drifted.
- Fix: regenerate all three in the same run as the clip (M-2-1), on the desktop viewport. If a clip
  cannot be produced before launch, at minimum re-shoot `home-pools.png` (M-2-2, wrong names) and
  drop or re-shoot `queen-capture.png` (rejected layout).

#### M-2-6 — No licence or usage line anywhere in the kit
- Severity: Low (trigger: a journalist deciding what they may do with the logo)
- Where: `press-kit/README.md` has no licence, permission or usage section; the "What's here" table
  (`:15-24`) has no per-file rights column. `NOTICE:18-22` reserves "the name 'KiriChess', the
  wordmark, and the marks in press-kit/logo/" out of both Apache-2.0 and CC BY 4.0.
- Impact: a press kit exists so a stranger can reproduce the mark without asking, and this one gives
  no answer — a careful reader who finds `NOTICE` stops, a careless one uses the mark for anything.
  The pitch text and screenshots also have no stated licence in the kit itself.
- Fix: **owned by `docs/audits/L.md` L-9-4** (a three-sentence grant at the top of the README:
  unmodified reproduction to illustrate coverage; no alteration / no implied endorsement;
  screenshots and pitch under CC BY 4.0 / Apache-2.0). Still unapplied as of this date. Not
  re-specified here.

### Already covered

- **Press kit ships the reserved mark with no permission grant** — `docs/audits/L.md` L-9-4
  (Medium). M-2-6 cross-references; the fix is L-9-4's.
- **The pulled phone clip / below-`lg:` stacked-clock layout** — `docs/audits/P.md` P-6-1 (High)
  and its action item 8; `docs/audits/C.md` C-11-1 (clock ordering). M-2-3 adds that AD.1's
  desktop recapture is *not* gated on P-6-1.
- **CITATION.cff licence flattening, `/learn` missing CC BY block, font/audio provenance** —
  `docs/audits/L.md` L-9-5/L-9-7/L-9-3. Not press-kit.
- **`/about` and `/terms` state the logo/wordmark licensing correctly** — `docs/audits/P.md`
  verdict. The gap is that the press kit itself is silent (M-2-6).
- **"Non-profit" / "open-source" claims, `[TODO:]` strings, invention date** — `docs/audits/P.md`
  P-9-1..P-9-3. `press-kit/pitch.md` says only "Free to play, no ads" — clean, no claim to check.
- **SEO / OG / metadata on public pages** — packet **M-1** (report not yet written). Not duplicated.

### Action list

Ordered. Owner steps are marked.

1. fix `e2e/press-kit/capture.spec.ts` — retarget to a ≥1024px viewport and
   pull the pool label from `POOL_NAMES`/`PRESETS` instead of the literal `"Flagfall"` (M-2-3). Same
   commit: parameterise the GIF width in `press-kit/convert.mjs` off a desktop-sized constant and
   note the `SSEOF` window is per-clip (M-2-4). *This touches `e2e/` and `press-kit/` tooling, not
   production `ui/` code — confirm that is in scope before committing on `dev`.*
2. **Owner (AD.1 / ROADMAP #1):** choose the featured clip from `press-kit/README.md`, run
   the fixed pipeline on desktop, regenerate all three screenshots in the same run, and have the
   owner confirm the clip reads in two seconds (M-2-1, M-2-5). Owner's eye is required.
3. re-shoot `home-pools.png` so it shows the current six
   pool names, and add an "as of <date>, re-shoot on pool/layout changes" caveat to every row of
   `press-kit/README.md`'s file table (M-2-2). Drop `queen-capture.png` from the folder until a
   desktop replacement exists (M-2-5).
4. add the three-sentence mark grant + asset-licence line
   to the top of `press-kit/README.md` (M-2-6).
5. **Not this packet:** the below-`lg:` board layout redesign is P-6-1; publishing the repo / the
   "is published" wording is L-1-7 / L-9-1. No standing position is touched.

---

## M-3 — Launch and communities
Date: 2026-08-31
Packet: docs/audits/INDEX.md, table 7

### Verdict

The launch draft is in good shape and the register is right for the audience: it leads with the
mechanic, promises nothing, states honestly that the playerbase is one person, and asks four
specific design questions rather than for praise. The sequencing (r/chessvariants alone first, wait
a day, then the lichess forum; chessvariants.com as a separate permanent citation) is well judged.
This packet is a red-team of that draft, not a rewrite, and the findings are almost all about the
**pre-post checklist** rather than the post text: the six-item checklist gates on deploy freshness,
the UI pass, Access removal, mail delivery, one full game, and half a day free, but it does not gate
on the two things most likely to embarrass the launch, namely a drifted rule number in the post and
a monitoring stack that is not actually watching yet (I-5). The post text itself needs one
reconciliation: it correctly says the repo is private, but the deployed site's TERMS §10 says the
opposite and links a URL that 404s (L-1-7), so a reader who follows the post to the site meets a
contradiction. Nothing here blocks the owner from posting; all of it is a stronger checklist and a
fuller Q&A prep.

### Findings

#### M-3-1 — The post says the repo is private; the deployed site says it is published and links a 404
- Severity: Medium (trigger: Access comes off and a reader follows the post to `/terms`)
- Where: `docs/plans/launch/POST-DRAFT.md:102` ("The working repository is not public") and `:141-142`
  ("Do not link the GitHub repo in the post") are **correct** and match reality (`gh repo view` is
  `PRIVATE`, per `docs/audits/L.md:157`). The contradiction is on the site: `docs/legal/TERMS.md:95`
  and `ui/app/[locale]/terms/page.tsx:213-216` state "The KiriChess source is published at
  https://github.com/MichielGekiere/kirichess", a link that 404s for anyone not logged in. Already
  filed as `L.md` L-1-7, `L.md` L-9-1, `P.md` P-9 (line 51).
- Impact: the launch post is internally honest, but the moment a r/chessvariants reader clicks
  through to the site to check the licence claim, TERMS tells them the source is public and hands
  them a dead GitHub link. On this audience specifically, a broken "here is the source" link reads
  as vaporware or as carelessness, which is the opposite of the trust the post is trying to build.
- Fix: make "TERMS §10 wording matches repo visibility (L-1-7 resolved)" a hard pre-post checklist
  item. Either the repo is made public before launch (O-4's snapshot pass owns that) and §10 stays,
  or §10 is reworded to "the source will be published under Apache-2.0; the rules manual and
  `config/kirichess.rules.json` are CC BY 4.0" with no live link. Bump `TermsVersion.Current` and
  the four legal-doc copies together (`LegalDocumentDriftTests`).

#### M-3-2 — The checklist does not gate on rule-number freshness, and the post quotes the numbers most likely to have drifted
- Severity: Medium
- Where: `docs/plans/launch/POST-DRAFT.md:3-6` (the draft's own header warns the numbers come from
  `RULES.md` and must be re-read if a rule is retuned) but the six-item "Before you post" list at
  `:14-26` has no corresponding item. The post states exact values at `:68-71` (pawn 5 / minor 10 /
  rook 15 / queen 25), `:76-82` (move tolls, castling flat 3, check chip 2s), and `:89-94` (KR "roughly
  70 seconds", KN "about 88", "1+0 pool you can be completely winning ... and unable to afford the
  win"), plus `:96-98` ("six rated pools", "1+0 up to 15+0", "different ... capture multipliers").
- Impact: `.claude/rules/engine.md` and the pool config have both been retuned during development,
  and `npm --prefix ui run gen:rules:check` is a CI gate precisely because these numbers drift. A
  wrong capture value or a wrong endgame-toll figure in the first post is, in the draft's own words,
  "the one correction that follows a variant around" — and the endgame conversion costs at `:89-94`
  are worked estimates, the single hardest class of number to keep current and the most quoted if
  wrong.
- Fix: add as checklist item 0: "`npm --prefix ui run gen:rules:check` is green on the deployed
  commit; re-read rules 1 to 4, the two piece-value tables, and the KR/KN conversion figures in the
  post against `docs/RULES.md` line by line; confirm the pool count, time-control range and
  economy/multiplier crossing against `config/kirichess.rules.json`."

#### M-3-3 — The checklist does not gate on the monitoring stack actually watching
- Severity: Medium
- Where: `docs/plans/launch/POST-DRAFT.md:143-146` (the "Notes for the first two hours" tell the
  owner to "Watch Better Stack and Sentry, not the thread") but no pre-post item confirms those are
  live. `docs/audits/I.md` I-5-2 / I-5-3 found `/metrics` dark since 2026-08-15 and the ops probes
  (synthetic "can a game be played", Neon watch) gated on a post-1-Sep-2026 private-repo Actions
  quota; `docs/audits/I.md` I-8-1 found every alert is email-only to one inbox with no runbook.
- Impact: the launch plan's success metric is "the uptime monitor and the error rate", but if the
  synthetic probe has never gone green and `/metrics` is not being scraped, the owner is watching a
  dashboard with no data behind it during the exact window (`:25-26`, "the first two hours decide
  whether the thread lives") when a real incident is most likely and least noticed.
- Fix: add to the checklist: "Better Stack shows a green synthetic 'can a game be played' run in the
  last hour, both edge monitors (`api.kirichess.com`, `kirichess.com`) are green, `/metrics` is
  being scraped (I-5-2 resolved), and a test error appears in Sentry." Cross-reference the B-4
  maintenance-calendar automated-checks table.

#### M-3-4 — Nothing checks that the Discord invite is permanent
- Severity: Medium (trigger: the thread outlives a 7-day invite)
- Where: `docs/plans/launch/POST-DRAFT.md:123` (`https://discord.gg/GRusewDw4` in the post body),
  `docs/audits/INDEX.md` M-3 row (same link, "live in the footer"), `ui/components/Footer.tsx`.
- Impact: a `discord.gg` invite defaults to expiring after 7 days and to a capped use count. A
  Reddit launch thread stays discoverable and gets trickle traffic for months; a chessvariants.com
  catalogue entry is permanent. If the invite in the post and the footer is the default kind, every
  reader after the first week hits "Invite Invalid", which for a variant whose stated growth problem
  is liquidity is a self-inflicted wound on the one channel meant to hold the early community
  together.
- Fix: checklist item: "the Discord invite used in the post and `Footer.tsx` is set to never expire
  and unlimited uses; confirmed by opening it in a browser with no Discord session." If the footer
  link and the post link are generated separately, make them the same permanent invite.

#### M-3-5 — No prepared answer for the licensing / self-hosting question, which this audience will ask first
- Severity: Low
- Where: `docs/plans/launch/POST-DRAFT.md:100-102` (one line: "The rules text is CC BY 4.0; the
  platform source is Apache-2.0. The working repository is not public") and `:129-146` (the Q&A prep
  covers "about time scrambles", "this is just X", "don't link the repo" but not licensing).
- Impact: r/chessvariants and the lichess variant forum are disproportionately made of people who
  will want to self-host, fork, or read the engine. "Apache but the repo is private" invites an
  obvious follow-up ("so how is it Apache if I can't see it?") that the draft has no ready answer
  for, and a fumbled answer on the licence undercuts the open-source framing the post leans on.
- Fix: add a prepared answer to the notes: the rules manual and `config/kirichess.rules.json` are
  CC BY 4.0 and on the site now; the platform is Apache-2.0 and a clean public snapshot is planned
  (O-4), not a push of the working history; the name is reserved (NOTICE), so a fork renames; a
  `docker-compose` path exists for running an instance (O-5). Keep it to three sentences.

#### M-3-6 — "What is there" names capabilities the checklist does not verify on the deployed site
- Severity: Low
- Where: `docs/plans/launch/POST-DRAFT.md:100-101` ("rated play with Glicko-2, a tutorial, replays
  with a post-game clock analysis, spectating, and bots to practise against. It works on a phone")
  vs the checklist item at `:24` which is only "You can play one full game against a second account".
- Impact: the post makes six concrete feature claims. P-1 (product surface) and P-6 (mobile) are the
  checks behind them, but the launch checklist gates only on one game being playable, so a broken
  replay analysis or a spectate page that 500s on the deployed build ships as a claim the first
  reader can falsify.
- Fix: widen the checklist item: "on the deployed site, each capability named in 'What is there' is
  exercised once: a rated game completes and updates a rating, the tutorial runs to its end, a
  finished game's replay shows the clock analysis, a game can be spectated, a bot game starts, and
  the board is usable on a phone-width viewport."

#### M-3-7 — chessvariants.com and archive.org are named but have no tracked action with a done-condition
- Severity: Low
- Where: `docs/plans/launch/POST-DRAFT.md:35-39` (chessvariants "separately, whatever happens here";
  archive.org implied via CLAIMS), `docs/game/CLAIMS.md:44-52` (the submission draft is
  `docs/plans/chessvariants/SUBMISSION-DRAFT.md`; "Left to do: find the site's current submission
  route ... decide how the inventor is named, and send it"), ROADMAP AD.3 / AD.12.
- Impact: CLAIMS.md calls the chessvariants entry "the single highest-leverage move available" for
  attribution, and it is blocked on a task (find the submission route) that has been open since the
  draft was written. If the launch happens and this slips, the permanent citation that outlasts the
  Reddit thread never gets filed.
- Fix: two explicit post-launch items with done-conditions: (a) chessvariants.com submission sent,
  resulting URL recorded on the CLAIMS.md checklist line; (b) an archive.org snapshot of
  `kirichess.com/rules` and `/about` captured within a week of Access coming off, URL recorded in
  CLAIMS.md.

#### M-3-8 — Low engagement in a small subreddit will read as failure against the "first two hours" note
- Severity: Low
- Where: `docs/plans/launch/POST-DRAFT.md:25-26` ("The first two hours decide whether the thread
  lives, and an unanswered thread is a dead one") and `:30-33` (r/chessvariants "the most forgiving
  audience").
- Impact: r/chessvariants is a small, slow subreddit; a thoughtful post there can sit at 5 upvotes
  and two comments for a day without that meaning the explanation failed. Reddit also frequently
  auto-removes a first link post from a low-karma or new account, so the thread can be invisible
  rather than ignored. Reading either as "the post is broken, rewrite before the lichess forum sees
  it" (`:32-33`) would be the wrong lesson.
- Fix: one line in the notes: before posting, confirm the account can post a link in r/chessvariants
  without auto-removal (age/karma, or ask a mod to whitelist); after posting, check the thread is
  actually visible while logged out; judge the post on the quality of the comments it does get, not
  the vote count, given the sub's size.

#### M-3-9 — Expect a same-day attempt to break the clock economy, and the notes only half-prepare for it
- Severity: Low (informational)
- Where: `docs/plans/launch/POST-DRAFT.md:89-94` (the post hands the reader precise endgame-cost
  figures and the "winning but can't afford the win" claim) and `:112-119` (the four questions
  explicitly invite stress-testing). The notes at `:136-137` prepare for the "it's about time
  scrambles" objection but not for a concrete refutation line.
- Impact: this is the intended outcome of posting to a strong audience, not a risk to avoid, but the
  most engaged reply will be someone posting "here is a forced line that farms the clock" or "Burn
  is trivially abusable, watch". If the owner is not ready to say "good, that's exactly what I
  wanted to know, let me look" and instead defends, the thread turns adversarial in public.
- Fix: add to the notes: a refutation attempt in the first day is a success condition, not an
  attack; the reply is "thank you, I'll work through that" plus a follow-up once it has actually
  been analysed, never a live counter-argument.

### Already covered

- **SEO, titles, OG, crawlability while Cloudflare Access blocks indexing** — `docs/audits/M.md`.
  M-3 does not re-check metadata.
- **Press kit, the convert script, a clip that is actually this game** — `docs/audits/M.md`.
- **TERMS §10 says the source is published while the repo is private; the GitHub link 404s** —
  `docs/audits/L.md` L-1-7, `docs/audits/L.md` L-9-1, `docs/audits/P.md` (line 51). M-3-1 only
  adds that this must be a pre-post gate because the launch post points readers at that page.
- **`/metrics` dark, ops probes gated on quota, email-only single-inbox alerting, no runbook** —
  `docs/audits/I.md` I-5-2/I-5-3, `docs/audits/I.md` I-8-1. M-3-3 only adds the pre-post gate.
- **Repo-public exposure (personal email in history, repo under a personal account)** —
  `docs/audits/O.md` O-4-1/O-4-2, `docs/audits/B.md` B-2-6/B-2-8. Not re-reported.
- **Attribution strategy (chessvariants as the real lever, public dated invention)** —
  `docs/game/CLAIMS.md`. M-3-7 only turns it into a tracked launch item.
- **`gen:rules:check` as the drift gate for rule numbers** — `.claude/rules/engine.md`,
  `docs/audits/C.md`, `docs/audits/C.md`. M-3-2 only asks that the launch post be diffed against
  the generated output by hand, since CI checks the file, not the prose.

### Action list

Ordered. The send itself is Owner-only; nothing here gets posted to any community.

1. M-3-1: resolve the TERMS §10 / repo-visibility contradiction
   (reword §10 or gate on the public snapshot), bump `TermsVersion.Current` + the four copies.
2. M-3-2, M-3-3, M-3-4, M-3-6: rewrite the "Before you post" checklist in
   `POST-DRAFT.md` to add rule-number verification, monitoring-is-live, permanent Discord invite,
   and per-capability verification as explicit items.
3. M-3-5, M-3-9: add prepared Q&A answers for licensing/self-hosting and for a same-day
   economy-refutation attempt to the "Notes for the first two hours".
4. **Owner, before posting** — M-3-8: confirm the posting account can post a link in
   r/chessvariants without auto-removal; check the live thread is visible while logged out.
5. **Owner, at launch** — M-3-4: set the Discord invite (post body and `Footer.tsx`) to never
   expire, unlimited uses; verify logged out.
6. **Owner, post-launch, tracked in CLAIMS.md** — M-3-7: send the chessvariants.com submission and
   capture an archive.org snapshot; record both URLs on the CLAIMS.md checklist lines.
7. **Owner** — the r/chessvariants post, then a day later the lichess forum, then chessvariants as a
   separate permanent submission, per `POST-DRAFT.md` "Where, and in what order".

No production code changed in this packet. No standing-position reversals. Only `docs/audits/M.md`
was created; the `POST-DRAFT.md` and TERMS edits in items 1 to 3 are follow-up work, not done here.

---

## M-4 — Analytics for a privacy-first site
Date: 2026-08-30
Packet: docs/audits/INDEX.md

### Verdict

The instrumentation is honest and the privacy position holds: `DailyCounters` is one table of
`(UTC date, allowlisted key, allowlisted source, count)` with no surrogate id, and the write path is
structurally incapable of seeing an IP, a cookie, a User-Agent, a referrer or an account id
(`CounterEndpoints.cs:58-75`, closing comment `:141-144`). That half of the packet is already
settled by **L-6** and deep **BE-3**; this report does not re-derive it. The open question this
packet owns is whether the counters answer AD.7's four questions — signups, first game, day-2
return, source mix — and the answer is: signups yes, first game yes, "return" only as *played
again* (not *came back*), and source mix only as *where a tagged landing came from*, never as
*which channel produced a player*. None of that is a bug in the code; it is the price of storing no
identifier, and it needs to be written into AD.7 as the definition of the targets rather than
discovered after the launch it is meant to measure. The one concrete instrumentation gap is that
Google-OAuth signups fire no funnel counter and carry no source at all. The admin dashboard is
correctly staff-gated on the server and leaks nothing identifying.

### Findings

#### M-4-1 — OAuth signups fire no `signup` counter and carry no source
- Severity: Medium
- Where: `ui/app/[locale]/login/page.tsx:170` fires `recordCounter(COUNTER_KEYS.signup)` only on
  the password-registration branch; `ui/app/[locale]/auth/callback/page.tsx:81-95` (the Google
  code-exchange) fires nothing. `?src=` is also lost across the round trip to `accounts.google.com`
  and back to `/auth/callback?code=…` (`resolveCampaignSource`, `ui/lib/api.ts:690-698`, resets to
  `null` on the full navigation and finds no `src` on return).
- Impact: `TrafficSummaryDto.TotalSignups` stays correct (it comes from `AspNetUsers.CreatedAt`,
  `CounterEndpoints.cs:101-104`), but `bySource` signups (`TrafficAnalytics.BuildBySource`,
  `TrafficAnalytics.cs:75-90`) systematically undercount, and the missing fraction is 100% of the
  Google cohort. AD.7's "source mix" is blind to every OAuth signup, and the dashboard's
  "browser-attributed signups vs database" reconciliation line (`analytics/page.tsx:750-755`) will
  always show a gap that is really just "these people used Google".
- Fix: return a `created: bool` on the external-code exchange response and, when true, call
  `recordCounter(COUNTER_KEYS.signup)` in the callback — accepting that its source will almost
  always be `direct`. Alternatively, state in AD.7 that OAuth signups are unattributable to a
  channel by design and read the split from `AspNetUsers` provider data instead.

#### M-4-2 — "Source mix" can attribute a landing, never a player
- Severity: Medium (definitional — for AD.7 to absorb, not a code fix)
- Where: source is stored only on `DailyCounter.Source` and only for page-view, client `signup`,
  and `learn-*` keys. It is deliberately never written to the account row (`DailyCounter.cs:19-24`;
  the whole point of scope decision 15). `RetentionAnalytics` and the cohort table
  (`RetentionAnalytics.cs:85-105`) therefore have no source dimension at all.
- Impact: the counters answer "where did views and signups originate" but cannot answer "did the
  Reddit cohort play a game or come back". AD.7 lists "source mix" beside "first game" and "D2
  return" as if they compose; they do not, because joining a source to downstream behaviour would
  require an identifier the design refuses to keep.
- Fix: write this limit into AD.7 explicitly — the source target is "tagged landings and signups by
  channel", full stop. Per-source retention is a privacy decision (a `SignupSource` column), not a
  reporting tweak, and is out of scope for this project's standing position.

#### M-4-3 — `?src=` attribution is a floor, not a count
- Severity: Low
- Where: `resolveCampaignSource` (`ui/lib/api.ts:690-698`) resolves once from
  `window.location.search`, holds the value in a module variable for the tab's life, and never
  persists it (correct — L-6 confirms this is the load-bearing privacy choice). The allowlist is 12
  channels plus `direct` (`CounterSources`, `src/KiriChess.Server/Analytics/CounterKeys.cs:89-112`;
  mirrored `ui/lib/api.ts:666-669`).
- Impact: a visitor who lands on `/?src=reddit`, closes the tab and returns the next day from a
  bookmark is `direct`; a hard refresh mid-session drops the tag; any channel not on the allowlist
  (Mastodon, Bluesky, a newsletter, a specific subreddit) collapses to `direct`. Every per-source
  number is a lower bound on that channel's real traffic.
- Fix: none to the mechanism. Before launch, add the channels actually being used to
  `CounterSources` + `COUNTER_SOURCES` (one constant and one allowlist entry each). Phrase AD.7's
  source target as "tagged landings", so a low number reads as "few links carried the tag", not
  "few people came".

#### M-4-4 — First-game and retention counters do not separate human games from bot-pool games
- Severity: Low (trigger: Phase 9f.5 `bot:` API pools become active)
- Where: AD.7 (`docs/ROADMAP.md:2092`) asks for "games between two humans". `TrafficAnalytics`
  `gamesStarted`/`gamesFinished` filter only `PlyCount > 0` (`CounterEndpoints.cs:110-118`);
  `RetentionAnalytics.BuildFacts` joins on any `db.Games` seat (`RetentionAnalytics.cs:42-49`); and
  `QueueJoin` is recorded for `bot:` pool joins too (`MatchmakingService.cs:70-76,199`).
- Impact: none today — no API bots are live and client-side Stockfish practice is not persisted as
  a `Game` — but the moment bot pools activate, bot-vs-bot games and their queue joins inflate the
  exact funnel AD.7 reads.
- Fix: when `bot:` pools go live, exclude `RatingPool LIKE 'bot:%'` from the traffic and retention
  game queries (or add a human-only variant beside them).

#### M-4-5 — The retention curve counts "played again", not "came back"
- Severity: Low (cross-ref P-3, ROADMAP 11a.1)
- Where: `RetentionAnalytics.ComputePoint` (`RetentionAnalytics.cs:170-185`) marks an account
  retained only if `LastGameAt >= SignedUpAt.AddDays(day)`.
- Impact: a returning player who queues into an empty lobby, never gets paired and leaves is
  indistinguishable from one who never returned — and during the launch window the empty lobby is
  the default. AD.7's "day-2 return" question most needs to see exactly that population. The
  `QueueJoin` / `QueueMatch` aggregate counters (`FirstSessionDto`, `TrafficDtos.cs:33-43`) are the
  compensating read, but they are site-wide, not per cohort.
- Fix: note in AD.7 / P-3 that the retention curve understates intent-to-return while the lobby is
  thin, and that `QueueMatchPercent` (searches that found an opponent) is the number to watch
  alongside it.

#### M-4-6 — AD.7 says "D2"; the instrument produces rolling D1/D3/D7/D14/D30
- Severity: Low (definition mismatch)
- Where: `RetentionAnalytics.Horizons = [1, 3, 7, 14, 30]` (`RetentionAnalytics.cs:17`), rolling
  ("played again a week *or more* after signup", scope decision 1), not calendar-day buckets.
- Impact: "D2 return" as literally worded is not computed. Rolling D1 ("played again at least a day
  after signing up") is the near equivalent and is arguably the better metric at this volume, but
  the wording in the packet and the wording in the code disagree.
- Fix: AD.7 should adopt the rolling D1/D7 definitions the dashboard already renders. A classic
  calendar-day-2 metric would need a new computation and is not worth it below a few hundred
  signups a week.

#### M-4-7 — Client and server counter allowlists are hand-mirrored with nothing enforcing it
- Severity: Low (cross-ref C-9)
- Where: `ui/lib/analytics.ts:6-20` (`COUNTER_KEYS`) and `ui/lib/api.ts:666-669`
  (`COUNTER_SOURCES`) mirror `CounterKeys.All` and `CounterSources.All` by hand; both files carry a
  "keep them in step" comment. No test references either constant (`grep` across `*.test.*` is
  empty).
- Impact: renaming or retiring a key silently drops counts (the server answers `204` for an unknown
  key, `CounterEndpoints.cs:65`) rather than failing CI — the same generated-vs-hand-written drift
  class the repo's contract tests exist to catch.
- Fix: a small Vitest (or C# fixture) asserting the TS sets are subsets of the C# constants, folded
  into the C-9 drift-guard family. Cheapest is a tiny JSON both sides import.

### Already covered

- **Privacy claim holds (no cookie / IP / UA / referrer / account id).** L-6 verdict and its
  "verifiably clean" list (`docs/audits/L.md:19-40`); deep **BE-3** summary
  (`docs/audits/C.md (section BE-3)`). Confirmed here, not re-derived.
- **Rate-limit IP keys stay out of the database.** L-6 (`L.md:40`); `RETENTION.md:27`. The
  anonymous beacon is partitioned by IP in transient limiter state only
  (`RateLimiting.cs:284-293` → `IpKey`).
- **`DailyCounters` retention (kept, never swept).** The gap the ROADMAP AD.6 note flagged is
  closed and documented — `docs/decisions/RETENTION.md:26` and §4 item 4 (`:129-131`).
- **CancellationToken not threaded through the balance/retention handlers; leaderboard warmup;
  rating double-apply.** Deep **BE-3** findings 1–3 (`BE-3.md:28-76`). Not repeated.
- **`?src=` producing duplicate indexable URLs.** M-1 SEO canonicalisation finding
  (`docs/audits/M.md:56-63`). That is the crawler's view; this packet is the counter's view.
- **Counter test coverage ("11 cases, not thin").** C-10 (`docs/audits/C.md:189-191`).
- **"Views can never mean people" rationale.** Phase 8a.5 / AD.6, restated on the dashboard
  (`analytics/page.tsx:620-625`). Confirmed.

### Notes (not findings)

- **Admin gating is correct.** All three reads are `.RequireAuthorization(ModerationPolicies.Admin)`
  (`AnalyticsEndpoints.cs:40`, `CounterEndpoints.cs:128`); the page double-gates with
  `StaffOnly` + `RequireAuth` (`analytics/page.tsx:1092-1093`) but the server is authoritative. The
  Phase 8.3 shared-secret bearer was removed (`AnalyticsEndpoints.cs:12-16`).
- **No PII in any response.** Every field of `TrafficSummaryDto` / `RetentionSummaryDto` /
  `BalanceSummaryDto` is a count, rate or date; `UserFact.UserId` exists only to join in memory and
  is never serialised (`RetentionDtos.cs:22-23`). At very low volume a "by day" cohort row of one
  signup is linkable to a person by the operator — who is the sole controller, so no finding.
- **`register` is a page key** (`CounterKeys.cs:52`) fired manually when the register tab is shown
  (`login/page.tsx:125`), so one visitor on `/login` who opens that tab counts as two page views.
  "Views are events, not people" is disclaimed on the dashboard; acceptable.
- **`QueueJoin` / `QueueMatch` are always `source = direct`** by construction
  (`MatchmakingService.cs:188-201`) — the server cannot know the arrival query string. Correct, and
  documented; it just means the first-session funnel has no source breakdown.

### Action list

1. **Owner (AD.7 itself):** write the three launch numbers against the definitions the
   instrument actually produces — rolling D1/D7 not classic D2 (M-4-6); "source mix" = tagged
   landings and signups by channel, not per-channel player quality (M-4-2); "first game" = first
   game vs any queued opponent (M-4-4); and note that the retention curve understates
   intent-to-return while the lobby is thin, with `QueueMatchPercent` as the companion number
   (M-4-5).
2. fire the `signup` counter on the OAuth callback for newly-created accounts (add a
   `created` flag to the exchange response), or record in AD.7 that OAuth signups are channel-
   unattributable — M-4-1.
3. add the channels actually being used to `CounterSources` /
   `COUNTER_SOURCES`, one constant and one allowlist entry each — M-4-3.
4. bind the TypeScript counter-key and source lists to the C# constants,
   folded into the C-9 drift guards — M-4-7.
5. exclude `bot:` games from the traffic and
   retention queries — M-4-4.

---

## M-5 — Mail and push
Date: 2026-08-30
Packet: docs/audits/INDEX.md

### Verdict

The headline holds: this system is transactional-only. It sends exactly four emails — account
confirmation, password reset, "your challenge was accepted" (PairingFound), "your opponent wants a
rematch" (RematchOffered) — and two web-push messages mirroring the last two. There is no digest,
no "we miss you", no newsletter, no campaign table, no list. `EmailMessage` is single-recipient by
construction (`EmailMessage.cs:8`) and the sender comments make "no bulk mail" a reviewable
invariant. The two reach-back mails are genuine service messages (a counterparty acted; you have a
pending decision), which L-6 already assessed as ePrivacy Art. 13 service messages where opt-out is
the correct default — this packet does not reopen that. Both carry a working footer unsubscribe and
a per-category profile toggle; confirmation and password reset correctly carry no opt-out. The
unsubscribe token is a keyed HMAC over (userId, category), constant-time compared, no expiry — a
proportionate design for something that can only flip a mail checkbox (`UnsubscribeTokenService.cs`).
Push is real opt-in: browser permission plus an explicit toggle, revocable, dead endpoints pruned on
410, no marketing payloads, fail-closed without VAPID keys.

The gaps are all deliverability and channel-consistency, not marketing-creep. The reach-back mail
ships no `List-Unsubscribe` header, so mailbox providers get no reputation signal and render no
native unsubscribe button — and the endpoint behind the footer link could not answer an RFC 8058
one-click POST even if the header were added. Web push bypasses the category preferences entirely,
so opting out of one email category still leaves its push firing. Bounce/complaint handling and the
`p=none` DMARC posture are owned elsewhere but bear on sender reputation, which is the M-5 stake.

### Findings

#### M-5-1 — Reach-back mail carries no List-Unsubscribe / List-Unsubscribe-Post header
- Severity: Medium (trigger: PairingFound / RematchOffered volume to Gmail, Yahoo and Outlook
  recipients once the site is public and the lobby has traffic)
- Where: `src/KiriChess.Server/Infrastructure/Email/ResendEmailSender.cs:47-54` — the Resend payload
  is `from / to / subject / html / text` only, no `headers`. The footer link built at
  `EmailTemplates.cs:107-119` is the sole opt-out mechanism. The one-click endpoint at
  `src/KiriChess.Server/Infrastructure/Email/NotificationEndpoints.cs:16-34` exists but nothing
  emits a header pointing at it.
- Impact: mailbox providers read `List-Unsubscribe` (RFC 2369) and
  `List-Unsubscribe-Post: List-Unsubscribe=One-Click` (RFC 8058) as both a deliverability/reputation
  signal and the source of the native "Unsubscribe" control shown next to the sender name. Without
  them, game-event mail (which reads as promotional to a spam filter — a single prominent CTA, no
  prior-message thread) is more likely to land in spam, and the recipient is pushed to hunt for the
  footer link instead of the one tap their mail client offers. Gmail/Yahoo's 2024 sender guidance
  makes this expected behaviour the moment send volume is non-trivial.
- Fix: for the two governed categories, thread the already-built `unsubscribeUrl` into `EmailMessage`
  and add to the Resend payload `headers: { "List-Unsubscribe": "<{url}>, <mailto:contact@kirichess.com?subject=unsubscribe>", "List-Unsubscribe-Post": "List-Unsubscribe=One-Click" }`.
  Leave confirmation and password reset headerless as they are today. Pair with M-5-2.

#### M-5-2 — The unsubscribe endpoint cannot satisfy an RFC 8058 one-click POST
- Severity: Low (compounds M-5-1 — the header is only useful if the endpoint answers the request it
  names)
- Where: `NotificationEndpoints.cs:16-19` binds a JSON `UnsubscribeRequest` body
  (`{ userId, category, token }`). An RFC 8058 one-click is `POST <List-Unsubscribe URL>` with a
  form body of exactly `List-Unsubscribe=One-Click` and no JSON. The URL itself
  (`UnsubscribeLink.cs:15`) already carries `userId`, `category` and `token` as query params.
- Impact: a mail client's native one-click button would POST a form body to that URL and get a 400,
  because the handler only knows how to read JSON. The header would be a promise the server breaks.
  The frontend `/unsubscribe` page works only because it re-reads the query params in JS and issues
  its own JSON call (`ui/app/[locale]/unsubscribe/page.tsx:22-34`).
- Fix: have the handler read `userId` / `category` / `token` from the query string and accept a
  bodyless or `application/x-www-form-urlencoded` POST. Keep accepting the JSON shape for the
  existing frontend caller, or migrate the frontend to the query-param form so there is one code
  path.

#### M-5-3 — Web push ignores the per-category notification preferences; the only user control is a global switch
- Severity: Low (trigger: a user who unsubscribed from one category still receives it by push; only
  two categories exist today, so the blast radius is small — but the mental model is already wrong)
- Where: `src/KiriChess.Server/Infrastructure/Push/PushNotificationService.cs:47-58` sends to every
  subscription with no `NotificationPreferenceService` consultation.
  `src/KiriChess.Server/Games/PairingNotificationService.cs:67` fires the push *before and outside*
  the `IsSubscribedAsync` gate that guards the matching email at `:74`;
  `RematchNotificationService.cs:80` gates the mail but the push for that event
  (`PushNotificationService.NotifyRematchOffered`, fired from `GameHub`) is never gated. The only
  user-facing control is the browser-permission toggle in `ui/components/PushNotificationToggle.tsx`,
  which is all-or-nothing.
- Impact: "unsubscribe" from the email footer link or the profile toggle silences the email but not
  the push notification for the same event. A user cannot decline rematch pings while keeping
  pairing pings. `PairingNotificationService.cs:58-61` frames this as intentional ("a browser push
  subscription is its own opt-in"), but subscribing to the channel is not consent to every category
  carried on it — and the site presents "notification preferences" as one concept spanning both.
- Fix: smallest — have `PushNotificationService.NotifyChallengeAccepted` / `NotifyRematchOffered`
  call `NotificationPreferenceService.IsSubscribedAsync` for the corresponding
  `NotificationCategory` before sending, so the single stored preference governs both channels.
  Alternatively surface the two category toggles inside `PushNotificationToggle` too. Either keeps
  one model of "notification preferences" rather than two.

#### M-5-4 — No bounce/complaint feedback loop and DMARC at p=none: the sender-reputation angle
- Severity: Low (the cost and DNS halves are owned elsewhere; this finding records the reputation
  stake the packet asks about)
- Where: `docs/ops/DEPLOYMENT.md:596-599` ("No bounce or complaint handling. Resend's webhooks are
  not consumed ... the address is never suppressed"); `ResendEmailSender.SendAsync` never checks a
  suppression list before sending; `_dmarc.kirichess.com` is `v=DMARC1; p=none; ...`
  (`docs/ops/PROVIDER-ACCOUNTS.md:97`).
- Impact: repeated confirmation/reset sends to a hard-bouncing or spam-complaining address are
  invisible to the app and never suppressed, which erodes the shared sending-domain reputation that
  every future user's confirmation mail also depends on — the one asset I-9 notes takes weeks, not a
  weekend, to rebuild. `p=none` means a receiver seeing a spoof of `noreply@mail.kirichess.com` is
  told to do nothing about it.
- Fix: consume Resend's `email.bounced` and `email.complained` webhooks into a suppression check
  before enqueue (this also closes I-9-1's delivery-failure half and gives a durable signal the
  in-process queue lacks); advance DMARC to `p=quarantine` on B-4-3's schedule. Both already have
  owners — M-5 only records that reputation, not just cost or DNS hygiene, is why it matters for
  mail.
- Cross-ref: I-9-1 (free-tier cap + non-durable queue), B-4-3 (DMARC `p=none` with no review date),
  I-7 (SPF/DKIM/DMARC DNS depth). SPF and DKIM are present and verified
  (`PROVIDER-ACCOUNTS.md:90-92`).

#### M-5-5 — Queue drop-policy doc contradicts the code; reach-back mail has no Reply-To
- Severity: Low
- Where: `src/KiriChess.Server/Infrastructure/Email/EmailOptions.cs:14` says "the oldest-arriving
  write is dropped and logged", but `QueuedEmailSender.cs:28-33` uses
  `BoundedChannelFullMode.DropWrite`, which drops the *incoming* message — confirmed by
  `tests/KiriChess.Server.Tests/TransactionalEmailTests.cs:234-248` ("First" is kept, "Second" is
  dropped). Separately, every message is `From: noreply@mail.kirichess.com`
  (`docs/ops/DEPLOYMENT.md:578`) with no `Reply-To`.
- Impact: the doc error could mislead a future operator debugging which signup was lost under load
  (it is the newest, not the oldest). The `noreply` From is standard for confirmation/reset, but the
  two reach-back mails literally invite a reply ("wants to play again") and a reply currently reaches
  nobody.
- Fix: correct the `EmailOptions` comment to "the newest message is dropped". Optionally set
  `Reply-To: contact@kirichess.com` on the PairingFound and RematchOffered categories only.

### Already covered

- **Web push subscriptions survive account erasure; push subs and notification prefs missing from
  the data export** — `docs/audits/L.md` L-2-1 (erasure) and L-2 export findings (`:124-136`,
  action items 1-2). Not re-reported. The `WebPushSubscription` → `ApplicationUser` cascade
  (`KiriChessDbContext.cs:292-293`) never fires because erasure tombstones the user row rather than
  deleting it (`AccountDeletionService.cs:80-107`), and that service's explicit delete list omits
  `WebPushSubscriptions` — exactly L-2-1.
- **Web push and game-notification mail appear in no PRIVACY / ROPA entry; push endpoint URL is a
  third-country transfer to an uncontracted processor** — `docs/audits/L.md` L-6-2. The opt-out
  default for the two reach-back mails was assessed there as ePrivacy-correct; M-5 does not reopen
  it.
- **`/unsubscribe` is absent from `robots.ts` disallow, sets no `noindex`, and fires the state
  change automatically on page load** — `docs/audits/M.md` M-1-3 (confirmed still unaddressed:
  `ui/app/robots.ts:28-41` has no `/unsubscribe` entry). M-5 adds only that the endpoint should also
  accept the one-click POST shape (M-5-2); the indexability/prefetch angle is M-1's.
- **Resend 100-emails/day free-tier cap breaks confirmation mail on a launch spike; in-process queue
  is non-durable; no bounce webhook** — `docs/audits/I.md` I-9-1, `DEPLOYMENT.md` §8.4. M-5-4
  references these for the reputation framing only.
- **DMARC has sat at `p=none` since 2026-08-19 with no owner or review date** — `docs/audits/B.md`
  B-4-3 (calendar item to read aggregate reports and move to `p=quarantine`).
- **SPF/DKIM/DMARC DNS records and the Google OAuth redirect on the API host** — `docs/audits/INDEX.md`
  **I-7** (not yet run). M-5 confirms SPF + DKIM resolve and are Resend-verified
  (`PROVIDER-ACCOUNTS.md:90-92`, `:101-107`).
- **Day-2 retention strategy, weekly-play-hour, what anonymous analytics can answer about mail/push
  engagement** — `docs/audits/INDEX.md` **P-3** (not yet run).

### Action list

Ordered. No standing-position reversals (transactional-only holds; opt-out default for reach-back
mail stays, per L-6).

1. M-5-1 + M-5-2 together: add `List-Unsubscribe` / `List-Unsubscribe-Post` headers to
   the two governed categories and teach `POST /api/notifications/unsubscribe` to read its params
   from the query string and accept a form/bodyless POST. One change, one test asserting the header
   is present on PairingFound/RematchOffered and absent on confirmation/reset.
2. M-5-3: gate `PushNotificationService.Notify*` on `NotificationPreferenceService`
   for the matching category, so one stored preference silences both channels. Add a
   `PushNotificationServiceTests` case for "unsubscribed category → no push".
3. M-5-5: fix the `EmailOptions` drop-policy comment; decide on `Reply-To` for the two
   reach-back categories.
4. **Owner** — M-5-4: on B-4-3's schedule, read the DMARC aggregate backlog and move to
   `p=quarantine`; decide whether to wire Resend's bounce/complaint webhooks now (also closes
   I-9-1's delivery half) or record the manual recovery path as the accepted posture for launch.
5. **Owner (I-7)** — verify SPF/DKIM/DMARC end to end with a mail-tester 10/10 before the
   Cloudflare Access gate comes off, per `DEPLOYMENT.md` §8.4. Out of this packet; noted so it is
   not lost.

No production code changed in this packet. Only `docs/audits/M.md` was created.

---

## M-6 — Developer ecosystem

Date: 2026-08-30
Packet: docs/audits/INDEX.md

### Verdict

The developer surface is small and mostly honest. `/developers` is one short page
(`ui/app/[locale]/developers/page.tsx`, strings in `ui/messages/en.json:1091-1115`) plus a profile
card (`ui/components/ApiTokensCard.tsx`). Auth, scopes, the "no admin/2FA claim" promise, the
server-side-only CORS stance, and the three rate-limit numbers are all accurate against the code:
`ApiRead` 300/min, `ApiPlayCreate` 12/min, `ApiPlayMove` 60 burst / 30 per second
(`src/KiriChess.Server/Infrastructure/RateLimiting.cs:146-149`) match the page verbatim, and the
"moves are no faster than a human" commitment from ROADMAP 9f.5 decision 3 holds. The OpenAPI link
(`/openapi/v1.json`) resolves at runtime to the correctly curated `public` document — `/api/v1`
paths only, first-party routes filtered out (`Program.cs:758-762`, `:839-847`).

The weaknesses are all in the play path and in completeness, not in safety. The page names a preset
that does not exist; its hub walkthrough omits the one method (`JoinGame`) a bot must call to
receive moves; there is no statement of what the `/api/v1` stability promise actually is; and there
is no example bot anywhere in the repo. For a feature whose entire rationale (ROADMAP.md:1204) is
"third parties write clients and bots nobody funded", the missing runnable example is the biggest
gap. Everything here is a documentation-quality fix; none of it needs a program, a tier, or a
partner scheme.

### Findings

#### M-6-1 — `/developers` tells bot authors to queue a preset that does not exist
- Severity: Medium
- Where: `ui/messages/en.json:1102` (`developers.playPara`): "JoinQueue with an official preset
  (bounty1, flagfall, …)"
- Impact: The rated pools are `bounty1, burn1, steal1, steal2, burn2, steal3`
  (`config/kirichess.rules.json:68-165`, entries with `"rated": true`; surfaced as
  `RatingConstants.OfficialPools` = `KiriChessConfig.RatedPoolKeys`). There is no `flagfall` pool.
  A bot author who copies the documented example into `JoinQueue` hits
  `RatingConstants.MapJoinQueueArg` → null → `HubException` from
  `GameHub.JoinQueue` (`src/KiriChess.Server/Games/GameHub.cs:200-203`), with no hint that the
  preset name was the problem. The one concrete preset the page does give (`bounty1`) is real, so
  the error looks intermittent.
- Fix: replace the parenthetical with the real pool ids, or drop the list and point at
  `GET /api/v1/leaderboards/bot/{pool}` / the rules config as the source of truth. Ideally add a
  greppable test that every preset named in the `developers` catalogue is in
  `KiriChessConfig.RatedPoolKeys`.

#### M-6-2 — hub play walkthrough is missing `JoinGame`; a bot cannot complete a game from what is documented
- Severity: Medium
- Where: `ui/messages/en.json:1105` (`developers.hubMethods`): "Player methods: JoinQueue,
  SubmitMove, Resign, and the MatchFound / MovePlayed events."
- Impact: After `MatchFound`, a client must call `JoinGame(gameId)` — which returns the
  `GameSnapshotDto` and subscribes the connection to that game's group — before it receives any
  `MovePlayed` event or can meaningfully `SubmitMove` (`src/KiriChess.Server/Games/HubContract.cs:59`,
  and `GameHub.JoinGame`). The documented set (JoinQueue / SubmitMove / Resign) does not describe a
  working game loop, and the page never states the sequence MatchFound → JoinGame →
  MovePlayed/SubmitMove. Draw handling (`OfferDraw`, `RespondDraw`) and `LeaveQueue` are also
  undocumented, so a bot cannot respond to a draw offer. The HTTP alternative
  (`POST /api/v1/games/{id}/moves`) is not mentioned as a transport for hub-matched games either.
- Fix: document the connect → JoinQueue → MatchFound → JoinGame → loop(MovePlayed / SubmitMove) →
  GameEnded sequence explicitly, name `OfferDraw`/`RespondDraw`/`LeaveQueue`, and note that moves
  in a hub-matched game can also be posted over `/api/v1/games/{id}/moves`.

#### M-6-3 — no stability or breaking-change statement anywhere on `/developers` or in the public doc
- Severity: Medium
- Where: `ui/messages/en.json:1095` (`subheading`: "The published promise is /api/v1 only") and
  `:1111` (`specPara`: "The production document is /api/v1 only"); `docs/api/openapi-public.json:5`
  (`info.description`).
- Impact: The page asserts a "promise" but never says what it is. ROADMAP 9f.5 decision 1
  (`docs/ROADMAP.md:1234-1238`) commits to **path versioning** — breaking changes ship as a new
  `/api/vN`, `/api/v1` is not mutated underneath a caller — and decisions 1-4 are marked permanent
  ("once a stranger's client depends on it, changing it breaks their code",
  `docs/ROADMAP.md:1230-1232`). None of that reaches the developer. A reader cannot tell whether
  `/api/v1` is frozen, best-effort, or beta. "Breaking-change honesty" (the packet's own phrase) is
  currently neither a guarantee nor an honest "expect breakage".
- Fix: one or two sentences on `/developers`: what `/api/v1` guarantees (shape stays as documented;
  breaking changes go to `/api/v2`; additive changes may land without notice), whether there is any
  deprecation window, and an email/issue channel for API changes. Keep it to what the owner will
  actually honour.

#### M-6-4 — no example bot exists in the repo or is linked from `/developers`
- Severity: Low (trigger: the first external developer who tries to build a client)
- Where: no `examples/`, `samples/`, or `bots/` directory in the tree; `developers.sourcePara`
  (`ui/messages/en.json:1113`) links only the repo root. `simulator/` and `loadtest/` drive the
  engine and hub through internal C# harnesses, not through a `kc_` token against `/api/v1`.
- Impact: The stated purpose of the whole feature is ecosystem growth on the Lichess analogy
  (`docs/ROADMAP.md:1204-1206`). The single highest-leverage artifact for that — a short runnable
  client that a stranger can copy — does not exist. The `curl` snippets on the page cover auth and
  one create call but stop well before a game loop, and M-6-1/M-6-2 mean following them does not
  produce a played game.
- Fix: add `examples/bot-quickstart/` with a shell script (token → `POST /api/v1/games` →
  poll `GET /api/v1/games/{id}` + loop `POST /moves`) and one small scripting-language client for
  the hub path (negotiate with `access_token=kc_…` → JoinQueue → JoinGame → move loop). Link it
  from the `sourceHeading` section. ~50 lines each; it is documentation, not a product.

#### M-6-5 — `openapi-public.json` is a poor standalone artifact: no `security` requirement, no `servers`, self-referential description
- Severity: Low
- Where: `docs/api/openapi-public.json:2174-2181` (`securitySchemes.bearerAuth` declared) — but no
  operation carries a `security` block and there is no document-level `security`; `servers` is
  emptied (`Program.cs:870`); `info.description` (`:5`) ends "This document is generated — see
  docs/api/openapi-public.json", pointing a third party at a repo path.
- Impact: A developer importing the published document into Postman / `openapi-generator` /
  `openapi-typescript` gets a client with no base URL and no auth wired, despite every operation
  documenting a 401. The description sends them to a file they may not have. C-9 confirms the doc
  is drift-gated by `OpenApiContractTests`, so the curation (paths, schemas) is correct — this is
  about the doc being usable on its own.
- Fix: for the public document, add document-level `security: [{ "bearerAuth": [] }]`, set
  `servers` to the production API origin, and rewrite `info.description` to point at
  `https://kirichess.com/developers` rather than the repo path. Regenerate and let the contract
  test re-baseline.

#### M-6-6 — `/developers` omits the token lifecycle facts (10-token cap, no expiry, where to revoke)
- Severity: Low
- Where: `ui/messages/en.json:1091-1115` — the page never mentions the cap, expiry, or a link to
  the profile card. The cap is `ApiTokenClaims.cs` `MaxActivePerUser = 10`
  (`src/KiriChess.Server/Auth/ApiTokenClaims.cs:28`); `ApiTokenService.IssueAsync`
  (`:25-62`) sets no `ExpiresAt` and neither the API nor `ApiTokensCard` offers a way to set one,
  so tokens are permanent until revoked; revocation is profile → "API tokens" card only.
- Impact: The developer-facing page defers everything about token management to a card it does not
  link. `ApiTokensCard`'s footer note carries the cap (`en.json:950`) but the page a developer
  reads first does not, and "Issue a token on your profile" (`subheading`) is not a link.
- Fix: add a sentence to the Authentication section — cap of 10 active tokens, tokens do not
  expire, revoke on the profile page — and make "profile" a real `Link` to `/profile`.

#### M-6-7 — page implies `POST /api/v1/games` starts a playable game; it only creates a solo/self-play game
- Severity: Low
- Where: `ui/messages/en.json:1101-1102` (`playHeading` "Unrated HTTP, rated hub" / `playPara`);
  `curl -X POST … /api/v1/games -d '{"preset":"bounty1"}'` in `page.tsx:80-83`;
  handler `src/KiriChess.Server/Games/PublicApiEndpoints.cs:286-306`
  (`black = req.OpponentId ?? me.Value`).
- Impact: With no `opponentId`, the created game has the caller as both White and Black — a
  self-play board for engine testing, per `CreateGameRequest`'s own comment
  (`src/KiriChess.Server/Games/GameDtos.cs:14-20`), and `opponentId` pointing at anyone else is
  rejected (`PublicApiEndpoints.cs:286-290`). The page never says this, so a new author reasonably
  expects `POST /games` to matchmake. Separately, `openapi-public.json:835-870` marks
  `opponentId`, `preset` and `rated` all `required`, so the documented `curl` body
  (`{"preset":"bounty1"}`) fails strict schema validation. Cross-ref C-9 (schema/example drift) and
  L-11-3 (the challenge path as the only bot-vs-human route).
- Fix: state plainly that HTTP `/api/v1/games` is for solo / play-both-sides engine testing, that
  matched play (bot pool or a human via challenge) goes through the hub / `/api/v1/challenges`, and
  fix the `curl` body to satisfy the published schema (or relax the schema's `required` list to
  match the handler).

### Already covered

- `developers/page.tsx` redefines `API_BASE` instead of importing the shared constant —
  deep/`FE-4-3` (`docs/audits/C.md (section FE-4)`), also `docs/audits/P.md:217`.
- `ApiTokensCard` "Copied" label never resets; effect lacks a `cancelled` guard —
  deep/`FE-2` #3 (`docs/audits/C.md (section FE-2)`), `docs/audits/P.md:216`.
- `PublicApiEndpoints` move/resign path duplicates `GameEndpoints` and bypasses the distributed
  executor (bot gets a 404 off the owning instance) — `docs/audits/C.md:62-84`,
  deep/`BE-2` (`docs/audits/C.md (section BE-2)`), `IMPROVEMENT_PLAN.md:130-133`.
- TERMS §5 bans engine play with no bot carve-out while `/developers` invites it; `/api/v1/challenges`
  lets a token play a human unrated — `docs/audits/L.md:61-78`.
- No kill switch disables the public `/api/v1` write surface — `docs/audits/S.md:88-99`.
- `/developers` licence line omits the name/logo/wordmark reservation `/about` and `/terms` carry —
  `docs/audits/P.md:104-109`.
- The GitHub URL linked from `/developers` (and footer, `/about`, NOTICE, CITATION.cff) 404s —
  `docs/audits/O.md:60`, `docs/audits/L.md:38`.
- `/developers` missing from the axe accessibility scan list — `docs/audits/L.md:90-92`,
  `docs/audits/M.md` (metadata/OG).
- `GET /api/v1/account/playing` caps the live scan at 200 and filters client-side —
  `docs/audits/I.md:79-81`, `I.md (I-1)` AS-9.
- API-token issue/list/revoke, ten-token cap, scheme isolation, scope enforcement, logout-all
  behaviour are test-covered by `PublicApiTests` — `docs/audits/C.md:182-186`.
- `openapi.json` / `openapi-public.json` are drift-gated by `OpenApiContractTests`;
  `gen:api:check` gates the TS types — `docs/audits/C.md:10,133`.
- Scope hashing / revocation / rate-limit-by-token-id security belongs to **S-4** (unrun as
  of this date — no `docs/audits/readiness/` or `S.md` exists yet). This packet does not assess
  it.

### Action list

1. fix `/developers` copy: real preset ids (M-6-1), the MatchFound → JoinGame → loop
   sequence plus draw/leave methods (M-6-2), a token-lifecycle sentence with a `/profile` link
   (M-6-6), and the "HTTP games are solo" clarification with a schema-valid `curl` body (M-6-7).
   All are `ui/messages/en.json` + `page.tsx` edits; the other five locale catalogues need the same
   keys re-filled (`npm run gen:locale -- <locale> --fill`).
2. add a stability/versioning paragraph once the owner confirms the terms (M-6-3).
3. **Owner then** — decide the shape of an `examples/bot-quickstart/` (HTTP + hub), then an
   write the two small clients and links them from the page (M-6-4).
4. regenerate the public OpenAPI document with a global `security` block, real
   `servers`, and a non-self-referential description; re-baseline `OpenApiContractTests` (M-6-5).
5. add a test asserting every preset named in the `developers` catalogue exists in
   `KiriChessConfig.RatedPoolKeys` (guards M-6-1 from recurring).
6. No standing-position reversals. No dev-relations program, paid tiers, or partner scheme.

