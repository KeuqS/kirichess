# O — Open source and community

Merged packet reports for category **O**. Individual packets were `docs/audits/O-N.md`; they now live as sections below. Index: [INDEX.md](INDEX.md).

---

## O-1 — License stack
Date: 2026-08-21
Packet: docs/audits/INDEX.md

### Verdict

The four files agree with each other, and **L-9** reached the same conclusion from the attribution
side an hour before this packet ran: LICENSE is stock Apache-2.0, NOTICE carves out CC BY 4.0 for the
manual and GPL-3.0 for the vendored engine, README repeats both accurately, and CONTRIBUTING states
a matching inbound rule. The large finding, that the licence grant has no recipient because the
repository is private, is already owned twice over by **L-1-7** and **L-9-1**, and is not re-argued
here.

What is left for a licence-stack packet is one structural observation the individual reports could
not make, because each of them found one instance of it. NOTICE opens with a default clause, "Unless
a path below says otherwise, this repository is licensed under the Apache License, Version 2.0",
which claims everything not explicitly carved out. Four audits running the same week have now each
found something it claims and should not: ten sound files with no recorded provenance (**L-9-3**), a
self-hosted OFL font (**L-9-6**), LGPL-3.0 libvips inside the frontend build (**L-10-5**, **O-6-1**),
and a build-time dependency that is not open source at all (**O-6-2**). Individually each is a Low.
Together they say the exceptions list was written once, against what was in the tree that day, and
nothing keeps it current. The two findings below are the only defects in this packet that no other
report carries.

### Findings

#### O-1-1 — docs/README credits LICENSE with a carve-out it does not contain
- Severity: Low
- Where: `docs/README.md:35-37`
- Impact: "is named in [NOTICE](../NOTICE) and [LICENSE](../LICENSE) as the CC BY 4.0 manual".
  LICENSE is the unmodified 202-line Apache-2.0 text, appendix included, and names nothing at all.
  Only NOTICE carries the carve-out. Small in itself; it matters because it is a claim about where
  the licensing lives, made in the document whose whole job is telling people where things live, and
  because anyone verifying the CC BY split by opening LICENSE first will conclude the split is
  undocumented.
- Fix: Drop the LICENSE reference from that sentence.

#### O-1-2 — The CC BY boundary is drawn in website pages, not repo paths
- Severity: Low
- Where: `NOTICE:28-32`, `ui/scripts/gen-rules.mjs:23`, `ui/messages/en.json` (`rules` and `learn`
  namespaces), `ui/lib/rules.generated.ts`
- Impact: NOTICE names three CC BY carriers. Two are repo paths (`docs/RULES.md`,
  `config/kirichess.rules.json`); the third is a pair of URLs, "the /rules and /learn pages on the
  KiriChess website". Someone reading the tree, which is what a fork does, cannot map that onto
  files. In this repository the manual text also lives in `ui/lib/rules.generated.ts`, generated from
  the same config alongside `docs/RULES.md`, and in the `rules` and `learn` namespaces of
  `ui/messages/en.json`. A fork honouring the split has to guess which of those it may adapt under
  CC BY and which are Apache-2.0 code. **L-9-7** covers the related but separate problem that the
  `/learn` page carries no notice; this is about the repo-side map.
- Fix: One line in NOTICE naming `ui/lib/rules.generated.ts` and the `rules` and `learn` message
  namespaces alongside the page URLs.

### Already covered

Nine findings this packet would otherwise have filed are already carried elsewhere. One line each,
no re-reporting:

- TERMS section 10 asserting the source "is published" while the repository is private: **L-1-7**.
- The footer, `/about`, `/developers`, `NOTICE:6` and `CITATION.cff:8` all linking a URL that 404s:
  **L-9-1**.
- `CITATION.cff` flattening the three-way split to Apache-2.0: **L-9-5**.
- Ten shipped audio files with no recorded provenance: **L-9-3** (closed 2026-09-04: Kenney CC0).
- Montserrat served self-hosted under OFL-1.1 with no notice: **L-9-6**.
- `/learn` designated CC BY and shipping without the notice: **L-9-7**.
- The press kit shipping the reserved mark and granting nothing: **L-9-4**.
- No copy of the GPL-3.0 text beside the conveyed Stockfish binary, and the Corresponding Source
  directions not identifying this build: **L-10-1**, **L-10-2**.
- The copyleft hunt across both dependency trees: **L-10-5**. The two licences it did not surface,
  because they are not copyleft, are **O-6-2** and **O-6-5**.

### Action list

1. One consolidated "third-party components" section in NOTICE, written once, covering
   every item the four reports found: Stockfish GPL-3.0 (already present, plus the licence text per
   **L-10-1**), Montserrat OFL-1.1 (**L-9-6**), ~~the sound files once provenance is settled
   (**L-9-3**)~~ (closed 2026-09-04: Kenney CC0 in NOTICE + `KENNEY.LICENSE`), sharp and libvips LGPL-3.0-or-later plus lightningcss MPL-2.0 (**L-10-5**,
   **O-6-1**), `@sentry/cli` FSL-1.1-MIT as build-time only (**O-6-2**), and WebPush once its licence
   is resolved (**L-10-5**, **O-6-4**). Editing NOTICE once beats six passes.
