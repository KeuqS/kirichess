# Audit index — what to check

One packet per check. The deliverable is findings, not code.

Packet reports are grouped by category into one file each: [L.md](L.md), [O.md](O.md), [C.md](C.md),
[S.md](S.md), [I.md](I.md), [P.md](P.md), [M.md](M.md), [B.md](B.md), [funding.md](funding.md)
(`$1`–`$5`). Finding ids (`C-5-1`, …) are unchanged.

KiriChess is an **open-source, free, donation-funded** chess variant and hosted service. There is
no company, no paywall, and no ads. The software is Apache-2.0; the rules manual is CC BY 4.0; the
name is reserved ([NOTICE](../../NOTICE), [CLAIMS.md](../game/CLAIMS.md)). The hosted site at kirichess.com is
a hobby service run by one person in Belgium, in the Lichess shape: everything playable stays free,
and money if any is patronage, not a product. Capacity thinking still uses Lichess-scale concurrent
play as the stress case, because the game is real-time and the clock is the win condition — that is
an engineering ceiling, not a business plan.

This file is **check** work. The launch queue in [ROADMAP.md](../ROADMAP.md) (Todo `#1`–`#18`) is
**do** work. Do not mix them.

---



## How to run a packet

1. Read the standing positions below before opening source. Do not re-argue them.
2. Read any existing report named in the packet. **Never re-report** a finding those already
  carry; one-line cross-reference and move on.
3. Cite `file:line` or it did not happen. Findings that could appear in any repo are filler.
4. Severity: Critical (hurts a stranger or loses data) / High / Medium / Low (trigger-based, name
  the trigger).
5. Fix in 1–3 sentences. No exploit code, no attack walkthroughs, no payloads.
6. Append the report as a section in the category file `docs/audits/<LETTER>.md`
  (`funding.md` for `$`). Do not change production code in the same turn unless asked.

---



## Standing positions (not tasks)

These are policy. A packet that “fixes” them is out of scope.


| Position                       | Meaning | Checked |
| ------------------------------ | ----------------------------------------------------------------------------------------------------------------------------------------- | ------- |
| **Free forever**               | No paywall, no ads, no freemium (FS.1, FS.2, FS.5). Donations exist (`[/donate](../../ui/app/donate/page.tsx)`); they do not unlock play. | ✅ |
| **Cosmetics only**             | If patronage ever grows a badge or a skin, it must never touch rating, pairing, clocks, or pools (FS.6). | ✅ |
| **No correspondence**          | Time is the win condition. An asynchronous KiriChess is a different game. Liquidity problems are solved without it (Phase 11). | ~ |
| **Individual controller**      | Michiel Gekiere, Belgium, `privacy@kirichess.com`. No company, no DPO, no Art. 27 representative. [PRIVACY.md](../legal/PRIVACY.md) §1. | ✅ |
| **Age 16, declared**           | Checkbox, no date of birth, no geo-split. | ✅ |
| **Games and ratings kept**     | Indefinite retention; erasure anonymises. A game is a two-party record. [RETENTION.md](../decisions/RETENTION.md). | ✅ |
| **Sanctions survive deletion** | So a ban cannot be laundered by re-registering. | ✅ |
| **Name not licensed**          | Apache-2.0 does not grant trademark rights. Forks must not ship as “KiriChess”. | ✅ |
| **Redis unprovisioned**        | Single Fly machine until 8.4. Upstash is not a processor yet. | ✅ |
| **Tournaments parked**         | 9f.1 is not launch-gated. Do not start it. | ✅ |
| **Patreon later**              | FS.4 waits on traction. Recurring paid *tiers* are the grey-to-red legal edge (address, remuneration), not Stripe one-off donations. | ✅ |


Legal documents on the site say they were **not reviewed by a lawyer**. That is honest, not a defect
to “close” by rewriting them in a more confident voice.

---



## Reports already written

Category files hold the packet reports. Readiness reports are under `docs/audits/readiness/`.
Do not re-open a finding those files already carry.

Read [AUTH.md](../decisions/AUTH.md), [RETENTION.md](../decisions/RETENTION.md), [DEPLOYMENT.md](../ops/DEPLOYMENT.md),
[PROVIDER-ACCOUNTS.md](../ops/PROVIDER-ACCOUNTS.md), and `docs/legal/*` before claiming a gap those files
already name.

