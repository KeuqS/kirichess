#!/usr/bin/env bash
#
# Neon watch (ROADMAP 10c.5).
#
# /health/ready already pages when the app cannot open a connection. What it cannot see is storage
# approaching the plan limit, or a compute endpoint that has been disabled. This reads the Neon
# management API with the same NEON_API_KEY 9c.4's restore drill already uses, and never needs a
# database password.
#
# Usage:
#   NEON_API_KEY=... bash scripts/neon-watch.sh
#   NEON_API_KEY=... NEON_WATCH_HEARTBEAT_URL=https://uptime.betterstack.com/api/v1/heartbeat/... \
#     bash scripts/neon-watch.sh
#
# Optional:
#   NEON_PROJECT_ID              default plain-salad-47730828
#   NEON_STORAGE_LIMIT_BYTES     override if the API's branch_logical_size_limit_bytes is a sentinel
#   NEON_STORAGE_WARN_PERCENT    default 80

set -euo pipefail

PROJECT_ID="${NEON_PROJECT_ID:-plain-salad-47730828}"
API_KEY="${NEON_API_KEY:-}"
HEARTBEAT_URL="${NEON_WATCH_HEARTBEAT_URL:-}"
REQUIRE_HEARTBEAT="${REQUIRE_HEARTBEAT:-0}"
WARN_PERCENT="${NEON_STORAGE_WARN_PERCENT:-80}"
# Launch-plan floor used only when the API reports an unlimited-looking sentinel (> 1 TiB).
LAUNCH_STORAGE_BYTES=$((10 * 1024 * 1024 * 1024))
TIB=$((1024 * 1024 * 1024 * 1024))

fail() { printf '!!! FAILED: %s\n' "$*" >&2; exit 1; }

[[ -n "$API_KEY" ]] || fail "NEON_API_KEY is not set (Neon management API key, same secret as restore-drill.yml)."
if [[ "$REQUIRE_HEARTBEAT" == "1" && -z "$HEARTBEAT_URL" ]]; then
  fail "NEON_WATCH_HEARTBEAT_URL is not set. terraform output -raw neon_watch_heartbeat_url, then gh secret set."
fi

command -v jq >/dev/null || fail "jq is required."
command -v curl >/dev/null || fail "curl is required."

neon_get() {
  local path="$1"
  local tmp code
  tmp="$(mktemp)"
  code="$(curl -sS -o "$tmp" -w '%{http_code}' \
    -H "Authorization: Bearer ${API_KEY}" \
    -H "Accept: application/json" \
    "https://console.neon.tech/api/v2${path}")"
  if [[ "$code" != "200" ]]; then
    fail "GET ${path} returned ${code}. Body: $(cat "$tmp")"
  fi
  cat "$tmp"
  rm -f "$tmp"
}

project_json="$(neon_get "/projects/${PROJECT_ID}")"
size="$(jq -er '.project.synthetic_storage_size' <<<"$project_json")"
api_limit="$(jq -er '.project.branch_logical_size_limit_bytes // 0' <<<"$project_json")"

limit="${NEON_STORAGE_LIMIT_BYTES:-$api_limit}"
if [[ "$limit" -le 0 || "$limit" -gt "$TIB" ]]; then
  limit="$LAUNCH_STORAGE_BYTES"
fi

if (( size * 100 >= limit * WARN_PERCENT )); then
  fail "Neon synthetic storage ${size} bytes is at or above ${WARN_PERCENT}% of limit ${limit} bytes."
fi

endpoints_json="$(neon_get "/projects/${PROJECT_ID}/endpoints")"
rw_count="$(jq -er '[.endpoints[] | select(.type == "read_write")] | length' <<<"$endpoints_json")"
[[ "$rw_count" -gt 0 ]] || fail "project ${PROJECT_ID} has no read_write compute endpoint."

disabled="$(jq -er '[.endpoints[] | select(.type == "read_write" and .disabled == true)] | length' <<<"$endpoints_json")"
[[ "$disabled" -eq 0 ]] || fail "a read_write compute endpoint is disabled."

# Idle is logged, not failed: scale-to-zero is a plan setting, and /health/ready already pages when
# a connection cannot be opened. disabled (above) is the operator-intent failure this script owns.
idle="$(jq -er '[.endpoints[] | select(.type == "read_write" and .current_state == "idle")] | length' <<<"$endpoints_json")"
states="$(jq -er '[.endpoints[] | select(.type == "read_write") | .current_state] | join(",")' <<<"$endpoints_json")"
percent=$(( size * 100 / limit ))

printf 'ok: storage %s / %s bytes (%s%%, warn at %s%%), rw endpoints %s (states %s, idle %s)\n' \
  "$size" "$limit" "$percent" "$WARN_PERCENT" "$rw_count" "$states" "$idle"

if [[ -n "$HEARTBEAT_URL" ]]; then
  curl -sS -o /dev/null -f "$HEARTBEAT_URL" || fail "heartbeat ping failed."
fi
