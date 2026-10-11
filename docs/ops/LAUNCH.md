# Launch: before and right after

Owner checklist for opening `kirichess.com` to strangers. The ordered ROADMAP gates are **#12–#16**
([ROADMAP.md](../ROADMAP.md)). This page is the day-of runbook: what to verify, what to flip, what
not to touch, and what to do in the first hours.

Detail for topology, secrets, Redis, and capacity lives in [DEPLOYMENT.md](DEPLOYMENT.md). Alert
response and kill switches live in [INCIDENTS.md](INCIDENTS.md). The soft-launch post draft is
[plans/launch/POST-DRAFT.md](../plans/launch/POST-DRAFT.md).

**Capacity on launch day:** one `kirichess-api` machine, Redis off, about **100 concurrent games**.
Do not scale to two machines and do not set `Redis__Configuration` until `/health/ready` stays
`"redis":"up"` on staging without the host stopping ([DEPLOYMENT.md](DEPLOYMENT.md) §7).

---

## Before launch

Do these while Access is still on (or immediately before flipping it). Mark each when done.

### Verified 2026-10-07 / 2026-10-08 (Access still on)

| # | Result | Evidence |
| --- | --- | --- |
| A1 | ✅ | `https://api.kirichess.com/health/ready` → `{"status":"ready","redis":"disabled"}` (re-checked after prod deploy) |
| A2 | ✅ | `"redis":"disabled"` (not `"down"`) |
| A3 | ✅ | `kirichess-api`: one `started` machine (`shared-cpu-1x:512MB`, ams), checks 1/1 |
| A4 | ✅ | `kirichess-api-staging`: one machine, **stopped** (safe; not 2 with Redis off) |
| A5 | ✅ | Owner played games behind Access after the intentional prod deploy - playable |
| A6 | ✅ | Soft-launch tree via [PR #85](https://github.com/MichielGekiere/kirichess/pull/85) + follow-up [PR #86](https://github.com/MichielGekiere/kirichess/pull/86). Production dispatch [Deploy #37715058599](https://github.com/MichielGekiere/kirichess/actions/runs/37715058599) green. Live image `registry.fly.io/kirichess-api:deploy-b78f10232e154ba99bff37caffeb63efa85e1baa` (v54) |
| B1–B4 | ✅ | Secrets present by name: `Turnstile__SecretKey`, `Email__ApiKey`, `Jwt__Key`, Postgres (`ConnectionStrings__*`), `Stripe__WebhookSecret`, `Hub__ConnectionBudget__MaxAnonymousConnections` |
| B5 | ✅ | `Redis__Configuration` is **not** among production secrets |
| C1 | ✅ | Better Stack monitors confirmed Up (2026-10-08). Heartbeats / ops-probes green-path still tracked separately; not a ROADMAP launch gate (10c.5) |
| C2 | ✅ | Waking channel + Alert Triage fire confirmed (2026-10-08) — push/critical reaches phone |
| C3 | ✅ | Discord rules pasted; #announcements ready for "aware, investigating" (2026-10-08) |
| C4 | ✅ | Admin JWT → `GET /api/moderation/kill-switches` returned all five switches (2026-10-08) |
| C5 | ✅ | Rollback: previous distinct image is `registry.fly.io/kirichess-api:deploy-6fbd5ccd93f74b404574abbb3c388d8718c27498` (v53). Current is `…:deploy-b78f10232e154ba99bff37caffeb63efa85e1baa` (v54). Command: `fly deploy --app kirichess-api --image registry.fly.io/kirichess-api:deploy-6fbd5ccd93f74b404574abbb3c388d8718c27498 --ha=false` |

**#12 done 2026-10-08:** Access application removed; apex returns **200**. **I-5-5 done 2026-10-09:** Better Stack `edge_site` expects `[200]` only (`terraform apply`).

### A. Site and API are healthy

| # | Check | Command / where | Pass |
| --- | --- | --- | --- |
| A1 | Backend ready | `curl -sS https://api.kirichess.com/health/ready` | `{"status":"ready",…}` |
| A2 | Redis still disabled | same response | `"redis":"disabled"` (not `"down"`) |
| A3 | Exactly one API machine | `fly machines list --app kirichess-api` | one `started` machine |
| A4 | Staging also one machine | `fly machines list --app kirichess-api-staging` | one machine (or stopped); never leave staging at 2 with Redis off |
| A5 | Site behind Access still loads for you | private window after Access login | playable |
| A6 | Deploy is intentional | last production image matches what you mean to open | no dirty-tree deploy; config-only needs `--image` + `--ha=false` |

### B. Secrets and gates that must be on for strangers

| # | Check | Notes |
| --- | --- | --- |
| B1 | `Turnstile__SecretKey` set on `kirichess-api` | Without it, registration bot-check is a no-op |
| B2 | `Email__ApiKey` set | Confirmation / password-reset mail |
| B3 | `Jwt__Key`, Postgres, Stripe webhook secret | Already required for a working deploy; do not rotate on launch morning unless leaking |
| B4 | `Hub__ConnectionBudget__MaxAnonymousConnections` | Production may override the compiled default; leave the reserve in place so spectators cannot eat all sockets |
| B5 | Do **not** set `Redis__Configuration` | Upstash may exist; the .NET client must connect stably first. Setting it with a broken client stops the host |

Confirm secret **names** only: `fly secrets list --app kirichess-api`.

### C. Ops that can wake you

| # | Check | Notes |
| --- | --- | --- |
| C1 | Better Stack monitors green | Backend readiness, edge API, edge site - [INCIDENTS.md](INCIDENTS.md) |
| C2 | Waking channel works | You get a real page/SMS/call on a test alert, not only email |
| C3 | Discord #announcements ready | First reply when something breaks: "aware, investigating" ([INCIDENTS.md](INCIDENTS.md) step 0) |
| C4 | Kill-switch path rehearsed | Admin JWT → `GET/PUT /api/moderation/kill-switches` for `Registration`, `Pairing`, `Spectating`, `SeekList`, `Api` |
| C5 | Rollback path known | `fly releases --app kirichess-api` then `fly deploy --app kirichess-api --image <previous> --ha=false` |

#### Discord rules paste (O-3-2) - put in a Discord Rules channel / rules screen

```
KiriChess Discord - rules

Be kind. No harassment, hate, sexual content, doxxing, or spam.
This is a game community, not a place to cheat or recruit cheaters.

Report problems to contact@kirichess.com (one maintainer; no separate appeal body).
Full Code of Conduct: in the project repo when a public snapshot exists; until then, email that address.

Outages: watch #announcements. First reply from the maintainer is "aware, investigating."
```

Optional before open (do not block #12 on these; they are still worth doing if close):

- One successful restore or offsite backup story ([DEPLOYMENT.md](DEPLOYMENT.md) §5; ROADMAP 10c.1).
- Revoke a wide org deploy token once per-app deploy is on `main` ([PRIORITY_FIX.md](../audits/PRIORITY_FIX.md) B-4-2).

### D. What you will tell people

| # | Decision | Default for this launch |
| --- | --- | --- |
| D1 | Concurrent-play expectation | ~100 live games on one shared CPU; past that, expect refusals / latency, not a second machine |
| D2 | Soft-launch audience | r/chessvariants first per ROADMAP #16; no press pitch on day one |
| D3 | Public repo is the source of truth | Link [MichielGekiere/kirichess](https://github.com/MichielGekiere/kirichess) only. Never link `kirichess-archive` (pre-public private history). |

---

## Launch day: the flip (#12)

Order matters. Access off is the only gate that must change for strangers to reach the site.

1. **Remove the Cloudflare Access application** for `kirichess.com` (ROADMAP #12 / DH.3).
2. **Private window, never authenticated to Access:** open `https://kirichess.com`, register or log in, start a seek, play a move.
3. **Same day, I-5-5:** Better Stack edge-site monitor must stop accepting `302` as success. In
   `infra/betterstack/main.tf`, drop `302` from `expected_status_codes` (comment `# DH.3: drop 302`),
   restore redirect/cookie defaults, apply Terraform, and update `BetterStackAlertingDriftTests` to
   expect `[200]`. A 302 after Access is gone is a loop or misconfig, not a login wall.
4. Confirm `https://api.kirichess.com/health/ready` still `ready` and still one API machine.

Do **not** on this day:

- `fly scale count 2` on production or staging
- Set `Redis__Configuration`
- Deploy an uncommitted working tree
- Remove `--ha=false` from deploy while Redis is off
- Quote a 150- or 200-game capacity (no finished ladder above 100)

---

## Right after the flip (#13–#16 and the first hours)

### Immediately after Access is off

| # | Step | ROADMAP | Done when |
| --- | --- | --- | --- |
| E1 | Google OAuth live | #13 / DH.5 | ✅ 2026-10-09 — `v1.3.3` UI flag live; one successful Google sign-in (consent still Testing) |
| E2 | archive.org snapshot | #14 / AD.12 | ✅ 2026-10-09 — `/rules` and `/about` saved; URLs in [CLAIMS.md](../game/CLAIMS.md) |
| E3 | Soft-launch post | #16 / 8.7 | [POST-DRAFT.md](../plans/launch/POST-DRAFT.md) published (r/chessvariants first) |
| E4 | chessvariants.com (can wait hours/days) | #15 / AD.3 | Submission sent; link the public snapshot repo if it exists, never this private working tree |

Google OAuth **after** Access is off: Google's crawlers hit the consent URLs and get the Access wall if you do #13 first.

### First hours: watch

| Signal | Where | Action if bad |
| --- | --- | --- |
| Ready / edge monitors | Better Stack | [INCIDENTS.md](INCIDENTS.md); Discord "aware" first |
| Machine count | `fly machines list --app kirichess-api` | If a second machine appeared with Redis still disabled, scale back to 1 immediately |
| Hub errors / queue stuck | Better Stack charts | Kill switch or rollback per [INCIDENTS.md](INCIDENTS.md) |
| Confirmation mail | Resend dashboard / user reports | Free tier is 100/day; if signups spike, expect 429s - pause `Registration` kill switch or upgrade Resend for the launch window |
| Persistence debt | `kirichess_persistence_pending_snapshots` | Sustained growth under load is silent durability pressure ([DEPLOYMENT.md](DEPLOYMENT.md) §4.1) |

### If load hurts before Redis is ready

1. Flip kill switches (`Spectating` / `SeekList` / `Registration`) before raising Fly limits.
2. Keep one machine. Raising `hard_limit` or scaling to 2 without Redis makes split-brain or spectator-filled sockets worse, not better.
3. Tell Discord the site is capacity-limited; do not invent a second-machine ETA until staging shows `"redis":"up"` and a two-machine play check has passed.

---

## After launch (not day-of)

These improve the next capacity step; they are not part of the open flip.

1. Fix StackExchange.Redis connect from a Fly machine (`redis://`, not `rediss://` for the current Upstash proxy).
2. On staging only: set `Redis__Configuration` (+ `Redis__KeyPrefix` if needed), confirm ready stays `"redis":"up"` and the process does not exit.
3. `fly scale count 2` on **staging**, play one game whose sockets land on different machines, then a load rung.
4. Only then consider production Redis and a second machine.

Press kit recapture (ROADMAP #17) waits until the soft-launch thread has run. Do not block #16 on it.