---



## 1. Legal, IP, and regulatory — 12 packets

Proportionate to a one-person EU hobby host with public users, not to a company legal department.
The job is: the documents match the code, processors are named, rights work, and we do not claim
enforcement we do not do.


| ID | Packet | Read first | Check | Done |
| -------- | ------------------------------ | ------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ | ---- |
| **L-1** | Legal document fidelity | [TERMS.md](../legal/TERMS.md), [PRIVACY.md](../legal/PRIVACY.md), [ROPA.md](../legal/ROPA.md), `/terms`, `/privacy` | Every processing activity, processor, retention line, and “what the site does” claim vs code. `LegalDocumentDriftTests` is a floor. | ✅ |
| **L-2** | GDPR rights and data lifecycle | Readiness **PRIV**; [RETENTION.md](../decisions/RETENTION.md); `AccountDeletionService`, `AccountExportBuilder` | Export, anonymisation, DSAR mailbox, dormant accounts. Extra vs PRIV: sanctions-survive-deletion vs Art. 17; public games as personal data; API tokens in export/erasure. | ✅ |
| **L-3** | Processors, transfers, ROPA | [DPA-CHECKLIST.md](../legal/DPA-CHECKLIST.md), PRIVACY §4, ROPA §4, Fly secrets, `infra/` | Live stack vs tables: Fly, Neon, Cloudflare (+ R2 when dumps exist), Resend, Sentry, Stripe, Better Stack, Google (independent controller), future Upstash. Prepare the delta; the owner files the DPAs. | ✅ |
| **L-4** | Age and children | TERMS §2, PRIVACY §9 | 16 checkbox, no DOB. Confirm UGC surface (display names, reports, PGN — no uploads/chat today) so child-safety claims stay honest. | ✅ |
| **L-5** | Illegal content / notices | TERMS, moderation, public pages | What a tiny EU host actually owes: a way to report abuse, a contact, and that TERMS matches enforcement. Do not import VLOP/DSA corporate programmes. | ✅ |
| **L-6** | ePrivacy, cookies, tracking | Analytics counters, Turnstile, Sentry, Cloudflare, Stripe, web push, Google OAuth | `DailyCounters` claim: no cookie, no IP, no UA, no account id. Consent banner: needed or correctly absent. | ✅ |
| **L-7** | Donations vs remuneration | FS.3/FS.4/FS.5, `/donate`, PRIVACY §1 | Stripe donations are patronage, not payment for play. Flag only if the site starts looking like a paid service (address duty, VAT). No “form an ASBL” recommendation unless the owner asks; an entity does not buy privacy (ROADMAP FS.4). | ✅ |
| **L-8** | Accessibility as a duty | [ACCESSIBILITY.md](../decisions/ACCESSIBILITY.md), [NVDA-PASS.md](../plans/a11y/NVDA-PASS.md) | Engineering is **P-5**. This packet only: whether the public site has a legal a11y exposure worth a lawyer’s hour, given a free hobby service. | ✅ |
| **L-9** | IP, trademark, attribution | [CLAIMS.md](../game/CLAIMS.md), [LICENSE](../../LICENSE), [NOTICE](../../NOTICE) | Mechanics are not copyrightable. CC BY on the manual, Apache on the code, name reserved. Public repo as timestamp. Forks using the name. EUIPO is optional and later. | ✅ |
| **L-10** | Stockfish / GPL isolation | `ui/public/STOCKFISH.LICENSE`, `ui/lib/bot.ts`, NOTICE | Separate GPL program (Worker), not relicensed into Apache. Hunt other copyleft that would taint a fork. | ✅ |
| **L-11** | Fair-play ToS vs enforcement | TERMS §5, detection + sanctions code | Do not claim in TERMS what the detectors and mods cannot actually do. Overclaim is the finding. | ✅ |
| **L-12** | Counsel | TERMS/PRIVACY status line | Belgian counsel for TERMS/PRIVACY, donations, and the name — when the owner wants it. This does not close without counsel. | ✅ |


---



## 2. Open source and community — 7 packets, whole table in one run

This *is* the distribution and succession story. Optimise for a stranger who clones the repo and
for a fork that must rename.


