#!/usr/bin/env bash
#
# Off-site backup (ROADMAP 10c.1).
#
# Neon PITR (8a.3 / 9c.4) proves restore *into Neon*. This script is the copy that still exists if
# Neon is the thing that failed: dump production via a throwaway branch, put the dump in Cloudflare
# R2, then restore *that object* into a local Postgres and run restore-drill.sh's schema / boot /
# replay assertions against it. The restore target is deliberately not Neon.
#
# Two modes, one file, so the weekly dump and the disaster restore cannot drift into testing
# different objects:
#
#   dump     (default)  branch Neon, pg_dump, upload to R2, download the object back, restore it
#                       locally, assert. Touches production only by reading it.
#   restore             download the latest (or DUMP_KEY) object from R2 and restore it. No Neon.
#                       Target is local Docker Postgres, or RESTORE_DATABASE_URL if set.
#
# Usage:
#   NEON_API_KEY=... R2_ACCOUNT_ID=... R2_ACCESS_KEY_ID=... R2_SECRET_ACCESS_KEY=... \
#     ./scripts/offsite-backup.sh
#   R2_ACCOUNT_ID=... R2_ACCESS_KEY_ID=... R2_SECRET_ACCESS_KEY=... \
#     ./scripts/offsite-backup.sh restore
#   RESTORE_DATABASE_URL='postgresql://...' ./scripts/offsite-backup.sh restore
#
# The dump branch is deleted on the way out, including on failure. KEEP_BRANCH=true keeps it.

set -euo pipefail

MODE="${1:-dump}"
PROJECT_ID="${NEON_PROJECT_ID:-plain-salad-47730828}"
KEEP_BRANCH="${KEEP_BRANCH:-false}"
DRILL_ROLE="${DRILL_ROLE:-neondb_owner}"
DRILL_DATABASE="${DRILL_DATABASE:-kirichess}"
DRILL_PORT="${DRILL_PORT:-8080}"
BOOT_TIMEOUT_SECONDS="${BOOT_TIMEOUT_SECONDS:-120}"
KEEP_DUMPS="${KEEP_DUMPS:-8}"
R2_BUCKET="${R2_BUCKET:-kirichess-backups}"
LOCAL_PORT="${LOCAL_PORT:-55432}"
LOCAL_PASSWORD="${LOCAL_PASSWORD:-offsite-restore-not-a-secret}"

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
MIGRATIONS_DIR="$REPO_ROOT/src/KiriChess.Server/Data/Migrations"
WORK_DIR="${RUNNER_TEMP:-/tmp}/offsite-backup"
PUBLISH_DIR="${RUNNER_TEMP:-/tmp}/offsite-backup-app"
SERVER_LOG="${RUNNER_TEMP:-/tmp}/offsite-backup-server.log"
CONTAINER_NAME="offsite-restore-pg"
STAMP="$(date -u +%Y%m%dT%H%M%SZ)"

BRANCH_NAME=""
SERVER_PID=""
PG_MAJOR=""
DUMP_FILE=""
STARTED_CONTAINER=""

log()  { printf '\n=== %s\n' "$*"; }
fail() { printf '\n!!! FAILED: %s\n' "$*" >&2; exit 1; }

r2_endpoint() {
  [[ -n "${R2_ACCOUNT_ID:-}" ]] || fail "R2_ACCOUNT_ID is not set. It is the 32-character account id from the Cloudflare dashboard, a repository *variable*, not a secret."
  # Jurisdiction buckets (EU / US / FedRAMP) are unreachable on the default host. kirichess-backups
  # was created with EU jurisdiction, so the weekly job must use .eu. or every put 404s.
  local jurisdiction
  jurisdiction="$(printf '%s' "${R2_JURISDICTION:-}" | tr '[:upper:]' '[:lower:]')"
  case "$jurisdiction" in
    ""|default) printf 'https://%s.r2.cloudflarestorage.com' "$R2_ACCOUNT_ID" ;;
    eu|us|fedramp) printf 'https://%s.%s.r2.cloudflarestorage.com' "$R2_ACCOUNT_ID" "$jurisdiction" ;;
    *) fail "R2_JURISDICTION must be empty, eu, us, or fedramp. Got '$jurisdiction'." ;;
  esac
}