2. `docs/README.md` LICENSE reference (O-1-1).
3. CC BY repo paths in NOTICE (O-1-2), folded into action 1.
4. Everything else in this packet is somebody else's action list. **L-1-7** and **L-9-1** are the
   two that matter and both are owner decisions.

---

## O-2 — Contributor licensing
Date: 2026-08-21
Packet: docs/audits/INDEX.md

### Verdict

CONTRIBUTING states the right rule in the right place, and the decision not to run a CLA is correct
for a project this size: a CLA buys relicensing power that a single owner who already holds the
copyright does not need, at the cost of turning every drive-by fix into paperwork. Two gaps, both
small and both cheap. Nothing records a contributor's agreement per contribution, so the CC BY half
of the rule rests entirely on the contributor having read a file they are not required to open; and
the CC BY path CONTRIBUTING names is a generated file that the same repository tells people never to
edit. Neither has bitten yet, and neither can bite while the repository is private and no outside
pull request is possible.

### Findings

#### O-2-1 — The CC BY half of the inbound rule has no fallback if nobody reads CONTRIBUTING
- Severity: Medium (trigger: the first pull request from someone other than the owner)
- Where: `CONTRIBUTING.md:3`, `.github/pull_request_template.md:1-10`
- Impact: CONTRIBUTING says "By opening a pull request you license your contribution under
  Apache-2.0 ... If the change edits the rules manual ... that part is licensed under CC BY 4.0
  instead. There is no CLA." Inbound-equals-outbound by notice is a normal and defensible way to run
  a project. The weakness is the second sentence. The backstop when a contributor never reads
  CONTRIBUTING is GitHub's Terms of Service section D.6, which licenses submitted contributions
  under the terms of *the repository's licence* to the extent the repository has one. That produces
  Apache-2.0 and nothing else. It does not produce a CC BY grant on a rules-manual edit, so for
  exactly the material the project treats as separately licensed, the only evidence of agreement is
  a file the contributor may never have opened. The PR template, which every contributor does see,
  has four test-plan checkboxes and no licence line.
- Fix: One checkbox on `.github/pull_request_template.md`: "I license this contribution under
  Apache-2.0, and any rules-manual change under CC BY 4.0 (see CONTRIBUTING.md)." That is a
  per-contribution record, not a CLA, and it costs a contributor one click.

#### O-2-2 — CONTRIBUTING points rules-manual contributors at a generated file
- Severity: Low
- Where: `CONTRIBUTING.md:3` against `CONTRIBUTING.md:42-46`, `docs/README.md:33-37`,
  `.github/workflows/ci.yml` (the `gen:rules:check` step)
- Impact: The licence sentence names `docs/RULES.md` first as the thing a contributor might edit.
  Forty lines later the same file tells them that changing rules means editing
  `config/kirichess.rules.json` and regenerating. `docs/README.md` is blunter: "Do not hand-edit
  it", and CI fails if the generated copy drifts. A contributor who follows the licence sentence
  literally opens the wrong file and has their PR bounced by a check they were never warned about.
- Fix: Reorder the sentence so `config/kirichess.rules.json` is the editable path and `docs/RULES.md`
  is named as its generated output.

#### O-2-3 — "No DCO" is currently an absence, not a recorded decision
- Severity: Low (trigger: a contributor asks, or a contribution arrives from someone acting for an
  employer)
- Where: `CONTRIBUTING.md:3`
- Impact: CONTRIBUTING says there is no CLA, which is a stated decision. It says nothing about a
  Developer Certificate of Origin, so the absence reads as an oversight rather than a choice. The
  practical difference matters in one case only: a contributor who does not personally own what they
  are contributing. A DCO sign-off is the lightweight instrument for that, and adopting one is a
  `git commit -s` habit, not bureaucracy. At this size the honest answer is probably still "not
  yet".
- Fix: One clause in the same sentence: no CLA and no DCO sign-off required; contributors are taken
  at their word that they can license what they submit. Then the absence is a position.

### Already covered

- **L-1-7** and **L-9-1**: no external pull request can arrive at all today, because the repository is private.
  Everything in this packet is dormant until that changes, which is the correct time to fix it, not
  a reason to skip it.
- The Apache-2.0 / CC BY split itself, and whether it is stated consistently across the four
  licence-bearing files, is **O-1**.
- Trademark, which is the one thing no inbound licence grant touches, is **L-9** and `NOTICE:18-22`.

### Action list

1. Licence checkbox on the PR template (O-2-1). Two lines, no behaviour change.
2. Reorder the CC BY path sentence in CONTRIBUTING (O-2-2).
3. **Owner.** Decide the DCO position, then one clause in CONTRIBUTING (O-2-3).

---

## O-3 — Community health
Date: 2026-08-21
Packet: docs/audits/INDEX.md

### Verdict

