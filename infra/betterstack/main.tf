# 9c.5 + 10c.5 - Alerting. Provisions the "server is unwell rather than broken" layer ROADMAP.md
# calls for: an external uptime check on /health/ready, threshold alerts on the three app signals
# 9c.5 names (error rate, matchmaking queue depth stuck, flag-fall sweep duration climbing), and
# 10c.5's three signals that are not the app (Cloudflare-proxied hostnames, a synthetic play
# heartbeat, a Neon storage heartbeat).
#
# Two separate Better Stack products, two separate providers, two separate API tokens:
#   - betteruptime: the /health/ready check and its escalation policy.
#   - logtail (Better Stack's Telemetry product, formerly Logtail.com): the Prometheus scrape of
#     /metrics, plus a dashboard and metric alerts built on top of it.
#
# Before trusting any of this: see README.md "Confirm it works". The SQL in the dashboard_chart and
# dashboard_alert resources below reads a schema (metric name suffixes, the value_last/rate_avg
# aggregate-state columns) documented by Better Stack but never exercised against a real account -
# same "not proven" status 6d.2/6d.3 shipped with before DH.5 created a Sentry project. Check every
# chart against live data in the dashboard UI immediately after the first apply.

locals {
  name_suffix = var.environment == "production" ? "" : " (${var.environment})"

  # Shared by the /health/ready check, the two 10c.5 edge monitors, and the two heartbeats.
  policy_id = var.alert_policy_id != null ? tostring(var.alert_policy_id) : betteruptime_policy.kirichess[0].id

  # Single source of truth for the wire names 6d.3's Meter instruments take after the OpenTelemetry
  # Prometheus exporter mangles them (dots -> underscores, type-dependent suffixes).
  #
  # All four are now CONFIRMED against a real scrape rather than derived from the exporter's rules:
  # MetricsContractTests parses these very lines out of this file, exercises each instrument and
  # asserts every name appears in the exporter's output. Renaming an instrument in C# fails that test
  # instead of silently turning a chart here into "no data", which is the failure mode this block's
  # earlier per-line confidence annotations existed to hedge against. Add a name here and it is
  # covered automatically; the test only requires that at least four exist.
  metric_games_live     = "kirichess_games_live"
  metric_queue_depth    = "kirichess_matchmaking_queue_depth"
  metric_hub_errors     = "kirichess_hub_errors_total"
  metric_flagfall_sweep = "kirichess_flagfall_sweep_duration_milliseconds"

  # I-5-1: the write-behind persister's own durability instruments. Same mangling rules as the four
  # above, applied to KiriChessMetrics.cs / GameMetricsObserver.cs: dots to underscores, the
  # annotation units ({write}, {game}) drop out of the name entirely, a real "ms" unit becomes a
  # "_milliseconds" suffix, and a Counter gets "_total" appended. MetricsContractTests covers these
  # the same way it covers the original four.
  metric_snapshot_failures       = "kirichess_persistence_snapshot_failures_total"
  metric_pending_snapshots       = "kirichess_persistence_pending_snapshots"
  metric_snapshot_batch_duration = "kirichess_persistence_snapshot_batch_duration_milliseconds"

  # I-8-3: the runbook has to reach whoever is woken up, not sit in a repo they have to remember to
  # open. Better Stack's monitor and heartbeat resources have no description field (checked against
  # betteruptime provider 0.21.13's schema), so this cannot ride on each resource the way the finding
  # assumed. It rides in two places instead: an "instructions" step on the escalation policy, which
  # posts it into the timeline of every incident the five uptime resources raise, and incident_cause
  # on each metric alert, which do not route through that policy.
  #
  # The link points at main, where INCIDENTS.md is not yet: it landed in B49 on fix/audit-2026-08-31
  # and resolves once that branch merges. Pointing it at the branch instead would rot at the merge.
  runbook_url = "https://github.com/MichielGekiere/kirichess/blob/main/docs/ops/INCIDENTS.md"

  runbook_cause = "Runbook: ${local.runbook_url}#alerts-and-what-to-do - find this alert's row and follow it. Step 0 first: post \"aware, investigating\" to Discord #announcements."
}

