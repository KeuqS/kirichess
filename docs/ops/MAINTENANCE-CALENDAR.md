# Maintenance calendar

A one-person hobby project can legitimately go quiet for two months. The risk that creates is not
that something breaks loudly - it is that the safety net switches off silently, with the same "no
email because nothing failed" signature as everything actually being fine. This is the checklist
that catches that, run roughly quarterly. There is one calendar; do not create a second one
elsewhere.

## Item 1: confirm the scheduled workflows are still actually running

GitHub disables a scheduled workflow (`on: schedule`) after 60 days with no activity on the
repository. `CiWorkflowDriftTests` proves the workflow *files* still exist and still declare the
trigger the roadmap says they do; it cannot see that GitHub has administratively paused one, because
a disabled schedule and a passing one produce the same absence of an email.

**Open the repository's Actions tab and confirm the scheduled workflows below show a run inside
their expected window, and that none of those schedules is marked disabled.** Off-site backup is
the exception in the table: it has no schedule until GitHub's DPA is accepted, and a missing Sunday
run is the intended state.

| Workflow | Schedule | Expected window |
| --- | --- | --- |
| Restore drill (`.github/workflows/restore-drill.yml`) | `17 4 1 * *` | Once, in the first two days of each month |
| Off-site backup (`.github/workflows/offsite-backup.yml`) | none, until GitHub's DPA is accepted | Do not expect a Sunday run, and do not dispatch it. See [DPA-CHECKLIST.md](../legal/DPA-CHECKLIST.md) |
| Ops probes 10c.5 (`.github/workflows/ops-probes.yml`) | `3,18,33,48 * * * *` | Every 15 minutes, continuously |
| CodeQL (`.github/workflows/codeql.yml`) | `30 3 * * 1` | Once a week, on a Monday |
| Semgrep (`.github/workflows/semgrep.yml`) | `0 6 * * 1` | Once a week, on a Monday |

If a workflow is marked disabled, or its most recent run is older than its expected window plus a
few days, the fix is a manual `workflow_dispatch` run (or any commit at all) - either **re-arms the
60-day clock** and does not require re-enabling anything by hand. Doing this quarterly, on schedule,
is itself what keeps the clock from ever running out.

**Why this matters more than any single workflow failing.** A dormancy period silently switches off
PITR verification (restore drill), the only off-site backup path, the synthetic "can a game be
played" probe, and both code scanners together, and the failure mode looks identical to everything
being fine. See [docs/ops/INCIDENTS.md](INCIDENTS.md) for what to do about any one of these once it
is confirmed to actually be failing rather than merely disabled.

## Item 2: check whether any of L-12-4's triggers have fired

These four conditions each move a legal question from "optional, revisit later" to "do it now."
None of them is a routine engineering check, so this item is a five-minute read, not a task list -
if none has fired, the answer this quarter is "no change."

1. **Recurring paid tiers ship (FS.4).** The grey funding edge stops being grey the moment this
   happens: `docs/legal/PRIVACY.md` section 1 would need a geographic address, and the address itself
   needs budgeting. This is the strongest of the four triggers and is already booked on the roadmap,
   so it is the one most likely to actually fire.
2. **A user asserts a claim**, or a terminated player disputes a ban in writing. Worth a read of the
   relevant TERMS/moderation sections with fresh eyes, not a reflexive policy change.
3. **A fork ships under the name**, or a third party files the trademark. The EUIPO registration
   question (`docs/game/CLAIMS.md`) becomes timed rather than optional the moment this happens.
4. **A supervisory-authority contact**, of any kind. `docs/legal/ROPA.md` is filed and
   produce-on-request ready, so this is a "have someone read it with you" trigger, not a panic one.

## Item 3: review frontend dependencies sitting on a skipped major

Dependabot's `npm-minor-patch` group in `ui/` only proposes minor and patch bumps, so a major-version
gap accumulates invisibly until something needs it - a security patch that only shipped in a newer
major, or a peer-dependency conflict. `docs/audits/I.md` finding I-11-8 named this pattern (as of
that read: `react-chessboard`, `typescript`, and `eslint` each sitting a major
or more behind latest); check `npm outdated` in `ui/` for the current state rather than trusting
those specific numbers, since dependency versions move independently of this document.

No action is owed beyond awareness: bump a major only when it is otherwise convenient, or when a
patch that matters has landed only in a newer major line. The point of this line is noticing before
that happens under time pressure, not a standing to-do to clear every quarter.

## Item 4: renew the `Expires` date in `security.txt` before it lapses

`ui/public/.well-known/security.txt` is the only published way for someone outside the project to
report a vulnerability, since every other pointer names a GitHub advisory URL that requires access to
a private repository (`docs/audits/O.md`, O-3-1). RFC 9116 makes `Expires` mandatory, and a parser
that reads a lapsed file is entitled to treat the contact as stale.

This is on the calendar and the excluded list below is not a contradiction: the failure is invisible.
Nothing errors, no probe goes red, and the page still serves a 200 with a body that looks correct. The
channel simply stops being trusted, and the first evidence would be a report that never arrives.

