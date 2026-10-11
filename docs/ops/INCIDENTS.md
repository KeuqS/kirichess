# Incident runbook

What to do when an alert fires. One maintainer, no on-call rotation
(`DEPLOYMENT.md` section 10: "every alert e-mails the whole team; nobody is staffed to answer a page
yet"). This page is the index; the detail already lives in `DEPLOYMENT.md` and the files linked from
each row.

**Read `DEPLOYMENT.md` section 10.2 first if a name below is unfamiliar** - it explains what each
signal actually measures. This page only adds the missing half: what to do about it.

## Step 0, always: tell players something is known

There is no status page, by decision (`infra/betterstack/README.md:152-153`) - proportionate at this
size and not challenged here. The fallback is Discord: the server is linked in the site footer and
runs on infrastructure entirely separate from Fly/Neon/Cloudflare, so it stays up during a KiriChess
outage.

**Post to Discord #announcements: "aware, investigating."** This is an owner action - posting the
pinned message is not something this document can do, only remind you to do it first.

## Alerts and what to do

Every row below is a real resource in `infra/betterstack/main.tf` or a real scheduled job in
`.github/workflows/`, named exactly as it appears there. Nothing here is invented.

### Better Stack alerts

| Alert (Terraform resource) | Most likely cause | First check | Response | Detail |
| --- | --- | --- | --- | --- |
| **KiriChess backend readiness** (`betteruptime_monitor.health_ready`, polls the Fly hostname's `/health/ready`) | Backend process down, a bad deploy that boots but cannot serve, or Postgres unreachable | `curl https://kirichess-api.fly.dev/health/ready`; `fly status --app kirichess-api`; `fly logs --app kirichess-api` | If a deploy just happened, roll back (section 3). If Postgres is the problem, check the Neon project dashboard for an outage or a suspended compute before assuming app code | `DEPLOYMENT.md` sections 3, 3.1 |
| **KiriChess API at the edge** (`betteruptime_monitor.edge_api`, polls `https://api.kirichess.com/health/ready`) | Cloudflare edge/DNS/WAF issue, or the same origin problem as the row above seen through the proxy | Compare against "KiriChess backend readiness" in the same window: if that one is still green, the origin is up and the edge is the incident | If edge-only: check the Cloudflare dashboard for the zone (DNS, proxy status, WAF rules). If both monitors are down: treat as the row above | `DEPLOYMENT.md` section 10.2 |
| **KiriChess site at the edge** (`betteruptime_monitor.edge_site`, polls `https://kirichess.com`, expects 200 or 302) | Same edge/origin split as above, for the apex. A Cloudflare 5xx (522/530/1016) pages; 302 alone is the pre-DH.3 Access login wall and is expected, not an incident | `curl -I https://kirichess.com`; Cloudflare dashboard | Same as the row above | `DEPLOYMENT.md` section 10.2 |
| **Matchmaking queue stuck** (`logtail_dashboard_alert.queue_stuck`, queue depth over 20 sustained 10 min) | A bug in matchmaking, or the `Pairing` kill switch was flipped off and forgotten | Better Stack dashboard "Matchmaking queue depth" chart by pool; `GET /api/moderation/kill-switches` for `Pairing`'s state (see Kill switches, below) | If `Pairing` is off unintentionally, flip it back on. Otherwise this is an app bug: check `fly logs --app kirichess-api` for matchmaking exceptions | This page, Kill switches section |
| **Hub error rate high** (`logtail_dashboard_alert.hub_error_rate_high`, over 0.1/s sustained 5 min) | An unhandled exception in a SignalR hub method, most often introduced by the latest deploy | Better Stack "Hub error rate" chart, broken down by method; Sentry (backend project, confirmed deployed per `PROVIDER-ACCOUNTS.md:20` - `fly secrets list --app kirichess-api` is what actually settles it if in doubt) | If it started right after a deploy, roll back (section 3). Otherwise open the Sentry issue for the actual stack trace | `DEPLOYMENT.md` section 3 |
| **Flag-fall sweep falling behind** (`logtail_dashboard_alert.flagfall_sweep_slow`, over 800ms sustained 5 min) | The sweep is losing to load (near the roughly 250 concurrent-game ceiling) or a slow query/lock on `Games` | Better Stack "Flag-fall sweep duration" chart alongside the "Games live" chart for the same window | If concurrent games are near the ceiling, this is capacity, not a bug - see `DEPLOYMENT.md` section 4.1. If games are low and this still fires, treat as a performance regression from a recent deploy | `DEPLOYMENT.md` section 4.1 |
| **KiriChess synthetic play** (`betteruptime_heartbeat.synthetic_play`, period 900s / grace 900s - pages after about 30 minutes of silence) | `scripts/synthetic-play.sh` could not create a game, play a move, or see the clock move - or the `ops-probes.yml` schedule itself stopped running | GitHub Actions, "Ops probes (10c.5)", latest `synthetic-play` job. If there is no recent run at all, see Maintenance calendar item 1 (a workflow can be silently disabled after 60 days of repo inactivity) | If the script failed against a live app bug, treat as an app incident (see "KiriChess backend readiness" row). If the workflow stopped running, re-arm it with a manual `workflow_dispatch` | `DEPLOYMENT.md` section 10.2; `MAINTENANCE-CALENDAR.md` |
| **KiriChess Neon watch** (`betteruptime_heartbeat.neon_storage`, period 900s / grace 900s) | `scripts/neon-watch.sh` found storage at or above 80 percent of the plan limit, a disabled compute endpoint, or the workflow itself stopped running | GitHub Actions, "Ops probes (10c.5)", latest `neon-watch` job. If no recent run, same 60-day check as above | If storage is near the cap, see I-9 (cost) for the upgrade path, or reduce what is retained. If a compute endpoint is disabled, re-enable it from the Neon console | `DEPLOYMENT.md` section 10.2 |

**Note on the count:** the finding this page answers (I-8-1) says "seven Better Stack alerts."
Reading `infra/betterstack/main.tf:65-385` directly counts eight alerting resources: the three
status monitors above (`health_ready`, `edge_api`, `edge_site`), the two heartbeats, and the three
threshold alerts. All eight are listed in the table above; treat "seven" in the original finding as
an off-by-one, not a missing row.

### Cron-failure emails (GitHub's own notification on a failed scheduled run)

These are the faster, immediate signal for two of the rows above, plus two workflows with no Better
Stack counterpart at all. Each corresponds to one of the four `if: failure()` "Explain a failure"
steps across the three workflow files.

| Failing workflow / job | Most likely cause | First check | Response | Detail |
| --- | --- | --- | --- | --- |
| **Restore drill** (`.github/workflows/restore-drill.yml`, monthly, `17 4 1 * *`) | PITR itself does not work or does not reach back far enough, a boot failure against the restored branch, a schema mismatch, or a replay failure. Production is unaffected - the drill only reads it | GitHub Actions run log for "Restore drill" | Read which of the four numbered assertions failed and follow `DEPLOYMENT.md` section 5.2's table for what each one means. Not an active incident, but it means the Neon-restore recovery plan in section 5 is unproven until fixed | `DEPLOYMENT.md` section 5.2 |
| **Off-site backup** (`.github/workflows/offsite-backup.yml`, no schedule until GitHub's DPA is accepted) | If someone dispatched it anyway: Neon read failure, an R2 upload/round-trip failure, or the local restore-verify (schema/boot/replay) failed. Production is unaffected. A run before the DPA is accepted is the thing the schedule was removed to prevent | GitHub Actions run log for "Off-site backup" | Read section 5.3's assertion table. Do not dispatch this workflow until [DPA-CHECKLIST.md](../legal/DPA-CHECKLIST.md) records the GitHub acceptance | `DEPLOYMENT.md` section 5.3 |
| **Ops probes, synthetic-play job** (`.github/workflows/ops-probes.yml`, every 15 minutes, `3,18,33,48 * * * *`) | Same as "KiriChess synthetic play" above - this is the immediate signal, the heartbeat is the roughly 30-minute backstop for a silently disabled workflow | GitHub Actions, "Ops probes (10c.5)", `synthetic-play` job log | Respond here first; it is faster than waiting for the heartbeat to time out | `DEPLOYMENT.md` section 10.2 |
| **Ops probes, neon-watch job** (`.github/workflows/ops-probes.yml`, same schedule) | Same as "KiriChess Neon watch" above, immediate rather than the heartbeat's roughly 30-minute delay | GitHub Actions, "Ops probes (10c.5)", `neon-watch` job log | Respond here first, same reasoning | `DEPLOYMENT.md` section 10.2 |

### Not a scheduled alert, but the two incidents most likely to actually happen

| Situation | First check | Response | Detail |
| --- | --- | --- | --- |
| **Leaked secret** (a token or connection string turns up somewhere it should not - a log, a commit, a screenshot) | Identify which secret and which provider it authenticates: Fly, Neon, Cloudflare/R2, Resend, Sentry, Turnstile, Google OAuth, or a GitHub Actions secret | Rotate it immediately at the source (regenerate in that provider's own console), then update it everywhere it is set (`fly secrets set ...`, or the GitHub repository secret). A rotated `Jwt__Key` only logs everyone out on their next refresh (see `CONTINUITY.md`'s secret classification) - do not treat that one as a data-loss event | `PROVIDER-ACCOUNTS.md` (where each secret lives); `CONTINUITY.md` (which secrets are cheap to rotate) |
| **Bad deploy** (a `v*` tag or manual dispatch makes things worse, whether or not an alert has fired yet) | `fly releases --app kirichess-api` to find the previous good image | Roll back per section 3 (`fly deploy --app kirichess-api --image <previous-image-ref>`). If the release included a migration, read section 3.1's roll-forward-only policy and `MIGRATIONS.md`'s per-migration cost table before deciding whether rolling the app back is safe against the current schema | `DEPLOYMENT.md` sections 3, 3.1; `MIGRATIONS.md` |

## Operational kill switches

Five booleans, no deploy needed, read and set at `/api/moderation/kill-switches`
(`src/KiriChess.Server/Infrastructure/KillSwitchEndpoints.cs`), gated by `ModerationPolicies.Admin`
(the `Admin` role plus the `mfa` claim on the token - see `docs/decisions/AUTH.md` section 1.5). Get
the bearer token the same way as any session: log in as the `Admin` account and read the access token
out of that session. A flipped switch takes effect immediately on the instance you PUT to and within
about 10 seconds on any other instance (`KillSwitchService`'s cache duration) - irrelevant today at
one Fly machine.

```bash
# Read all five
curl -s https://api.kirichess.com/api/moderation/kill-switches \
  -H "Authorization: Bearer $ADMIN_JWT"

# Flip one off (names are case-insensitive: Registration, Spectating, SeekList, Pairing, Api)
curl -s -X PUT https://api.kirichess.com/api/moderation/kill-switches/Pairing \
  -H "Authorization: Bearer $ADMIN_JWT" \
  -H "Content-Type: application/json" \
  -d "{\"enabled\": false}"
```

| Switch | Threat it maps to | Known gap |
| --- | --- | --- |
| `Registration` | A signup flood over the password path | **Fixed.** Now also gates the Google OAuth create path (`docs/audits/S.md`, finding S-5-2): an existing account can still sign back in while this is off, only new-account creation is refused |
| `Spectating` | Anonymous spectator load crowding out players for the shared connection budget | none noted |
| `SeekList` | Abuse of the public open-seek list | none noted |
| `Pairing` | Meant as "nobody enters a new game while it holds," for an unwell database | **Fixed.** The gate now lives in `GameRegistry.CreateAsync`, the one function every creation path funnels through - queue, rematch accept, challenge accept, `POST /api/games`, and `POST /api/v1/games` all honour it (`docs/audits/S.md`, finding S-5-1) |
| `Api` | Abuse or load concentrated on the token-authenticated `/api/v1` bot surface, when it is not worth flipping `Pairing`/`SeekList` (which do not reach it) or revoking tokens one at a time | none noted - added for S-5-4. Refuses every `/api/v1` route with a 503; `/hubs/game` is untouched, so a bot already connected over the hub keeps playing until its token is separately revoked or the account is banned |

## Open questions

- `infra/betterstack/main.tf`'s own comment (lines 14-16) flags that the dashboard alert SQL was
  "never exercised against a real account" at write time; if a chart in the Better Stack dashboard
  shows "no data" during an incident, that may be the SQL rather than the absence of a real signal.
  Check the query editor's live column list before trusting an empty chart.
- `ops-probes.yml`'s own failure-step text still names "section 10.2" rather than this page.
  Updating that workflow file was outside this task's file scope (see the constraints under which
  this document was written). `DEPLOYMENT.md` section 10.2 now cross-references this page, so
  following either pointer lands here; the workflow text itself is still stale and worth a one-line
  fix later.
