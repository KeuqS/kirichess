# Claims & Recognition

## Action Checklist

- [ ] **Submit to The Chess Variant Pages** ([chessvariants.com](https://www.chessvariants.com)), a long-running catalog built for exactly this purpose, with invented variants credited to named inventors. This is probably the **single highest-leverage move** available.
  ```
  **Drafted 2026-08-18:** [docs/plans/chessvariants/SUBMISSION-DRAFT.md](../plans/chessvariants/SUBMISSION-DRAFT.md) has
  the page-style entry and a shorter cover-note version, both built from RULES.md so the numbers
  can't drift. Left to do: find the site's current submission route (both the `.com` and its
  `.org` mirror returned HTTP 403 while this was drafted), decide how the inventor is named,
  and send it. Once posted, record the resulting URL back on this line.
  ```

- [ ] **Post to communities**, r/chessvariants, the lichess and chess.com variant forums, chess-variant Discords. YouTube creators who cover invented variants are constantly looking for new material.

- [x] **Publish the manual** somewhere with a permanent, citable timestamp and your name on it, a personal site or an archive.org snapshot. A public GitHub repo can be a timestamp - but only with **no imported private history** (commit authors there include personal addresses). The clean public import is the development repo.
  ```
  **2026-08-19:** LICENSE/NOTICE split is done (Apache-2.0 software, CC BY 4.0 rules).

  **AD.13 / public source of truth (2026-10-11):** development happens at
  https://github.com/MichielGekiere/kirichess — a clean import (no private history),
  authored with GitHub noreply. Export mechanics:
  [docs/ops/PUBLIC-SNAPSHOT.md](../ops/PUBLIC-SNAPSHOT.md). Cutover plan:
  [docs/plans/open-source/PUBLIC-SOURCE-OF-TRUTH.md](../plans/open-source/PUBLIC-SOURCE-OF-TRUTH.md).
  Pre-public history lives in the archived private `kirichess-archive` (not for linking).

  **AD.12 / #14 done 2026-10-09** (Access off; public site live):
  - /rules → https://web.archive.org/web/20261009183825/https://kirichess.com/rules
  - /about → https://web.archive.org/web/20261009183859/https://kirichess.com/about
  ```

- [x] **Add an About / Origin page** to your platform when it exists. Being the place people actually *play* it is the strongest attribution channel there is, see how lichess credits variant inventors on its own variants page.
  ```
  **Done 2026-08-18:** `/about` (ROADMAP AD.2) carries who invented it, the date, the working
  title, the paragraph of why in the owner's own voice, and the name's dedication. Not yet
  reachable by anyone outside the owner, same Cloudflare Access gate as the rest of the site -
  it becomes real the moment the site is public.
  ```