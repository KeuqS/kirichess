#!/usr/bin/env bash
#
# Synthetic play probe (ROADMAP 10c.5).
#
# 9c.5's /health/ready check proves the process answers and a connection opens. This proves a game
# can actually be played: create an unrated bounty1 practice game (caller owns both sides, same as
# Play vs bot), submit e2e4, assert White's clock moved, resign so the session does not sit in
# memory until flag-fall.
#
# A pre-created account with a `play` API token, not a fresh register. Turnstile sits on
# /api/auth/register (DH.5), and a production register every 15 minutes would be a new account each
# time. 10c.4's Pairing kill switch does not gate POST /api/v1/games, so flipping that lever during
# an incident does not page this probe.
#
# I-2-5: also negotiates a real /hubs/game connection over HTTPS, the leg the REST checks above
# never touch. `api.kirichess.com` is DNS-only, not Cloudflare-proxied (docs/ops/DEPLOYMENT.md § 1,
# I-2-1/I-2-3, 2026-09-04), so this is not "the Cloudflare-proxied WebSocket leg" the finding
# originally worried about - that leg does not exist in the current topology, there is no CDN idle
# timeout in front of the hub to traverse. What this still proves, that nothing else does: the hub
# negotiate pipeline (JWT/API-token auth, HubConnectionBudget's negotiate-time probe, the route
# actually being mapped) answers through the public hostname. It stops at negotiate: holding an
# actual WebSocket open past SignalR's keep-alive needs a real SignalR client, which curl is not -
# see docs/ops/DEPLOYMENT.md § 4 for the keep-alive/idle-timeout numbers this does not exercise.
#
# Usage:
#   SYNTHETIC_API_TOKEN=kc_... bash scripts/synthetic-play.sh
#   SYNTHETIC_API_TOKEN=kc_... SYNTHETIC_HEARTBEAT_URL=https://uptime.betterstack.com/api/v1/heartbeat/... \
#     bash scripts/synthetic-play.sh
#
# Hits the public API hostname by default, not kirichess-api.fly.dev (OriginLock 404s that Host for
# everything but /health and /metrics, see I-2-2), so a working origin behind a broken DNS record or
# a misconfigured record still fails this check.

set -euo pipefail

API_BASE_URL="${API_BASE_URL:-https://api.kirichess.com}"
TOKEN="${SYNTHETIC_API_TOKEN:-}"
HEARTBEAT_URL="${SYNTHETIC_HEARTBEAT_URL:-}"
REQUIRE_HEARTBEAT="${REQUIRE_HEARTBEAT:-0}"

fail() { printf '!!! FAILED: %s\n' "$*" >&2; exit 1; }

[[ -n "$TOKEN" ]] || fail "SYNTHETIC_API_TOKEN is not set (create a play-scoped API token on a throwaway account)."
if [[ "$REQUIRE_HEARTBEAT" == "1" && -z "$HEARTBEAT_URL" ]]; then
  fail "SYNTHETIC_HEARTBEAT_URL is not set. terraform output -raw synthetic_play_heartbeat_url, then gh secret set."
fi

command -v jq >/dev/null || fail "jq is required."
command -v curl >/dev/null || fail "curl is required."

auth_header="Authorization: Bearer ${TOKEN}"
game_id=""
tmp="$(mktemp)"

cleanup() {
  local exit_code=$?
  if [[ -n "$game_id" ]]; then
    curl -sS -o /dev/null -X POST \
      -H "$auth_header" \
      -H "Content-Type: application/json" \
      "${API_BASE_URL}/api/v1/games/${game_id}/resign" \
      || true
  fi
  rm -f "$tmp"
  exit "$exit_code"
}
trap cleanup EXIT

http_json() {
  local method="$1" path="$2" body="${3:-}"
  local args=(-sS -D - -o "$tmp" -X "$method" -H "$auth_header" -H "Content-Type: application/json")
  if [[ -n "$body" ]]; then
    args+=(--data "$body")
  fi
  local headers
  headers="$(curl "${args[@]}" "${API_BASE_URL}${path}")"
  local code
  code="$(printf '%s' "$headers" | tr -d '\r' | awk 'BEGIN{IGNORECASE=1} /^HTTP\//{code=$2} END{print code}')"
  printf '%s' "$code"
}

code="$(http_json POST /api/v1/games '{"preset":"bounty1"}')"
[[ "$code" == "201" ]] || fail "POST /api/v1/games returned ${code}, expected 201. Body: $(cat "$tmp")"

game_id="$(jq -er '.gameId' "$tmp")"
white_before="$(jq -er '.whiteTime' "$tmp")"

code="$(http_json POST "/api/v1/games/${game_id}/moves" '{"uci":"e2e4"}')"
[[ "$code" == "200" ]] || fail "POST /api/v1/games/${game_id}/moves returned ${code}, expected 200. Body: $(cat "$tmp")"

white_delta="$(jq -er '.whiteDelta' "$tmp")"
white_after="$(jq -er '.state.whiteTime' "$tmp")"
move_count="$(jq -er '.state.moveCount' "$tmp")"

[[ "$move_count" == "1" ]] || fail "moveCount is ${move_count}, expected 1 after e2e4."
# Winder charges a pawn movement toll (moveCostPerPiece.pawn = 1) even at zero think time, so the
# clock must move. Either signal is enough; both failing is the 404-shaped outage this exists to catch.
if [[ "$white_delta" -eq 0 && "$white_after" -eq "$white_before" ]]; then
  fail "clock did not move after e2e4 (whiteDelta=${white_delta}, whiteTime ${white_before} -> ${white_after})."
fi

printf 'ok: game %s e2e4 whiteTime %s -> %s (delta %s)\n' "$game_id" "$white_before" "$white_after" "$white_delta"

# I-2-5: negotiate a real /hubs/game connection. access_token as both header and query string
# covers a JWT (read via the query string only inside JwtBearerEvents.OnMessageReceived for /hubs
# paths) and a kc_ API token (ApiTokenAuthenticationHandler reads either) without this script having
# to know which kind SYNTHETIC_API_TOKEN is.
negotiate_code="$(curl -sS -o "$tmp" -w '%{http_code}' -X POST \
  -H "$auth_header" \
  "${API_BASE_URL}/hubs/game/negotiate?negotiateVersion=1&access_token=${TOKEN}")"
[[ "$negotiate_code" == "200" ]] || fail "POST /hubs/game/negotiate returned ${negotiate_code}, expected 200. Body: $(cat "$tmp")"

transports="$(jq -er '[.availableTransports[].transport] | join(",")' "$tmp")"
case ",${transports}," in
  *,WebSockets,*) : ;;
  *) fail "negotiate did not offer WebSockets (offered: ${transports})." ;;
esac

printf 'ok: /hubs/game negotiate succeeded, transports: %s\n' "$transports"

if [[ -n "$HEARTBEAT_URL" ]]; then
  curl -sS -o /dev/null -f "$HEARTBEAT_URL" || fail "heartbeat ping failed."
fi