Read the current value at its own source rather than from a copy here. Renew it about a year out when
it is within a couple of months of lapsing, and check at the same time that the address in the file is
still monitored, since a live date on a dead mailbox is worse than a lapsed one.

## Item 5: export Stripe payment records ($3-1)

The application deliberately stores no donation amount (`DonationReference` carries a reference,
three timestamps and a user id, nothing else, `Account/DonationReference.cs`; the amount is read
once off the webhook, collapsed to a tier, and discarded). That is the right privacy posture and it
means the Stripe dashboard is not a second ledger, it is the *only* ledger for how much came in, from
whom, and when. If that account is ever lost or locked, the books are gone with it.

**Export payments from the Stripe dashboard (Payments, or Reports > Payments, CSV) and save the file
outside the Stripe account** - not in this repository, since the export carries donor names and
emails this application deliberately never receives, so it is a more sensitive file than anything in
the database. Where it is kept and for how long is an owner decision (`docs/audits/funding.md` action item
6); this item only ensures the export itself is not forgotten between now and that decision.

## Item 6: keep one database dump outside Cloudflare and Neon (I-4-2)

The R2 bucket is the copy that survives Neon. It lives in the same Cloudflare account as the
domain, DNS, the proxy, and Turnstile, so a lockout, suspension, or billing lapse on that account
takes the site and the backup together. That correlation is accepted. A second cloud account is
not required.

Once a dump exists, download the newest `kirichess/*.dump` and keep that one file somewhere that is
neither the Cloudflare account nor Neon. The maintainer's own disk is enough. Replace it each
quarter with the newest object, and remove the previous file so only one copy sits there. Do not
start until **Off-site backup** has a green run. Until GitHub's DPA is accepted, that workflow must
not be dispatched ([DPA-CHECKLIST.md](../legal/DPA-CHECKLIST.md)), and Neon is still the only copy
([DEPLOYMENT.md](DEPLOYMENT.md) §5.3).

The file is a plaintext player database. Keep it with the same care as the Stripe export in item 5:
not in this repository. Pull the single object with the credentials and key named in the disaster
block of [DEPLOYMENT.md](DEPLOYMENT.md) §5.3. A full `offsite-backup.sh restore` is not required
for this item.

## Item 7: rotate the Fly deploy token and the Neon management key (B-4-2)

Annual, before the current token's expiry. A leaked deploy token is the ability to ship a new
image. A dead Neon management key fails the restore drill, the off-site backup, and the ops probes
on their next run, and the only signal is one GitHub email.

**Fly.** Deploy uses four per-app tokens as GitHub environment secrets: `FLY_API_TOKEN_API` and
`FLY_API_TOKEN_UI` on `production` and on `staging`. The old repository secret `FLY_API_TOKEN` and
the org tokens `github-actions-deploy`, `github-actions-deploy-2026-10-04`, and
`github-actions-deploy-2026-10-04-restore` were revoked or deleted 2026-10-07 after that workflow
landed on `main` and a staging deploy succeeded with the per-app tokens. Annual rotation: mint a
new `fly tokens create deploy -a <app>` for each of the four apps, set the matching environment
secret, confirm a staging deploy, then revoke the previous four.

**Neon.** First rotation done 2026-10-04. The live key is `3398623` (`github-actions-2026-10-04`), scoped to project `plain-salad-47730828`, stored as the repository secret `NEON_API_KEY` and in `.env.neon`. `3267669` (`kirichess-api-key`) was revoked the same day. The next rotation is the same shape: `neon auth`, create a project key, store it in both places, confirm `branches list`, then revoke the previous id.

## What this calendar deliberately does not cover

- **DPA dates, TERMS/PRIVACY version dates, domain auto-renewal.** These exist and are worth an
  occasional glance (`docs/legal/ROPA.md` section 4 for DPA dates, the `Last updated` / `Version`
  line at the top of `docs/legal/TERMS.md` and `docs/legal/PRIVACY.md`, and the domain registrar's own
  renewal reminder), but this document does not restate specific dates that live correctly in those
  files already and would only drift here. Read them at their own source rather than trusting a copy
  on this page.
- **Anything B-4's packet did not ask for.** This page answers B-4-1, L-12-4, and the I-11-8
  cross-reference, plus item 4, which O-3-1's fix created rather than found: publishing a
  `security.txt` puts a date on the calendar that was not there before. Later findings name this
  calendar as their mitigation and land here for the same reason ("there is one calendar; do not
  create a second one elsewhere"): item 5 is **$3-1**, the missing second record of a donation,
  item 6 is **I-4-2**, one database dump kept outside both Cloudflare and Neon, and item 7 is
  **B-4-2**, the annual rotation of the Fly deploy token and the Neon management key. It is not a
  general ops checklist; `docs/ops/DEPLOYMENT.md` and `docs/ops/UI-CHECKLIST.md` cover their own
  ground.