aws_r2() {
  # Newer AWS CLI versions default to checksum algorithms R2 rejects. Force the older behaviour.
  AWS_ACCESS_KEY_ID="${R2_ACCESS_KEY_ID:-}" \
  AWS_SECRET_ACCESS_KEY="${R2_SECRET_ACCESS_KEY:-}" \
  AWS_DEFAULT_REGION=auto \
  AWS_REQUEST_CHECKSUM_CALCULATION=WHEN_REQUIRED \
  AWS_RESPONSE_CHECKSUM_VALIDATION=WHEN_REQUIRED \
    aws --endpoint-url "$(r2_endpoint)" "$@"
}

cleanup() {
  local exit_code=$?

  if [[ -n "$SERVER_PID" ]] && kill -0 "$SERVER_PID" 2>/dev/null; then
    kill "$SERVER_PID" 2>/dev/null || true
    wait "$SERVER_PID" 2>/dev/null || true
  fi

  if [[ $exit_code -ne 0 && -f "$SERVER_LOG" ]]; then
    printf '\n--- server log (tail) ---\n' >&2
    tail -n 80 "$SERVER_LOG" >&2 || true
  fi

  if [[ -n "$STARTED_CONTAINER" ]]; then
    docker rm -f "$CONTAINER_NAME" >/dev/null 2>&1 || true
  fi

  if [[ -n "$BRANCH_NAME" ]]; then
    if [[ "$KEEP_BRANCH" == "true" ]]; then
      printf '\nKEEP_BRANCH=true, leaving branch %s in place. Delete it when done:\n' "$BRANCH_NAME"
      printf '  npx neonctl branches delete %s --project-id %s\n' "$BRANCH_NAME" "$PROJECT_ID"
    else
      log "Deleting dump branch $BRANCH_NAME"
      npx --yes neonctl branches delete "$BRANCH_NAME" --project-id "$PROJECT_ID" \
        || printf '\n!!! Could not delete branch %s. Delete it by hand.\n' "$BRANCH_NAME" >&2
    fi
  fi

  exit $exit_code
}
trap cleanup EXIT

require_r2() {
  [[ -n "${R2_ACCESS_KEY_ID:-}" ]] || fail "R2_ACCESS_KEY_ID is not set. Create the R2 bucket and add the two repository secrets in docs/ops/DEPLOYMENT.md § 5.3."
  [[ -n "${R2_SECRET_ACCESS_KEY:-}" ]] || fail "R2_SECRET_ACCESS_KEY is not set. Create the R2 bucket and add the two repository secrets in docs/ops/DEPLOYMENT.md § 5.3."
  command -v aws >/dev/null 2>&1 || fail "aws CLI is required (s3 cp against R2). Install awscli, or run this through the offsite-backup workflow instead."
  command -v docker >/dev/null 2>&1 || fail "docker is required (pg_dump / pg_restore / the local restore target). Install Docker, or run this through the offsite-backup workflow instead."
  command -v jq >/dev/null 2>&1 || fail "jq is required."
  command -v dotnet >/dev/null 2>&1 || fail "dotnet is required (the restore-verify boots this build against the restored dump)."
  command -v curl >/dev/null 2>&1 || fail "curl is required."
}

pg_image() {
  printf 'postgres:%s-alpine' "$PG_MAJOR"
}

detect_pg_major_from_uri() {
  local uri="$1"
  # psql 16 can talk to 16 or 17 enough to run SHOW; pg_dump 16 cannot dump 17. Detect, then pick
  # the matching image for dump and restore so we never mix majors.
  local version
  version="$(docker run --rm --entrypoint psql postgres:16-alpine "$uri" -tAc 'SHOW server_version' | tr -d '[:space:]')"
  [[ -n "$version" ]] || fail "Could not read server_version from $uri."
  PG_MAJOR="${version%%.*}"
  printf 'Postgres major version: %s (from %s)\n' "$PG_MAJOR" "$version"
}

