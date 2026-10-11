# B — Continuity of a one-maintainer project

Merged packet reports for category **B**. Individual packets were `docs/audits/B-N.md`; they now live as sections below. Index: [INDEX.md](INDEX.md).

---

## B-1 — Bus factor
Date: 2026-08-31
Packet: docs/audits/INDEX.md, table 9

### Verdict

The recoverability of the *system* is in unusually good shape: the Data Protection key ring is in
Postgres (`Program.cs:177-179`), not the container filesystem, so it survives every failure Postgres
survives; Neon PITR is proven and an off-site R2 dump path exists; the deploy, migration, restore
and rollback procedures are all written down; the software is Apache-2.0 with a `docker-compose`
path (O-5). What is missing is the recoverability of the *project by a person who is not the
maintainer*. There is no document that says which provider holds what, which email address recovers
each account, who is allowed to touch billing, or whether a trusted person could even get in to keep
`kirichess.com` playable. Every provider account, every billing relationship, and the single
`Admin` role are bound to one individual with no second identity and no escrow. This is the one
packet in the audit whose deliverable is a document that can only be sketched, because the
contents (recovery emails, a password vault location, a named contact) are secrets that must not
live in the repo. The skeleton and the decisions a successor needs are below; filling and storing it
is Owner work.

### Findings

#### B-1-1 — No "if I disappear" document exists
- Severity: Medium
- Where: `docs/ops/` holds `DEPLOYMENT.md`, `PROVIDER-ACCOUNTS.md`, `MIGRATIONS.md` and (per B-4)
  a planned `MAINTENANCE-CALENDAR.md`, but nothing named continuity/succession/handover.
  `docs/audits/I.md:29-33` explicitly defers "the full 'if I disappear' succession doc (where
  every secret lives, who can freeze billing, whether a fork can keep the variant playable)" to this
  packet. `docs/audits/B.md` and `docs/audits/B.md` both reference B-1 as not-yet-run.