The four documents a project this size should have all exist and are the right ones: Contributor
Covenant 2.1, a security policy that says the right things about scope and disclosure, a bug issue
template that routes vulnerabilities away from public issues, and a PR template. Every one of them
lives inside a private repository. The packet line assumed the opposite ("Security reporting works
now that the repo is public"), and it does not: the advisory URL that three separate files point at
returns 404 to anyone who is not the owner, the site publishes no security contact of any kind, and
the Discord invite in the footer is a live community space whose Code of Conduct nobody in it can
read. The documents are fine. Their distribution is the whole finding.

### Findings

#### O-3-1 — After Access comes off, a public service with no reachable way to report a vulnerability
- Severity: High
- Where: `SECURITY.md:7-9`, `.github/ISSUE_TEMPLATE/config.yml:4`,
  `.github/ISSUE_TEMPLATE/bug.yml:8`, `ui/components/Footer.tsx:58-63`, `ui/public/` (no
  `.well-known/security.txt`), `ui/app/` (no `security` route)
- Impact: All three pointers name
  `https://github.com/MichielGekiere/kirichess/security/advisories/new`. GitHub's private
  vulnerability reporting form is only reachable by users who can see the repository, and the
  repository is private and stays private (`docs/ROADMAP.md:54`, `docs/ROADMAP.md:1871`). SECURITY.md
  itself is equally unreachable, so its email fallback is invisible too. On the site, the footer's
  legal row links only `/terms` and `/privacy`, there is no `/security` route, and there is no
  `ui/public/.well-known/security.txt`. The result after item #12 removes Cloudflare Access: a
  public chess service handling accounts, tokens and payment redirects, with no published address
  for someone who finds a flaw. The realistic outcome is not a quiet report, it is a public tweet
  or nothing at all. `contact@kirichess.com` is in the footer as a general contact, which is a
  usable channel that no document tells a researcher to use for this.
- Fix: Add `ui/public/.well-known/security.txt` (RFC 9116: `Contact: mailto:contact@kirichess.com`,
  `Preferred-Languages`, `Expires`). It is a static file, it is the convention researchers actually
  check, and it costs one file. Optionally mirror the scope paragraph from SECURITY.md onto a small
  `/security` page. Keep SECURITY.md as-is for the day a public snapshot exists.

#### O-3-2 — The Discord is a live community space governed by a document its members cannot read
- Severity: Medium
- Where: `ui/components/Footer.tsx:27` (`https://discord.gg/GRusewDw4`),
  `docs/plans/launch/POST-DRAFT.md:123`, `CODE_OF_CONDUCT.md:51-57`, `CODE_OF_CONDUCT.md:62-63`
- Impact: The invite is live in the site footer and the launch post ends by pointing at it, so it is
  a public front door that opens before the first game does for some visitors. The Code of Conduct
  says it "applies within all community spaces", which nominally covers Discord, and names
  `contact@kirichess.com` for reports. Nobody in the server can read either sentence: the CoC exists
  only in the private repository, and nothing in the server or on the site links to it. A server
  with no visible rules and no visible report address is the shape that goes wrong quietly, and the
  moderation tooling the project does have (`src/KiriChess.Server/Moderation/`) governs the game, not
  a chat platform.
- Fix: Paste a short version of the CoC standards plus the report address into a Discord rules
  channel, and set it as the server's rules screen so joining requires acknowledging it. Zero code.
  Same text as `CODE_OF_CONDUCT.md:28-37` and the enforcement address.

#### O-3-3 — The enforcement ladder describes a body that does not exist
- Severity: Low
- Where: `CODE_OF_CONDUCT.md:39-49`, `CODE_OF_CONDUCT.md:69-112`
- Impact: The unmodified Contributor Covenant speaks of "community leaders" in the plural,
  distributes responsibility among them, and lays out a four-rung ladder ending in a permanent ban.
  There is one person. Nothing in it is wrong, and swapping in a hand-written CoC would be worse
  than the boilerplate. What is genuinely missing is the sentence that tells a reporter what they
  are actually dealing with: who enforces, that the enforcer and the person they may be reporting
  about can be the same person, and that there is no appeal body. Saying so is more credible than a
  ladder implying a committee.
- Fix: One line under Enforcement: enforcement is the maintainer, reports go to
  `contact@kirichess.com`, and there is no separate appeal body at this size.

#### O-3-4 — SECURITY.md promises a response window with a bus factor of one
- Severity: Low (trigger: the maintainer is away when a report lands)
- Where: `SECURITY.md:11-12`, `SECURITY.md:26-29`
- Impact: "You should hear back within a few days" is a commitment made by one person with no
  backup. The supported-versions section is correct and refreshingly honest for a hobby deploy. The
  response promise is the one line that can quietly become false, and a researcher who hears nothing
  for two weeks reasonably concludes the channel is dead and discloses publicly.
- Fix: Either soften to a best-effort phrasing, or keep the promise and treat it as an input to
  **B-1** (bus factor), which is where "what happens when nobody is reading the mailbox" belongs.

### Already covered

- The repository staying private is settled policy (10c.2, `docs/ROADMAP.md:1871`). Nothing here
  proposes reversing it; every fix above works with a private repository.
- The **INDEX.md** line for this packet says "Security reporting works now that the repo is public".
  That premise is stale, and O-3-1 is the consequence. Worth correcting in the index when it is next
  touched.
- First-party vulnerability hunting is **S-1** and the security review. This packet is
  only about whether a stranger has a way to tell us.
- Maintainer harassment exposure through these same addresses is **B-2**.

### Action list

1. `ui/public/.well-known/security.txt` (O-3-1). Should land before item #12.
2. **Owner.** Discord rules channel carrying the CoC summary and the report address (O-3-2).
3. Enforcement clarification line in `CODE_OF_CONDUCT.md` (O-3-3).
4. **Owner.** Decide whether the SECURITY.md response window stays as written (O-3-4).
5. Correct the O-3 row premise in `docs/audits/INDEX.md` next time that file is edited.

---

## O-4 — Public-repo hygiene
Date: 2026-08-21
Packet: docs/audits/INDEX.md

### Verdict

This is the packet that came back cleanest, and the reason is that someone already did the work:
`.gitignore` covers the four things that would hurt, `TrackedTreeHygieneTests` is a real test rather
than a note, `.gitleaks.toml` carries dated allowlist entries with reasons instead of blanket
suppressions, and a pickaxe sweep of all 433 commits for live-credential patterns turned up nothing
but placeholder strings and one substring inside minified Stockfish. The known exposure is personal
email addresses in commit authorship, which is documented, decided, and the reason the repository
stays private. What is missing is the other half of that decision: the clean public snapshot that
three documents refer to has no procedure, no owner and no queue entry, so the single distribution
channel the open-source story has is currently a clause rather than a plan.

### Findings

#### O-4-1 — The hygiene sweep the snapshot depends on has never been written down
- Severity: Medium
- Where: `docs/ROADMAP.md:26`, `docs/ROADMAP.md:1880`, `docs/game/CLAIMS.md:28`,
  `docs/game/CLAIMS.md:30`, `tests/KiriChess.Server.Tests/TrackedTreeHygieneTests.cs:8-12`
- Impact: **L-9-2** found that the one documented instruction, `docs/game/CLAIMS.md:30`'s "run
  `scripts/export-public-snapshot.ps1`", points at a script that is not in the repository, and it
  hands the sweep itself to this packet. That sweep does not exist in any form: no list of what the
  snapshot contains and what it strips, no statement of what its first commit says (the timestamp
  `AD.12` cares about), no check to run before cutting it, and no row in the launch queue or the
  after-1-Sep list. So the position is worse than a missing script. Even if someone rewrote the
  script tomorrow, nothing records what it is supposed to do. The cost is not risk today, it is that
  the snapshot eventually gets cut in a hurry, by hand, against a mental checklist, which is the
  situation the Wrangler-cache commit of 2026-08-20 came out of.
- Fix: One short `docs/ops/PUBLIC-SNAPSHOT.md`: what ships (working tree at a tagged commit), what is
  stripped (`.private/`, `.mailmap`, `.claude/settings.local.json`, every `.env*` except the example,
  `docs/legal/*.pdf`), what must be verified before pushing (the three `TrackedTreeHygieneTests`
  facts, a full-history gitleaks run per O-4-3, and that no file names a provider account id), and
  what the snapshot's own README says about its relationship to this repository. Then either restore
  the script against that document or delete the CLAIMS.md line pointing at it, which is **L-9-2**'s
  half.

#### O-4-2 — `.mailmap` is ignored, so it protects nothing and reaches nobody
- Severity: Low
- Where: `.gitignore:94-96`, `.mailmap:1-4`,
  `tests/KiriChess.Server.Tests/TrackedTreeHygieneTests.cs:26-31`
- Impact: The file's own first line says it exists for "git shortlog / GitHub contributor display".
  It is gitignored on purpose, and a test asserts it stays that way, which is the right call given
  it contains three personal addresses. But an untracked file is only read by the working tree it
  sits in: it never reaches GitHub, never reaches a clone, and never reaches the snapshot. GitHub
  additionally does not apply `.mailmap` to its contributor graph at all. So the file does exactly
  one thing, tidy the owner's local `git shortlog`, while its comment claims a scope it cannot have.
  The identity question it gestures at is real, and O-4-1 is where it actually gets decided.
- Fix: Reword the comment to what it does (local shortlog only), or delete the file and let O-4-1
  own the identity decision.

#### O-4-3 — Nothing runs a full-history secret scan, and the snapshot is when it matters
- Severity: Low
- Where: `.github/workflows/secret-scan.yml:19-22`, `.gitleaks.toml:14-31`
- Impact: The gitleaks job checks out with `fetch-depth: 0` and scans what a push or PR introduces,
  which is the right default for everyday work. The allowlist in `.gitleaks.toml` exists precisely
  because a full-repository scan does hit older strings: two dead placeholder tokens and the
  Wrangler cache path are called out by commit and by date. That is good practice and it also shows
  nobody is running the full scan on a schedule. It only genuinely matters once, at the moment a
  snapshot is cut, and at that moment there is no step that runs it.
- Fix: Make `gitleaks detect --log-opts="--all"` a required step in the O-4-1 procedure. No new
  workflow needed.

#### O-4-4 — The free gitleaks tier depends on the repository staying under a personal account
- Severity: Low (trigger: the repository or the snapshot moves under a GitHub organization)
- Where: `.github/workflows/secret-scan.yml:24-26`
- Impact: The comment states it plainly: gitleaks-action only needs `GITLEAKS_LICENSE` for private
  repositories owned by an Organization, and this one is a personal-account repository. If a
  snapshot or the working repository ever moves under an org, that job starts failing. The comment
  is the mitigation, and it is in the right file, so this is a note rather than a defect.
- Fix: Nothing now. Carry it into the O-4-1 procedure so whoever cuts the snapshot decides
  deliberately where it lives.

### Already covered

- **Verified, no finding:** a pickaxe sweep of all refs for `sk_live`, `sk_test`, `whsec_`, `FlyV1`,
  `fo1_`, `AKIA`, `postgres://`, `postgresql://` and `neon.tech` produced only placeholder strings in
  scripts and documentation, one API URL in a workflow, and one accidental substring inside minified
  `stockfish.js`. `.claude/settings.local.json` has never been committed (zero commits touch it), so
  the Cloudflare-token incident recorded in `.claude/rules/security.md` stayed local.
- The personal addresses in commit authorship (370 commits from one address, 35 from another) are
  known, documented in `.mailmap` and `docs/game/CLAIMS.md:28`, and are the reason for the
  visibility decision. Not re-reported.
- The missing `scripts/export-public-snapshot.ps1` that `docs/game/CLAIMS.md:30` tells the reader to
  run is **L-9-2**. O-4-1 is the content that script would need, not its absence.
- That the site currently claims the source is published: **L-1-7** and **L-9-1**.
- The repository staying private is settled policy (10c.2). Nothing here reverses it.
- Secrets held in provider accounts rather than in git are **I-7** and `docs/ops/PROVIDER-ACCOUNTS.md`.

### Action list

1. Write `docs/ops/PUBLIC-SNAPSHOT.md` (O-4-1), including the
   full-history scan step (O-4-3) and the org-ownership decision (O-4-4).
2. **Owner.** Add one ROADMAP row pointing at it, in the after-1-Sep block rather than the launch
   queue.
3. Reword or remove `.mailmap` (O-4-2).

---

## O-5 — Forkability
Date: 2026-08-21
Packet: docs/audits/INDEX.md

### Verdict

The engineering side of forkability is in better shape than most projects this age: both images
build from the repository root with no private registry, `docker-compose.prod.yml` runs the real
production images with the migration deliberately behind a profile, the PGN `Site` tag is derived
from configuration rather than hardcoded to kirichess.com, and DEPLOYMENT.md section 6 documents
running the production stack locally. What is missing is everything that turns "the code runs" into
"someone else can run it as their own thing". NOTICE states the rename obligation and no file
anywhere tells a fork how to discharge it, the self-host path is documented only inside a
Fly-oriented ops runbook, and the one sentence a respectful fork most wants, how to credit the
variant while using a different name, does not exist. All of it is moot until the repository is
reachable at all.

### Findings

#### O-5-1 — NOTICE forbids the name and nothing says what carries it
- Severity: Medium
- Where: `NOTICE:18-22`, `README.md:41-48`, `ui/components/BrandMark.tsx`, `ui/public/logo.svg`,
  `ui/public/logo-wordmark.png`, `ui/app/icon.svg`, `ui/messages/en.json` (`Footer.brand`,
  `Footer.tagline`), `press-kit/logo/`
- Impact: The trademark paragraph is clear about the obligation: you may not run a competing service,
  or distribute a fork, under the name KiriChess. It gives no way to comply. Around 291 tracked files
  under `ui/`, `src/` and `config/` contain the string, and they are not one category: some are brand
  (the marks, the wordmark, the footer brand and tagline copy, the press-kit logo), some are inert
  identifiers a fork can keep or change freely (compose `container_name`, the `kirichess` database
  name, `Jwt:Issuer` and `Jwt:Audience`, assembly and project names, the rules config filename), and
  some are the CC BY attribution that a fork is supposed to keep. Without that split written down, a
  careful fork either over-renames (and breaks its own JWTs and migrations) or under-renames (and
  breaches the one thing NOTICE actually asked for).
- Fix: A "Running your own instance" section in README, roughly ten lines: the four asset paths and
  two message keys to replace, the identifiers that are safe to leave alone, and the note that
  `docs/RULES.md` attribution stays. Draft it from the file list above; the trademark wording is
  the owner's.

#### O-5-2 — The self-host path is documented only inside the Fly ops runbook
- Severity: Medium
- Where: `README.md:17-27`, `docs/ops/DEPLOYMENT.md:477` (section 6), `docker-compose.prod.yml:33-34`,
  `docker-compose.prod.yml:64-66`
- Impact: README documents one thing, `docker compose up`, which is the development stack: an SDK
  image running `dotnet watch` with the whole repository bind-mounted and `SeedDemoData=true`. That
  is the right first command for a contributor and the wrong one for anyone standing up an instance.
  The production compose file exists and is well-commented, but a reader only meets it inside
  DEPLOYMENT.md, a document about Fly machines, Neon branches, Better Stack monitors and Cloudflare,
  where a self-hoster is not looking. They will also miss two things they cannot guess: migrations
  do not run at boot (`RunMigrationsOnStartup=false`) and are a separate
  `--profile migrate run --rm migrate` invocation, and `NEXT_PUBLIC_*` values are baked at image
  build time, so getting them wrong means rebuilding, not restarting.
- Fix: Four or five lines in README pointing at `docker-compose.prod.yml` and naming those two
  gotchas, with a link to DEPLOYMENT.md section 6 for the longer version.

#### O-5-3 — `.env.example` does not say which values a real instance must set
- Severity: Low
- Where: `.env.example:1-9`, `README.md:19`, `src/KiriChess.Server/Games/PgnBuilder.cs:111`
- Impact: The file is thorough about what each variable does and honest about which integrations
  fail closed when empty, which is genuinely better than most. What it does not do is separate "set
  this or nothing works" from "leave empty unless you want the feature". README names only
  `Jwt__Key`. A fork that leaves `Frontend__BaseUrl` at its localhost default sends account emails
  with localhost links, and leaves every exported game's PGN `Site` tag pointing at a replay URL
  nobody can open, since that tag is built from the base URL. Both fail silently and late.
- Fix: A three-line "minimum for a real instance" block at the top of `.env.example`:
  `Jwt__Key`, `ConnectionStrings__Postgres`, `Frontend__BaseUrl`, `NEXT_PUBLIC_API_BASE`,
  `NEXT_PUBLIC_SITE_URL`, `Cors__AllowedOrigins__0`.

#### O-5-4 — There is no sentence telling a renamed fork how to credit the variant
- Severity: Low
- Where: `NOTICE:18-22`, `NOTICE:38-44`
- Impact: NOTICE gives the CC BY attribution string for the rules text, which covers quoting the
  manual. It does not cover the case a fork actually lands in: a running service under a different
  name, playing this variant. That fork wants one line it can put in its own footer, and the absence
  of one means it either invents a phrasing that overclaims association or says nothing, which is
  worse for the attribution case `docs/game/CLAIMS.md` rests on. This is the cheapest protection the
  name has, and it is missing.
- Fix: One line in NOTICE, next to the trademark paragraph, giving the exact wording a fork should
  use, along the lines of "Plays the KiriChess variant, invented by Michiel Gekiere, rules CC BY
  4.0", with the note that this credits the rules and does not imply endorsement.

### Already covered

- **L-1-7** and **L-9-1**: nobody can clone this repository, so no fork exists to be helped by any of the above.
  Every fix here is written for the snapshot (**O-4-1**) rather than for today.
- Third-party licence obligations a fork inherits are **O-6**; the notice set that should record
  them is **O-1**'s consolidated NOTICE action.
- Trademark strategy, EUIPO and the recognition case are **L-9** and `docs/game/CLAIMS.md`.
- Whether the compose path still boots at all is a CI concern already covered by the E2E job, which
  runs `docker-compose.prod.yml` plus the E2E overlay on every push.

### Action list

1. README "Running your own instance": the production compose path plus the two gotchas
   (O-5-2), and the rename list (O-5-1).
2. Minimum-variables block at the top of `.env.example` (O-5-3).
3. **Owner.** Approve the fork-attribution sentence, then adds it to NOTICE (O-5-4).
4. Handoff accepted from **L-10-5**: the "Running your own instance" section is where the
   one line about LGPL-3.0 libvips obligations belongs, since they attach if a fork publishes the
   frontend image to a public registry and not before. Detail in **O-6-3**.
5. All of the above are snapshot-time work. None of it is launch-gated.

---

## O-6 — SBOM and license compatibility
Date: 2026-08-21
Packet: docs/audits/INDEX.md

### Verdict

**L-10-5**, which ran in a parallel session an hour before this packet, already did the copyleft
hunt across both dependency trees and reached the right answer: nothing taints a fork, the MPL-2.0
packages are build-time and test-only, the LGPL-3.0 inside sharp attaches only on conveyance, and
`WebPush` is the one .NET package whose licence the repository cannot prove. That is not re-argued
here.

Two things fall outside a copyleft hunt by construction, because neither is copyleft, and neither
appears in **L-10** or anywhere else: one dependency in this tree is not open source at all, and the
only non-permissive licence on the .NET side sits on a test package. A licence-compatibility packet
is where those belong. There is also one refinement worth recording against L-10-5's conveyance
analysis, since it is the fact that decides whether either of the two matters: of the two
non-standard licences, the LGPL one does reach the shipped image and the FSL one does not.

### Findings

#### O-6-1 — `@sentry/cli` is FSL-1.1-MIT, which is not an open-source licence
- Severity: Medium
- Where: `ui/package-lock.json` (`node_modules/@sentry/cli` 2.58.6 and its `@sentry/cli-*` platform
  binaries, none flagged `dev`), reached through `@sentry/nextjs` at `ui/package.json:19`
- Impact: FSL-1.1-MIT is the Functional Source License: source-available, carrying a restriction
  against competing use, converting to MIT two years after each release. It is not OSI-approved, so
  it is invisible to a copyleft scan (**L-10-5** was looking for reciprocity and correctly did not
  flag it) and highly visible to any licence-policy scanner or SBOM consumer, which will report a
  non-open-source dependency in a tree the project describes as Apache-2.0. It is also the only
  licence in either tree that restricts what the software may be used *for*. Two things limit it,
  both verified rather than assumed: it is a build-time tool, used for source-map upload, and it does
  not reach the runtime image. `ui/.next/standalone/node_modules/@sentry/` contains `nextjs` and
  nothing else, so the image `Dockerfile.frontend:45` assembles carries no FSL-licensed code. What
  does carry it is the install: anyone running `npm ci` against the snapshot pulls it, and a fork
  vendoring `node_modules` would redistribute it.
- Fix: Record it, do not remove it. Dropping it means dropping Sentry source maps, which is a poor
  trade for a licence that constrains nobody here. One line in the consolidated NOTICE section
  proposed in **O-1**, naming it as a build-time dependency under FSL-1.1-MIT, so a fork learns it
  from this repository rather than from a scanner.

#### O-6-2 — MS-PL on a test package, and the sentence it invalidates
- Severity: Low (trigger: the dependency is copied into shipped code, or someone repeats the summary)
- Where: `Xunit.SkippableFact` 1.5.61, `tests/KiriChess.Server.Tests/KiriChess.Server.Tests.csproj:23`
  (nuspec declares `<license type="expression">MS-PL</license>`)
- Impact: The Microsoft Public License is OSI-approved but is file-level reciprocal and is
  GPL-incompatible, which makes it the only non-permissive licence anywhere in the .NET tree. It is
  test-only and never shipped, so there is no obligation and no exposure. The reason to write it
  down is that **L-10-5**'s .NET summary lists the shipped packages and stops there, and "everything
  on the .NET side is permissive" is exactly the kind of shorthand that gets repeated once and then
  relied on by someone deciding whether a pattern can move from `tests/` into `src/`.
- Fix: None required. Recorded so the next licence question starts from the full list.

#### O-6-3 — Conveyance refinement: the LGPL payload is in the image, the FSL one is not
- Severity: Low (evidence, not a defect)
- Where: `ui/.next/standalone/node_modules/@img/` (present) against
  `ui/.next/standalone/node_modules/@sentry/` (contains only `nextjs`), `Dockerfile.frontend:43-46`,
  `ui/package-lock.json:1092`
- Impact: **L-10-5** establishes that nothing is conveyed today, because the images are built and
  deployed to Fly and no workflow pushes to a registry, which this packet re-checked and confirms:
  `.github/workflows/` contains no `docker/build-push-action`, no `ghcr` reference, and no `docker
  push`. The refinement is which components would be conveyed the day that changes. Next's
  `output: "standalone"` trims `node_modules` to what the server actually loads, and the trimmed
  result keeps `@img` (so libvips, LGPL-3.0-or-later, ships) and drops `@sentry/cli` (so the FSL
  package does not). That is the whole practical difference between the two findings above, and it
  is a fact about the build output rather than about either licence, so it is worth having written
  down before anyone re-derives it.
- Fix: Nothing to fix. Carry it into whatever records the LGPL notice, per **O-1**'s action 1.

### Already covered

- The full copyleft hunt across `ui/node_modules`, `e2e/node_modules` and the NuGet tree, including
  MPL-2.0 on `lightningcss` and `axe-core`, LGPL-3.0-or-later on `@img/sharp-*`, and the conclusion
  that nothing taints a fork: **L-10-5**. This packet's independent scan of 653 plus 8 npm packages
  agrees with it.
- `WebPush` 1.0.13 declaring its licence only as a deprecated `licenseUrl`, with no SPDX expression
  and no bundled text: **L-10-5**. One lookup, then record it.
- The absence of an SBOM artifact and the fact that any licence hunt is therefore a snapshot rather
  than a standing guard: **L-10-6**.
- GPL-3.0 isolation of the vendored engine, the missing licence text and the Corresponding Source
  directions: **L-10-1**, **L-10-2**, **L-10-3**.
- Vulnerabilities, outdated packages, and the Trivy and npm-audit waivers: **I-11** and readiness
  **DEPS**. Untouched here by design.

### Action list

1. Fold `@sentry/cli` (O-6-1) into the single consolidated NOTICE section that **O-1**
   action 1 already proposes. No separate edit.
2. Nothing else. No dependency needs replacing, no licence conflicts with Apache-2.0, and O-6-2 and
   O-6-3 are records rather than tasks.

---

## O-7 — Contributor UX
Date: 2026-08-21
Packet: docs/audits/INDEX.md

### Verdict

A contributor with Docker, the right Node and a bash shell gets to green, and the documentation
they need is short, accurate and in the two files they will actually open. Three of those four
conditions are unstated. Nothing anywhere names a Node, npm or .NET SDK version, even though CI
pins all three and a lockfile written by the wrong npm is a failure that only shows up in a
container build. Every copy-paste block is a bash `&&` chain, which is a parser error in the shell
the maintainer's own machine runs by default. And the "without Docker" path, the one a contributor
takes when they only want to touch the frontend, silently assumes a local PostgreSQL that no
document mentions. None of these is hard. All of them are the first five minutes of a stranger's
experience, and all of them fail with an error that does not name its cause.

### Findings

#### O-7-1 — Nothing pins the toolchain, and the npm version is a known trap
- Severity: Medium
- Where: no `.nvmrc`, no `.node-version`, no `global.json`, no `engines` block in `ui/package.json`;
  against `.github/workflows/ci.yml:20` (`dotnet-version: "10.0.x"`),
  `.github/workflows/ci.yml:74` and `:131` (`node-version: 20`), `Dockerfile.frontend:3` and
  `Dockerfile.backend` (both digest-pinned)
- Impact: CI and the images agree on Node 20 and .NET 10. A contributor's machine is told nothing.
  The sharp edge is npm: `ui/package-lock.json` is lockfileVersion 3, and a lockfile regenerated by
  npm 11 resolves differently from what `npm ci` inside `node:20-alpine` consumes, so the local run
  is green and every container build breaks. That is not hypothetical, it is the shape of a failure
  this project has already hit. The .NET side is milder but real: with no `global.json`, whichever
  SDK the contributor has installed is the one that builds, and `dotnet test` on a different major
  is not the same test run CI made.
- Fix: Add `.nvmrc` containing `20` and a `global.json` pinning the 10.0 SDK feature band with
  `rollForward: latestFeature`, then one line in CONTRIBUTING's Setup section naming Node 20 and the
  npm major that produced the committed lockfile. Cheap, and it converts a silent divergence into a
  file the tooling reads by itself.

#### O-7-2 — Every documented command is a bash chain, on a Windows-first project
- Severity: Medium
- Where: `README.md:31-35`, `CONTRIBUTING.md:29-33`, `CONTRIBUTING.md:37-46`, `ui/README.md:34-40`
- Impact: `cd ui && npm run lint && npx tsc --noEmit && npm test && npm run build` is the verify
  command in three files. In Windows PowerShell 5.1, still the default `powershell.exe` on Windows
  and the shell this project is developed in, `&&` is not an operator and the line is a parser
  error before anything runs. PowerShell 7 accepts it, Git Bash accepts it, so the failure depends
  on which shell the contributor happens to open, and the error message ("The token '&&' is not a
  valid statement separator") tells them nothing about what to do instead. The project is otherwise
  careful about this: `docs/ops/DEPLOYMENT.md:775` has a section explicitly written in PowerShell.
  The contributor-facing files did not get the same treatment.
- Fix: One line under CONTRIBUTING's "Before you open a PR": on Windows PowerShell run the commands
  one per line, or use Git Bash. Cheaper and more durable than maintaining two copies of every
  block.

#### O-7-3 — The "without Docker" path does not mention it needs a database
- Severity: Low
- Where: `README.md:27`, `ui/README.md:11-21`, `src/KiriChess.Server/appsettings.Development.json:6`
- Impact: Both READMEs offer a Docker-free path as a one-liner: run the API, run `npm run dev`. The
  development connection string is `Host=localhost;Port=5432;Database=kirichess;Username=postgres;Password=postgres`,
  so that path needs a PostgreSQL on localhost with those credentials and a `kirichess` database. A
  contributor who only wants to fix a component takes this route precisely because they are avoiding
  Docker, and meets a connection failure at boot with no hint that a database was ever expected. The
  fix is not to remove the path, which is genuinely the nicer frontend loop, but to say what it
  assumes.
- Fix: One sentence in both READMEs: the non-Docker backend expects PostgreSQL on `localhost:5432`
  with `postgres`/`postgres` and a `kirichess` database, or `docker compose up db` for just that
  container.

#### O-7-4 — `.env.example` generates secrets with a tool Windows does not have
- Severity: Low
- Where: `.env.example:8`, `.env.example:22`
- Impact: "Generate one with: `openssl rand -base64 48`" and "`openssl rand -hex 32`". Git Bash
  ships openssl, stock Windows does not, and this is the very first instruction a contributor
  follows, since `Jwt__Key` is the one value README says they must set. Small, but it is the first
  step of the first task.
- Fix: Append the PowerShell equivalent in the same comment, one line each.

#### O-7-5 — CONTRIBUTING's pre-PR checklist omits the E2E suite that CI blocks on
- Severity: Low
- Where: `CONTRIBUTING.md:27-33`, `.github/workflows/ci.yml:123-127` (the `e2e` job),
  `.github/pull_request_template.md:5-10`
- Impact: The E2E job is a normal blocking job by design, builds both production images and runs
  Playwright against them. Neither CONTRIBUTING's checklist nor the PR template mentions `e2e/`
  exists. Leaving it out of the required local run is the right call, since it needs Docker and
  twenty-five minutes, but a contributor whose cross-page change breaks a spec learns it from red CI
  with no idea the suite existed or how to run it locally.
- Fix: One line after the checklist: cross-page changes are also covered by `e2e/` (Playwright
  against the production compose images, `cd e2e && npm test`, needs Docker), and CI runs it on
  every PR.

### Already covered

- **L-1-7** and **L-9-1**: no outside contributor can reach this repository today, so all of this is written for
  the snapshot (**O-4-1**) and for the maintainer's own second machine.
- Inbound licensing and the PR template's licence line are **O-2**.
- CI workflow correctness and drift are **I-3** and `CiWorkflowDriftTests`; this packet only read
  the workflows to find out what a contributor's PR is actually judged against.
- The four test layers and which one a change needs are documented in `.claude/rules/testing.md`,
  which is the long form. O-7-5 is the human-facing one-line version of the same fact.

### Action list

1. `.nvmrc`, `global.json`, and the toolchain line in CONTRIBUTING (O-7-1). Verify with a
   clean `npm ci` and `dotnet test` before claiming it.
2. PowerShell note in CONTRIBUTING (O-7-2).
3. Database prerequisite sentence in both READMEs (O-7-3).
4. PowerShell secret-generation alternatives in `.env.example` (O-7-4).
5. E2E line in CONTRIBUTING (O-7-5).
6. All five are documentation-only and none is launch-gated.

