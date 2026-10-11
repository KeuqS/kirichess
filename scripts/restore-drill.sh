#!/usr/bin/env bash
#
# Restore drill (ROADMAP 9c.4 by hand, 9e.8 on a schedule).
#
# Restores production to a throwaway Neon branch at a point in the past, boots this exact build of
# the server against it, and asserts the restored database is not merely reachable but *current and
# readable*. A backup nobody has restored from since the schema last changed is a hypothesis, and
# `CanConnectAsync` returning true is not the same as the data being there.
#
# Four assertions, in the order they get harder:
#
#   1. BRANCH   Neon can create a branch from a timestamp in the past at all (this is PITR itself;
#               if it fails, every recovery plan in docs/ops/DEPLOYMENT.md § 5 is fiction).
#   2. BOOT     the app starts against the restored branch and /health/ready returns 200.
#   3. SCHEMA   __EFMigrationsHistory's newest row equals the newest migration in this checkout.
#               This is 9e.8's actual worry: a restore that predates the last migration boots fine
#               and then fails on the first query touching a new column.
#   4. REPLAY   the most recently finished game replays through the rule engine end to end, via the
#               real /api/games/{id}/replay endpoint. Proves stored moves survived the round trip,
#               not just that rows exist.
#
# They are numbered by severity, not by execution order: SCHEMA runs before BOOT, because a stale
# schema is the likeliest cause of a failed boot and naming it first turns "the app did not start"
# into a specific sentence.
#
# Assertion 4 is skipped, loudly but without failing, when the restored database contains no
# finished game at all (which is the state of production before 8.7). It is *not* skipped when
# games exist and none of them replay, which is the failure this drill is for.
#
# Usage:
#   NEON_API_KEY=... ./scripts/restore-drill.sh
#   NEON_API_KEY=... RESTORE_POINT=2026-08-16T09:00:00Z ./scripts/restore-drill.sh
#
# The drill branch is deleted on the way out, including on failure. Set KEEP_BRANCH=true to keep it
# for inspection, in which case the branch name is printed at the end and deleting it is yours.

set -euo pipefail

PROJECT_ID="${NEON_PROJECT_ID:-plain-salad-47730828}"
RESTORE_POINT="${RESTORE_POINT:-}"
KEEP_BRANCH="${KEEP_BRANCH:-false}"
# `neonctl connection-string` refuses to guess when a branch carries more than one role, and this
# project carries four: neondb_owner plus the authenticator/anonymous/authenticated trio. Only
# neondb_owner owns the application's tables, so it is the one that can read __EFMigrationsHistory.
DRILL_ROLE="${DRILL_ROLE:-neondb_owner}"
# Same refusal-to-guess, one parameter along: the project carries both `kirichess` and Neon's
# default `neondb`. The application's tables live in `kirichess` (appsettings.Development.json uses
# the same name locally), and `neondb` is an empty leftover from project creation. Pointing the
# drill at `neondb` would find no __EFMigrationsHistory and report a restore failure that isn't one.
DRILL_DATABASE="${DRILL_DATABASE:-kirichess}"
DRILL_PORT="${DRILL_PORT:-8080}"
BOOT_TIMEOUT_SECONDS="${BOOT_TIMEOUT_SECONDS:-120}"

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
MIGRATIONS_DIR="$REPO_ROOT/src/KiriChess.Server/Data/Migrations"
PUBLISH_DIR="${RUNNER_TEMP:-/tmp}/restore-drill-app"
SERVER_LOG="${RUNNER_TEMP:-/tmp}/restore-drill-server.log"

BRANCH_NAME=""
SERVER_PID=""

log()  { printf '\n=== %s\n' "$*"; }
fail() { printf '\n!!! FAILED: %s\n' "$*" >&2; exit 1; }

cleanup() {
  local exit_code=$?

  if [[ -n "$SERVER_PID" ]] && kill -0 "$SERVER_PID" 2>/dev/null; then
    kill "$SERVER_PID" 2>/dev/null || true
    wait "$SERVER_PID" 2>/dev/null || true
  fi

  # Print the server log on failure only. On success it is a few hundred lines of JSON nobody reads;
  # on failure it is the whole diagnosis, and the runner discards the file with the workspace.
  if [[ $exit_code -ne 0 && -f "$SERVER_LOG" ]]; then
    printf '\n--- server log (tail) ---\n' >&2
    tail -n 80 "$SERVER_LOG" >&2 || true
  fi

  if [[ -n "$BRANCH_NAME" ]]; then
    if [[ "$KEEP_BRANCH" == "true" ]]; then
      printf '\nKEEP_BRANCH=true, leaving branch %s in place. Delete it when done:\n' "$BRANCH_NAME"
      printf '  npx neonctl branches delete %s --project-id %s\n' "$BRANCH_NAME" "$PROJECT_ID"
    else
      log "Deleting drill branch $BRANCH_NAME"
      # Never let cleanup failure mask the drill's own result, but do say so: a leaked branch is a
      # compute endpoint that keeps costing money.
      npx --yes neonctl branches delete "$BRANCH_NAME" --project-id "$PROJECT_ID" \
        || printf '\n!!! Could not delete branch %s. Delete it by hand.\n' "$BRANCH_NAME" >&2
    fi
  fi

  exit $exit_code
}
trap cleanup EXIT