| ID | Packet | Read first | Check | Done |
| ------- | ---------------------------- | --------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------- | ---- |
| **O-1** | License stack | LICENSE, NOTICE, README, CONTRIBUTING, `CITATION.cff` | Apache vs CC BY vs trademark vs GPL. The four files agree. Third-party notices complete. | ✅ |
| **O-2** | Contributor licensing | [CONTRIBUTING.md](../../CONTRIBUTING.md) | No CLA by choice. PRs license software Apache-2.0 and rules-manual edits CC BY 4.0. Is CONTRIBUTING enough, or is a DCO/PR checkbox missing? Do not invent a CLA. | ✅ |
| **O-3** | Community health | `CODE_OF_CONDUCT.md`, `SECURITY.md`, `.github/ISSUE_TEMPLATE/`, PR template | Security reporting works now that the repo is public. CoC with one maintainer: what is enforceable. | ✅ |
| **O-4** | Public-repo hygiene | `.gitignore`, `.gitleaks.toml`, `.env.example`, git history | Secrets, tokens, private comments, staging hostnames, Neon project ids, mailmap. History as well as HEAD. | ✅ |
| **O-5** | Forkability | README, docker compose, NOTICE trademark paragraph | A fork can run an instance. What they must rename. Compose path vs Fly-only assumptions. How to attribute the variant. | ✅ |
| **O-6** | SBOM / license compatibility | Overlaps **I-11**; this packet is *license*, that one is *vulns* | Transitive copyleft besides Stockfish (`dotnet` + `ui/` + `e2e/`). | ✅ |
| **O-7** | Contributor UX | README, CONTRIBUTING, `.env.example`, Windows vs bash runbooks | First-time contributor gets a green `dotnet test` / `ui` lint. Known trap: bash snippets on PowerShell. | ✅ |


---



## 3. Code quality, correctness, consistency — 12 packets, 1 per run (Run twice)

The variant is original; **clock economy is the product**. A chess-engine pass that skips time
transfers is incomplete.


| ID | Packet | Read first | Check | Done |
| -------- | ----------------------------- | ----------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ---- |
| **C-1** | Sectioned code quality | `.claude/rules/`, `src/`, `ui/` | FE / BE / ENG / INF, one section at a time. | ✅ |
| **C-2** | General code review | `.claude/rules/` | Do not run the same week as **C-1** without merging reports. | ✅ |
| **C-3** | Engine and chess rules | `.claude/rules/engine.md`, `config/kirichess.rules.json`, [RULES.md](../RULES.md), `/rules`, `/learn` | Legal moves, check/mate/stalemate, castling, en passant, promotion. Config is source of truth; generated RULES must not drift. | ✅ |
| **C-4** | Clock economy | Engine cost vectors, custom games, flag fall | Captures/moves/checks add or remove time. Zero, overflow, simultaneous flag, think-time measurement, custom configs. | ✅ |
| **C-5** | Realtime and live state | `.claude/rules/realtime.md`, `GameHub`, `Games/Distributed/` | Ownership, reconnect, `FlagFallMonitor`, latency compensation, spectator vs player budgets, shutdown flush (10a.3). | ✅ |
| **C-6** | Matchmaking and pairing | `MatchQueue`, seeks, challenges, rematch, no-show | Pool isolation, rated vs unrated, bot vs human, challenge tokens. | ✅ |
| **C-7** | Ratings | `RatingService`, Glicko-2, leaderboards | Constants, provisional, cache vs Postgres, rated bot games, interaction with sandbagging. | ✅ |
| **C-8** | PGN, replay, spectate, custom | [PGN.md](../game/PGN.md), replay/watch pages | Export fidelity; custom rule injection cannot break clocks or crash the engine. | ✅ |
| **C-9** | Drift | OpenAPI tests, `gen:api:check`, `gen:rules:check`, legal drift tests, `CiWorkflowDriftTests` | Code vs docs vs legal vs OpenAPI vs `ui/lib/api.ts` vs hub payloads vs staging/prod `fly.*.toml` vs `.env.example` vs `.claude/rules` vs `.cursor/rules`. Find what the gates do **not** cover. | ✅ |
| **C-10** | Tests | Readiness **TEST** | Ranked top 10 missing tests. Include clock economy, public API, API tokens, counters, web push, hub auth overlay if still thin. | ✅ |
| **C-11** | Frontend correctness | `.claude/rules/frontend.md`, live board, hub contract | Optimistic moves, clock UI, reconnect, auth callback, generated client vs hand wrappers. | ✅ |
| **C-12** | Schema and migrations | [MIGRATIONS.md](../ops/MIGRATIONS.md), DbContext, retention table | Invariants, indexes, anonymisation leftovers, roll-forward-only vs `immediate` deploy, `RunMigrationsOnStartup=false`. | ✅ |


