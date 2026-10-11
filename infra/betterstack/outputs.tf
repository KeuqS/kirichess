# Both values feed the fly-log-shipper app's secrets (BETTER_STACK_SOURCE_TOKEN /
# BETTER_STACK_INGESTING_HOST), not this repo. Read the token with
# `terraform output -raw log_shipper_source_token` and pipe it straight into `fly secrets set` -
# never let it land in a file this repo tracks or in a chat transcript.
#
# ingesting_host is a bare hostname (e.g. sNNNN.us-west-2a.betterstackdata.com). Vector's sink
# already prefixes https://; if a scheme ever appears here, strip it before fly secrets set or
# the URI becomes https://https://... and Better Stack 401s.

output "log_shipper_source_token" {
  description = "BETTER_STACK_SOURCE_TOKEN for the fly-log-shipper app (fly.logshipper.toml)."
  value       = logtail_source.app_logs.token
  sensitive   = true
}

output "log_shipper_ingesting_host" {
  description = "BETTER_STACK_INGESTING_HOST for the fly-log-shipper app. Bare hostname, no scheme. Not sensitive by itself, but meaningless without the token above."
  value       = logtail_source.app_logs.ingesting_host
}

# 10c.5 heartbeats. The URL is the capability: anyone who can GET it marks the check healthy, so
# treat it like a secret. Pipe `terraform output -raw ...` into `gh secret set`, never a bare
# `terraform output`.

output "synthetic_play_heartbeat_url" {
  description = "SYNTHETIC_HEARTBEAT_URL for scripts/synthetic-play.sh (and the ops-probes workflow)."
  value       = betteruptime_heartbeat.synthetic_play.url
  sensitive   = true
}

output "neon_watch_heartbeat_url" {
  description = "NEON_WATCH_HEARTBEAT_URL for scripts/neon-watch.sh (and the ops-probes workflow)."
  value       = betteruptime_heartbeat.neon_storage.url
  sensitive   = true
}
