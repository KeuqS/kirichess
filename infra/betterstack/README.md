# Better Stack (9c.5 alerting + 9c.6 log shipping + 10c.5)

Terraform for the observability layer [ROADMAP.md 9c.5](../../docs/ROADMAP.md),
[9c.6](../../docs/ROADMAP.md) and [10c.5](../../docs/ROADMAP.md) ask for: an external uptime check
on `/health/ready`, threshold alerts on three named app signals, a destination for application logs
that outlives Fly's own buffer, plus the three signals 9c.5 cannot see (the edge, Neon storage, a
game actually being played). One Better Stack account, one `terraform apply`, two unrelated products
under the hood (Uptime and Telemetry). See [DEPLOYMENT.md § 10](../../docs/ops/DEPLOYMENT.md#10-alerting--uptime-9c5)
and [§ 11](../../docs/ops/DEPLOYMENT.md#11-log-retention--aggregation-9c6) for the narrative version.

## What this provisions

- `betteruptime_monitor.health_ready` - polls `https://<backend>/health/ready` every 3 minutes
  (free-tier floor) on the **Fly hostname**, opens an incident on a sustained non-2xx (i.e. the 503
  `HealthEndpoints.cs` returns when the database is unreachable), e-mails the escalation policy.
- `betteruptime_monitor.edge_api` / `edge_site` (10c.5) - the same check through Cloudflare
  (`api.kirichess.com/health/ready` and `kirichess.com`). Origin-up/edge-down is how you tell a
  Cloudflare 5xx from a dead app. After DH.3 / I-5-5 the site monitor expects 200 only (302 would
  hide a redirect loop or accidental Access re-enable).
- `betteruptime_heartbeat.synthetic_play` / `neon_storage` (10c.5) - pinged by
  [scripts/synthetic-play.sh](../../scripts/synthetic-play.sh) and
  [scripts/neon-watch.sh](../../scripts/neon-watch.sh) on a 15-minute GitHub Actions schedule.
  Playwright monitors would do the synthetic in-process at Better Stack and are a paid add-on;
  heartbeats stay inside the free-tier 10-monitor allowance.
- `logtail_source.metrics` (platform `prometheus_scrape`) - scrapes `/metrics` every 30s,
  authenticating with the same bearer token `MetricsEndpoint.cs` already gates the endpoint on.
- `logtail_dashboard` with four charts (games live, queue depth, hub error rate, flag-fall sweep
  duration) and three threshold alerts on the last three.
- `logtail_source.app_logs` (platform `fly_io`, 9c.6) - a destination for every app's stdout. This
  resource only creates the sink; the collector that fills it is a separate Fly app,
  [fly.logshipper.toml](../../fly.logshipper.toml) at the repo root, deployed independently (see
  DEPLOYMENT.md § 11). Its token and ingesting host come out as sensitive Terraform outputs
  (`outputs.tf`), never written to a file this repo tracks.

## Before you trust this - read first

**The account exists and `terraform apply` has run** (2026-08-15, all 12 resources). The uptime
monitor is genuinely polling. What is still unproven is everything downstream of a **scrape**, and
until 2026-08-16 that was not the schema's fault: `/metrics` returned `404` to any caller off Fly's
private network, correct bearer token or not, because `MetricsEndpoint`'s port gate and token gate
were a conjunction. That is fixed in code and **needs a backend deploy** before any chart here can
draw a line; see [DEPLOYMENT.md § 10.1](../../docs/ops/DEPLOYMENT.md#101-state-as-of-2026-08-16-uptime-is-live-metrics-needs-one-redeploy).

### Metric names: all four now confirmed (2026-08-16)

This section used to grade each name by confidence, from "confirmed" down to "unverified". That is
resolved. `MetricsContractTests` parses the `metric_*` lines out of [main.tf](main.tf)'s `locals`,
exercises each instrument and asserts every name appears in the exporter's real output, so a rename
in C# fails the build instead of silently turning a chart here into "no data". Adding a fifth name
to `locals` is covered automatically.

`flagfall_sweep_slow` shipped with `paused = true` purely because its histogram name was the least
certain of the four; that name is now confirmed and **the alert is unpaused**.

**One real finding came out of writing that test, and it changes how one chart reads.**
`kirichess_matchmaking_queue_depth` is an observable gauge that emits one measurement _per pool with
somebody waiting in it_ (`GameMetricsObserver.ObserveQueueDepth` projects `MatchQueue.Depths()`).
With an empty queue, which is the normal state, **the series does not exist on the wire at all**, so
the queue-depth chart reads "no data" rather than "0" whenever nobody is queuing. That is not a
fault and it does not weaken the `queue_stuck` alert, an absent series cannot exceed a threshold and
`on_missing_data = "dont_fire"` covers it explicitly. It does mean an empty chart there is not
evidence of a broken scrape, which is exactly the wrong conclusion to reach at 3am.

**What is still unproven** is the _SQL_, not the names: the dashboard and alert queries were written
against Better Stack's documented metrics schema (`name`, `tags`/`label()`,
`value_last`/`rate_avg`/`bucket_sum` aggregate-state columns) and have still never run against a
live scrape, because until the fixed build is deployed there is nothing to scrape.

## Prerequisites

1. A Better Stack account (free tier covers this: metric alerts, uptime checks and log ingest all
   fit in it - see ROADMAP.md 9c.5/9c.6's shared-vendor recommendation).
2. Two API tokens (Uptime and Telemetry are separate products, separate tokens even on one
   account): **Uptime -> Team Settings -> API tokens**, **Logs & Metrics -> Team Settings -> API
   tokens**.
3. The `Metrics__ScrapeToken` Fly secret already set on the target app (DEPLOYMENT.md's existing
   `/metrics` gate) - this run needs the value, not just its existence.
4. `terraform` >= 1.5 installed locally (or run it wherever the rest of this repo's ops tooling runs;
   nothing here is CI-triggered).

**I-2-11, 2026-09-05: `terraform.tfstate` and `terraform.tfvars` are local-only, with no remote
backend and no backup.** `versions.tf` declares no `backend` block, so state lives in a plain file
in this directory, and `.gitignore` correctly keeps both it and `.tfvars` out of git (they hold the
Better Stack tokens and the resulting resource IDs). That is safe for the resources themselves - the
monitors, the heartbeats, the log source keep running with no Terraform involved - but it is this
directory's own bus-factor edge: losing the machine this ran on does not lose the 12+ provisioned
resources, only the ability to manage them _through this directory_ without a `terraform import` per
resource, including the two heartbeat URLs that are already GitHub secrets and the log source token
`kirichess-log-shipper` authenticates with. **Whether to move state to a remote backend, or just make
sure `terraform.tfstate`/`terraform.tfvars` land in whatever personal backup B-1 settles on, is an
owner decision, not made here** - this note only records that neither has happened yet.

## Running it

```bash
cd infra/betterstack
terraform init
cp terraform.tfvars.example terraform.tfvars   # fill in the three tokens, or use TF_VAR_* env vars instead and skip this file entirely
terraform plan
terraform apply
```

For staging, either pass `-var backend_base_url=https://kirichess-api-staging.fly.dev -var environment=staging`
or point `terraform.tfvars` at it - the two environments are not meant to share one `terraform
apply`, run it twice with different vars (or split into a second `terraform workspace`/directory
if this grows past a one-off).

## Confirm it works

1. **First confirm the scrape can succeed at all**, otherwise every chart reads "no data" for a
   reason that has nothing to do with the metric names:

   ```bash
   curl -sS -o /dev/null -w '%{http_code}\n' \
     -H "Authorization: Bearer $METRICS_SCRAPE_TOKEN" \
     https://kirichess-api.fly.dev/metrics    # 200 = fixed build is deployed; 404 = it is not
   ```

2. Open the Telemetry dashboard in Better Stack and check all four charts are drawing real lines,
   not "no data" - this is the actual verification step for the schema caveat above, faster than
   reading any more documentation.
3. If a chart shows no data, the metric **name** is no longer the first thing to suspect (they are
   all test-confirmed now) - the query is. Two exceptions worth knowing before chasing one:
   `queue_depth` is legitimately absent whenever nobody is queuing, and `hub_error_rate` is
   legitimately absent until a hub method has actually thrown; on a quiet server both are correct.
4. Force a `/health/ready` failure in staging (stop the database, or set an unreachable
   `ConnectionStrings__Postgres`) and confirm the uptime monitor actually opens an incident and
   sends the e-mail, not just that Terraform created it.
5. Log shipping is live (DEPLOYMENT.md § 11, 2026-08-19): Better Stack -> Live tail for
   `KiriChess app logs` shows real `kirichess-api` lines. Re-check there if the shipper is
   redeployed.

## 10c.5 after apply

The four new Uptime resources (two monitors, two heartbeats) exist after this directory's
`terraform apply`. **Secrets landed 2026-08-20** (`SYNTHETIC_API_TOKEN`,
`SYNTHETIC_HEARTBEAT_URL`, `NEON_WATCH_HEARTBEAT_URL`). `NEON_API_KEY` was already set (9c.4).
Do not register from the probe - Turnstile sits on that path.

Replay if a secret is rotated (`gh secret set` from the repo root is fine; `terraform output`
must run in this directory so stdout never hits scrollback):

```bash
terraform output -raw synthetic_play_heartbeat_url | gh secret set SYNTHETIC_HEARTBEAT_URL
terraform output -raw neon_watch_heartbeat_url     | gh secret set NEON_WATCH_HEARTBEAT_URL
```

**Do not dispatch yet.** August private-repo minutes are spent. After 1 Sep, dispatch **Ops probes
(10c.5)** once from the Actions tab. The heartbeats go green; until that first ping they will
open an incident after `period + grace` (30 minutes).

`BetterStackAlertingDriftTests` fails the build if those four resource blocks, or the two scripts,
disappear.

## What this does not do

- **No on-call rotation.** `alert_policy_id` defaults to `null`, which e-mails the whole team on
  every incident - there is no on-call rotation to page instead (see ROADMAP.md 8.7, nobody
  is staffed to answer one yet).
- **Log shipping is live.** `logtail_source.app_logs` is the sink;
  `kirichess-log-shipper` forwards into it (DEPLOYMENT.md § 11). Do not `terraform apply` to
  "fix" an empty Live tail - that is a Fly secret / deploy problem.
- **No status page.** Not asked for by 9c.5's text; add a `betteruptime_status_page` resource here
  later if that changes.
- **Thresholds are starting guesses**, not measured limits. `queue_stuck` (20, sustained 10 min),
  `hub_error_rate_high` (0.1/s, sustained 5 min) and `flagfall_sweep_slow` (800ms) all have no real
  traffic behind them yet - expect to retune after the first week of real games.
- **No hosted Playwright synthetic.** Better Stack sells those by the minute. The play probe is a
  GitHub Action pinging a heartbeat, which stays inside the free-tier monitor allowance.
- **Idle Neon compute is not a page.** `/health/ready` already covers "cannot open a connection".
  `neon-watch.sh` pages on storage ≥ 80% of the plan limit and on a disabled read_write endpoint.