- Impact: if the maintainer is unavailable for weeks (illness, travel with no connectivity) or
  permanently, nobody else knows the shape of the deployment: which five providers are load-bearing,
  which account email recovers each, that production data has an off-site copy in R2 and how to
  restore it, or that the software is open enough to keep running at all. `PROVIDER-ACCOUNTS.md` is
  the closest artefact but it is a setup runbook ("here is every field the Google console will ask
  for"), not an inventory a stranger could act on under pressure. The players lose the service with
  no path to recovery, and the canonical instance of the variant goes dark while one person holds
  all the knowledge in their head.
- Fix: create `docs/ops/CONTINUITY.md` from the skeleton below. The **structure** (which providers,
  what each does, what breaks if it lapses, what a successor must decide) goes in the repo; the
  **access half** (recovery email per provider, card, vault location, named contact) is filled in a
  copy kept in the maintainer's password manager or a sealed document, never committed. Link the
  repo copy from `docs/README.md` and `docs/ops/DEPLOYMENT.md` §12.

#### B-1-2 — Every provider account, billing relationship and admin role is one person with no backup
- Severity: Medium
- Where: `docs/ops/PROVIDER-ACCOUNTS.md:29-31` (Fly runs two paid apps, Neon provisioned, "both
  were paid for already" — one card); `docs/audits/funding.md` (the Stripe account's legal entity is the
  individual in `PRIVACY.md §1`); `docs/audits/I.md` (Cloudflare Registrar, Cloudflare zone);
  `docs/audits/B.md` B-2-8 (repo under a personal GitHub account); `ModerationSeeder` /
  `.claude/rules/security.md` ("exactly one role ... seeded from config at boot ... deliberately no
  self-service way to become a moderator"); `docs/audits/I.md` I-8-1 (the Better Stack "team" is
  one person, alerts to one inbox).
- Impact: a billing lapse on Fly, Neon, Cloudflare or the domain registrar (expired card, a card
  reported lost, a disputed charge, a provider dunning email missed in the shared inbox per B-2-4)
  silently degrades or ends production, and no second person can add a payment method, read the
  invoice, or even confirm what happened. There is no second Cloudflare/Fly/Neon member, no second
  registrar contact, and the `Admin` role cannot be granted to a co-maintainer without a config
  change and redeploy. "Who could freeze billing" has a one-word answer today: only the maintainer,
  and only if reachable.
- Fix: in `CONTINUITY.md`, record the billing card, its expiry, and each provider's renewal month,
  so a lapse is visible before it bites (fold into B-4's calendar). Separately, consider adding a
  second member/owner on the two accounts whose loss is unrecoverable (the domain registrar and
  Neon) held by one trusted person or a shared vault. Adding a second `Admin` is a bigger step that
  trips `docs/decisions/AUTH.md` §1.7's "second moderator" trigger (the `mfa`-on-refresh-token gap),
  so it is a deliberate decision, not a default.

#### B-1-3 — `Jwt__Key` exists only as a Fly secret and cannot be read back; losing the Fly account loses it
- Severity: Low
- Where: `docs/ops/PROVIDER-ACCOUNTS.md:23` (`Jwt__Key` deployed on `kirichess-api`);
  `docs/audits/B.md:223` (token table: blast radius "forge access tokens (rotation logs everyone
  out)"); `Program.cs:177-179` (Data Protection ring is in Postgres, so it is **not** in this
  category — it survives a Fly loss via Neon PITR and the R2 dump).
- Impact: `fly secrets` values are write-only; they exist only inside the running machine. If the
  Fly account is lost, `Jwt__Key` is gone. The practical consequence is limited: a new key on a
  rebuilt instance just forces every user to log in again, which is acceptable. The finding is that
  this is nowhere written down as "acceptable and expected", so a successor rebuilding the stack
  would not know whether the missing key is a catastrophe or a shrug. The genuinely irreplaceable
  cryptographic material (the DP ring) is already in the database and in scope for backup, which is
  the right design and should be stated as such.
- Fix: one line in `CONTINUITY.md` classifying each secret: `Jwt__Key` is regenerate-and-move-on
  (`openssl rand -base64 48`, everyone re-logs-in); the Data Protection ring is in the
  `DataProtectionKeys` table and restored with the database; provider API tokens are re-minted from
  their consoles. Cross-reference B-4-2's operational-secret inventory rather than duplicating it.

#### B-1-4 — The line between "keep kirichess.com alive" and "fork the software" is undocumented for a successor
- Severity: Low
- Where: `docs/audits/O.md` (forkability: `docker-compose` build from repo root, real prod
  images, what must be renamed per NOTICE); `docs/legal/TERMS.md:100-101` (running your own instance
  is not use of the service); no document addresses the successor case specifically.
- Impact: O-5 answers "a stranger wants to run *an* instance". It does not answer "a trusted person
  wants to keep *this* instance, with its domain and its players' accounts, running". Those need the
  domain transferred, the Neon project's connection string, the Fly apps or a redeploy target, the
  Cloudflare zone, and a decision on Stripe (transfer, or run with donations disabled). A successor
  under time pressure needs a checklist of transfer-or-abandon per provider, not a fork guide.
- Fix: a "keeping the hosted instance alive vs. forking" section in `CONTINUITY.md`: for each of
  domain, DNS/Cloudflare, Neon, Fly, Stripe, Resend, Sentry, Better Stack, state whether a successor
  must obtain it, can rebuild it, or can drop it (Stripe and Sentry are droppable; the domain and
  the database are not). Point at O-5 for the clean-fork path and at the R2 dump for the data.

#### B-1-5 — No mechanism designates who receives access
- Severity: Low (trigger: the owner wants continuity to actually function, not just be documented)
- Where: nothing in the repo; out of scope for the repo by nature.
- Impact: a complete `CONTINUITY.md` is inert if the recovery mailbox and the password vault are not
  reachable by the intended person. Password managers (1Password, Bitwarden) offer emergency-access
  grants; without one, or a sealed envelope with a named person, or a line in a will, the plan
  describes a handover that cannot happen.
- Fix: Owner-only. Set up password-manager emergency access for one named person, or a sealed
  credentials document held by someone trusted, covering the recovery email, the vault, and the
  domain registrar login. Named here so it is on the list, not because it can be done from the repository.

### Already covered

- **Incident detection, alert routing, single inbox, no runbook, no second on-call** —
  `docs/audits/I.md` I-8-1 through I-8-4. B-1 is the "nobody can take over the project at all"
  layer; I-8 is "an alert has one recipient and no playbook". Not re-reported.
- **Operational-secret inventory, rotation cadence, `FLY_API_TOKEN` burned once** —
  `docs/audits/B.md` B-4-2 and the token table in B-4's `MAINTENANCE-CALENDAR.md` skeleton. B-1
  adds only the escrow / successor-access classification (B-1-3).
- **Replies from shared mailboxes leak a personal address; statutory mail buried in one inbox** —
  `docs/audits/B.md` B-2-1, B-2-4.
- **Personal email in git history; repo under a personal GitHub account; clean public snapshot** —
  `docs/audits/O.md` O-4-1/O-4-2, `docs/audits/B.md` B-2-6/B-2-8.
- **Stripe entity, KYC, payout bank account, legal name on record** — `docs/audits/funding.md`. B-1-2
  only adds "no second person can see or act on billing".
- **Forkability, compose path, what a fork must rename** — `docs/audits/O.md`. B-1-4 adds the
  successor (keep-this-instance) case O-5 does not cover.
- **Off-site backup has never produced a copy; provider-account-deletion scenario** —
  `docs/audits/B.md` B-4-4, `docs/audits/I.md`, `docs/decisions/RETENTION.md` §6.
- **Domain / registrar-card health unmonitored** — `docs/audits/B.md` B-4-6.

### Action list

Ordered. No standing-position reversals (no company, no hire, no second controller).

1. create `docs/ops/CONTINUITY.md` from the skeleton below (structure only; no secret
   values). Link from `docs/README.md` and `DEPLOYMENT.md` §12.
2. **Owner, once** — fill the private copy: per provider, the account email, the recovery email, the
   2FA method and its backup codes' location, the billing card and its expiry, each renewal month.
   Store it in the password manager, not the repo.
3. **Owner, once** — B-1-5: set up password-manager emergency access or a sealed document for one
   named person, covering the recovery mailbox, the vault, and the registrar login.
4. **Owner, consider** — B-1-2: a second member on the Cloudflare account (domain + zone) and the
   Neon project; weigh a second `Admin` against `AUTH.md` §1.7 before doing it.
5. **Owner** — B-1-3: when Redis is provisioned (8.4), re-check whether any
   restart-surviving state moves out of Postgres, and update the secret classification in
   `CONTINUITY.md` and B-4's token table together.
6. **Owner, quarterly** — fold the "card valid, renewals > 6 months out, recovery email still
   works" check into B-4's `MAINTENANCE-CALENDAR.md` manual section.

---

#### Skeleton: `docs/ops/CONTINUITY.md`

> One page. What a trusted person would need to keep `kirichess.com` playable, or to shut it down
> cleanly, if the maintainer is unavailable. **No credential, recovery email, or card number is in
> this file** — those live in `<password manager> / sealed document>`, and the emergency-access
> grantee is `<name>`. This file is the map; that vault is the keys.

##### The five load-bearing providers

| Provider | Holds | If the account is lost | If billing lapses | Successor must |
| --- | --- | --- | --- | --- |
| Cloudflare (Registrar + zone) | `kirichess.com`, all DNS, edge TLS, Turnstile, Email Routing, R2 backups | Domain and all mail/links dead; not rebuildable | Domain stops resolving after grace; silent until then | **Obtain** — transfer the domain; without it nothing else matters |
| Neon | Production Postgres (all accounts, games, ratings, sanctions, DP key ring) | Data gone unless the R2 dump is current (B-4-4) | Compute suspends, then project deletion risk | **Obtain or rebuild** from the latest R2 dump into a new Postgres |
| Fly.io | `kirichess-api`, `kirichess-ui`, `kirichess-log-shipper`; all app secrets incl. `Jwt__Key` | Apps down; `Jwt__Key` gone (users re-login, acceptable) | Apps stop; in-memory games lost | **Rebuild** — redeploy the images from the repo; re-mint secrets |
| GitHub | The source, CI/CD, all workflow secrets, Dependabot | No deploys; scheduled safety nets stop | n/a (free) | **Obtain** — repo access; re-run `deploy.yml` |
| Stripe | Donation processing only | Donations stop; play unaffected | n/a | **Drop** — run with donations disabled, or transfer later |

##### Secondary providers (drop or rebuild, none blocks play)

| Provider | Holds | Successor action |
| --- | --- | --- |
| Resend | Transactional email (confirm, reset) | Rebuild — new API key, re-verify the sending domain; until then, no signup emails |
| Sentry | Error tracking | Drop or rebuild |
| Better Stack | Uptime monitors, log aggregation, alerting | Rebuild from `infra/betterstack/` Terraform |
| Google Cloud | OAuth client (sign-in with Google) | Rebuild per `PROVIDER-ACCOUNTS.md` §1; the button is inert without it, not broken |

##### Recovery order if starting from only the R2 dump and the repo

1. New Postgres, restore the latest `kirichess/latest` object (`scripts/offsite-backup.sh restore`).
2. New Fly apps (or any host) from the repo images; set `ConnectionStrings__Postgres`, a fresh
   `Jwt__Key`, `Cors__AllowedOrigins__0`, `Frontend__BaseUrl`.
3. Point DNS at the new hosts; re-issue TLS.
4. Re-mint Resend / Turnstile / Sentry keys as needed; Google OAuth last.
5. The Data Protection ring rides along in the restored database, so email links issued before the
   cutover keep working.

##### What is deliberately unrecoverable, and why that is acceptable

| Item | Why it is gone | Why that is fine |
| --- | --- | --- |
| `Jwt__Key` (if Fly is lost) | Write-only Fly secret | A new key just forces re-login |
| In-progress live games at cutover | In-process memory, single machine (`DEPLOYMENT.md` §12) | Known gap 6c; games are re-startable, ratings are in Postgres |
| Anything since the last R2 dump | Dump cadence is weekly | Neon PITR covers the gap while Neon exists; the dump is the Neon-is-gone floor |

##### Shutdown, if there is no successor

The honest option. In order: post a notice (kill switch / a static page), stop new signups
(`KillSwitchService`, `docs/audits/S.md`), export what a departing project owes its users, let the
domain lapse, close the Stripe account, cancel Fly and Neon. The rules manual (CC BY 4.0) and the
software (Apache-2.0) stay published so the variant can be revived by anyone.

No production code changed in this packet. No standing-position reversals. Only `docs/audits/B.md`
was created; `docs/ops/CONTINUITY.md` (action item 1) is follow-up work, not done here.

---

## B-2 — Maintainer exposure
Date: 2026-08-30
Packet: docs/audits/INDEX.md, table 9

### Verdict

The structural decisions are sound and mostly already made: no postal address in PRIVACY §1 (a
standing position, correctly reasoned at `PRIVACY.md:20-26`), the sanctioned-user API withholds the
moderator's identity by construction (`MySanctionDto` carries no issuer and no note, and
`SanctionBanner.tsx` shows only the reason string), the export cannot be used to unmask a reporter
(L-2, PRIVACY §7), and the personal-email exposure in git history is a documented, deliberate reason
the repository stays private (O-4). What is thin is the layer between *a stranger sends mail* and
*the maintainer answers it*. All four published addresses (`privacy@`, `contact@`, `abuse@`,
`dmarc@`) plus every ops and cron alert forward to one personal inbox, and Cloudflare Email Routing
only forwards — it does not send — so every reply the maintainer writes to a DSAR, an abuse report,
a vulnerability disclosure, or a ban appeal leaves from a personal address unless an alias is
configured, handing that address to a correspondent who is frequently hostile by definition. There
is no "manifestly unfounded or excessive" process for the statutory mailbox, and the Stripe
customer-facing descriptor — the one place a donor's card statement could surface the maintainer's
legal name — is specified nowhere. None of this is a code bug; all of it is a small policy or
one-time-setup gap.