# ---------------------------------------------------------------------------
# Uptime: /health/ready
# ---------------------------------------------------------------------------

resource "betteruptime_policy" "kirichess" {
  # Only created when no existing policy was named: an unused escalation policy left sitting in the
  # account is clutter the alert_policy_id override exists to avoid.
  count = var.alert_policy_id == null ? 1 : 0

  name = "KiriChess${local.name_suffix}"

  # Posted into the incident timeline before anyone is paged, so the first thing visible on the phone
  # at 3am is where the procedure lives. reminder_enabled stays off: the unchecked boxes are a
  # checklist, not a second nagging mechanism on top of repeat_count below.
  steps {
    type        = "instructions"
    wait_before = 0
    comment     = <<-EOT
      **Runbook: [docs/ops/INCIDENTS.md](${local.runbook_url}#alerts-and-what-to-do)**

      - [ ] Post "aware, investigating" to Discord #announcements (INCIDENTS.md step 0)
      - [ ] Find this alert's row in the runbook and follow it
      - [ ] Acknowledge the incident, or this policy keeps paging
    EOT
  }

  steps {
    type        = "escalation"
    wait_before = 0
    urgency_id  = data.betteruptime_severity.high.id
    step_members { type = "entire_team" }
  }

  # I-8-2: one notification that never repeats is not a page. This re-pages 10 minutes later if the
  # incident is still unacknowledged, and repeat_count/repeat_delay then run the whole policy three
  # more times at 15-minute spacing - roughly an hour of escalating noise before it gives up, which
  # is about the point at which a single maintainer is either awake or genuinely unreachable.
  steps {
    type        = "escalation"
    wait_before = 600
    urgency_id  = data.betteruptime_severity.high.id
    step_members { type = "entire_team" }
  }

  repeat_count = 3
  repeat_delay = 900
}

data "betteruptime_severity" "high" {
  # Better Stack's own Terraform example doc shows "High Severity" (title case) - the actual
  # default on a real account is lowercase "severity". Confirmed via GET /api/v2/urgencies (the
  # REST resource is called "urgency"; "severity" is the Terraform-only rebrand of the same thing).
  name = "High severity"
}

resource "betteruptime_monitor" "health_ready" {
  url                = "${var.backend_base_url}/health/ready"
  monitor_type       = "status"
  pronounceable_name = "KiriChess backend readiness${local.name_suffix}"

  # Matches the free-tier floor (Better Stack's paid plans go down to 30s); tighten once on a paid
  # plan. HealthEndpoints.cs's /health/ready touches the database on every call, a faster external
  # poll just adds load for no earlier signal than the 180s bucket the DO/Fly's own health check
  # already lives on.
  check_frequency = 180

  # A single slow request must not open an incident; the 503 has to hold for a minute.
  confirmation_period = 60
  recovery_period     = 60

  regions = ["us", "eu"]

  policy_id = local.policy_id

  # I-8-2: email alone cannot wake anybody. critical_alert is the iOS permission that lets a page
  # through the mute switch and Do Not Disturb; without it the push arrives silently at 3am, which is
  # the case this whole layer exists for.
  email          = true
  push           = true
  critical_alert = true
}

# ---------------------------------------------------------------------------
# Metrics: scrape /metrics into Better Stack's Telemetry product
# ---------------------------------------------------------------------------

resource "logtail_source" "metrics" {
  name     = "KiriChess metrics${local.name_suffix}"
  platform = "prometheus_scrape"

  scrape_urls           = ["${var.backend_base_url}/metrics"]
  scrape_frequency_secs = 30
  scrape_request_headers = [
    {
      name  = "Authorization"
      value = "Bearer ${var.metrics_scrape_token}"
    }
  ]

  # /metrics is public over HTTPS behind MetricsEndpoint.cs's bearer-token gate (no VPC, no
  # allowlist to configure): Fly apps have no private network boundary to punch a hole in, the
  # token above is the entire access control, same fail-closed contract MetricsOptions.Enabled
  # already enforces for every other scraper.
  #
  # That was true of the design and not of the deployed build until 2026-08-16: the guard also
  # required the request's local port to equal Metrics__Port (9091, private), so this scrape got a
  # 404 with a correct token and the charts below drew nothing. Fixed in MetricsEndpoint.Evaluate;
  # it needs a *backend deploy*, not a terraform apply. If the charts are empty, check that first.
}

resource "logtail_dashboard" "observability" {
  name             = "KiriChess observability${local.name_suffix}"
  date_range_from  = "now-6h"
  date_range_to    = "now"
  refresh_interval = 60

  variable {
    name          = "source"
    variable_type = "source"
    values        = [logtail_source.metrics.id]
  }
}

resource "logtail_dashboard_chart" "games_live" {
  dashboard_id = logtail_dashboard.observability.id
  chart_type   = "line_chart"
  name         = "Games live"
  description  = "Concurrent live games (kirichess.games.live) - the headline capacity number for this single-instance server."
  x            = 0
  y            = 0
  w            = 6
  h            = 4

  query {
    query_type = "sql_expression"
    sql_query  = <<-SQL
      SELECT {{time}} AS time, anyLastMerge(value_last) AS value
      FROM {{source}}
      WHERE dt BETWEEN {{start_time}} AND {{end_time}} AND name = '${local.metric_games_live}'
      GROUP BY time
      ORDER BY time
    SQL
  }
}

resource "logtail_dashboard_chart" "queue_depth" {
  dashboard_id = logtail_dashboard.observability.id
  chart_type   = "line_chart"
  name         = "Matchmaking queue depth"
  description  = "Players waiting per pool (kirichess.matchmaking.queue.depth). Persistent depth in one pool means nobody is being paired."
  x            = 6
  y            = 0
  w            = 6
  h            = 4

  query {
    query_type = "sql_expression"
    sql_query  = <<-SQL
      SELECT {{time}} AS time, label('pool') AS series, anyLastMerge(value_last) AS value
      FROM {{source}}
      WHERE dt BETWEEN {{start_time}} AND {{end_time}} AND name = '${local.metric_queue_depth}'
      GROUP BY time, series
      ORDER BY time
    SQL
  }
}

resource "logtail_dashboard_chart" "hub_error_rate" {
  dashboard_id = logtail_dashboard.observability.id
  chart_type   = "line_chart"
  name         = "Hub error rate"
  description  = "Unhandled SignalR hub exceptions per second (kirichess.hub.errors), by method."
  x            = 0
  y            = 4
  w            = 6
  h            = 4

  query {
    query_type = "sql_expression"
    sql_query  = <<-SQL
      SELECT {{time}} AS time, label('method') AS series, avgMerge(rate_avg) AS value
      FROM {{source}}
      WHERE dt BETWEEN {{start_time}} AND {{end_time}} AND name = '${local.metric_hub_errors}'
      GROUP BY time, series
      ORDER BY time
    SQL
  }
}

resource "logtail_dashboard_chart" "flagfall_sweep_duration" {
  dashboard_id = logtail_dashboard.observability.id
  chart_type   = "line_chart"
  name         = "Flag-fall sweep duration"
  description  = "Average wall time of one flag-fall sweep (kirichess.flagfall.sweep.duration). Approaching 1000ms means the safety net is falling behind."
  x            = 6
  y            = 4
  w            = 6
  h            = 4

  # bucket_sum/bucket_count column names are inferred from Better Stack's docs (only sumMerge(bucket_sum)
  # is shown there by name), not confirmed against a real histogram source - same "unverified" status
  # as local.metric_flagfall_sweep above. This chart may show "no data" even once the metric name is
  # fixed; check both against the query editor's live column list before trusting it.
  query {
    query_type = "sql_expression"
    sql_query  = <<-SQL
      SELECT {{time}} AS time, sumMerge(bucket_sum) / countMerge(bucket_count) AS value
      FROM {{source}}
      WHERE dt BETWEEN {{start_time}} AND {{end_time}} AND name = '${local.metric_flagfall_sweep}'
      GROUP BY time
      ORDER BY time
    SQL
  }
}

resource "logtail_dashboard_chart" "snapshot_failures" {
  dashboard_id = logtail_dashboard.observability.id
  chart_type   = "line_chart"
  name         = "Snapshot write failures"
  description  = "Write-behind move-snapshot writes that threw (kirichess.persistence.snapshot_failures), after the one-at-a-time retry (LiveGamePersister.cs:182) also failed. Non-zero means durability is degraded."
  x            = 0
  y            = 8
  w            = 6
  h            = 4

  query {
    query_type = "sql_expression"
    sql_query  = <<-SQL
      SELECT {{time}} AS time, avgMerge(rate_avg) AS value
      FROM {{source}}
      WHERE dt BETWEEN {{start_time}} AND {{end_time}} AND name = '${local.metric_snapshot_failures}'
      GROUP BY time
      ORDER BY time
    SQL
  }
}

resource "logtail_dashboard_chart" "pending_snapshots" {
  dashboard_id = logtail_dashboard.observability.id
  chart_type   = "line_chart"
  name         = "Pending snapshots"
  description  = "Games with a coalesced move-snapshot write still queued (kirichess.persistence.pending_snapshots). Sits near zero in healthy operation; sustained growth means Postgres is not keeping up and a crash would lose more than the tail move."
  x            = 6
  y            = 8
  w            = 6
  h            = 4

  query {
    query_type = "sql_expression"
    sql_query  = <<-SQL
      SELECT {{time}} AS time, anyLastMerge(value_last) AS value
      FROM {{source}}
      WHERE dt BETWEEN {{start_time}} AND {{end_time}} AND name = '${local.metric_pending_snapshots}'
      GROUP BY time
      ORDER BY time
    SQL
  }
}

resource "logtail_dashboard_chart" "snapshot_batch_duration" {
  dashboard_id = logtail_dashboard.observability.id
  chart_type   = "line_chart"
  name         = "Snapshot batch write duration"
  description  = "Wall time of one batched move-snapshot round trip to Neon (kirichess.persistence.snapshot_batch.duration). No alert on this one: it is the write RTT that sets the persister's throughput ceiling (9b.3 / 9c.7), and its value is for a human to read and compute that ceiling from, the same way it replaces the unmeasured 200-600 concurrent-game band in DEPLOYMENT.md section 4.1."
  x            = 0
  y            = 12
  w            = 6
  h            = 4

  # Same bucket_sum/bucket_count column shape as flagfall_sweep_duration above, and the same
  # confirmation status: MetricsContractTests records to this histogram and asserts the exported
  # name, but the SQL's aggregate-state columns are still Better Stack's documented shape, not
  # something exercised against a real account.
  query {
    query_type = "sql_expression"
    sql_query  = <<-SQL
      SELECT {{time}} AS time, sumMerge(bucket_sum) / countMerge(bucket_count) AS value
      FROM {{source}}
      WHERE dt BETWEEN {{start_time}} AND {{end_time}} AND name = '${local.metric_snapshot_batch_duration}'
      GROUP BY time
      ORDER BY time
    SQL
  }
}

resource "logtail_dashboard_alert" "queue_stuck" {
  dashboard_id = logtail_dashboard.observability.id
  chart_id     = logtail_dashboard_chart.queue_depth.id
  name         = "Matchmaking queue stuck${local.name_suffix}"
  alert_type   = "threshold"
  operator     = "higher_than"
  value        = 20

  check_period        = 60
  query_period        = 600 # sustained, not a momentary spike - matching "persistent depth" in the metric's own description
  confirmation_period = 300
  on_missing_data     = "dont_fire"

  email          = true
  push           = true
  critical_alert = true
  incident_cause = local.runbook_cause

  dynamic "escalation_target" {
    for_each = var.alert_policy_id != null ? [1] : []
    content {
      policy_id = var.alert_policy_id
    }
  }
}

resource "logtail_dashboard_alert" "hub_error_rate_high" {
  dashboard_id = logtail_dashboard.observability.id
  chart_id     = logtail_dashboard_chart.hub_error_rate.id
  name         = "Hub error rate high${local.name_suffix}"
  alert_type   = "threshold"
  operator     = "higher_than"
  value        = 0.1 # one unexpected hub exception every ~10s sustained; tune once real traffic exists

  check_period        = 60
  query_period        = 300
  confirmation_period = 60
  on_missing_data     = "treat_as_zero"

  email          = true
  push           = true
  critical_alert = true
  incident_cause = local.runbook_cause

  dynamic "escalation_target" {
    for_each = var.alert_policy_id != null ? [1] : []
    content {
      policy_id = var.alert_policy_id
    }
  }
}

resource "logtail_dashboard_alert" "flagfall_sweep_slow" {
  dashboard_id = logtail_dashboard.observability.id
  chart_id     = logtail_dashboard_chart.flagfall_sweep_duration.id
  name         = "Flag-fall sweep falling behind${local.name_suffix}"
  alert_type   = "threshold"
  operator     = "higher_than"
  value        = 800 # ms; the sweep runs once a second, GameMetricsObserver's own comment calls ~1000ms the point it can no longer keep up

  check_period        = 60
  query_period        = 300
  confirmation_period = 60
  on_missing_data     = "dont_fire"

  # Unpaused 2026-08-16. It shipped paused because metric_flagfall_sweep was the one name here with
  # genuinely unverified suffix behaviour (histogram + a real "ms" unit, where the exporter's
  # unit-suffix mapping is version-sensitive). MetricsContractTests now records to that histogram and
  # asserts the exported name, so the reason for the pause is gone. `on_missing_data = "dont_fire"`
  # above remains the safety net: before the fixed build is deployed there is nothing to scrape, and
  # an alert with no data must stay quiet rather than page on absence.
  paused = false

  email          = true
  push           = true
  critical_alert = true
  incident_cause = local.runbook_cause

  dynamic "escalation_target" {
    for_each = var.alert_policy_id != null ? [1] : []
    content {
      policy_id = var.alert_policy_id
    }
  }
}

resource "logtail_dashboard_alert" "snapshot_failures_high" {
  dashboard_id = logtail_dashboard.observability.id
  chart_id     = logtail_dashboard_chart.snapshot_failures.id
  name         = "Snapshot write failures sustained${local.name_suffix}"
  alert_type   = "threshold"
  operator     = "higher_than"
  value        = 0.05 # about 1 failed write every 20s sustained over the query window. A guess, not a
  # measured failure rate (no real traffic has hit this yet) - calibrate against the first weeks of
  # production. Deliberately a rate over several minutes, not "> 0 once": a lone transient failure
  # self-heals, because the next snapshot writes the whole move list rather than a delta
  # (LiveGamePersister.cs:174), so only a sustained rate means Neon itself is unwell.

  check_period        = 60
  query_period        = 300
  confirmation_period = 300 # sustained, matching the "self-heals" reasoning above, not a momentary blip
  on_missing_data     = "treat_as_zero"

  email          = true
  push           = true
  critical_alert = true
  incident_cause = local.runbook_cause

  dynamic "escalation_target" {
    for_each = var.alert_policy_id != null ? [1] : []
    content {
      policy_id = var.alert_policy_id
    }
  }
}

# I-5-2: none of the alerts above ever fire on a dark scrape - queue_stuck and flagfall_sweep_slow
# are both `on_missing_data = "dont_fire"` by design, and logtail_source.metrics itself has no
# freshness check. `/metrics` returned a bare 404 in production from the first apply
# (2026-08-15) until a redeploy that fixed MetricsEndpoint's guard, and for that whole window every
# chart on this dashboard drew "no data" with nothing paging - the regression was found by a person
# noticing an empty chart, not by this alerting layer. `/health/ready` does not catch this either:
# the app is up, only its telemetry is dark.
#
# kirichess_games_live is the series to watch for absence: it is a single-value observable gauge
# with no per-tag fan-out (MetricsContractTests's own comment on it), so a healthy scrape - even an
# idle server with zero live games - always emits a real 0, never nothing. "No data" for this one
# series can only mean the scrape itself broke (the /metrics guard regressing, a rotated or wrong
# scrape token, a bad deploy), never "nobody is playing right now". The threshold condition below
# is deliberately one this metric can never actually breach (games_live cannot go negative);
# `on_missing_data = "start_incident"` is the entire alert, and confirmation_period is spent on
# purpose - a scrape can legitimately miss a single interval - so this needs a genuinely dark
# source for the better part of ten minutes before it pages.
resource "logtail_dashboard_alert" "metrics_scrape_stale" {
  dashboard_id = logtail_dashboard.observability.id
  chart_id     = logtail_dashboard_chart.games_live.id
  name         = "Metrics scrape stale${local.name_suffix}"
  alert_type   = "threshold"
  operator     = "lower_than"
  value        = 0 # unreachable: games_live is never negative, so only on_missing_data can fire this

  check_period        = 60
  query_period        = 300
  confirmation_period = 300
  on_missing_data     = "start_incident"

  email          = true
  push           = true
  critical_alert = true
  incident_cause = "The /metrics scrape has produced no data for kirichess_games_live for several minutes. This metric always emits a real 0 on a healthy idle server, so its absence means the scrape broke (guard regression, rotated/wrong token, or /metrics itself down), not that nobody is playing. ${local.runbook_cause}"

  dynamic "escalation_target" {
    for_each = var.alert_policy_id != null ? [1] : []
    content {
      policy_id = var.alert_policy_id
    }
  }
}

resource "logtail_dashboard_alert" "pending_snapshots_high" {
  dashboard_id = logtail_dashboard.observability.id
  chart_id     = logtail_dashboard_chart.pending_snapshots.id
  name         = "Pending snapshots backing up${local.name_suffix}"
  alert_type   = "threshold"
  operator     = "higher_than"
  value        = 10 # games queued for a write-behind snapshot, sustained. GameMetricsObserver.cs's own
  # description calls this "near zero" in healthy operation with only brief spikes, but nobody has
  # watched it under real load yet - this ceiling is a starting guess, calibrate once real traffic exists.

  check_period        = 60
  query_period        = 600 # sustained, not a momentary spike - same "persistent depth" reasoning as queue_stuck
  confirmation_period = 300
  on_missing_data     = "dont_fire" # a dark scrape is "no data", not "backed up" - see I-5-2 for the separate absence alert this deployment still needs

  email          = true
  push           = true
  critical_alert = true
  incident_cause = local.runbook_cause

  dynamic "escalation_target" {
    for_each = var.alert_policy_id != null ? [1] : []
    content {
      policy_id = var.alert_policy_id
    }
  }
}

# ---------------------------------------------------------------------------
# Logs: a destination for every app's stdout (9c.6)
# ---------------------------------------------------------------------------
#
# 6d.2's AddJsonConsole is already the right format; what's missing is a destination that outlives
# Fly's own log buffer (a few hours), which is currently the only copy of the evidence behind a
# cheating report or a disputed game (6e.2's report table is otherwise read with psql). This
# resource is only the destination - the collector that fills it is a separate Fly app
# (fly.logshipper.toml at the repo root, running superfly/fly-log-shipper's prebuilt Vector image),
# deployed by hand with this source's token. See DEPLOYMENT.md "Log retention / aggregation (9c.6)"
# for the full runbook, and outputs.tf for how to read the token without it ever touching this repo.
resource "logtail_source" "app_logs" {
  name     = "KiriChess app logs${local.name_suffix}"
  platform = "fly_io"

  # No logs_retention override: 8a.3's retention policy governs account *data*, not operational
  # logs, so there is no existing decision to inherit here. Whatever the account's plan default is
  # applies until someone deliberately buys more.
}

# ---------------------------------------------------------------------------
# 10c.5: the three signals 9c.5's alerting does not have
# ---------------------------------------------------------------------------
#
# 9c.5 watches the app (hub errors, queue depth, flag-fall sweep) plus /health/ready on the Fly
# hostname. That leaves the database (storage, not just "can we open a connection"), the edge
# (Cloudflare 5xx, which the fly.dev probe never sees), and whether a game can actually be played.
#
# Playwright monitors would be the obvious shape for the synthetic, and they are a paid add-on
# ($1/100 minutes) on top of the free-tier 10 monitors this account was sized for. Heartbeats plus
# a GitHub Action that runs scripts/synthetic-play.sh and scripts/neon-watch.sh stay inside that
# allowance: two new status monitors + two heartbeats, five of ten with the existing /health/ready
# check. See DEPLOYMENT.md § 10.2.

resource "betteruptime_monitor" "edge_api" {
  url                = "${var.edge_api_url}/health/ready"
  monitor_type       = "status"
  pronounceable_name = "KiriChess API at the edge${local.name_suffix}"

  check_frequency     = 180
  confirmation_period = 60
  recovery_period     = 60
  regions             = ["us", "eu"]

  policy_id = local.policy_id

  # I-8-2: email alone cannot wake anybody. critical_alert is the iOS permission that lets a page
  # through the mute switch and Do Not Disturb; without it the push arrives silently at 3am, which is
  # the case this whole layer exists for.
  email          = true
  push           = true
  critical_alert = true
}

# I-5-5 / DH.3: Access is off; apex must answer 200. Accepting 302 would hide a redirect loop,
# accidental Access re-enable, or redirecting Cloudflare rule. Defaults for follow_redirects /
# remember_cookies are fine again (no 3xx expected codes). Do not add 403: that would swallow a
# WAF misconfiguration. Cloudflare origin errors are 5xx (522/530/1016) and still page.
resource "betteruptime_monitor" "edge_site" {
  url                   = var.edge_site_url
  monitor_type          = "expected_status_code"
  expected_status_codes = [200]
  pronounceable_name    = "KiriChess site at the edge${local.name_suffix}"

  check_frequency     = 180
  confirmation_period = 60
  recovery_period     = 60
  regions             = ["us", "eu"]

  policy_id = local.policy_id

  # I-8-2: email alone cannot wake anybody. critical_alert is the iOS permission that lets a page
  # through the mute switch and Do Not Disturb; without it the push arrives silently at 3am, which is
  # the case this whole layer exists for.
  email          = true
  push           = true
  critical_alert = true
}

# Period 900 / grace 900 matches the ops-probes workflow's "every 15 minutes, off the hour".
# GitHub cron on a quiet personal repo can slip by tens of minutes; the grace is that slip, not
# a preference for slow alerts. A missed ping for 30 minutes is the page.
resource "betteruptime_heartbeat" "synthetic_play" {
  name   = "KiriChess synthetic play${local.name_suffix}"
  period = 900
  grace  = 900

  policy_id = local.policy_id

  # I-8-2: email alone cannot wake anybody. critical_alert is the iOS permission that lets a page
  # through the mute switch and Do Not Disturb; without it the push arrives silently at 3am, which is
  # the case this whole layer exists for.
  email          = true
  push           = true
  critical_alert = true
}

resource "betteruptime_heartbeat" "neon_storage" {
  name   = "KiriChess Neon watch${local.name_suffix}"
  period = 900
  grace  = 900

  policy_id = local.policy_id

  # I-8-2: email alone cannot wake anybody. critical_alert is the iOS permission that lets a page
  # through the mute switch and Do Not Disturb; without it the push arrives silently at 3am, which is
  # the case this whole layer exists for.
  email          = true
  push           = true
  critical_alert = true
}