[[ -n "${NEON_API_KEY:-}" ]] || fail "NEON_API_KEY is not set. It is a Neon *management* API key (branch create/delete), not a database password."
command -v psql >/dev/null 2>&1 || fail "psql is required (assertions 3 and 4 read the restored database directly). Install postgresql-client, or run this through the restore-drill workflow instead."
command -v jq   >/dev/null 2>&1 || fail "jq is required."

# Default to one hour ago: recent enough that the schema matches this checkout, old enough that this
# is a genuine point-in-time restore rather than a copy of the current head.
if [[ -z "$RESTORE_POINT" ]]; then
  if date -u -d '1 hour ago' +%Y-%m-%dT%H:%M:%SZ >/dev/null 2>&1; then
    RESTORE_POINT="$(date -u -d '1 hour ago' +%Y-%m-%dT%H:%M:%SZ)"   # GNU date
  else
    RESTORE_POINT="$(date -u -v-1H +%Y-%m-%dT%H:%M:%SZ)"             # BSD/macOS date
  fi
fi

BRANCH_NAME="drill-$(date -u +%Y%m%dT%H%M%SZ)"

# --- 1. BRANCH ---------------------------------------------------------------------------------
log "Creating branch $BRANCH_NAME from $RESTORE_POINT (project $PROJECT_ID)"
# `--parent` takes a branch, an LSN, or a timestamp; given a timestamp it branches the project's
# default branch as it stood at that moment. That is the restore under test, so it is deliberately
# not `--parent production` (which would copy the current head and assert nothing about PITR).
npx --yes neonctl branches create \
  --project-id "$PROJECT_ID" \
  --name "$BRANCH_NAME" \
  --parent "$RESTORE_POINT" \
  || fail "Could not branch from $RESTORE_POINT. Either PITR does not reach that far back on this plan, or the timestamp predates the project."

CONNECTION_STRING="$(npx --yes neonctl connection-string "$BRANCH_NAME" \
  --project-id "$PROJECT_ID" \
  --role-name "$DRILL_ROLE" \
  --database-name "$DRILL_DATABASE" \
  --pooled)"
[[ -n "$CONNECTION_STRING" ]] || fail "Empty connection string for $BRANCH_NAME (role $DRILL_ROLE, database $DRILL_DATABASE)."

# psql takes the URI as-is. Npgsql does not: it parses key/value pairs only, so a `postgresql://`
# URI with `;SSL Mode=...` glued on the end is rejected at parse time rather than at connect time.
# That is what made this assertion fail on its first real run: `CanConnectAsync` swallowed the parse
# error, returned false, and `/health/ready` answered 503 in about a millisecond, which reads like a
# dead database and was actually a malformed string. Same conversion as docs/ops/DEPLOYMENT.md § 2.1.
PSQL_URI="$CONNECTION_STRING"
uri_rest="${CONNECTION_STRING#postgresql://}"
uri_userpass="${uri_rest%%@*}"
uri_hostrest="${uri_rest#*@}"
db_user="${uri_userpass%%:*}"
db_pass="${uri_userpass#*:}"
db_host="${uri_hostrest%%/*}"
db_name="${uri_hostrest#*/}"; db_name="${db_name%%\?*}"
[[ "$uri_rest" != "$CONNECTION_STRING" && -n "$db_host" && -n "$db_name" ]] \
  || fail "Could not parse the Neon connection string into Npgsql's key/value form (expected a postgresql:// URI)."
# VerifyFull checks the chain and the host name. The drill branch is a Neon endpoint with a
# publicly trusted certificate, same hop the app uses in production (I-2-8).
APP_CONNECTION_STRING="Host=${db_host};Port=5432;Database=${db_name};Username=${db_user};Password=${db_pass};SSL Mode=VerifyFull"

# --- 3. SCHEMA ----------------------------------------------------------------------------------
# Runs before assertion 2 even though it is numbered after it: a stale schema is the likeliest cause
# of a boot failure, so checking it first turns "the app did not start" into a specific sentence.
log "Checking migration history against this checkout"
# psql's own stderr is deliberately not suppressed: a connection failure here and an empty table are
# different problems and must not both surface as the message below.
APPLIED_MIGRATION="$(psql "$PSQL_URI" -tAc 'SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY "MigrationId" DESC LIMIT 1' || true)"
[[ -n "$APPLIED_MIGRATION" ]] || fail "Could not read __EFMigrationsHistory from the restored branch: it is unreachable (see psql's error above), the restore point predates the first migration, or this is not the application's database."