detect_pg_major_from_dump_file() {
  local dump="$1"
  # I-4-4: the local-Docker restore target has no server to ask SHOW server_version of, so read the
  # major off the dump file itself instead. A custom-format archive's first bytes are a block of
  # ";"-prefixed header comments that pg_restore --list prints ahead of the TOC, including
  # "Dumped from database version: X.Y". That header is plain text and every recent pg_restore can
  # read it even from an archive whose TOC it cannot restore, so probe with a fixed image rather
  # than the still-unknown target major. An explicit PG_MAJOR always wins over detection.
  if [[ -n "${PG_MAJOR:-}" ]]; then
    printf 'PG_MAJOR=%s set explicitly; skipping archive header detection.\n' "$PG_MAJOR"
    return
  fi
  local header version
  header="$(docker run --rm --entrypoint pg_restore -v "${dump}:/tmp/dump.dump:ro" postgres:16-alpine --list /tmp/dump.dump 2>/dev/null || true)"
  version="$(printf '%s\n' "$header" \
    | grep -m1 'Dumped from database version:' \
    | sed -n 's/.*Dumped from database version:[[:space:]]*\([0-9][0-9]*\).*/\1/p' || true)"
  if [[ "$version" =~ ^[0-9]+$ ]]; then
    PG_MAJOR="$version"
    printf 'Postgres major version: %s (from archive header)\n' "$PG_MAJOR"
  else
    # Empty header, truncated file, or the archive is not custom-format (pg_restore --list exits
    # non-zero and prints an error to stderr, which is discarded above). The "|| true" on the
    # parse pipeline above is load-bearing rather than decoration: this script runs under
    # "set -euo pipefail", so a grep that matches nothing returns 1, pipefail propagates that
    # past sed, and set -e would kill the run here instead of reaching this branch. That would
    # abort a disaster restore with no message, on exactly the malformed archive this fallback
    # exists to survive. Fall back rather than fail,
    # matching docker-compose.prod.yml. pg_restore will fail loudly on a real mismatch and the
    # operator can set PG_MAJOR explicitly; see DEPLOYMENT.md § 5.3.
    PG_MAJOR="16"
    printf 'Could not read a server version from the archive header (empty, truncated, or not a custom-format dump); falling back to PG_MAJOR=%s. Set PG_MAJOR= to override.\n' "$PG_MAJOR" >&2
  fi
}

parse_postgres_uri_to_npgsql() {
  # Same conversion restore-drill.sh needed: npgsql wants key=value, psql/pg_dump want the URI.
  local uri="$1"
  local ssl_mode="${2:-Disable}"
  local uri_rest uri_userpass uri_hostrest db_user db_pass hostport db_host db_port db_name
  uri_rest="${uri#postgresql://}"
  uri_rest="${uri_rest#postgres://}"
  uri_userpass="${uri_rest%%@*}"
  uri_hostrest="${uri_rest#*@}"
  db_user="${uri_userpass%%:*}"
  db_pass="${uri_userpass#*:}"
  hostport="${uri_hostrest%%/*}"
  db_host="${hostport%%:*}"
  if [[ "$hostport" == *:* ]]; then
    db_port="${hostport##*:}"
    db_port="${db_port%%\?*}"
  else
    db_port="5432"
  fi
  db_name="${uri_hostrest#*/}"; db_name="${db_name%%\?*}"; db_name="${db_name%%;*}"
  [[ "$uri_rest" != "$uri" && -n "$db_host" && -n "$db_name" ]] \
    || fail "Could not parse a postgresql:// URI into Npgsql's key/value form."
  # Callers that talk to Neon pass VerifyFull. The local Docker target does not use this
  # function; it has no certificate and connects with SSL Mode=Disable (I-2-8).
  printf 'Host=%s;Port=%s;Database=%s;Username=%s;Password=%s;SSL Mode=%s' \
    "$db_host" "$db_port" "$db_name" "$db_user" "$db_pass" "$ssl_mode"
}

assert_schema_from_uri() {
  local uri="$1"
  log "Checking migration history against this checkout"
  local applied expected
  applied="$(docker run --rm --entrypoint psql "$(pg_image)" "$uri" -tAc 'SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY "MigrationId" DESC LIMIT 1' || true)"
  [[ -n "$applied" ]] || fail "Could not read __EFMigrationsHistory from the restored database."
  expected="$(ls "$MIGRATIONS_DIR"/*.Designer.cs | sed 's#.*[/\\]##; s#\.Designer\.cs$##' | sort | tail -n 1)"
  [[ -n "$expected" ]] || fail "Found no migrations in $MIGRATIONS_DIR."
  if [[ "$applied" != "$expected" ]]; then
    fail "Restored schema is $applied, this checkout's newest migration is $expected.
       Either the dump predates a migration that has since shipped, or production is behind."
  fi
  printf 'Schema is current: %s\n' "$applied"
  APPLIED_MIGRATION="$applied"
}

