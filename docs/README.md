# Documentation

KiriChess is an open-source, free, donation-funded chess variant. This folder is the project
documentation. Working conventions for the code live in [`.claude/rules/`](../.claude/rules/).

```
docs/
├── README.md              ← you are here
├── ROADMAP.md             work queue (launch, phases, standing positions)
├── RULES.md               generated rules manual (CC BY 4.0) - do not move
├── game/                  the variant itself
│   ├── PGN.md             export contract
│   └── CLAIMS.md          invention, attribution, trademark
├── ops/                   running kirichess.com
│   ├── DEPLOYMENT.md      topology, secrets, backup, alerting
│   ├── LAUNCH.md          before / launch day / right after the Access flip
│   ├── INCIDENTS.md       alert -> first check -> response, kill switches
│   ├── CONTINUITY.md      if the maintainer disappears; moving off Fly/Neon
│   ├── MAINTENANCE-CALENDAR.md  quarterly checklist
│   ├── MIGRATIONS.md      roll-forward-only schema policy
│   ├── PROVIDER-ACCOUNTS.md
│   ├── PUBLIC-SNAPSHOT.md cutting a clean public snapshot repo
│   └── UI-CHECKLIST.md    manual styling / UI pass
├── decisions/             standing "why", not a queue
│   ├── AUTH.md            session threat model
│   ├── ACCESSIBILITY.md   why no conformance duty applies, and its triggers
│   └── RETENTION.md       what the sweeper actually deletes
├── legal/                 player-facing legal + internal ROPA/DPAs
├── api/                   generated OpenAPI (first-party + public /api/v1)
├── plans/                 drafts that are not yet standing docs
│   ├── launch/            soft-launch post
│   ├── chessvariants/     catalogue submission
│   ├── open-source/       public repo as source of truth (develop + Fly)
│   └── a11y/              NVDA pass
└── audits/                check work (INDEX, PRIORITY_FIX, one report per letter)
```

## What stays at the `docs/` root

- **`RULES.md`** is generated (`ui/scripts/gen-rules.mjs`). Wording is the English `/rules` page
  (`ui/messages/en.json`); every number comes from `config/kirichess.rules.json`. It is named in
  [NOTICE](../NOTICE) as the CC BY 4.0 manual. LICENSE is the unmodified Apache-2.0 text and carves
  out nothing; only NOTICE carries the CC BY split. CI fails if RULES.md drifts. Do not hand-edit
  it; do not move it.
- **`ROADMAP.md`** is the living work queue. Hundreds of links assume this path.

## Start here

| If you want… | Read |
|---|---|
| What to build next | [ROADMAP.md](ROADMAP.md) |
| How the variant works | [RULES.md](RULES.md) (or [kirichess.com/rules](https://kirichess.com/rules)) |
| How to run or deploy | [ops/DEPLOYMENT.md](ops/DEPLOYMENT.md) |
| What to do before / on / right after launch | [ops/LAUNCH.md](ops/LAUNCH.md) |
| What to do when an alert fires | [ops/INCIDENTS.md](ops/INCIDENTS.md) |
| If the maintainer disappears | [ops/CONTINUITY.md](ops/CONTINUITY.md) |
| Manual UI / styling pass | [ops/UI-CHECKLIST.md](ops/UI-CHECKLIST.md) |
| Turning the patron badge on | [ops/PATRON-BADGE-GOLIVE.md](ops/PATRON-BADGE-GOLIVE.md) |
| Making the public repo the source of truth (community + Fly) | [plans/open-source/PUBLIC-SOURCE-OF-TRUTH.md](plans/open-source/PUBLIC-SOURCE-OF-TRUTH.md) |
| Cutting a clean export (no private history) | [ops/PUBLIC-SNAPSHOT.md](ops/PUBLIC-SNAPSHOT.md) |
| Why sessions work this way | [decisions/AUTH.md](decisions/AUTH.md) |
| Terms / privacy | [legal/TERMS.md](legal/TERMS.md), [legal/PRIVACY.md](legal/PRIVACY.md) |
| Public HTTP API | [api/openapi.json](api/openapi.json); bots: [api/openapi-public.json](api/openapi-public.json) |
| What to check | [audits/INDEX.md](audits/INDEX.md) |
| Open weaknesses to fix | [audits/PRIORITY_FIX.md](audits/PRIORITY_FIX.md) |
| Origin and attribution | [game/CLAIMS.md](game/CLAIMS.md) |

New standing docs go in one of the folders above, not next to this file.