---



## 4. Security, trust, fair play — 5 packets, 1–2 per run

Defensive, first-party, owner-authorised. Findings and fixes only. Never exploit code.


| ID | Packet | Read first | Check | Done |
| ------- | -------------------------- | ----------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ---- |
| **S-1** | Security and authorization | [AUTH.md](../decisions/AUTH.md) | Do not re-derive localStorage / CSP / moderator-2FA unless a named AUTH trigger has fired. Include `ApiToken`*, `HubAuthOverlayMiddleware`, public `/api/v1`. | ✅ |
| **S-2** | Fair play | Detection analyzers, TERMS §5 | Engine assistance, sandbagging, multi-account, API bots in human pools, spectator+engine, disconnect-to-flag. False-positive cost on a tiny volunteer mod team (one person). | ✅ |
| **S-3** | Moderation | `Moderation/`, display-name policy | Reports, sanctions, impersonation, ban evasion via anonymised re-register, appeal path (likely absent — say if that is acceptable at this size). | ✅ |
| **S-4** | Public / bot API | `PublicApiEndpoints`, API tokens, `/developers` | Scopes, hashing, revocation, rate limit by token id, hub play via token, public vs internal OpenAPI. | ✅ |
| **S-5** | Kill switch | `KillSwitchService` | What it gates; how to disable matchmaking, signups, or the API without a deploy; whether a future on-call would find it. | ✅ |


---



## 5. Infrastructure, ops, scale — 11 packets, ~3 per run

Managed services (Fly, Neon, Cloudflare), not bare metal. The OSS compose path still has to boot
for contributors (**O-5**). Production truth is [DEPLOYMENT.md](../ops/DEPLOYMENT.md).


| ID | Packet | Read first | Check | Done |
| -------- | ---------------------------- | ------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ---- |
| **I-1** | Architecture and speed | [DEPLOYMENT.md](../ops/DEPLOYMENT.md), `Games/` | Concurrent live games, Neon write RTT, SignalR, caching, load shedding. Hobby bill vs that ceiling. | ✅ |
| **I-2** | Topology and IaC | DEPLOYMENT.md, `fly.*.toml`, `infra/betterstack/` | Cloudflare → Fly UI/API → Neon; staging twin; toml drift; compose vs what actually runs; Redis still empty. | ✅ |
| **I-3** | CI/CD and release | `.github/workflows/`, DEPLOYMENT.md | Tag vs `main`, staging auto-deploy, migration gate, rollback = fix-forward, image scans. `FLY_API_TOKEN` has already burned once. | ✅ |
| **I-4** | Backup and DR | DEPLOYMENT.md §5, restore-drill, offsite-backup | Neon PITR is proven. Offsite R2 plumbing, bucket, token and secrets exist; the first dump never ran (private-repo Actions quota, parked 2026-08-20). Provider-account-deletion scenario. Compose `pg_dump` is not production. | ✅ |
| **I-5** | Observability | Readiness **OBS**, 10c.5 | DB, edge 5xx, synthetic “can a game be played”, Sentry PII off, `/metrics` scrape gate, Better Stack names vs instruments. | ✅ |
| **I-6** | Capacity and load | `loadtest/`, 9e.8, 10a.3 | Ladder authorized and unrun. Shutdown flush measured. Connection budgets. Neon suspend. The owner runs the load; write the number into DEPLOYMENT.md from the saved output. | ✅ |
| **I-7** | Secrets, DNS, TLS, mail auth | PROVIDER-ACCOUNTS, DH.5 | Fly secrets vs `.env.example`; Google redirect on the **API** host; SPF/DKIM/DMARC; Turnstile; Access removal is launch-day only. | ✅ |
| **I-8** | Incidents and bus factor | DEPLOYMENT.md | One maintainer. Who gets the alert, where the runbook is, no status page. Findings are checklists, not a hired NOC. | ✅ |
| **I-9** | Cost | Provider invoices, Fly/Neon plans | Coffee-money now vs cliffs if traffic spikes. Free-tier traps (Sentry, Resend). Lock-in. Not a profitability model. | ✅ |
| **I-10** | Portability | compose, EF, DEPLOYMENT.md | Leave Neon or Fly in a weekend? What is irreplaceable. Matters because this is OSS and a hobby host. | ✅ |
| **I-11** | Dependencies | Readiness **DEPS** | NuGet/npm/e2e outdated; Trivy and npm-audit waivers still dated and justified. | ✅ |