boot_and_replay() {
  local app_connection_string="$1"
  local psql_uri="$2"

  log "Publishing the server"
  dotnet publish "$REPO_ROOT/src/KiriChess.Server/KiriChess.Server.csproj" -c Release -o "$PUBLISH_DIR" --nologo -v quiet

  log "Booting against the restored database"
  env \
    ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS="$DRILL_PORT" \
    ConnectionStrings__Postgres="$app_connection_string" \
    RunMigrationsOnStartup=false \
    Jwt__Key="offsite-backup-signing-key-not-a-secret-32-bytes-plus" \
    Cors__AllowedOrigins__0="http://localhost:3000" \
    dotnet "$PUBLISH_DIR/KiriChess.Server.dll" > "$SERVER_LOG" 2>&1 &
  SERVER_PID=$!

  local deadline=$(( SECONDS + BOOT_TIMEOUT_SECONDS ))
  until curl -fsS "http://localhost:$DRILL_PORT/health/ready" >/dev/null 2>&1; do
    kill -0 "$SERVER_PID" 2>/dev/null || fail "Server exited during boot."
    (( SECONDS < deadline )) || fail "/health/ready did not return 200 within ${BOOT_TIMEOUT_SECONDS}s."
    sleep 2
  done
  printf 'Ready: %s\n' "$(curl -fsS "http://localhost:$DRILL_PORT/health/ready")"

  log "Replaying the most recently finished game"
  local game_id total replay ply_count result
  game_id="$(docker run --rm --entrypoint psql "$(pg_image)" "$psql_uri" -tAc "SELECT \"Id\" FROM \"Games\" WHERE \"Result\" <> 'Ongoing' ORDER BY \"EndedAt\" DESC NULLS LAST LIMIT 1" | tr -d '[:space:]')"

  if [[ -z "$game_id" ]]; then
    total="$(docker run --rm --entrypoint psql "$(pg_image)" "$psql_uri" -tAc 'SELECT count(*) FROM "Games"' | tr -d '[:space:]')"
    [[ "$total" == "0" ]] || fail "$total games exist but none are finished, so nothing could be replayed. Investigate before trusting this dump."
    printf '\n*** WARNING: the restored dump contains no games, so replay proved nothing.\n'
    printf '*** Schema and boot passed. This warning disappears on its own once real games exist.\n'
  else
    replay="$(curl -fsS "http://localhost:$DRILL_PORT/api/games/$game_id/replay")" \
      || fail "Replay of game $game_id returned an error. The dump restored but this build cannot read it."
    ply_count="$(printf '%s' "$replay" | jq '.plies | length')"
    result="$(printf '%s' "$replay" | jq -r '.result')"
    [[ "$ply_count" -gt 0 ]] || fail "Game $game_id replayed to zero plies; the move list did not survive the dump/restore."
    [[ "$result" != "Ongoing" ]] || fail "Game $game_id replayed as Ongoing despite being selected as finished."
    printf 'Replayed game %s: %s plies, result %s\n' "$game_id" "$ply_count" "$result"
  fi
}

start_local_postgres() {
  log "Starting local Postgres $(pg_image) on port $LOCAL_PORT"
  docker rm -f "$CONTAINER_NAME" >/dev/null 2>&1 || true
  docker run -d --name "$CONTAINER_NAME" \
    -e POSTGRES_PASSWORD="$LOCAL_PASSWORD" \
    -e POSTGRES_DB="$DRILL_DATABASE" \
    -p "${LOCAL_PORT}:5432" \
    "$(pg_image)" >/dev/null
  STARTED_CONTAINER=1

  local deadline=$(( SECONDS + 60 ))
  until docker exec "$CONTAINER_NAME" pg_isready -U postgres >/dev/null 2>&1; do
    (( SECONDS < deadline )) || fail "Local Postgres did not become ready within 60s."
    sleep 1
  done
}

restore_dump_into_local() {
  local dump="$1"
  docker cp "$dump" "$CONTAINER_NAME:/tmp/dump.dump"
  docker exec "$CONTAINER_NAME" pg_restore -U postgres -d "$DRILL_DATABASE" \
    --no-owner --no-acl /tmp/dump.dump
}

