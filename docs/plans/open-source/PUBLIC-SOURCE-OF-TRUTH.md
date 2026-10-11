# Plan: public repo as source of truth (develop + community + Fly)

> **Status:** in progress (owner decisions locked 2026-10-11). Not standing policy until cutover is done
> and `docs/ROADMAP.md` / `docs/ops/PUBLIC-SNAPSHOT.md` are updated to match.
>
> **Locked decisions:**
> | # | Choice |
> | --- | --- |
> | D1 | Public: `MichielGekiere/kirichess`. Private renamed: `MichielGekiere/kirichess-archive`. |
> | D2 | Personal account |
> | D3 | Full tracked tree (ops workflows stay; drift tests require them) |
> | D4 | Archive private after first green prod deploy from public |
> | D5 | Public URL live before launch / chessvariants cite a repo |
> | D6 | Landed #93; Dependabot stays for public tip |
>
> **Goal:** a new **public** GitHub repository that is the only place KiriChess is developed,
> reviewed, and deployed from. Community issues and PRs land there. Production Fly deploys
> (`v*` tags → `.github/workflows/deploy.yml`) run from that repo. This private working tree
> is frozen or archived afterward.
>
> **Supersedes (after cutover):** AD.13’s “thin snapshot, working repo stays private forever”
> (10c.2). The **export mechanics** in [`docs/ops/PUBLIC-SNAPSHOT.md`](../../ops/PUBLIC-SNAPSHOT.md)
> still apply; the **product shape** changes (public repo is not a read-only mirror).

## Owner decisions (resolve before an agent starts Phase B)

Record answers at the top of the PR or in chat before execution:

| # | Decision | Default if unset |
| --- | --- | --- |
| D1 | Public repo name and owner (`user/repo` or `org/repo`) | Owner must set |
| D2 | Personal account vs GitHub Organization | Personal OK for v1; org later needs `GITLEAKS_LICENSE` |
| D3 | Omit any trees from the export? (e.g. `docs/audits/`, parts of `docs/ops/`) | **Ship full tracked tree** unless owner lists paths |
| D4 | Freeze private repo as archive, or keep as private ops-only fork? | **Archive** after first green prod deploy from public |
| D5 | Cutover timing vs launch post / chessvariants submission | Prefer public URL live before those cite a repo |
| D6 | Merge open private PRs first? (e.g. hide Install app [#93](https://github.com/MichielGekiere/kirichess/pull/93)) | Yes — land wanted private work, then export that tip |

## Non-negotiables

1. **Never** push, fork, squash-merge, or shallow-clone this private history into the public repo.
   Commit objects carry student / personal / machine identities (see PUBLIC-SNAPSHOT §1).
2. Export **tracked files only** (`git archive` or equivalent). Never zip the working directory.
3. First public commit author/committer = GitHub `noreply` (or another project identity that is
   none of the three identities in PUBLIC-SNAPSHOT §1).
4. Secrets stay in Fly / GitHub **environment** secrets. Never commit tokens, `.env*`, tfstate.
5. Fork PRs must not receive deploy / prod secrets (see Phase C).

## Phase A — Prepare the private tip

Agent checklist:

- [ ] Confirm D1–D6 with the owner.
- [ ] Land any private PRs the owner wants in the export (D6).
- [ ] On the export tip (private `main` or agreed SHA):
  ```bash
  dotnet test KiriChess.slnx --filter "FullyQualifiedName~TrackedTreeHygiene"
  gitleaks detect --log-opts="--all"
  ```
- [ ] Manually re-check absent / untracked (PUBLIC-SNAPSHOT §3–4):
  - `infra/betterstack/terraform.tfstate*`
  - `.claude/settings.local.json`
  - `.mailmap`, `.private/`, `.wrangler/`
- [ ] Skim `docs/ops/PROVIDER-ACCOUNTS.md` for provider account / project / zone IDs that
      should not be world-readable; redact in the **private** tip before export, or add path
      to D3 omit list.
- [ ] Note the exact private commit SHA that will be archived (`git rev-parse HEAD`).

**Stop if** hygiene or gitleaks is red for a new (non-allowlisted) finding.

## Phase B — Create the public repository

- [ ] Create empty public repo at D1 (no README/license init from GitHub UI if that would
      create a conflicting first commit — prefer empty).
- [ ] Export tracked tree from the private tip:
  ```bash
  # from private clone, at the agreed SHA
  git archive --format=tar HEAD | tar -x -C /path/to/public-worktree
  ```
  Apply D3 omissions (delete those paths from the worktree before the first commit).
- [ ] First commit in the new repo with noreply identity, message along the lines of:
  `Initial public import of KiriChess (no private history).`
- [ ] Push `main` to the public remote.
- [ ] Enable: Issues, PRs, Discussions (optional), **Private vulnerability reporting**.
- [ ] Branch protection on `main`: require PR + required CI checks (mirror private policy as
      closely as the plan allows). Owner may need to click through GitHub UI.
- [ ] Confirm `LICENSE`, `NOTICE`, `SECURITY.md`, `CONTRIBUTING.md`, `CODE_OF_CONDUCT.md`
      (if present) are in the tree.

**Stop if** any of PUBLIC-SNAPSHOT §3 paths appear in `git ls-files` on the public repo.

## Phase C — Move CI and Fly deploy

Fly apps (`kirichess-api`, `kirichess-ui`, staging twins) **do not move**. Only GitHub wiring does.

### C1. Secrets and environments on the public repo

Copy (do not delete from private until C3) GitHub Environments:

| Environment | Secrets (at minimum) |
| --- | --- |
| `production` | `FLY_API_TOKEN_API`, `FLY_API_TOKEN_UI` |
| `staging` | same names for staging apps |

Also recreate any **repository** secrets used by:

| Workflow | Typical secrets / notes |
| --- | --- |
| `deploy.yml` | env secrets above |
| `ci.yml` | usually none beyond `GITHUB_TOKEN` |
| `secret-scan.yml` | org-only `GITLEAKS_LICENSE` if D2 = org |
| `offsite-backup.yml` | R2 / backup credentials (only if this workflow ships publicly — see C2) |
| `ops-probes.yml` | heartbeat / synthetic tokens (only if shipped) |
| `restore-drill.yml` | as today |

Owner pastes secret values from the password manager / private repo settings; the agent must
**not** print secret values into chat or commit them.

### C2. Which workflows ship publicly

Default recommendation:

| Ship in public | Hold private or disable until reviewed |
| --- | --- |
| `ci.yml`, `deploy.yml`, `secret-scan.yml`, `codeql.yml`, `semgrep.yml` | — |
| — | `offsite-backup.yml`, `ops-probes.yml`, `restore-drill.yml` unless D3 kept them **and** owner confirms secrets + that public fork PRs cannot trigger them with credentials |

If a workflow is omitted from the public tree, document that backups/probes still run from the
archived private repo **or** re-add them later with explicit fork protection.

### C3. Fork / secret hardening

- [ ] Ensure deploy jobs use `environment: production|staging` (already true in `deploy.yml`).
- [ ] Confirm Environments do **not** grant secrets to `pull_request` from forks (GitHub default
      for `pull_request` is no secrets; do **not** switch those jobs to `pull_request_target`).
- [ ] Optional: environment required reviewers if the GitHub plan allows (private hobby repos
      previously could not; public may differ — owner checks UI).

### C4. First deploy from public

- [ ] Push a no-op or real change to public `main`, wait for CI green.
- [ ] Tag next semver after private’s latest production tag (check `git ls-remote --tags` on
      **both** remotes; as of private tip, latest was in the `v1.3.x` line — do not reuse a tag).
  ```bash
  git tag vX.Y.Z
  git push origin vX.Y.Z
  ```
- [ ] Watch `Deploy` workflow on the **public** repo through backend + ui.
- [ ] Smoke: load `kirichess.com` in a private window; play or hit `/api` health as in
      `docs/ops/LAUNCH.md` / DEPLOYMENT post-deploy checks.
- [ ] `fly machines list --app kirichess-api` → exactly one machine (Redis-off invariant).

### C5. Disable deploy on the private repo

- [ ] Disable or delete `deploy.yml` on the private repo, **or** archive the private repo so
      Actions stop (D4).
- [ ] Confirm a tag push on private can no longer ship production.

## Phase D — Product and docs cutover

Update URLs and wording so strangers never hit the private 404. Do this **on the public repo**
(and mirror any still-needed private archive note).

### D1. Must update (tracked)

- [ ] Root `README.md` — remove “working repository stays private”; link the public clone URL;
      invite PRs via `CONTRIBUTING.md`.
- [ ] `SECURITY.md` — GitHub advisory URL + email; drop “until snapshot” hedging.
- [ ] `docs/legal/TERMS.md` + generated / mirrored terms UI (`ui/app/.../terms`) — development
      repo is public at `<url>`; bump terms version if that document’s versioning rules require it
      (`LegalDocumentDriftTests` / TERMS version process).
- [ ] `docs/game/CLAIMS.md` — public source URL; retire or rewrite the missing
      `scripts/export-public-snapshot.ps1` pointer.
- [ ] `docs/plans/chessvariants/SUBMISSION-DRAFT.md` — link public repo, never the private one.
- [ ] `docs/plans/launch/POST-DRAFT.md` — if it cites source, use public URL.
- [ ] `.github/ISSUE_TEMPLATE/*` — advisory / bug links → public repo.
- [ ] `docs/ops/PUBLIC-SNAPSHOT.md` — add a short banner: cutover done; this doc remains the
      **historical export procedure**; source of truth is the public repo.
- [ ] `docs/ROADMAP.md` — mark AD.13 / #11a done **as “public source of truth”**, and strike or
      rewrite 10c.2 “working repo stays private permanently” so agents do not undo the cutover.
- [ ] `docs/ops/LAUNCH.md` / `docs/ops/DEPLOYMENT.md` — deploy remote = public repo; D3 row about
      never linking the private tree.
- [ ] `docs/README.md` — point “Cutting a public snapshot” at this plan (completed) + keep
      PUBLIC-SNAPSHOT as the export appendix.

### D2. Site copy

- [ ] `/about` and any footer/GitHub link: public repo only (private link must stay hidden).
- [ ] Redeploy if terms / about need a production release after the doc PR.

### D3. Archive private

- [ ] GitHub → private repo → **Archive** (or rename + clear description: “pre-public archive,
      do not deploy”).
- [ ] Local clones: re-point `origin` to the public URL; keep a `private-archive` remote if useful.

## Phase E — Community readiness (same week, not blocking first deploy)

- [ ] `CONTRIBUTING.md` accurate for public (build, test, PR expectations).
- [ ] Code of Conduct linked from README (if not already).
- [ ] Labels: `good first issue`, `bug`, `enhancement` (light set).
- [ ] One or two starter issues filed on the **public** repo.
- [ ] Dependabot / existing automation still opens PRs on the public repo.

## Verification (definition of done)

| Check | Pass |
| --- | --- |
| Public `main` has a single-root history (or only commits after import) with noreply authors | yes |
| `git log` on public shows **no** private SHAs / student identities | yes |
| CI green on public `main` | yes |
| Production deploy run is on the **public** Actions tab | yes |
| `kirichess.com` healthy after that deploy | yes |
| Private repo cannot deploy (archived or workflow disabled) | yes |
| Stranger (logged-out) can open repo, LICENSE, SECURITY, Issues | yes |
| TERMS / about / launch drafts cite public URL only | yes |

## What this plan deliberately does not do

- Prove invention priority (still AD.3 catalogue + AD.12 archive.org — ROADMAP).
- Move Fly apps, Neon, Cloudflare, or DNS.
- Rewrite private git history in place.
- Require an org account on day one.

## Agent operating notes

1. Prefer executing Phases A→E in order; do not start C4 until C1 secrets exist.
2. Do not `git push --force` to public `main` after others may have cloned.
3. Do not print secret values.
4. When docs say “working repo stays private,” treat that as **pre-cutover** text to replace in
   Phase D, not as an instruction to abort this plan.
5. After cutover, new feature work happens only on the public repo.

## Related docs

- Export / hygiene detail: [`docs/ops/PUBLIC-SNAPSHOT.md`](../../ops/PUBLIC-SNAPSHOT.md)
- Deploy topology: [`docs/ops/DEPLOYMENT.md`](../../ops/DEPLOYMENT.md)
- Launch checklist: [`docs/ops/LAUNCH.md`](../../ops/LAUNCH.md)
- Queue context: [`docs/ROADMAP.md`](../../ROADMAP.md) (AD.13 / #11a, 10c.2)