---



## 6. Product, UX, accessibility, content — 10 packets, ~2 per run

A stranger should understand the clock in one session and be able to play when the lobby is empty
(bot, seek, challenge). Retention cannot use correspondence.


| ID | Packet | Read first | Check | Done |
| -------- | --------------------------- | ---------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ---- |
| **P-1** | Product surface | `ui/app/`** | Every route: play, custom, learn, rules, game, watch, replay, challenge, leaderboards, profile, auth, donate, developers, moderation, analytics, offline, unsubscribe. Empty-lobby paths. | ✅ |
| **P-2** | Onboarding | `/learn`, `/rules`, `/about` | First-game funnel, `?src=`. Can a stranger grasp the economy. | ✅ |
| **P-3** | Day-2 retention | ROADMAP Phase 11 | Mail, prefs, web push, weekly play hour. What analytics can and cannot answer (anonymous counters). | ✅ |
| **P-4** | Failure UX | reconnect notice, `/offline`, email/OAuth/Turnstile errors | Disconnect, flag, 5xx, killed machine — does it look like a freeze. | ✅ |
| **P-5** | Accessibility (engineering) | Readiness **A11Y**, NVDA pass | Live board is outside axe. NVDA pass is written, not run (9f.2). Reduced motion, zoom, keyboard. | ✅ |
| **P-6** | Mobile and browsers | Game layout below `lg:` | Press-kit already tripped on stacked clocks. iOS Safari WebSockets, background-tab clock, PWA/offline. | ✅ |
| **P-7** | Frontend performance | Core Web Vitals, Stockfish worker, board bundle | Client cost for a volunteer’s phone. Overlaps **I-1**; this packet is the browser. | ✅ |
| **P-8** | i18n | `ui/messages/`, `next-intl` | English-only is an acceptable position; mixed hardcoded leftovers are a bug. No “translate to 40 locales” plan. | ✅ |
| **P-9** | Copy and claims | `/about`, CLAIMS.md, TERMS | Origin, dedication, inventor claims, honesty. Logo/wordmark vs Apache. Donate CTA must not look like a paywall. | ✅ |
| **P-10** | Visual consistency | `ui/components/`, board, clocks | Spacing, typography, dark mode, footer legal links. | ✅ |


---



## 7. Discovery and community (not a growth org) — 6 packets, whole table in one run

Adoption is the scarce resource ([CLAIMS.md](../game/CLAIMS.md), ROADMAP AD.*). Marketing here means
being findable and citable, plus one launch thread, not a funnel team.


| ID | Packet | Read first | Check | Done |
| ------- | ---------------------------------- | ---------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------- | ---- |
| **M-1** | SEO and public pages | `ui/app/sitemap.ts`, `e2e/tests/seo.spec.ts` | Titles, OG, `/rules` `/about` `/developers`. Cloudflare Access still blocks crawlers until DH.3 — say that rather than filing “not indexed” as a code bug. | ✅ |
| **M-2** | Press kit | [press-kit/README.md](../../press-kit/README.md), AD.1 | Recapture, convert script, logo license, a clip that is actually this game. | ✅ |
| **M-3** | Launch and communities | [POST-DRAFT.md](../plans/launch/POST-DRAFT.md), AD.3/AD.12 | Draft red-team only. Owner sends chessvariants, archive.org. Discord invite is live in the footer (`https://discord.gg/GRusewDw4`). | ✅ |
| **M-4** | Analytics for a privacy-first site | `Analytics/`, admin tabs | Anonymous counters and `?src=`. Enough to read AD.7 targets. Do not recommend identifiable product analytics. | ✅ |
| **M-5** | Mail and push | templates, unsubscribe, push prefs | Transactional only. Deliverability. No accidental marketing under ePrivacy. | ✅ |
| **M-6** | Developer ecosystem | `/developers`, `docs/api/openapi-public.json` | Token UX, rate limits, example bot, breaking-change honesty. Bots are part of the OSS surface. | ✅ |


