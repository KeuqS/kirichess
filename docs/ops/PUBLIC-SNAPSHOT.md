# Public snapshot procedure

> **Cutover status (2026-10-11):** the public development repo
> [MichielGekiere/kirichess](https://github.com/MichielGekiere/kirichess) is the source of truth
> (community + Fly deploys). Pre-public history is archived as `MichielGekiere/kirichess-archive`.
> This document remains the **historical export / hygiene procedure** used for that import; do not
> treat “working repo stays private permanently” below as current product policy — that was the
> pre-cutover 10c.2 stance, superseded by
> [`docs/plans/open-source/PUBLIC-SOURCE-OF-TRUTH.md`](../plans/open-source/PUBLIC-SOURCE-OF-TRUTH.md).

The procedure for cutting a clean public snapshot of this repository. Referenced by
`docs/ROADMAP.md`'s **AD.13** (queue item #11a) and `docs/game/CLAIMS.md`, neither of which
previously pointed at anything more specific than an instruction to run a script. This document is
that missing procedure, written to close `docs/audits/O.md`'s **O-4-1**.

## 1. Why a snapshot, and not a push of this repository

The pre-public working repository stayed private (10c.2, later superseded for *product shape* by
the public-source-of-truth cutover). The reason a clean import was required is a fact about git
itself, not a policy that could be relaxed by cleaning up files: every commit's author and
committer identity is stored in the commit object. `.mailmap` only changes how `git shortlog` and
some git tooling *display* a name; it does not rewrite what is stored, and GitHub's contributor
graph does not read it at all. Rewriting the archive repository's history to remove the stored
identities was considered and declined, which `.mailmap`'s own comment records.

The identities involved are described here rather than quoted, for the same reason
`docs/audits/B.md`'s **B-2-6** and `.mailmap` itself do not quote them: writing the literal
strings into a tracked file is exactly how a supposedly-fixed leak comes back, which is what
happened once already (see the note at the top of `TrackedTreeHygieneTests.cs` and its
`Tracked_files_do_not_contain_personal_mailbox_addresses` test). In shape, not in full, the history
contains:

- a personal mailbox address, not a project-owned one;
- an address on a named university's student subdomain, which narrows the author's institution,
  and by extension age and location, beyond anything `/about` discloses by choice;
- a bare local machine hostname, used automatically as a git identity on a machine with no
  configured email.

Because this lives in commit objects, no amount of stripping tracked files removes it. The only way
to publish this project's source without publishing those identities is a tree with no imported
history at all: a new repository, a single first commit, authored under an identity that is none of
the three above (a GitHub-issued `noreply` address, per the original plan recorded in
`docs/game/CLAIMS.md`, is one option).

**What ships:** the working tree at one tagged commit of this repository, as file content only.
**What does not ship, under any form:** this repository's commit history. That includes a shallow
clone, a squash-merge that still lists the original authors, and a fork.

## 2. Building the export

Build the snapshot from tracked file content, not from a raw copy of the working directory. The
distinction matters: most of the paths in section 3 are excluded from this repository by
`.gitignore`, but a plain recursive copy (a folder copy, a zip of the directory) does not consult
`.gitignore` and carries them straight into the new repository. An export that goes through git
instead (`git archive` of the tagged commit, or a fresh checkout of that commit into an empty
directory before re-initializing) only ever produces tracked file content, so most of section 3 is
handled by the export method itself rather than by a separate manual strip pass.

`docs/game/CLAIMS.md:30` currently tells the reader to run `scripts/export-public-snapshot.ps1` for
this step. That script is not in the repository (`docs/audits/L.md`'s **L-9-2**); whether it is
restored, rewritten, or retired is an owner decision that has not been made, and this document does
not make it. This procedure does not depend on the answer: if the script comes back, it should
implement the method described here; if it does not, run the equivalent commands by hand. Either
way, `docs/game/CLAIMS.md:30` still names a script that is not present in the repository today, and
that mismatch is not resolved by writing this document.

## 3. What gets stripped

Checked against this repository's actual `.gitignore` and tracked-file list, not copied from the
finding without verifying:

| Path | Status |
|---|---|
| `.private/` | gitignored, present, untracked |
| `.mailmap` | gitignored, present, untracked |
| `.claude/settings.local.json` | gitignored, present, untracked |
| every `.env*` except the example files | gitignored via `.env`, `.env.*`, `.env*.local`; `.env.example`, `.env.neon.example`, and `scripts/discord-setup/.env.example` are tracked and ship as-is |
| `docs/legal/*.pdf` | gitignored, present, untracked |

All five checked out; none needed correcting. All five are already excluded by `.gitignore`, which
is exactly why building the export through git (section 2) rather than a raw directory copy is the
part that matters, not a manual delete list.

Two more paths follow the same rule and deserve the same defense-in-depth check, since
`docs/ROADMAP.md`'s AD.13 entry calls out the first of them by name as something to re-confirm
absent before pushing: `infra/betterstack/terraform.tfstate` and its `.backup` (matched by
`*.tfstate*`), and `.wrangler/` (the cache directory `TrackedTreeHygieneTests.Wrangler_cache_is_gitignored`
pins).

Because a git-based export can only ever include tracked content, the practical failure mode is not
"the export command included a stripped path", it is "one of these paths got tracked by mistake
before the export was cut". Section 4 checks for that directly, in the source repository, before any
export happens.

## 4. Verify before pushing

### 4a. `TrackedTreeHygieneTests`

Run this against the repository the snapshot is being built from, before cutting it:

```bash
dotnet test KiriChess.slnx --filter "FullyQualifiedName~TrackedTreeHygiene"
```

It pins three facts:

- `.mailmap` is listed in `.gitignore`, so it can never end up in a tracked-file export regardless
  of export method.
- `.wrangler/` is listed in `.gitignore`, same reasoning, for the Wrangler cache directory.
- No tracked file's content contains any of a fixed set of needles built from the personal
  identities described in section 1. The needles are assembled at runtime specifically so the test
  file itself never holds a tracked copy of them.

A red result means one of those three facts stopped holding in the *source* repository; fix that
before proceeding, rather than working around it. This check reads the source repository's tracked
files, not the exported snapshot. If the export method ever stops being "tracked-file content only",
re-run the same three checks against the exported tree directly rather than assuming they still
hold.

### 4b. Full-history secret scan

`.github/workflows/secret-scan.yml`'s gitleaks job diffs commit ranges by default: it checks only
what a push or pull request introduces. `.gitleaks.toml`'s allowlist exists precisely because that
default misses older tracked strings that a full scan does catch; today it carries dated entries for
two dead placeholder tokens and the Wrangler cache path (`docs/audits/O.md`'s **O-4-3**). Run the
full scan once, against the source repository, before cutting the snapshot:

```bash
gitleaks detect --log-opts="--all"
```

Check anything it flags against `.gitleaks.toml`'s existing entries. A new hit that is not an
already-documented placeholder is a stop-and-fix, not something to allowlist on the spot.

### 4c. No provider account id in any file

Separate from live secrets (covered by 4b): confirm no tracked file names a specific provider
account, project, or zone identifier, such as a hosting organization or app id, a database project
id, a CDN account or zone id, or a payment-provider account id. `docs/ops/PROVIDER-ACCOUNTS.md` is
the file most likely to carry one, since it is, by its own description, the accounts-and-keys
runbook; read it directly rather than relying on a keyword grep alone, since an account id does not
look like a secret and gitleaks is not tuned to catch it.

## 5. What the snapshot's own README should say

The new repository's README should state, near the top, that it is a snapshot export of a private
working repository, not the repository the project is actually developed in: no issues, pull
requests, or history from before the snapshot's own first commit exist there, and the working
repository's commit history, including its authorship, was deliberately not carried over. It should
link back to whatever public page currently carries the canonical claim of authorship and license
(`/about`, `LICENSE`, `NOTICE`), so a reader lands on the same attribution the live site already
makes instead of inferring one from an unfamiliar repository with a single commit.

## 6. Where the snapshot lives

`.github/workflows/secret-scan.yml` notes that `gitleaks-action` only requires a `GITLEAKS_LICENSE`
secret for private repositories owned by a GitHub Organization; this repository, and by default a
new snapshot repository created under the same account, is personal-account, so the job runs free
today. Moving either repository under an organization later makes that job start failing closed
unless the license secret is added in the same change (`docs/audits/O.md`'s **O-4-4**).

Pulling the other way, `docs/audits/B.md`'s **B-2-8** notes that a personal-account repository also
makes every public issue, pull request, and security advisory present as the maintainer's personal
account rather than the project's.

Neither point settles where the snapshot repository lives. Whoever cuts it should decide this on
purpose rather than default to a personal account by omission: if an organization is chosen, budget
for `GITLEAKS_LICENSE` in the same change that moves the repository, instead of discovering the job
has gone red afterward.

## 7. Open items this document does not resolve

- **L-9-2**: whether `scripts/export-public-snapshot.ps1` is restored, rewritten, or retired, and
  whether `docs/game/CLAIMS.md:30`'s line pointing at it is updated to match. An owner decision, not
  made here; this procedure stands regardless of which way it goes (section 2).
- A `docs/ROADMAP.md` row pointing at this document, in the after-1-Sep block rather than the launch
  queue (`docs/audits/O.md`'s action list, item 2), is a separate owner step this document does
  not itself add.