local_exec_psql() {
  docker exec "$CONTAINER_NAME" psql -U postgres -d "$DRILL_DATABASE" "$@"
}

assert_schema_local() {
  log "Checking migration history against this checkout"
  local applied expected
  applied="$(local_exec_psql -tAc 'SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY "MigrationId" DESC LIMIT 1' || true)"
  [[ -n "$applied" ]] || fail "Could not read __EFMigrationsHistory from the locally restored database."
  expected="$(ls "$MIGRATIONS_DIR"/*.Designer.cs | sed 's#.*[/\\]##; s#\.Designer\.cs$##' | sort | tail -n 1)"
  [[ -n "$expected" ]] || fail "Found no migrations in $MIGRATIONS_DIR."
  if [[ "$applied" != "$expected" ]]; then
    fail "Restored schema is $applied, this checkout's newest migration is $expected.
       Either the dump predates a migration that has since shipped, or production is behind."
  fi
  printf 'Schema is current: %s\n' "$applied"
  APPLIED_MIGRATION="$applied"
}

boot_and_replay_local() {
  local app_connection_string
  app_connection_string="Host=127.0.0.1;Port=${LOCAL_PORT};Database=${DRILL_DATABASE};Username=postgres;Password=${LOCAL_PASSWORD};SSL Mode=Disable"

  log "Publishing the server"
  dotnet publish "$REPO_ROOT/src/KiriChess.Server/KiriChess.Server.csproj" -c Release -o "$PUBLISH_DIR" --nologo -v quiet

  log "Booting against the locally restored dump"
  env \
    ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS="$DRILL_PORT" \
    ConnectionStrings__Postgres="$app_connection_string" \
    RunMigrationsOnStartup=false \
    Jwt__Key="offsite-backup-signing-key-not-a-secret-32-bytes-plus" \
    Cors__AllowedOrigins__0="http://localhost:3000" \
    dotnet "$PUBLISH_DIR/KiriChess.Server.dll" > "$SERVER_LOG" 2>&1 &
  SERVER_PID=$!

  local deadline=$(( SECONDS + BOOT_TIMEOUT_SECONDS ))
  until curl -fsS "http://localhost:$DRILL_PORT/health/ready" >/dev/null 2>&1; do
    kill -0 "$SERVER_PID" 2>/dev/null || fail "Server exited during boot."
    (( SECONDS < deadline )) || fail "/health/ready did not return 200 within ${BOOT_TIMEOUT_SECONDS}s."
    sleep 2
  done
  printf 'Ready: %s\n' "$(curl -fsS "http://localhost:$DRILL_PORT/health/ready")"

  log "Replaying the most recently finished game"
  local game_id total replay ply_count result
  game_id="$(local_exec_psql -tAc "SELECT \"Id\" FROM \"Games\" WHERE \"Result\" <> 'Ongoing' ORDER BY \"EndedAt\" DESC NULLS LAST LIMIT 1" | tr -d '[:space:]')"

  if [[ -z "$game_id" ]]; then
    total="$(local_exec_psql -tAc 'SELECT count(*) FROM "Games"' | tr -d '[:space:]')"
    [[ "$total" == "0" ]] || fail "$total games exist but none are finished, so nothing could be replayed. Investigate before trusting this dump."
    printf '\n*** WARNING: the restored dump contains no games, so replay proved nothing.\n'
    printf '*** Schema and boot passed. This warning disappears on its own once real games exist.\n'
  else
    replay="$(curl -fsS "http://localhost:$DRILL_PORT/api/games/$game_id/replay")" \
      || fail "Replay of game $game_id returned an error. The dump restored but this build cannot read it."
    ply_count="$(printf '%s' "$replay" | jq '.plies | length')"
    result="$(printf '%s' "$replay" | jq -r '.result')"
    [[ "$ply_count" -gt 0 ]] || fail "Game $game_id replayed to zero plies; the move list did not survive the dump/restore."
    [[ "$result" != "Ongoing" ]] || fail "Game $game_id replayed as Ongoing despite being selected as finished."
    printf 'Replayed game %s: %s plies, result %s\n' "$game_id" "$ply_count" "$result"
  fi
}