### Findings

#### B-2-1 — Replies from the shared mailboxes leak the maintainer's personal email address
- Severity: Medium
- Where: Cloudflare Email Routing forwards `privacy@`, `abuse@`, `dmarc@`, `contact@` to a personal
  inbox and runs no mail server. Also
  `docs/ops/DEPLOYMENT.md:594-599` (transactional send is Resend-only, for confirmation/reset;
  nothing sends operator mail), `SECURITY.md:9`, `PRIVACY.md:18-20`, `CODE_OF_CONDUCT.md:62-63`.
- Impact: Email Routing is forward-only. When the maintainer replies to anything sent to
  `privacy@`/`contact@`/`abuse@` — a DSAR, a GDPR erasure request, an abuse report, a CoC
  complaint, a vulnerability disclosure per `SECURITY.md`, or a ban appeal — the reply is sent from
  whatever account owns the personal inbox, putting a personal address (the git history shows the
  owner's personal Gmail address) in the `From`/`Reply-To` headers. The people on the other end of
  these channels are disproportionately hostile: a banned user appealing, a reporter whose report
  was dismissed, a rejected DSAR filer. A personal Gmail address is a pivot to a real name, a
  Google profile, and other services. `git config user.email` and the reply address are the same
  string today.
- Fix: configure "send mail as" / a send alias in the personal inbox so replies to those four
  addresses leave as the alias (`privacy@` / `contact@`), and verify with one test round-trip that
  no personal address appears in the delivered headers. Record it in `DEPLOYMENT.md §8` as a
  required step (it currently documents only inbound routing and outbound Resend). Cheapest
  alternative: a dedicated free mail account that owns the aliases and is never used personally.

#### B-2-2 — No "manifestly unfounded or excessive" process for the statutory mailbox
- Severity: Medium
- Where: `PRIVACY.md:197-208` (§8 lists the rights "subject to the above" with no abuse carve-out).
  `privacy@` is the single published route for Articles 15 to 22, and a request starts a statutory
  one-month clock. `docs/audits/L.md` verified the mailbox works and did not address abuse.
- Impact: a hostile actor — most plausibly a banned user — can send repeated access and erasure
  requests to `privacy@`, each restarting a one-month Art. 12(3) clock and each demanding a
  substantive answer, with the answer itself carrying B-2-1's exposure. GDPR Art. 12(5) explicitly
  allows a controller to charge a reasonable fee for, or refuse, requests that are "manifestly
  unfounded or excessive, in particular because of their repetitive character". Nothing in the
  policy or in any ops note invokes that carve-out or describes a triage rule, so the default posture
  is "answer every one on the clock".
- Fix: one short paragraph in `PRIVACY.md §8` stating that manifestly unfounded or excessive
  requests (Art. 12(5)) may be refused or subject to a reasonable fee, and a two-line internal note
  on how repeat or abusive requests are logged and handled. A policy edit means bumping `TermsVersion.Current` and
  the four legal-doc copies together (`LegalDocumentDriftTests`).

#### B-2-3 — Stripe customer-facing descriptor is specified nowhere, so a donor receipt can surface the maintainer's legal name
- Severity: Medium (trigger: donations are live; `$1` confirms the path ships)
- Where: `docs/ops/PATRON-BADGE-GOLIVE.md` (the go-live runbook — no mention of statement
  descriptor, public business name, or receipt branding anywhere in it), `docs/audits/funding.md:54-55`
  (the Stripe account's legal entity is the individual named in `PRIVACY.md §1`), `PRIVACY.md:76`
  (card details go to Stripe directly; KiriChess sends no name).
- Impact: on a Stripe account registered to an individual with no business entity, the card
  statement descriptor, the shortened descriptor, and the Stripe-sent email receipt default to the
  account holder's name unless the public business name and descriptors are explicitly set. Every
  donor would then see the maintainer's legal name (and, on some statement formats, a city) on their
  bank statement and receipt. `$3` covers KYC and the payout entity; neither `$1` nor `$3` covers
  the customer-facing descriptor.
- Fix: in the Stripe dashboard set the public business name and both statement descriptors to
  `KIRICHESS` / `KIRICHESS.COM`, and verify against one real test donation — the email receipt and
  an actual card-statement line, not just the dashboard preview. Add this as a step to
  `PATRON-BADGE-GOLIVE.md`.

#### B-2-4 — Four published addresses plus all ops alerts share one personal inbox with no separation
- Severity: Low
- Where: `docs/audits/I.md:94` ("alerts and cron-failure
  mail land in a personal inbox"), `docs/audits/B.md:78` (DMARC aggregate reports delivered to
  the same inbox, unread).
- Impact: a real DSAR with a statutory deadline competes for attention with DMARC XML, Better Stack
  alert mail, GitHub cron-failure mail, and general `contact@` traffic in one undifferentiated
  inbox. One hostile actor emailing all four published addresses reaches one person. There is no
  filter rule, label, or separate view that isolates the statutory channel, so a time-critical
  request can be buried by noise.
- Fix: inbox filters routing `privacy@` (and `abuse@`) to their own label/view, distinct from
  `contact@`, `dmarc@`, and machine alerts. This is inbox configuration, not new infrastructure.
  Overlaps I-8-4 (no second human sees an alert) and B-4-3 (DMARC reports unowned) — those own the
  ops-visibility and DMARC angles; this is only the "statutory mail must not be buried" slice.

#### B-2-5 — Nothing warns the moderator not to self-identify in player-visible sanction text
- Severity: Low
- Where: `src/KiriChess.Server/Data/Entities/UserSanction.cs:33-37` (`Reason` is "shown to the
  sanctioned player verbatim"; `ModeratorNote` is correctly withheld),
  `src/KiriChess.Server/Moderation/ModerationEndpoints.cs:208-223` (`/api/moderation/my-sanctions`
  returns `MySanctionDto` — no issuer, no note, no id), `ui/components/SanctionBanner.tsx:41-46,95`
  (renders `reason` and type only). `LiftReason` (`UserSanction.cs:48`) has the same player-visible
  exposure and no doc note at all.
- Impact: the structural design is correct — a sanctioned user is never told who sanctioned them
  through the API or UI. But `Reason` and `LiftReason` are free text written by the one person who
  is both the only moderator and the named controller, and a banned user already knows from `/about`
  and `PRIVACY.md §1` that one individual runs everything. A reason string that reads "warned by
  Michiel for…" or is signed adds a direct, quotable attribution to what is otherwise an
  unattributed action. There is no guidance saying that field is player-facing.
- Fix: one line in the `SanctionType` / `UserSanction` doc comment and in any moderation runbook:
  `Reason` and `LiftReason` are shown to the player verbatim and must not name the moderator or be
  signed. No code change; `ModeratorNote` already exists for anything that should not be seen.

#### B-2-6 — git history ties the maintainer to a named institution beyond what `/about` discloses

> The three identities are described here rather than quoted. Quoting them is what kept
> `TrackedTreeHygieneTests.Tracked_files_do_not_contain_personal_mailbox_addresses` red: the
> finding that named the leak was the last tracked copy of it. Do not paste the literal addresses
> back in to make this read more precisely; the precision is what the test is there to stop.
- Severity: Low (trigger: the repository is made public without a history rewrite)
- Where: git author/committer identities include a `@student.` address at a named Belgian
  university (~35 commits), the owner's personal Gmail address (~370 commits), and a bare machine
  hostname identity; `.mailmap:1-8` documents this and records that rewriting was declined;
  `docs/game/CLAIMS.md:28` ("**not this working repo**, whose commit authors include personal
  addresses").
- Impact: O-4 owns this exposure and treats it as the decided reason the repo stays private, and
  the O-4-1 public-snapshot procedure ships a working tree at a tagged commit (no history), which
  avoids it. The B-2-specific point O-4 does not make: the `student.howest.be` address is an
  identity-linkage vector in its own right — it names a specific Belgian university college, implies
  student status, and narrows age and location — distinct from the real name that `/about` discloses
  by choice. It should be called out by name so a future snapshot cut in a hurry does not become a
  history push.
- Fix: nothing new here — when `docs/ops/PUBLIC-SNAPSHOT.md` is written (O-4-1), have it name the
  `student.howest.be` address explicitly as a reason the snapshot is a working-tree export and the
  working repo's history is never pushed. Cross-reference only; do not duplicate O-4.

#### B-2-7 — WHOIS privacy is assumed but unverified in any repo artifact
- Severity: Low
- Where: `kirichess.com` is on Cloudflare Registrar (`docs/audits/I.md:48`). No artifact records the actual WHOIS
  output.
- Impact: Cloudflare Registrar redacts registrant contact data by default and does not resell the
  domain above cost, so the exposure is very likely already nil — but "very likely" is not
  "checked", and a registrant name or a Belgian address in public WHOIS would undo the entire
  no-postal-address position in PRIVACY §1 without anyone noticing.
- Fix: one-time — run `whois kirichess.com` and confirm the registrant shows Cloudflare's
  redaction proxy (e.g. "DATA REDACTED" / "GDPR Masked"), not a personal name or address. Record
  the date in `PROVIDER-ACCOUNTS.md`. Add it to B-4's annual domain-health line.

#### B-2-8 — The repo lives under the maintainer's personal GitHub account, linked site-wide
- Severity: Low (trigger: the repository is made public)
- Where: `ui/components/Footer.tsx:29`, `ui/app/[locale]/about/page.tsx:18`,
  `ui/app/[locale]/terms/page.tsx:238-241`, `ui/app/[locale]/developers/page.tsx:135`,
  `.github/ISSUE_TEMPLATE/config.yml:4`, `SECURITY.md:7` — all point at
  `github.com/MichielGekiere/kirichess`.
- Impact: the real name is a deliberately disclosed standing position (`/about`), so the name
  itself is not the finding. But routing every public issue, PR, discussion and security advisory
  through a personal account also exposes that account's activity graph, followers, stars, other
  repositories and any profile bio/location to everyone who engages with the project — and couples
  the project's moderation of its own issue tracker to a personal identity. O-4-4 already notes the
  org-ownership trade-off (gitleaks licensing); this is the personal-safety half of the same
  decision.
- Fix: before the repo goes public, consider moving it to a `kirichess` GitHub organization owned
  by the same account. Issues, PRs and advisories then present as the project, not the person, and
  the personal profile is one hop away rather than the primary surface. Weigh against O-4-4 (a
  private org repo needs `GITLEAKS_LICENSE`). Owner decision, not urgent.

### Already covered

- **`SECURITY.md` promises a response window with a bus factor of one** — `docs/audits/O.md`
  O-3-4. B-2-1 is a different point (the reply's `From` header), not the response time.
- **No appeal body / the enforcement ladder describes a body that does not exist** —
  `docs/audits/O.md` O-3-3; moderation-mechanics appeal path is S-3's packet (not yet run).
  B-2 does not re-open the appeal-process question, only the "the appeal reply leaks a personal
  address" slice (B-2-1).
- **Single ops inbox; no second human can see an alert during an incident** — `docs/audits/I.md`
  I-8-4. B-2-4 is scoped to statutory mail being buried, not to incident visibility.
- **DMARC at `p=none` makes a spoof of `privacy@`/`abuse@`/`contact@` a live social-engineering
  vector against the maintainer** — `docs/audits/B.md` B-4-3 (already flagged as "B-2 territory"
  there); `docs/audits/M.md` M-5-4 (sender-reputation angle). Not re-reported.
- **Personal emails in commit authorship, `.mailmap`, and the need for a clean public snapshot** —
  `docs/audits/O.md` O-4-1, O-4-2; `docs/game/CLAIMS.md:28`. B-2-6 cross-references, adds only the
  institution-linkage note.
- **Stripe account entity, KYC, payout bank account, legal name on record** — `docs/audits/funding.md`.
  B-2-3 covers only the customer-facing descriptor, which `$3` and `$1` do not.
- **Reporter identity is never revealed to the reported user; the export cannot unmask a reporter**
  — `docs/audits/L.md`; `PRIVACY.md:184-192` (§7); `src/KiriChess.Server/Data/Entities/Report.cs:29`.
  Verified, no finding.
- **`MySanctionDto` withholds the moderator's identity from the sanctioned user by construction** —
  verified at `src/KiriChess.Server/Moderation/ModerationDtos.cs` and `ModerationEndpoints.cs:208-223`.
  No structural leak; B-2-5 is only the free-text-field guidance gap.
- **No postal address in PRIVACY §1** — standing position (`docs/audits/INDEX.md` "Standing
  positions"); the donations "address trigger has fired" angle is `docs/audits/L.md` L-1-5 and
  `docs/audits/L.md`. Out of scope to reverse here.
- **Reach-back / notification mail carries no `Reply-To`** — `docs/audits/M.md` M-5-5.

### Action list

Ordered. No standing-position reversals (no company, no PO box mandate, no hire).

1. **Owner, once** — B-2-1: configure a send-as alias so replies to `privacy@` / `contact@` /
   `abuse@` leave as the alias; confirm with a test round-trip that no personal address is in the
   delivered headers; add the step to `DEPLOYMENT.md §8`.
2. **Owner, once** — B-2-3: set the Stripe public business name and both statement descriptors to
   `KIRICHESS` / `KIRICHESS.COM`; verify against a real test donation's email receipt and card
   statement; add the step to `docs/ops/PATRON-BADGE-GOLIVE.md`.
3. B-2-2: add an Art. 12(5) "manifestly unfounded or excessive"
   paragraph to `PRIVACY.md §8` and a short internal triage note; bump `TermsVersion.Current` and
   the four legal-doc copies together.
4. **Owner, once** — B-2-4: inbox filters isolating `privacy@` / `abuse@` from `contact@`, `dmarc@`
   and machine alerts; consider a dedicated project mail account.
5. B-2-5: one-line doc note that `Reason` / `LiftReason` are player-visible and must not
   name or be signed by the moderator (`UserSanction.cs`, moderation runbook).
6. B-2-6: when `docs/ops/PUBLIC-SNAPSHOT.md` is written (O-4-1), name the
   `student.howest.be` address explicitly as a reason history is never pushed.
7. **Owner, once** — B-2-7: verify WHOIS redaction on `kirichess.com`; record the date in
   `PROVIDER-ACCOUNTS.md`; fold into B-4's annual domain line.
8. **Owner, before public** — B-2-8: decide whether to move the repo to a `kirichess` GitHub org
   (weigh against O-4-4's gitleaks-licensing note).

No production code changed in this packet. No standing-position reversals. Only `docs/audits/B.md`
was created.

---

## B-3 — Liability
Date: 2026-08-31
Packet: docs/audits/INDEX.md, table 9

### Verdict

TERMS §7 does the honest, proportionate thing for a free service run by one person: it is provided
as is, with no warranty and no uptime guarantee, and it warns that accounts, games and ratings may
be lost while the project is pre-launch. §9 correctly does not try to strip EU consumer mandatory
protections. For a service that takes no payment for play and earns no revenue from it, the real
liability exposure is genuinely small, and this packet does **not** recommend drafting new clauses
speculatively or buying insurance. What it does is assemble the two or three questions that are
worth one hour of Belgian counsel, because they are questions should not be answered from the repository: whether
§7 should carry an explicit limitation of liability and what Belgian law on exoneration clauses
allows a free service to exclude; whether voluntary donations change the "no consideration"
position that a free service's liability defence leans on; and whether the permanent, reasoned
sanction record needs a "moderator's assessment, appealable" framing to limit a defamation angle.
Insurance at this scale would cost more than the project and is explicitly out of scope.

### Findings

#### B-3-1 — TERMS §7 disclaims warranty but contains no limitation of liability
- Severity: Low (trigger: a user asserts a loss, e.g. a data breach, a wrongful ban, "your outage
  cost me a rated tournament entry")
- Where: `docs/legal/TERMS.md:64-72` (§7: as is, no warranty of any kind, no guaranteed uptime,
  accounts/games/ratings "may be lost or reset without notice"). `docs/legal/TERMS.md:80-88` (§9
  preserves Rome I Art. 6(2) and Brussels I bis Art. 18 consumer protections). No clause caps
  damages, excludes indirect or consequential loss, or sets a liability ceiling.
- Impact: a warranty disclaimer and a limitation of liability are different instruments. §7 says the
  service is not guaranteed; it does not say what, if anything, the maintainer could owe if held
  liable for something (a personal-data breach, a defamatory sanction reason per B-3-3, loss of
  user data through negligence). For a free service the realistic exposure is low and Belgian law
  would in any case limit how far such a clause can go, so the absence is not a defect to rush-fix.
  It is a question for counsel: an over-broad exoneration clause is void under Belgian law and reads
  worse to a regulator than none at all.
- Fix: put on the counsel-hour agenda: "should §7 carry an explicit limitation of liability, and
  what is enforceable for a free, non-commercial service under Belgian law on exoneration clauses
  (which does not permit excluding liability for one's own fraud, gross negligence, or personal
  injury, and can void a clause that purports to)?" Do not draft it without that advice. If counsel
  recommends a clause, adding it bumps `TermsVersion.Current` and the four legal-doc copies together
  (`LegalDocumentDriftTests`).

#### B-3-2 — The "free, no consideration" framing is partly undercut by donations
- Severity: Low
- Where: `docs/legal/TERMS.md:64-67` (§7, "free ... funded out of pocket and by voluntary donations
  rather than by advertising, subscriptions or paid features"), `docs/legal/TERMS.md:108-132` (§12,
  donations "buy nothing", carefully framed as gifts), `docs/audits/funding.md` (the donation path),
  `docs/audits/L.md` and `docs/audits/L.md` L-1-5 (FS.4 recurring-tier address/VAT trigger).
- Impact: a free service's liability position leans on there being no contract for consideration.
  §12 frames one-off donations as gifts that unlock nothing, which is the right framing and is
  consistent throughout the site. A lawyer should confirm that framing holds for liability and
  consumer-law purposes under Belgian and EU law, and flag what changes if recurring patron tiers
  ever ship (FS.4), since a recurring paid relationship is a different animal.
- Fix: one question for the same counsel hour: "do voluntary one-off donations affect the liability
  or consumer-law characterisation of the service, and what would change with recurring tiers?"
  This overlaps L-7 (which owns the address/VAT angle); B-3 asks only the liability slice.

#### B-3-3 — Permanent, reasoned sanction records plus public display names create a defamation surface
- Severity: Low (trigger: a factually wrong or leaked sanction reason)
- Where: `docs/legal/TERMS.md:54-62` (§5/§6: a sanction is "recorded permanently against your
  account, including its reason" and survives deletion), `docs/audits/B.md` B-2-5 (`Reason` and
  `LiftReason` are shown to the player verbatim and are free text written by the sole
  moderator/controller), `src/KiriChess.Server/Data/Entities/UserSanction.cs:33-48`. The moderation
  mechanics and the missing appeal path are S-3's packet (not yet run) and `docs/audits/O.md`
  O-3-3.
- Impact: if a sanction reason is factually wrong, or a `ModeratorNote` leaks, or a permanent
  reasoned record continues against an anonymised row, there is a defamation and a GDPR-accuracy
  angle. Most of this is S-3's and B-2's territory. The liability-specific slice is whether TERMS
  should characterise a sanction reason as the moderator's assessment rather than a statement of
  fact, and whether the absence of any appeal route is itself a liability exposure for a service
  that records the outcome permanently.
- Fix: counsel-hour question: "does the permanent public sanction record need an 'assessment, not a
  finding of fact' framing in TERMS §6, and is the lack of an appeal path a liability concern given
  the record is permanent?" Cross-reference S-3 for the enforcement mechanics; do not pre-empt it.

#### B-3-4 — No insurance, and none is proportionate
- Severity: Low (informational, so it is not re-raised)
- Where: n/a.
- Impact: for a zero-revenue free project run by an individual in Belgium, professional-indemnity or
  cyber-liability insurance would cost more than the entire project's running costs and would insure
  against exposure that is already close to zero. The packet brief itself says not to recommend
  "corporate insurance theatre".
- Fix: record "insurance considered and rejected as disproportionate at zero revenue; revisit only
  if the service takes on paid obligations" as a standing line, in `TERMS.md`'s status note or this
  action list, so a future reviewer does not re-open it.

### Already covered

- **TERMS matches what the code actually does (every processing activity, retention line, claim)** —
  `docs/audits/L.md` (done). B-3 does not re-verify fidelity; it asks a forward-looking
  liability question about clauses that are absent.
- **TERMS §10 says the source is published while the repo is private; the link 404s** —
  `docs/audits/L.md` L-1-7, `docs/audits/L.md` L-9-1. Not a liability point.
- **FS.4 recurring-tier address and VAT trigger; donations as remuneration** — `docs/audits/L.md`,
  `docs/audits/L.md` L-1-5, `docs/audits/funding.md`. B-3-2 is only the liability characterisation.
- **Moderation mechanics, ban-evasion, the absent appeal path** — `docs/audits/O.md` O-3-3;
  `docs/audits/S.md` packet (not yet run). B-3-3 is only the defamation/liability slice.
- **Sanction reason is player-visible free text; moderator should not self-identify in it** —
  `docs/audits/B.md` B-2-5.
- **Belgian counsel for TERMS / PRIVACY / the name generally** — `docs/audits/L.md` (Owner).
  B-3 contributes the specific liability agenda items for that same engagement.

### Action list

Ordered. No standing-position reversals (no company, no non-profit form, no insurance).

1. **Owner + counsel, one hour** — take B-3-1, B-3-2, B-3-3 as the agenda, alongside L-12's TERMS /
   PRIVACY / name questions so it is one engagement, not three. The three specific asks: (a)
   enforceable limitation of liability for a free non-commercial service under Belgian
   exoneration-clause law; (b) whether donations (one-off now, recurring later) change the liability
   or consumer-law position; (c) whether the permanent public sanction record needs an "assessment,
   appealable" framing.
2. if counsel recommends specific wording, add it to TERMS §7 and/or
   §6 and bump `TermsVersion.Current` with the four legal-doc copies together.
3. **Owner** — B-3-4: record the "insurance is disproportionate" decision as a standing line so it
   is not re-litigated.

No production code changed in this packet. No standing-position reversals. Only `docs/audits/B.md`
was created.

---

## B-4 — Calendar

Date: 2026-08-30
Packet: docs/audits/INDEX.md, table 9

### Verdict

Every recurring obligation this project actually has is *already implemented* — the restore drill,
the off-site dump, the ops probes, the retention sweep, the two static-analysis re-scans, the legal
drift tests, at-cost domain auto-renew. The plumbing is unusually complete for a one-person project.
What is missing is a single page that lists them with their cadence, and two structural gaps that a
list would expose: (1) GitHub silently disables every scheduled workflow after 60 days without a
repo commit, so the restore drill, the off-site backup and the ops probes can all stop at once with
no signal — a passing cron and a disabled cron look identical; and (2) none of the long-lived
provider tokens the safety nets depend on (`FLY_API_TOKEN`, `NEON_API_KEY`, the R2 keys, the
synthetic-play token) has a rotation cadence or an expiry that anything tracks, and `FLY_API_TOKEN`
has already burned once (INDEX.md:228). Neither is a live incident. The fix for both is the same
thing the packet asks for: a quarterly checklist the maintainer runs by hand, because the one
obligation that cannot be automated is checking that the automation is still armed. That checklist
is in the Action list below.

### Findings

#### B-4-1 — No maintenance calendar exists, and GitHub's 60-day cron disable can zero the whole scheduled-safety-net layer silently
- Severity: Medium
- Where: `.github/workflows/restore-drill.yml:15-18` (cron `17 4 1 * *`),
  `.github/workflows/offsite-backup.yml:14-17` (cron `41 5 * * 0`),
  `.github/workflows/ops-probes.yml:13-17` (cron `3,18,33,48 * * * *`),
  `.github/workflows/codeql.yml:15-16`, `.github/workflows/semgrep.yml:14-15`. The disable behaviour
  and its consequence are named at `docs/ops/DEPLOYMENT.md:398-400` ("a *disabled* schedule is silent
  in exactly the same way a passing one is (GitHub disables cron on repos with 60 days of no
  activity)") but only for the restore drill, not as a cross-cutting risk, and nowhere is there a
  document that enumerates all five schedules.
- Impact: For a one-maintainer hobby project that can legitimately go quiet for two months, a
  dormancy period silently switches off PITR verification, the only off-site backup path, the
  synthetic "can a game be played" probe and both code scanners — together, with the same "no email
  because nothing failed" signature. `CiWorkflowDriftTests` proves the *files* still exist and still
  declare a `schedule:` trigger; it cannot see that GitHub has administratively paused them. The
  restore drill last genuinely ran 2026-08-17 (`DEPLOYMENT.md:402`); the off-site backup and ops
  probes have never had a green run at all (finding B-4-4, and I-4 / RETENTION.md:136-143).
- Fix: add `docs/ops/MAINTENANCE-CALENDAR.md` (skeleton in the Action list) and put "open the
  Actions tab, confirm all five scheduled workflows show a run in the expected window and none is
  marked disabled" as item 1 on the quarterly pass. Re-running any workflow via `workflow_dispatch`,
  or any commit, re-arms the 60-day clock. No product code change.

#### B-4-2 — No rotation cadence or expiry tracking for the long-lived tokens the safety nets run on
- Severity: Medium
- Where: `FLY_API_TOKEN` (`.github/workflows/deploy.yml`, "has already burned once" per
  `docs/audits/INDEX.md:228`), `NEON_API_KEY` (repository secret consumed by `restore-drill.yml`,
  `offsite-backup.yml`, `ops-probes.yml`; setup at `DEPLOYMENT.md:390-394`), `R2_ACCESS_KEY_ID` /
  `R2_SECRET_ACCESS_KEY` (`DEPLOYMENT.md:446-449`), `SYNTHETIC_API_TOKEN` (a pre-created `play` API
  token, `DEPLOYMENT.md:712,721`), the log-shipper org `ACCESS_TOKEN` (`DEPLOYMENT.md:772-774`),
  `Metrics__ScrapeToken`, `Jwt__Key`. `docs/decisions/AUTH.md:190-191` records that the retired
  analytics token "did not expire, did not rotate, and had no reuse detector" and was deleted for
  exactly that shape — the same shape these tokens have now.
- Impact: A silently-expired or revoked `NEON_API_KEY` breaks the restore drill and the off-site
  backup the same way a disabled cron does — the next monthly run fails, GitHub emails once, and if
  that mail is missed the safety net is just off. A leaked `FLY_API_TOKEN` is production deploy
  access. `SYNTHETIC_API_TOKEN` sits on an API token whose row `RetentionSweeper` will delete 30
  days after any `RevokedAt`/`ExpiresAt` (`RETENTION.md:16,79`), so an accidental revoke becomes
  permanent silently. Nothing inventories these or prompts a look.
- Fix: list every operational secret once (the Action-list checklist has the table), with where it
  lives and its blast radius, and add "rotate `FLY_API_TOKEN` and `NEON_API_KEY`, confirm the
  synthetic-play token still resolves" to the annual row of the calendar. Rotation is a few
  `fly tokens create` / Neon-console / `gh secret set` commands; the missing thing is the reminder,
  not the capability.

#### B-4-3 — DMARC has sat at `p=none` since 2026-08-19 with no owner and no review date
- Severity: Low (trigger: a spoofed `@kirichess.com` mail, or a launch that never tightens)
- Where: `docs/ops/DEPLOYMENT.md:562-571` (§8.2, "read the aggregate reports for a couple of weeks
  before tightening to `p=quarantine` and then `p=reject`"), `docs/ops/PROVIDER-ACCOUNTS.md:96-99`,
  `docs/ROADMAP.md:1851`. The record is `v=DMARC1; p=none; …` at `_dmarc.kirichess.com`.
- Impact: at `p=none` nothing is enforced — a forged sender using the apex domain is not quarantined
  or rejected, which matters because `privacy@` / `abuse@` / `contact@` are the published contact
  routes and a spoof of them is a plausible social-engineering vector against the maintainer (B-2
  territory). The "couple of weeks" elapsed months ago. `rua=mailto:dmarc@kirichess.com` reports
  are being delivered to a personal inbox and, as far as any repo artifact shows, not being read.
- Fix: one calendar entry — "read the last month of DMARC aggregate reports; if clean, move
  `p=none` → `p=quarantine`, then `→ p=reject` a month later" — and a note in `DEPLOYMENT.md §8.2`
  recording the date each step was taken. This is a two-or-three-visit task, not perpetual, but it
  is stalled and unowned today.

#### B-4-4 — The off-site backup has never produced a copy, and only a manual check will notice
- Severity: Low (already a stated position; on the checklist because it is the highest-value line)
- Where: `docs/ops/DEPLOYMENT.md:472-476` ("Two dispatches on 2026-08-20 never got a runner
  (private-repo Actions quota). Parked until a green run"), `docs/decisions/RETENTION.md:136-143`
  (§6), `.github/workflows/offsite-backup.yml`. Retention once it runs: the 8 newest dumps
  (`DEPLOYMENT.md:463-465`).
- Impact: until the first green run, Neon PITR is the only copy of production — the exact
  single-provider dependency §5.3 exists to remove. The repo is now public (quota concern resolved),
  so the blocker has likely lifted, but nothing re-checks that; a green run has to be confirmed by a
  human looking. This overlaps I-4 (Backup and DR, not yet run) and L-3-3 (R2 + GitHub become
  processors on first success) — see Already covered.
- Fix: quarterly checklist item — "confirm `offsite-backup.yml` has a green scheduled run in the
  last 8 days and `kirichess/latest` in R2 points at a recent object; if it has never gone green,
  dispatch it once and watch it." Pair the first green run with the L-3-3 record updates.

#### B-4-5 — Sub-processor / DPA review is deliberately event-based, but no one is subscribed to the events
- Severity: Low
- Where: `docs/legal/ROPA.md:170-173` (§6, "Revisit when any of the following happens, and not on a
  calendar: a new sub-processor is added, a new category of data is collected, …"),
  `docs/legal/DPA-CHECKLIST.md:146-148` ("Subscribe to sub-processor change notifications where
  offered … you cannot exercise a right you never hear about"), version table at
  `DPA-CHECKLIST.md:134-140` (five DPAs filed 2026-08-17; Cloudflare v6.4 of 3 Apr 2026, Neon
  schedule of 5 Aug 2026, Sentry 5.1.0, Resend 2025-12-31), Upstash carve-out at
  `DPA-CHECKLIST.md:42-46`.
- Impact: the event-based policy is defensible for a project this size and should not be turned into
  a calendar obligation. But two of its inputs are passive: whether a processor has published a new
  DPA version, and whether a processor has added a sub-processor. If the notification subscriptions
  in `DPA-CHECKLIST.md:146` were never set up, those events arrive nowhere. The controller then
  can't demonstrate an Art. 28(2) objection right it never had the chance to exercise.
- Fix: one low-frequency checklist line — "confirm sub-processor-change notifications are still
  subscribed for Cloudflare, Neon, Resend, Sentry, Fly; glance at each DPA page for a version bump;
  if Redis was provisioned, file the Upstash DPA and add its ROPA §4 row." Annual is enough.

#### B-4-6 — Domain and registrar-card health is unmonitored
- Severity: Low (trigger: an expired payment card on the Cloudflare account at renewal time)
- Where: Cloudflare Registrar, at-cost renewals with no second-year markup. `kirichess.com` was
  bought and the zone is on Cloudflare. TLS is auto-managed: Fly terminates for both apps (`DEPLOYMENT.md:18-20`),
  Cloudflare at the edge. The edge-site Better Stack monitor (`infra/betterstack/main.tf`,
  `edge_site`) would eventually notice a dead apex but gives no lead time.
- Impact: Cloudflare Registrar auto-renews, so this is a low-probability path, but a failed
  auto-renew (lapsed card, billing dispute) is silent until the domain stops resolving — and the
  domain string is load-bearing for CORS, OAuth redirect, mail auth and every email link.
  TLS auto-renewal similarly depends on the Fly apps keeping their
  DNS records pointed; a records change can quietly block a Fly cert renewal.
- Fix: checklist line — "Cloudflare dashboard: confirm `kirichess.com` shows auto-renew on with a
  valid card and an expiry > 6 months out; confirm Fly `certs list` shows both hostnames valid." No
  new monitor needed at this scale; the yearly glance is proportionate.

### Already covered

- **R2 + GitHub become processors on the first successful off-site backup; PRIVACY §4 / ROPA §4 /
  ROPA §3 need rows and a bucket lifecycle rule** — `docs/audits/L.md` (L-3-3). Not re-reported;
  B-4-4 only tracks the *run*, not the records.
- **Backup & DR depth (Neon PITR proven, offsite plumbing, provider-account-deletion scenario)** —
  `docs/audits/INDEX.md` I-4 (not yet run) and `docs/decisions/RETENTION.md` §6.
- **`/metrics` dark since 2026-08-15, ops-probes waiting on the post-1-Sep dispatch, no absence
  alert, no on-call rotation** — `docs/audits/I.md` (I-5-2, I-5-3, Already-covered §).
- **Edge-site monitor's `302` allowance has no removal trigger after Cloudflare Access comes off** —
  `docs/audits/I.md` (I-5-5); belongs on the DH.3 / Todo #12 checklist, not this one.
- **`ci.yml` / `deploy.yml` / `restore-drill.yml` missing `permissions:` and `timeout-minutes`;
  `restore-drill.yml` stale `setup-dotnet` pin** — `docs/audits/C.md (section INF-1)` (INF-1-2,
  INF-1-3, INF-1-4).
- **Recurring donation tiers already ship, so FS.4's address/VAT trigger has fired** —
  `docs/audits/L.md`, `docs/audits/L.md`. A money-changes-hands review is ROPA §6's own trigger,
  not a calendar item.
- **Kill-switch levers (disable matchmaking / signups / API without a deploy)** —
  `docs/audits/S.md`.
- **Retention sweep** runs daily in-process (`RetentionSweeper`, `RETENTION.md` §§2-3,
  `DEPLOYMENT.md` §5.1), self-observing via one log line + `kirichess.retention.rows_deleted`; no
  maintainer action, listed in the checklist only as a "confirm it is still `Enabled`" glance.
- **Dependency / SBOM currency, npm-audit and Trivy waiver dates** — `docs/audits/INDEX.md` I-11 and
  readiness **DEPS** (not yet run). The quarterly checklist references it; it does not re-audit it.

### Action list

Ordered. All items are Owner (maintainer) unless marked. No standing-position reversals.

1. create `docs/ops/MAINTENANCE-CALENDAR.md` from the skeleton below (this is the packet
   deliverable). Link it from `docs/README.md` and from `docs/ops/DEPLOYMENT.md` §12.
2. **Owner, once** — set up the DMARC-report habit (B-4-3): read the backlog of aggregate reports at
   `dmarc@kirichess.com`; if clean, move to `p=quarantine`, record the date in `DEPLOYMENT.md` §8.2.
3. **Owner, once** — confirm sub-processor-change notification subscriptions exist for all five
   processors (B-4-5, `DPA-CHECKLIST.md:146`).
4. **Owner, once** — write the operational-secret inventory into the calendar's token table (B-4-2),
   then do a first rotation of `FLY_API_TOKEN` and `NEON_API_KEY`.
5. **Owner, quarterly thereafter** — run the checklist below.
6. **Owner** — on the first green `offsite-backup.yml` run, apply the L-3-3 record updates
   (PRIVACY §4, ROPA §3/§4, R2 bucket lifecycle rule).

---

#### Skeleton: `docs/ops/MAINTENANCE-CALENDAR.md`

> One page. Every recurring obligation, its cadence, where it is enforced, and whether anything
> automated guards it. The rule of thumb: **if it is automated, the maintainer's job is to confirm
> the automation is still armed** — a passing cron and a GitHub-disabled cron look identical from
> outside (`DEPLOYMENT.md:398-400`).

##### Automated — verify each quarter that it is still running

| Obligation | Cadence | Enforced by | Guard | Confirm |
| --- | --- | --- | --- | --- |
| PITR restore drill (branch + boot + schema + replay) | Monthly, 1st 04:17 UTC | `.github/workflows/restore-drill.yml` (`scripts/restore-drill.sh`) | cron + `CiWorkflowDriftTests`; GitHub email on failure only | Actions tab: green run this month, not disabled. Last real pass: 2026-08-17 |
| Off-site dump to Cloudflare R2 (+ restore-verify) | Weekly, Sun 05:41 UTC | `.github/workflows/offsite-backup.yml` (`scripts/offsite-backup.sh`) | cron + `CiWorkflowDriftTests`; GitHub email on failure | Green run in last 8 days; `kirichess/latest` in R2 is recent. **Never green yet — see B-4-4** |
| Synthetic "can a game be played" + Neon storage/compute watch | Every 15 min | `.github/workflows/ops-probes.yml` (`synthetic-play.sh`, `neon-watch.sh`) + Better Stack heartbeats | cron + `CiWorkflowDriftTests` + `BetterStackAlertingDriftTests` | Better Stack heartbeats green; no open incident. First dispatch was gated on post-1-Sep-2026 quota |
| CodeQL scan (C#, JS/TS) | Weekly, Mon 03:30 UTC + every PR/push | `.github/workflows/codeql.yml` | cron + `CiWorkflowDriftTests` | Actions tab: weekly run present |
| Semgrep scan | Weekly, Mon 06:00 UTC + every PR/push | `.github/workflows/semgrep.yml` | cron + `CiWorkflowDriftTests` (`--error`, digest-pinned) | Actions tab: weekly run present |
| Data-retention sweep (5 tables) | Daily, in-process | `RetentionSweeper` (`RETENTION.md` §§2-3) | log line + `kirichess.retention.rows_deleted` counter | `Retention__Enabled` still `true`; sweep log line present |
| Edge monitors (`api.kirichess.com`, `kirichess.com`) | Every 3 min | `infra/betterstack/main.tf` | `BetterStackAlertingDriftTests` | Monitors green |
| Legal-doc 4-copy consistency (TERMS/PRIVACY md + tsx + `TermsVersion.Current`) | Every CI run | `LegalDocumentDriftTests` | test suite | Green (only trips on an edit) |
| GitHub Action SHA pins (Dependabot bumps) | Continuous | Dependabot `github-actions` ecosystem | `CiWorkflowDriftTests.Every_workflow_pins_...` | No stale open Dependabot PRs |

##### Manual — nothing watches these

| Obligation | Cadence | Where it lives | Notes |
| --- | --- | --- | --- |
| Confirm all 5 scheduled workflows are not GitHub-disabled | **Quarterly** | Actions tab | 60 days of no commits disables every cron at once (B-4-1). Any `workflow_dispatch` or commit re-arms it |
| DMARC policy progression `p=none` → `quarantine` → `reject` | Until at `reject`, then done | `DEPLOYMENT.md` §8.2; record dates there | Read `dmarc@` aggregate reports first (B-4-3) |
| Operational-secret review (see token table below) | Quarterly glance / annual rotate | this file | `FLY_API_TOKEN` already burned once (INDEX.md:228) |
| Rotate `FLY_API_TOKEN`, `NEON_API_KEY`; confirm `SYNTHETIC_API_TOKEN` still resolves | Annual | `gh secret set`, `fly tokens create`, Neon console | A revoked API token row is deleted 30 days later (`RETENTION.md:79`) |
| Sub-processor / DPA review (version bump? new sub-processor? notifications still subscribed?) | Annual, or on any ROPA §6 event | `DPA-CHECKLIST.md`, `ROPA.md` §4 | Provision Redis ⇒ file Upstash DPA + ROPA row (`DPA-CHECKLIST.md:42-46`) |
| TERMS / PRIVACY re-read against what the code actually does | Annual, or on any behaviour change | `docs/legal/`, bump `TermsVersion.Current` + all 4 copies together | `LegalDocumentDriftTests` catches divergence, not staleness |
| Domain auto-renew + registrar card valid; Fly `certs list` both hostnames valid | Annual | Cloudflare dashboard, `fly certs list` (B-4-6) | Registrar is Cloudflare, at-cost auto-renew |
| Dependency currency (NuGet / npm / e2e), re-date Trivy + npm-audit waivers | Quarterly | INDEX.md I-11 / readiness DEPS | Weekly CodeQL/Semgrep is not a dependency-version check |
| Dormant-account scan (`GET /api/moderation/retention/dormant`) | Quarterly, **post-launch only** | `RETENTION.md` §4 | No auto-erasure by decision; review trigger is verified mail + real signups |
| Better Stack log-retention plan still adequate | Annual | `DEPLOYMENT.md` §11 ("no retention policy chosen beyond plan default") | |
| Neon / Fly / Sentry / Resend free-tier headroom vs. actual usage | Quarterly | provider dashboards | INDEX.md I-9; a free-tier trip is silent until it bites |

##### Operational-secret inventory (fill in and keep current — B-4-2)

| Secret | Lives in | Used by | Blast radius if leaked | Rotatable via |
| --- | --- | --- | --- | --- |
| `FLY_API_TOKEN` | GitHub repo secret | `deploy.yml` (both jobs) | Production deploy access | `fly tokens create deploy` → `gh secret set` |
| `NEON_API_KEY` | GitHub repo secret | `restore-drill.yml`, `offsite-backup.yml`, `ops-probes.yml` | Neon branch create/delete (not DB passwords) | Neon console → `gh secret set` |
| `R2_ACCESS_KEY_ID` / `R2_SECRET_ACCESS_KEY` | GitHub repo secrets | `offsite-backup.yml` | Read/write the backup bucket | Cloudflare R2 → API Tokens |
| `SYNTHETIC_API_TOKEN` | GitHub repo secret | `ops-probes.yml` synthetic play | Play API as one throwaway account | Recreate `play` token on that account |
| log-shipper `ACCESS_TOKEN` | Fly app secret (`kirichess-log-shipper`) | Vector log shipper | Read-only org log stream | `fly tokens create readonly` |
| `Metrics__ScrapeToken` | Fly app secret (`kirichess-api`) | Better Stack Prometheus scrape | Read `/metrics` | `fly secrets set` + Better Stack source |
| `Jwt__Key` | Fly app secret (`kirichess-api`) | Token signing | Forge access tokens (rotation logs everyone out) | `fly secrets set` (`openssl rand -base64 48`) |
| `NEON_WATCH_HEARTBEAT_URL` / `SYNTHETIC_HEARTBEAT_URL` | GitHub repo secrets | `ops-probes.yml` | Suppress a heartbeat alarm | `terraform output` → `gh secret set` |

No production code changed in this packet. No standing-position reversals. Only `docs/audits/B.md`
was created.