---



## 8. Funding (patronage, not a product) — 4 packets (2 assignable), whole table in one run

Money follows players. Near-term costs are coffee money. The wrong outcome is a storefront with
nobody playing.


| ID | Packet | Read first | Check | Done |
| ------ | -------------------------------------- | ----------------------------------- | --------------------------------------------------------------------------------------------------------------------- | ---- |
| **$1** | Donations path | `/donate`, Stripe webhooks, PRIVACY | Payment links, webhooks, what Stripe stores vs the policy, failure modes. GitHub Sponsors is additive and still open. | ✅ |
| **$2** | Pay-to-win line | FS.6, patron fields, pairing, UI | Badge/flag must not leak into matchmaking, clocks, or implied strength. | ✅ |
| **$3** | Money admin | Stripe dashboard | KYC, payouts to an individual, books. Owner + accountant if it ever matters. Not a task to “monetise”. | ✅ |
| **$4** | Recurring patronage (sealed) | FS.4 | Do **not** assign. Trigger: real traction. Then: cosmetics-only, PRIVACY address, TERMS. | N/A (sealed) |
| **$5** | Patron supporter icon on Kirichess.com | `/donate` | If the code works correctly. A one-off badge lasts `Patron:OneTimeGrantMonths`, default 12 months, not 1. | ✅ |


---



## 9. Continuity of a one-maintainer project — 4 packets, whole table in one run


| ID | Packet | Read first | Check | Done |
| ------- | ------------------- | ------------------------------------------------------------------------------- | --------------------------------------------------------------------------------------------------------------------------- | ---- |
| **B-1** | Bus factor | Fly, Neon, Cloudflare, GitHub, Stripe, domain, JWT, Data Protection keys | A written “if I disappear”: where the secrets live, who could freeze billing, whether a fork can keep the variant playable. | ✅ |
| **B-2** | Maintainer exposure | PRIVACY §1 (no postal address) | `contact@` / `privacy@` harassment, git history / mailmap, moderator identity. Policy gaps only. | ✅ |
| **B-3** | Liability | TERMS limitation of liability | Optional hour with counsel. Do not recommend corporate insurance theatre. | ✅ |
| **B-4** | Calendar | restore-drill cron, offsite backup, DPA dates, terms version, domain auto-renew | A checklist the maintainer can run quarterly. | ✅ |


---



## Suggested order

Launch is still the ROADMAP queue. This index only says which checks come first.

1. **Stranger-facing:** S-1, L-1, L-2, L-3, C-4, C-5, I-4, I-5, I-7, P-5
2. **The game:** C-3, C-6, C-7, S-2, S-4
3. **OSS going public:** O-1, O-4, O-5, L-9, L-10
4. **Scale as a ceiling:** I-1, I-6, I-9, $1, $2
5. **After 8.7:** M-1–M-6, P-2, P-3, O-7
6. **Housekeeping:** C-1 or C-2 (not both at once), C-9, C-10, I-11, B-4

Do not assign **$4**, **9f.1**, or anything that reopens the standing positions.

---



## Applying the findings

Every report is written and its **Done** box is ticked. What is still blocked or waiting on you
is [PROGRESS.md](PROGRESS.md). The open weaknesses and bugs, in fix order, are
[PRIORITY_FIX.md](PRIORITY_FIX.md). The category reports hold the original findings.

---



## Report format (section in `docs/audits/<LETTER>.md`, or `funding.md` for `$`)

Packets for one letter share one file: `L.md`, `O.md`, `C.md`, `S.md`, `I.md`, `P.md`, `M.md`,
`B.md`, `funding.md`. Each packet is an `## <ID> — <name>` section inside that file.

```markdown
## <ID> — <name>
Date: YYYY-MM-DD
Packet: docs/audits/INDEX.md

### Verdict
One paragraph. Theme, not a score.

### Findings
#### <ID>-1 — <title>
- Severity:
- Where: `file:line`
- Impact:
- Fix:

### Already covered
One-liners pointing at earlier reports or ROADMAP.

### Action list
Ordered. No standing-position reversals.
```