verify_dump_locally() {
  local dump="$1"
  [[ -n "$PG_MAJOR" ]] || fail "PG_MAJOR is unset; detect the source version before restoring."
  start_local_postgres
  log "Restoring $(basename "$dump") into local Postgres"
  restore_dump_into_local "$dump"
  assert_schema_local
  boot_and_replay_local
}

download_latest_dump() {
  local dest="$1"
  local key="${DUMP_KEY:-}"
  if [[ -z "$key" ]]; then
    # Newest by name is newest by time: STAMP is a sortable timestamp. This used to read a
    # `kirichess/latest` pointer object that every run overwrote. I-4-1's R2 bucket lock refuses an
    # overwrite inside the retention window, so rewriting one fixed key on a weekly cron is a write
    # that fails whenever the run lands early, and `set -e` then aborts before prune and before the
    # restore-verify, leaving a pointer that still names the *previous* dump. A disaster restore
    # would follow it and quietly recover a week-old database. Listing has no fixed key to clobber.
    local newest
    newest="$(aws_r2 s3 ls "s3://${R2_BUCKET}/kirichess/" | awk '/\.dump$/ { print $4 }' | sort -r | awk 'NR == 1')"
    [[ -n "$newest" ]] || fail "No .dump objects under s3://${R2_BUCKET}/kirichess/. No dump has been uploaded yet."
    key="kirichess/${newest}"
  fi
  log "Downloading s3://${R2_BUCKET}/${key}"
  aws_r2 s3 cp "s3://${R2_BUCKET}/${key}" "$dest"
  [[ -s "$dest" ]] || fail "Downloaded dump is empty."
}

prune_old_dumps() {
  local keys
  # Newest first. Keep KEEP_DUMPS timestamped dumps. Every key this deletes is KEEP_DUMPS weeks old
  # (about 8 weeks on the weekly cron), well past the bucket lock's retention window, so the delete
  # the lock would refuse is never one this reaches.
  keys="$(aws_r2 s3 ls "s3://${R2_BUCKET}/kirichess/" | awk '/\.dump$/ { print $4 }' | sort -r)"
  local kept=0
  local key
  while IFS= read -r key; do
    [[ -n "$key" ]] || continue
    kept=$((kept + 1))
    if (( kept > KEEP_DUMPS )); then
      log "Pruning s3://${R2_BUCKET}/kirichess/${key}"
      aws_r2 s3 rm "s3://${R2_BUCKET}/kirichess/${key}"
    fi
  done <<< "$keys"
}

# --- restore mode (no Neon) -------------------------------------------------------------------

restore_mode() {
  require_r2
  mkdir -p "$WORK_DIR"
  DUMP_FILE="$WORK_DIR/from-r2.dump"
  download_latest_dump "$DUMP_FILE"

  if [[ -n "${RESTORE_DATABASE_URL:-}" ]]; then
    log "Restoring into RESTORE_DATABASE_URL (disaster path)"
    detect_pg_major_from_uri "$RESTORE_DATABASE_URL"
    docker run --rm --entrypoint pg_restore -v "$DUMP_FILE:/tmp/dump.dump:ro" "$(pg_image)" \
      --no-owner --no-acl -d "$RESTORE_DATABASE_URL" /tmp/dump.dump
    assert_schema_from_uri "$RESTORE_DATABASE_URL"
    boot_and_replay "$(parse_postgres_uri_to_npgsql "$RESTORE_DATABASE_URL" VerifyFull)" "$RESTORE_DATABASE_URL"
  else
    # Local Docker target. Major version is stored nowhere in the dump file name, so read it off
    # the archive's own header (I-4-4) rather than assuming 16: pg_restore 16 cannot read a
    # pg_dump 17 archive, and that failure is a header-version error against the only remaining
    # copy, mid-incident. See detect_pg_major_from_dump_file for the parse and its fallback.
    detect_pg_major_from_dump_file "$DUMP_FILE"
    verify_dump_locally "$DUMP_FILE"
  fi

  log "Off-site restore passed (schema ${APPLIED_MIGRATION:-unknown})"
}

# --- dump mode --------------------------------------------------------------------------------

