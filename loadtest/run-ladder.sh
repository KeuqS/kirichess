#!/usr/bin/env bash
# POSIX equivalent of run-ladder.ps1. See that file's header comment for the reasoning (fresh stack
# per rung, why this exists as a script rather than inline commands).
#
# Usage:
#   LOAD_CPUS=1.0 LOAD_MEMORY=512m GAMES="10 25 50 100 200 400" LABEL=A-cpu1-mem512 ./run-ladder.sh
#
# Env vars (all optional except LOAD_CPUS/LOAD_MEMORY/GAMES/LABEL):
#   POOL (steal3) API (http://localhost:8080) SCRAPE_TOKEN (load-only-not-a-secret-scrape-token)
#   THINK_MS (3000) JITTER_MS (1000) WARMUP_SECONDS (90) WINDOW_SECONDS (180)
#   PRESENCE (true) SEED (20260808) LOAD_LAG_COMPENSATION (true) PINNED_CPUS (1.0)
#   OUT_DIR (loadtest/results) STOP_ON_FAIL (1)

set -euo pipefail

: "${LOAD_CPUS:?set LOAD_CPUS}"
: "${LOAD_MEMORY:?set LOAD_MEMORY}"
: "${GAMES:?set GAMES, e.g. GAMES=\"10 25 50\"}"
: "${LABEL:?set LABEL}"

POOL="${POOL:-steal3}"
API="${API:-http://localhost:8080}"
SCRAPE_TOKEN="${SCRAPE_TOKEN:-load-only-not-a-secret-scrape-token}"
THINK_MS="${THINK_MS:-3000}"
JITTER_MS="${JITTER_MS:-1000}"
WARMUP_SECONDS="${WARMUP_SECONDS:-90}"
WINDOW_SECONDS="${WINDOW_SECONDS:-180}"
PRESENCE="${PRESENCE:-true}"
SEED="${SEED:-20260808}"
LOAD_LAG_COMPENSATION="${LOAD_LAG_COMPENSATION:-true}"
PINNED_CPUS="${PINNED_CPUS:-1.0}"
OUT_DIR="${OUT_DIR:-loadtest/results}"
STOP_ON_FAIL="${STOP_ON_FAIL:-1}"

export LOAD_CPUS LOAD_MEMORY LOAD_LAG_COMPENSATION

COMPOSE=(docker compose -p kirichess-load -f docker-compose.prod.yml -f docker-compose.load.yml)

echo "=== Ladder: label=$LABEL cpus=$LOAD_CPUS memory=$LOAD_MEMORY lagCompensation=$LOAD_LAG_COMPENSATION games=$GAMES ==="

for n in $GAMES; do
    echo
    echo "--- Rung N=$n ---"
    echo "Bringing up db redis backend..."
    "${COMPOSE[@]}" up -d db redis backend

    echo "Verifying deploy.resources.limits applied..."
    docker inspect kirichess-backend --format '{{.HostConfig.NanoCpus}} {{.HostConfig.Memory}}'

    echo "Running migrations..."
    "${COMPOSE[@]}" --profile migrate run --rm migrate

    rung_label="${LABEL}-n${n}"
    out="${OUT_DIR}/${rung_label}.json"

    echo "Running harness: N=$n label=$rung_label out=$out"
    set +e
    dotnet run -c Release --project loadtest/KiriChess.LoadTest -- \
        --games "$n" --pool "$POOL" --api "$API" --scrape-token "$SCRAPE_TOKEN" \
        --think-ms "$THINK_MS" --jitter-ms "$JITTER_MS" \
        --warmup-seconds "$WARMUP_SECONDS" --window-seconds "$WINDOW_SECONDS" \
        --presence "$PRESENCE" --seed "$SEED" --pinned-cpus "$PINNED_CPUS" --load-memory "$LOAD_MEMORY" \
        --label "$rung_label" --out "$out"
    harness_exit=$?
    set -e

    echo "Tearing down (down -v)..."
    "${COMPOSE[@]}" down -v

    if [ "$harness_exit" -ne 0 ]; then
        echo "Rung N=$n did not pass every criterion (exit $harness_exit). See $out."
        if [ "$STOP_ON_FAIL" = "1" ]; then
            echo "Stopping ladder on first failing/void rung."
            break
        fi
    else
        echo "Rung N=$n passed."
    fi
done

echo
echo "=== Ladder finished ==="