# Migration ids sort chronologically because EF prefixes them with a UTC timestamp, and the
# .Designer.cs file is named for the id exactly. Not `find -printf`: that is GNU-only, and this
# script is also meant to be runnable by hand on a laptop (9c.4).
EXPECTED_MIGRATION="$(ls "$MIGRATIONS_DIR"/*.Designer.cs | sed 's#.*[/\\]##; s#\.Designer\.cs$##' | sort | tail -n 1)"
[[ -n "$EXPECTED_MIGRATION" ]] || fail "Found no migrations in $MIGRATIONS_DIR."

if [[ "$APPLIED_MIGRATION" != "$EXPECTED_MIGRATION" ]]; then
  fail "Restored schema is $APPLIED_MIGRATION, this checkout's newest migration is $EXPECTED_MIGRATION.
       This is the failure 9e.8 exists to catch: production is behind (a migration never ran), or the
       restore point predates a migration that has since shipped. Confirm which before trusting any
       recovery plan that assumes this backup is restorable by the current build."
fi
printf 'Schema is current: %s\n' "$APPLIED_MIGRATION"

# --- 2. BOOT -----------------------------------------------------------------------------------
log "Publishing the server"
dotnet publish "$REPO_ROOT/src/KiriChess.Server/KiriChess.Server.csproj" -c Release -o "$PUBLISH_DIR" --nologo -v quiet

log "Booting against the restored branch"
# RunMigrationsOnStartup=false on purpose: the drill asserts the *restored* schema is usable, and a
# boot-time migration would quietly repair the exact discrepancy assertion 3 exists to detect.
# GameRecoveryService still runs and may write to this branch (recovering or aborting games that
# were live at the restore point). That is intended, it exercises the recovery path against real
# restored state, and the branch is thrown away minutes later.
env \
  ASPNETCORE_ENVIRONMENT=Production \
  ASPNETCORE_HTTP_PORTS="$DRILL_PORT" \
  ConnectionStrings__Postgres="$APP_CONNECTION_STRING" \
  RunMigrationsOnStartup=false \
  Jwt__Key="restore-drill-signing-key-not-a-secret-32-bytes-plus" \
  Cors__AllowedOrigins__0="http://localhost:3000" \
  dotnet "$PUBLISH_DIR/KiriChess.Server.dll" > "$SERVER_LOG" 2>&1 &
SERVER_PID=$!

deadline=$(( SECONDS + BOOT_TIMEOUT_SECONDS ))
until curl -fsS "http://localhost:$DRILL_PORT/health/ready" >/dev/null 2>&1; do
  kill -0 "$SERVER_PID" 2>/dev/null || fail "Server exited during boot."
  (( SECONDS < deadline )) || fail "/health/ready did not return 200 within ${BOOT_TIMEOUT_SECONDS}s."
  sleep 2
done
printf 'Ready: %s\n' "$(curl -fsS "http://localhost:$DRILL_PORT/health/ready")"

# --- 4. REPLAY ---------------------------------------------------------------------------------
log "Replaying the most recently finished game"
# GameResult is stored as a string (KiriChessDbContext: HasConversion<string>), so this compares
# against 'Ongoing' rather than an ordinal.
GAME_ID="$(psql "$PSQL_URI" -tAc "SELECT \"Id\" FROM \"Games\" WHERE \"Result\" <> 'Ongoing' ORDER BY \"EndedAt\" DESC NULLS LAST LIMIT 1" | tr -d '[:space:]')"

if [[ -z "$GAME_ID" ]]; then
  TOTAL_GAMES="$(psql "$PSQL_URI" -tAc 'SELECT count(*) FROM "Games"' | tr -d '[:space:]')"
  [[ "$TOTAL_GAMES" == "0" ]] || fail "$TOTAL_GAMES games exist but none are finished, so nothing could be replayed. Investigate before trusting this drill's result."
  printf '\n*** WARNING: the restored database contains no games, so assertion 4 (replay) proved nothing.\n'
  printf '*** Branch, boot and schema all passed. This warning disappears on its own once real games exist.\n'
else
  REPLAY="$(curl -fsS "http://localhost:$DRILL_PORT/api/games/$GAME_ID/replay")" \
    || fail "Replay of game $GAME_ID returned an error. The rows restored but this build cannot read them."

  PLY_COUNT="$(printf '%s' "$REPLAY" | jq '.plies | length')"
  RESULT="$(printf '%s' "$REPLAY" | jq -r '.result')"

  [[ "$PLY_COUNT" -gt 0 ]] || fail "Game $GAME_ID replayed to zero plies; the move list did not survive the restore."
  [[ "$RESULT" != "Ongoing" ]] || fail "Game $GAME_ID replayed as Ongoing despite being selected as finished."
  printf 'Replayed game %s: %s plies, result %s\n' "$GAME_ID" "$PLY_COUNT" "$RESULT"
fi

log "Restore drill passed (restore point $RESTORE_POINT, schema $APPLIED_MIGRATION)"