dump_mode() {
  require_r2
  [[ -n "${NEON_API_KEY:-}" ]] || fail "NEON_API_KEY is not set. It is a Neon *management* API key (branch create/delete), not a database password."
  command -v npx >/dev/null 2>&1 || fail "npx is required (neonctl)."

  mkdir -p "$WORK_DIR"
  BRANCH_NAME="offsite-${STAMP}"

  # Copy of current HEAD, not a timestamp: this is a backup of now, not a PITR drill. 9c.4 already
  # covers branching from the past.
  log "Creating branch $BRANCH_NAME from current HEAD (project $PROJECT_ID)"
  npx --yes neonctl branches create \
    --project-id "$PROJECT_ID" \
    --name "$BRANCH_NAME" \
    || fail "Could not create dump branch $BRANCH_NAME."

  local connection_string
  connection_string="$(npx --yes neonctl connection-string "$BRANCH_NAME" \
    --project-id "$PROJECT_ID" \
    --role-name "$DRILL_ROLE" \
    --database-name "$DRILL_DATABASE")"
  [[ -n "$connection_string" ]] || fail "Empty connection string for $BRANCH_NAME (role $DRILL_ROLE, database $DRILL_DATABASE)."
  # Unpooled on purpose. The app uses `--pooled`; pg_dump through pgbouncer is how dumps come back empty.

  detect_pg_major_from_uri "$connection_string"

  DUMP_FILE="$WORK_DIR/kirichess-${STAMP}.dump"
  log "Dumping $DRILL_DATABASE from $BRANCH_NAME"
  docker run --rm --entrypoint pg_dump -v "$WORK_DIR:/out" "$(pg_image)" \
    --format=custom --compress=9 --no-owner --no-acl \
    --file="/out/$(basename "$DUMP_FILE")" "$connection_string"
  [[ -s "$DUMP_FILE" ]] || fail "pg_dump wrote an empty file."
  local dump_bytes
  dump_bytes="$(wc -c < "$DUMP_FILE" | tr -d '[:space:]')"
  (( dump_bytes > 1024 )) || fail "pg_dump wrote ${dump_bytes} bytes; a real schema dump is larger. Refusing to upload."
  printf 'Dump is %s bytes\n' "$dump_bytes"

  local key="kirichess/${STAMP}.dump"
  log "Uploading s3://${R2_BUCKET}/${key}"
  aws_r2 s3 cp "$DUMP_FILE" "s3://${R2_BUCKET}/${key}"

  # Round-trip: restore the object that landed in R2, not the local file we just uploaded. A
  # truncated put would pass every assertion against the local copy and still be unrestorable.
  local downloaded="$WORK_DIR/from-r2.dump"
  log "Re-downloading s3://${R2_BUCKET}/${key} for restore-verify"
  aws_r2 s3 cp "s3://${R2_BUCKET}/${key}" "$downloaded"
  local downloaded_bytes
  downloaded_bytes="$(wc -c < "$downloaded" | tr -d '[:space:]')"
  [[ "$downloaded_bytes" == "$dump_bytes" ]] || fail "R2 object is ${downloaded_bytes} bytes, local dump was ${dump_bytes}. The upload did not round-trip."

  # Verify before prune (I-4-3). The byte-count check above only proves the object matches what we
  # sent, not that it restores. prune_old_dumps deletes the oldest of the newest KEEP_DUMPS objects,
  # and this run's object counts toward that count the moment it lands. Pruning before verification
  # meant a dump that then failed schema/boot/replay could evict the previous, known-good copy to
  # make room for itself. Running verify_dump_locally first means a failure here calls fail() and
  # exits before prune_old_dumps ever runs, so the older good copies are untouched. The new object
  # itself is not deleted on failure (R2's per-key retention lock enforces at least a week either
  # way, see DEPLOYMENT.md §5.3), so it is still the newest-named object in the bucket; a manual
  # disaster restore run during that window should pass DUMP_KEY= naming the last object recorded
  # as passing, rather than trust "newest" blindly. See DEPLOYMENT.md §5.3.
  verify_dump_locally "$downloaded"
  prune_old_dumps

  log "Off-site backup passed (object $key, schema $APPLIED_MIGRATION, ${dump_bytes} bytes)"
}

case "$MODE" in
  dump)    dump_mode ;;
  restore) restore_mode ;;
  *)       fail "Unknown mode '$MODE'. Use dump (default) or restore." ;;
esac
